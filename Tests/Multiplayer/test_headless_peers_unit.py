"""Fast harness protocol and failure-cleanup tests; no game assets required."""
import json
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest import mock

import test_headless_peers as harness


class ProtocolTests(unittest.TestCase):
    def test_fragmented_header_and_body(self):
        sender, receiver = socket.socketpair()
        self.addCleanup(sender.close)
        self.addCleanup(receiver.close)
        receiver.settimeout(1)
        data = b'a real length-prefixed payload'
        wire = struct.pack('<I', len(data)) + data

        def write():
            for byte in wire:
                sender.sendall(bytes([byte]))
            sender.shutdown(socket.SHUT_WR)

        thread = threading.Thread(target=write)
        thread.start()
        try:
            self.assertEqual(harness.receive_frame(receiver), data)
        finally:
            thread.join(timeout=1)
        self.assertFalse(thread.is_alive())

    def test_truncated_payload_fails(self):
        sender, receiver = socket.socketpair()
        self.addCleanup(sender.close)
        self.addCleanup(receiver.close)
        sender.sendall(struct.pack('<I', 10) + b'only3')
        sender.shutdown(socket.SHUT_WR)
        with self.assertRaisesRegex(RuntimeError, 'framed bytes missing'):
            harness.receive_frame(receiver)

    def test_zero_and_oversize_frames_fail_before_allocation(self):
        for size in (0, harness.MAX_CONTROL_BYTES + 1):
            with self.subTest(size=size):
                sender, receiver = socket.socketpair()
                try:
                    sender.sendall(struct.pack('<I', size))
                    with self.assertRaisesRegex(RuntimeError, 'Invalid harness frame size'):
                        harness.receive_frame(receiver)
                finally:
                    sender.close()
                    receiver.close()

    def test_read_timeout_fails(self):
        sender, receiver = socket.socketpair()
        self.addCleanup(sender.close)
        self.addCleanup(receiver.close)
        receiver.settimeout(0.01)
        with self.assertRaises(socket.timeout):
            harness.receive_frame(receiver)

    def test_deadline_applies_across_fragmented_reads(self):
        sender, receiver = socket.socketpair()
        self.addCleanup(sender.close)
        self.addCleanup(receiver.close)
        stop = threading.Event()

        def dribble():
            try:
                sender.sendall(struct.pack('<I', 20))
                while not stop.wait(0.01):
                    sender.sendall(b'x')
            except OSError:
                pass

        thread = threading.Thread(target=dribble)
        started = time.monotonic()
        thread.start()
        try:
            with self.assertRaises((RuntimeError, socket.timeout)):
                harness.receive_frame(receiver, started + 0.05)
        finally:
            stop.set()
            thread.join(timeout=1)
        self.assertLess(time.monotonic() - started, 2)
        self.assertFalse(thread.is_alive())

    def test_default_impairment_requires_observed_paths(self):
        args = SimpleNamespace(rtt_ms=200, jitter_ticks=2, loss=0.03, duplicates=0.02)
        relay = {'random_dropped': 2, 'duplicated': 3, 'reordered': 4}
        statuses = [{'rollbacks': 1}, {'rollbacks': 2}]
        coverage = harness.validate_impairment_coverage(args, statuses, relay)
        self.assertTrue(coverage['default_profile_assertions'])
        self.assertEqual(coverage['unexercised_requested'], [])
        for key in relay:
            with self.subTest(key=key), self.assertRaisesRegex(RuntimeError, 'never exercised'):
                harness.validate_impairment_coverage(args, statuses, dict(relay, **{key: 0}))
        with self.assertRaisesRegex(RuntimeError, 'rollback'):
            harness.validate_impairment_coverage(args, [{'rollbacks': 0}] * 2, relay)
        args.loss = args.duplicates = 1e-20
        coverage = harness.validate_impairment_coverage(args, statuses, dict(relay, random_dropped=0, duplicated=0))
        self.assertEqual(coverage['unexercised_requested'], ['random_loss', 'duplicates'])

    def test_response_preserves_exact_production_bytes(self):
        status = json.dumps({'protocol': 1, 'slot': 0}).encode()
        packet = bytes(range(80))
        decoded, returned = harness.parse_response(b'R' + struct.pack('<I', len(status)) + status + packet, b'R')
        self.assertEqual(decoded['slot'], 0)
        self.assertEqual(returned, packet)
        payload = harness.tick_payload(True, [packet, packet])
        self.assertEqual(payload[:6], b'T\1\2\0\0\0')
        self.assertEqual(payload[10:90], packet)
        self.assertEqual(payload[94:], packet)

    def test_malformed_response_and_command_rejected(self):
        for payload in (b'', b'R\1\0\0\0', b'Z\1\0\0\0x', b'R\0\0\0\0'):
            with self.subTest(payload=payload), self.assertRaises(RuntimeError):
                harness.parse_response(payload, b'R')
        with self.assertRaisesRegex(RuntimeError, 'Invalid production packet'):
            harness.tick_payload(True, [b'short'])
        with self.assertRaisesRegex(RuntimeError, 'Too many packets'):
            harness.tick_payload(True, [bytes(40)] * 1025)

    def test_baseline_and_confirmation_failure_are_detected(self):
        good = {'protocol': 1, 'slot': 0, 'pid': 12340, 'frame': 600, 'confirmed': 600,
                'verified': 600, 'emitted': 600, 'state_digest': 'a', 'confirmed_digest': 'a',
                'baseline_final_digest': 'a', 'state_chain': 'b', 'baseline_state_chain': 'b',
                'effect_chain': 'c', 'baseline_effect_chain': 'c', 'rejected_batches': 0,
                'cars': [{'elapsed': 10, 'displacement': 2}], 'contacts': 0, 'impacts': 0}
        client = dict(good, slot=1, pid=12341)
        harness.validate_final([good, client], 600, 'healthy', {})
        with self.assertRaisesRegex(RuntimeError, 'effect_chain differs'):
            harness.validate_final([good, dict(client, effect_chain='wrong')], 600, 'healthy', {})
        with self.assertRaisesRegex(RuntimeError, 'emitted=599'):
            harness.validate_final([good, dict(client, emitted=599)], 600, 'healthy', {})


