"""Two real native processes exchanging production packets over loopback TCP.

The relay applies deterministic logical 60 Hz latency/loss/reordering/outages.
This is core integration coverage, NOT the Unity scene, lobby, Steam/EOS transport,
rendering, Android/iOS, real-time performance, saves, or a complete played race.
Each process independently records an ordinary-driving offline baseline and
checks every confirmed frame and selected effect against that same-input oracle.

Build headless_online_peer, then run:
  python Tests/Multiplayer/test_headless_peers.py --peer PATH --root Native
Optional --output must name a NEW directory; otherwise a temporary report
folder is created. All listeners bind only to 127.0.0.1. No dependencies beyond
Python's standard library and the compiled native peer are required.
"""
from __future__ import annotations

import argparse
import hashlib
import heapq
import json
import math
import os
from pathlib import Path
import platform
import random
import socket
import struct
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
MAX_CONTROL_BYTES = 2 * 1024 * 1024
CASES = ('healthy', 'impaired', 'outage-to-host', 'outage-to-client', 'outage-both')
OUTAGE_START, OUTAGE_END = 180, 360


def check(condition, reason):
    if not condition:
        raise RuntimeError(reason)


def receive_exact(connection, count, deadline=None):
    chunks = []
    remaining = count
    while remaining:
        if deadline is not None:
            connection.settimeout(remaining_time(deadline))
        block = connection.recv(remaining)
        if not block:
            raise RuntimeError(f'Peer closed with {remaining} framed bytes missing')
        chunks.append(block)
        remaining -= len(block)
    return b''.join(chunks)


def receive_frame(connection, deadline=None):
    size, = struct.unpack('<I', receive_exact(connection, 4, deadline))
    check(0 < size <= MAX_CONTROL_BYTES, f'Invalid harness frame size: {size}')
    return receive_exact(connection, size, deadline)


def send_frame(connection, payload):
    check(0 < len(payload) <= MAX_CONTROL_BYTES, 'Invalid outgoing harness frame size')
    connection.sendall(struct.pack('<I', len(payload)) + payload)


def parse_response(payload, expected):
    check(len(payload) >= 5 and payload[:1] == expected, 'Unexpected native peer response')
    size, = struct.unpack_from('<I', payload, 1)
    check(0 < size <= len(payload) - 5, 'Invalid response JSON length')
    status = json.loads(payload[5:5 + size])
    check(isinstance(status, dict) and status.get('protocol') == 1, 'Unknown harness protocol')
    packet = payload[5 + size:]
    if expected == b'R':
        check(40 <= len(packet) <= 1064, 'Invalid production packet size in response')
    else:
        check(not packet, 'Unexpected bytes following hello/quit response')
    return status, packet


def tick_payload(advance, packets):
    check(len(packets) <= 1024, 'Too many packets scheduled in one tick')
    result = bytearray(b'T' + bytes([bool(advance)]) + struct.pack('<I', len(packets)))
    for packet in packets:
        check(40 <= len(packet) <= 1064, 'Invalid production packet size in request')
        result += struct.pack('<I', len(packet)) + packet
    return bytes(result)


def remaining_time(deadline):
    seconds = deadline - time.monotonic()
    check(seconds > 0, 'Headless peer case exceeded --timeout')
    return seconds


def stop_processes(processes):
    """Reap every owned child, including one spawned before partial startup failed."""
    for process in processes:
        if process.poll() is None:
            process.terminate()
    for process in processes:
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)


def accept_peers(listener, processes, race, deadline):
    peers = {}
    try:
        while len(peers) < 2:
            for process in processes:
                check(process.poll() is None, f'Native peer exited during startup (code {process.returncode}); see peer log')
            listener.settimeout(min(0.1, remaining_time(deadline)))
            try:
                connection, address = listener.accept()
            except socket.timeout:
                continue
            try:
                check(address[0] == '127.0.0.1', 'Non-loopback peer refused')
                connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                connection.settimeout(remaining_time(deadline))
                status, _ = parse_response(receive_frame(connection, deadline), b'H')
                slot = status.get('slot')
                check(slot in (0, 1) and slot not in peers, 'Duplicate/unknown peer role')
                check(status.get('pid') == processes[slot].pid and status.get('race') == race, 'Unexpected peer identity')
                peers[slot] = (connection, status)
            except BaseException:
                connection.close()
                raise
        return peers
    except BaseException:
        for connection, _ in peers.values():
            connection.close()
        raise


