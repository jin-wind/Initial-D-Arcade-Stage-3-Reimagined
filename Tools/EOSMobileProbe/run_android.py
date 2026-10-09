"""Execute one role on an emulator; only allowlisted probe reports are persisted."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

PACKAGE = 'com.idas3.eosprobe'
FILES = f'/sdcard/Android/data/{PACKAGE}/files'
CONFIG = f'{FILES}/probe-config.json'
REPORT = f'{FILES}/report.jsonl'
FIELDS = {'phase', 'result', 'role', 'elapsedMs', 'hasPuid', 'cleanupOk',
          'relayedConnection', 'reliableReceived', 'unreliableReceived',
          'reliableAcked', 'unreliableAcked', 'completionHandshake',
          'distinctPeer', 'members', 'payloadBytes'}
REQUIRED = {'hasPuid', 'cleanupOk', 'relayedConnection', 'reliableReceived',
            'unreliableReceived', 'reliableAcked', 'unreliableAcked',
            'completionHandshake', 'distinctPeer'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--apk', type=Path, required=True)
    parser.add_argument('--role', choices=['host', 'join'], required=True)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--serial')
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', args.run_id):
        raise RuntimeError('Invalid run marker')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text('', encoding='utf-8')
    adb = [os.environ.get('ADB', 'adb')]
    if args.serial:
        adb += ['-s', args.serial]

    def command(*parts, data=None, check=True):
        result = subprocess.run(adb + list(parts), input=data, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=90)
        if check and result.returncode:
            # Never render raw Android/SDK output, environment, or input data.
            raise RuntimeError('ADB operation failed: ' + parts[0])
        return result

    config = {'role': args.role, 'runId': args.run_id, 'waitSeconds': 600}
    for field, name in [('productId', 'EOS_PRODUCT_ID'), ('sandboxId', 'EOS_SANDBOX_ID'),
                        ('deploymentId', 'EOS_DEPLOYMENT_ID'), ('clientId', 'EOS_CLIENT_ID'),
                        ('clientSecret', 'EOS_CLIENT_SECRET')]:
        value = os.environ.get(name, '')
        if not value.strip():
            raise RuntimeError('Missing setting: ' + name)
        config[field] = value
    command('install', '-r', str(args.apk.resolve()))
    command('shell', 'am', 'force-stop', PACKAGE)
    command('shell', 'mkdir', '-p', FILES)
    command('shell', 'rm', '-f', REPORT, CONFIG)
    seen = 0
    last_phase = None
    try:
        # Stream through stdin so the secret appears in neither argv nor host files.
        command('shell', f'cat > {CONFIG}', data=json.dumps(config).encode('utf-8'))
        config.clear()
        command('shell', 'am', 'start', '-n', PACKAGE + '/com.unity3d.player.UnityPlayerActivity')
        deadline = time.monotonic() + 750
        while time.monotonic() < deadline:
            result = command('shell', 'cat', REPORT, check=False)
            lines = result.stdout.decode('utf-8-sig', errors='replace').splitlines()
            for line in lines[seen:]:
                try:
                    event = json.loads(line)
                except (ValueError, TypeError):
                    break  # A writer may still be appending the final line.
                clean = {}
                for key, value in event.items():
                    if key not in FIELDS:
                        continue
                    if isinstance(value, (int, bool)):
                        clean[key] = value
                    elif isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9_:-]{1,64}', value):
                        clean[key] = value
                seen += 1
                with args.report.open('a', encoding='utf-8') as stream:
                    stream.write(json.dumps(clean) + '\n')
                phase = (clean.get('phase'), clean.get('result'))
                if phase != last_phase:
                    print(args.role, *phase, flush=True)
                    last_phase = phase
                if clean.get('phase') == 'summary':
                    passed = (clean.get('result') == 'Passed' and
                              clean.get('role') == args.role and
                              clean.get('members') == 2 and
                              all(clean.get(key) is True for key in REQUIRED))
                    if not passed:
                        raise RuntimeError('Android EOS probe did not pass all relay checks')
                    if command('shell', 'test', '-e', CONFIG, check=False).returncode == 0:
                        raise RuntimeError('Runtime configuration was not consumed')
                    return
            time.sleep(2)
        raise RuntimeError('Android EOS probe timed out')
    finally:
        config.clear()
        command('shell', 'rm', '-f', CONFIG, check=False)
        command('shell', 'am', 'force-stop', PACKAGE, check=False)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(str(error) if isinstance(error, RuntimeError) else 'ADB operation timed out', file=sys.stderr)
        sys.exit(1)