class CleanupTests(unittest.TestCase):
    @staticmethod
    def args(timeout=0.15):
        return SimpleNamespace(peer=Path(sys.executable), root=Path('.'), frames=600,
                               imported=None, timeout=timeout, seed=7)

    def test_child_exit_during_startup_is_reported_and_reaped(self):
        # Python rejects native peer arguments and exits before connecting.
        with tempfile.TemporaryDirectory() as directory:
            result = harness.run_case(self.args(timeout=2), 'healthy', 0, Path(directory))
        self.assertFalse(result['passed'])
        self.assertIn('exited during startup', result['error'])
        self.assertEqual(len(result['process_exit_codes']), 2)
        self.assertTrue(all(code is not None for code in result['process_exit_codes']))

    def test_deadline_terminates_real_unresponsive_children(self):
        real_popen = subprocess.Popen
        children = []

        def launch(*args, **kwargs):
            child = real_popen([sys.executable, '-c', 'import time; time.sleep(30)'], **kwargs)
            children.append(child)
            return child

        with tempfile.TemporaryDirectory() as directory, mock.patch.object(harness.subprocess, 'Popen', side_effect=launch):
            result = harness.run_case(self.args(), 'healthy', 0, Path(directory))
        self.assertFalse(result['passed'])
        self.assertIn('exceeded --timeout', result['error'])
        self.assertEqual(len(children), 2)
        self.assertTrue(all(child.poll() is not None for child in children))
        self.assertLess(result['elapsed_seconds'], 5)

    def test_partial_spawn_failure_reaps_first_child(self):
        real_popen = subprocess.Popen
        children = []

        def launch(*args, **kwargs):
            if children:
                raise OSError('deliberate second spawn failure')
            child = real_popen([sys.executable, '-c', 'import time; time.sleep(30)'], **kwargs)
            children.append(child)
            return child

        with tempfile.TemporaryDirectory() as directory, mock.patch.object(harness.subprocess, 'Popen', side_effect=launch):
            result = harness.run_case(self.args(), 'healthy', 0, Path(directory))
        self.assertFalse(result['passed'])
        self.assertIn('deliberate second spawn failure', result['error'])
        self.assertEqual(len(children), 1)
        self.assertIsNotNone(children[0].poll())

    def test_terminate_timeout_escalates_to_kill(self):
        process = mock.Mock()
        process.poll.return_value = None
        process.wait.side_effect = [subprocess.TimeoutExpired('fake peer', 3), -9]
        harness.stop_processes([process])
        process.terminate.assert_called_once_with()
        process.kill.assert_called_once_with()
        self.assertEqual(process.wait.call_count, 2)


if __name__ == '__main__':
    unittest.main()