def validate_final(statuses, frames, case, relay):
    for slot, status in enumerate(statuses):
        check(status['slot'] == slot, 'Peer role changed')
        for key in ('frame', 'confirmed', 'verified', 'emitted'):
            check(status[key] == frames, f"{case}: peer {slot} {key}={status[key]}, expected {frames}")
        for actual, expected in (('state_digest', 'baseline_final_digest'), ('confirmed_digest', 'baseline_final_digest'),
                                 ('state_chain', 'baseline_state_chain'), ('effect_chain', 'baseline_effect_chain')):
            check(status[actual] == status[expected], f'{case}: peer {slot} {actual} differs from offline oracle')
        check(status['rejected_batches'] == 0, f'{case}: valid relay packet rejected')
        for car in status['cars']:
            check(car['elapsed'] > 0 and car['displacement'] > 1, f'{case}: car did not drive after countdown')
    check(len({statuses[0]['pid'], statuses[1]['pid'], os.getpid()}) == 3, 'Peer processes are not isolated')
    for key in ('state_digest', 'state_chain', 'effect_chain', 'cars', 'contacts', 'impacts'):
        check(statuses[0][key] == statuses[1][key], f'{case}: peers disagree on {key}')
    if case.startswith('outage-'):
        check(relay['outage_dropped'] > 0, 'Outage was never exercised')
        check(sum(status['stalls'] for status in statuses) > 0, 'Outage never exhausted bounded history')
        check(relay['stale_injected'] == 2, 'Post-outage stale packets were not exercised')


def validate_impairment_coverage(args, statuses, relay):
    observed = {'random_loss': relay['random_dropped'] > 0,
                'duplicates': relay['duplicated'] > 0,
                'reordering': relay['reordered'] > 0,
                'rollback': any(status['rollbacks'] > 0 for status in statuses)}
    default_profile = (args.rtt_ms, args.jitter_ticks, args.loss, args.duplicates) == (200, 2, 0.03, 0.02)
    if default_profile:
        for name, exercised in observed.items():
            check(exercised, f'Default impaired case never exercised {name}')
    requested = {'random_loss': args.loss > 0, 'duplicates': args.duplicates > 0,
                 'reordering': args.jitter_ticks > 0 or args.duplicates > 0, 'rollback': True}
    # A custom tiny probability can legitimately produce zero events. Make that
    # gap explicit, while the standard CI profile requires every path above.
    return {'default_profile_assertions': default_profile, 'observed': observed,
            'unexercised_requested': [name for name, wanted in requested.items() if wanted and not observed[name]]}


def run_case(args, case, scenario, directory):
    started = time.monotonic()
    deadline = started + args.timeout
    seed = args.seed + scenario * 101 + CASES.index(case) * 1009
    rng = random.Random(seed)
    race = 726001 + scenario * 10 + CASES.index(case)
    result = {'case': case, 'scenario': scenario, 'seed': seed, 'passed': False,
              'clock': 'deterministic 60 Hz logical ticks; no real-time latency claim'}
    processes, log_files, peers = [], [], {}
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        listener.bind(('127.0.0.1', 0))
        listener.listen(2)
        port = listener.getsockname()[1]
        result['relay_address'] = f'127.0.0.1:{port}'
        for slot, role in enumerate(('host', 'client')):
            log_path = directory / f'scenario-{scenario}-{case}-{role}.log'
            log = log_path.open('xb')
            log_files.append(log)
            command = [str(args.peer), '--root', str(args.root), '--port', str(port),
                       '--slot', str(slot), '--frames', str(args.frames), '--scenario', str(scenario), '--race', str(race)]
            if args.imported:
                command += ['--imported', str(args.imported)]
            processes.append(subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log))
        peers = accept_peers(listener, processes, race, deadline)
        listener.close()
        statuses = [peers[slot][1] for slot in range(2)]
        check(statuses[0]['baseline_state_chain'] == statuses[1]['baseline_state_chain'], 'Independent offline baselines disagree')
        check(statuses[0]['baseline_effect_chain'] == statuses[1]['baseline_effect_chain'], 'Independent offline effects disagree')
        result['initial'] = list(statuses)
        result['peers'] = statuses
        queue, stale = [], {}
        sequence = 0
        last_received = [-1, -1]
        relay = dict(sent=0, delivered=0, random_dropped=0, outage_dropped=0,
                     duplicated=0, reordered=0, stale_injected=0, payload_bytes_sent=0)
        result['relay'] = relay
        outage_receivers = {'outage-to-host': {0}, 'outage-to-client': {1}, 'outage-both': {0, 1}}.get(case, set())
        half_rtt_ticks = max(1, math.ceil(args.rtt_ms / (2 * 1000 / 60))) if case == 'impaired' else 1
        max_ticks = args.frames * 3 + OUTAGE_END + 600 + half_rtt_ticks * 4
        outage_stalls_before = None
        for wall in range(max_ticks):
            result['ticks'] = wall + 1
            remaining_time(deadline)
            if outage_receivers and wall == OUTAGE_START:
                outage_stalls_before = [status['stalls'] for status in statuses]
            incoming = [[], []]
            while queue and queue[0][0] <= wall:
                _, serial, receiver, packet = heapq.heappop(queue)
                if receiver in outage_receivers and OUTAGE_START <= wall < OUTAGE_END:
                    relay['outage_dropped'] += 1
                    continue
                incoming[receiver].append(packet)
                relay['delivered'] += 1
                if serial < last_received[receiver]:
                    relay['reordered'] += 1
                last_received[receiver] = max(last_received[receiver], serial)
            for slot in range(2):
                connection = peers[slot][0]
                connection.settimeout(remaining_time(deadline))
                # Client is deliberately paced at 2/3 speed in outage cases.
                # The transport must continue even when either endpoint stalls.
                advance = not outage_receivers or slot == 0 or wall % 3 != 0
                send_frame(connection, tick_payload(advance, incoming[slot]))
                statuses[slot], packet = parse_response(receive_frame(connection, deadline), b'R')
                receiver = 1 - slot
                relay['sent'] += 1
                relay['payload_bytes_sent'] += len(packet)
                sequence += 1
                if outage_receivers and wall == OUTAGE_START - 5:
                    stale[receiver] = (sequence, packet)
                if case == 'impaired' and rng.random() < args.loss:
                    relay['random_dropped'] += 1
                    continue
                jitter = rng.randint(-args.jitter_ticks, args.jitter_ticks) if case == 'impaired' else 0
                delay = max(1, half_rtt_ticks + jitter)
                heapq.heappush(queue, (wall + delay, sequence, receiver, packet))
                if case == 'impaired' and rng.random() < args.duplicates:
                    relay['duplicated'] += 1
                    heapq.heappush(queue, (wall + delay + rng.randint(1, 3), sequence, receiver, packet))
            if outage_receivers and wall == OUTAGE_END - 1:
                result['outage_stalls'] = [status['stalls'] - before for status, before in zip(statuses, outage_stalls_before)]
            if outage_receivers and wall == OUTAGE_END + 5:
                for receiver, (serial, packet) in stale.items():
                    heapq.heappush(queue, (wall + 1, serial, receiver, packet))
                    relay['stale_injected'] += 1
            if all(all(status[key] == args.frames for key in ('frame', 'confirmed', 'verified', 'emitted')) for status in statuses):
                break
        else:
            raise RuntimeError(f'Peers failed to confirm/verify within {max_ticks} logical ticks')
        result.update(ticks=wall + 1, logical_seconds=(wall + 1) / 60, peers=statuses)
        validate_final(statuses, args.frames, case, relay)
        if outage_receivers:
            check(sum(result.get('outage_stalls', [])) > 0, 'No bounded-history stall during the outage')
        if case == 'impaired':
            result['impairment_coverage'] = validate_impairment_coverage(args, statuses, relay)
        for slot in range(2):
            connection = peers[slot][0]
            connection.settimeout(remaining_time(deadline))
            send_frame(connection, b'Q')
            final, _ = parse_response(receive_frame(connection, deadline), b'B')
            check(final == statuses[slot], 'Quit changed final peer state')
        for process in processes:
            check(process.wait(timeout=remaining_time(deadline)) == 0, 'Peer exited with failure; see its log')
        result['passed'] = True
    except Exception as error:
        result['error'] = f'{type(error).__name__}: {error}'
    finally:
        listener.close()
        for connection, _ in peers.values():
            connection.close()
        stop_processes(processes)
        result['process_exit_codes'] = [process.returncode for process in processes]
        result['elapsed_seconds'] = round(time.monotonic() - started, 3)
        for log in log_files:
            log.close()
    return result


def list_argument(value, allowed):
    values = value.split(',')
    if not values or len(set(values)) != len(values) or any(item not in allowed for item in values):
        raise argparse.ArgumentTypeError('Choose distinct comma-separated values from: ' + ','.join(allowed))
    return values


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--peer', required=True, type=Path, help='Compiled headless_online_peer executable')
    parser.add_argument('--root', type=Path, default=ROOT / 'Native', help='Native asset root containing data/')
    parser.add_argument('--output', type=Path, help='NEW report directory (default: a new temporary directory)')
    parser.add_argument('--frames', type=int, default=1800, help='Race frames per peer, at least 600 (default: 1800)')
    parser.add_argument('--scenarios', type=lambda value: list_argument(value, [str(i) for i in range(18)]), default=['3'])
    parser.add_argument('--cases', type=lambda value: list_argument(value, CASES), default=list(CASES))
    parser.add_argument('--imported', type=Path, help='Optional repository imported-course pack')
    parser.add_argument('--seed', type=int, default=54321)
    parser.add_argument('--timeout', type=float, default=120, help='Real seconds per case, including startup (default: 120)')
    parser.add_argument('--rtt-ms', type=float, default=200, help='Logical impaired-case round-trip latency (default: 200)')
    parser.add_argument('--jitter-ticks', type=int, default=2)
    parser.add_argument('--loss', type=float, default=0.03)
    parser.add_argument('--duplicates', type=float, default=0.02)
    args = parser.parse_args(argv)
    if not 600 <= args.frames <= 100000:
        parser.error('--frames must be between 600 and 100000')
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error('--timeout must be a finite positive number')
    if not math.isfinite(args.rtt_ms) or not 0 <= args.rtt_ms <= 1000 or not 0 <= args.jitter_ticks <= 30:
        parser.error('RTT must be 0..1000 ms and jitter must be 0..30 ticks')
    if not 0 <= args.loss <= 0.5 or not 0 <= args.duplicates <= 0.5:
        parser.error('Loss and duplicate probabilities must be in 0..0.5')
    args.peer, args.root = args.peer.resolve(), args.root.resolve()
    if not args.peer.is_file():
        parser.error('--peer must name a compiled executable file')
    if not (args.root / 'data').is_dir():
        parser.error('--root must contain the repository data directory')
    if args.imported:
        args.imported = args.imported.resolve()
        if not args.imported.exists():
            parser.error('--imported path does not exist')
    args.scenarios = [int(value) for value in args.scenarios]
    return args


def main(argv=None):
    args = arguments(argv)
    if args.output:
        directory = args.output.resolve()
        directory.mkdir(parents=True, exist_ok=False)
    else:
        directory = Path(tempfile.mkdtemp(prefix='initial-d-headless-peers-'))
    report = {
        'schema_version': 1, 'passed': False, 'harness': 'separate native processes + loopback TCP relay',
        'peer_executable': str(args.peer), 'peer_sha256': hashlib.sha256(args.peer.read_bytes()).hexdigest(),
        'platform': platform.platform(), 'python': platform.python_version(),
        'asset_root': str(args.root), 'imported_course': str(args.imported) if args.imported else None,
        'frames_per_peer': args.frames, 'network': {'logical_tick_hz': 60, 'impaired_rtt_ms': args.rtt_ms,
            'jitter_ticks': args.jitter_ticks, 'loss_probability': args.loss, 'duplicate_probability': args.duplicates,
            'outage_tick_range': [OUTAGE_START, OUTAGE_END]},
        'validates': ['production simulation, timeline, packet codec and hash exchange in separate OS processes',
                      'all confirmed state hashes and selected contact/countdown/rule/engine/tire effects against offline baseline',
                      'exactly-once ordered confirmations, bounded outage stalls, recovery and driving progress'],
        'not_validated': ['Unity application/scene or managed transport', 'Steam/EOS lobbies, matchmaking, NAT or authentication',
                          'Android/iOS builds or cross-platform determinism', 'rendering, audio playback or real-time performance',
                          'persistence, race awards or a complete played race'],
        'cases': [],
    }
    print(f'Report directory: {directory}', flush=True)
    try:
        for scenario in args.scenarios:
            for case in args.cases:
                result = run_case(args, case, scenario, directory)
                report['cases'].append(result)
                if result['passed']:
                    peers = result['peers']
                    print(f"PASS scenario={scenario} {case}: {args.frames * 2} verified peer frames; "
                          f"rollbacks={sum(peer['rollbacks'] for peer in peers)} stalls={sum(peer['stalls'] for peer in peers)}", flush=True)
                else:
                    print(f"FAIL scenario={scenario} {case}: {result['error']}", file=sys.stderr, flush=True)
        report['passed'] = all(case['passed'] for case in report['cases'])
        report['verified_peer_frames'] = sum(sum(peer['verified'] for peer in case.get('peers', [])) for case in report['cases'])
    except KeyboardInterrupt:
        report['error'] = 'Interrupted by user'
    finally:
        (directory / 'report.json').write_text(json.dumps(report, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
