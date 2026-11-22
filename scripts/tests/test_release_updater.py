"""Integration checks using real file copies/rsync and mocked host/network commands.
Run: python3 -m unittest discover -s scripts/tests -v
"""
import fcntl
import base64
import hashlib
import io
import http.server
import threading
import pty
import select
import time
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'update-spawner-release.sh'
SHA = 'a' * 40


class ReleaseUpdaterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.install = self.root / 'install'
        self.backend = self.install / 'app/backend'
        self.frontend = self.install / 'www'
        self.backend.mkdir(parents=True)
        self.frontend.mkdir()
        (self.backend / 'Spawner.dll').write_text('old')
        (self.backend / 'obsolete.dll').write_text('obsolete')
        (self.backend / 'appsettings.Production.json').write_text('local config')
        (self.backend / 'Instances').mkdir()
        (self.backend / 'Instances/world').write_text('world data')
        (self.frontend / 'index.html').write_text('old UI')
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.scenario = ''
        self.requests = []
        test = self
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass

            def do_GET(self):
                test.requests.append((self.path, dict(self.headers)))
                new = (test.backend / 'Spawner.dll').read_text() == 'new'
                scenario = test.scenario
                status, body = 200, {}
                if self.headers.get('X-Forwarded-Proto') != 'https':
                    status = 400
                elif new and scenario in ('unhealthy', 'auth-unhealthy'):
                    status = 503
                elif new and scenario == 'slow-start' and not (test.root / 'startup-retried').exists():
                    (test.root / 'startup-retried').touch()
                    self.connection.close()
                    return
                elif self.path.endswith('/auth/session'):
                    if not new and not scenario.startswith('auth'):
                        status = 404
                    else:
                        body = dict(authenticated=False, csrfToken='test-csrf')
                elif self.path.endswith('/servers'):
                    if (new or scenario.startswith('auth')) and 'session=valid' not in self.headers.get('Cookie', ''):
                        status = 401
                    else:
                        body = {'servers': [{'id': 'test', 'status': 'running' if scenario in ('busy', 'auth-busy') else 'offline'}]}
                else:
                    status = 404
                self.send_response(status)
                if self.path.endswith('/auth/session'):
                    self.send_header('Set-Cookie', '__Host-csrf=valid; Path=/; Secure; HttpOnly')
                self.end_headers()
                self.wfile.write(json.dumps(body).encode())

            def do_POST(self):
                test.requests.append((self.path, dict(self.headers)))
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                valid = (self.headers.get('Origin') == 'https://spawner.example.com'
                         and self.headers.get('X-CSRF-Token') == 'test-csrf'
                         and '__Host-csrf=valid' in self.headers.get('Cookie', '')
                         and self.headers.get('X-Forwarded-Proto') == 'https')
                if self.path.endswith('/login'):
                    valid = valid and body == dict(username='admin', password='correct-password')
                self.send_response(204 if valid else 403)
                if valid:
                    self.send_header('Set-Cookie', 'session=valid; Path=/; Secure; HttpOnly')
                self.end_headers()
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.api_url = f'http://127.0.0.1:{server.server_port}'
        config = self.root / 'config'
        config.write_text(f'INSTALL_ROOT={self.install}\nBACKUP_ROOT={self.root}/backups\nHEALTH_ATTEMPTS=1\nAPI_URL={self.api_url}\n')
        config.chmod(0o644)
        # Relocate only the config path; production code/operations are otherwise unchanged.
        self.script = self.root / 'updater.sh'
        self.script.write_text(SCRIPT.read_text().replace('/etc/spawner-update.conf', str(config)))
        self.mock('id', '#!/bin/sh\necho 0\n')
        self.mock('stat', '#!/bin/sh\necho 0\n')
        self.mock('uname', '#!/bin/sh\necho x86_64\n')
        self.mock('dotnet', '#!/bin/sh\necho "Microsoft.AspNetCore.App 10.0.0 [/mock]"\n')
        self.mock('sleep', '#!/bin/sh\nexit 0\n')
        self.mock('chown', '#!/bin/sh\nexit 0\n')
        self.mock('systemctl', '''#!/bin/sh
printf '%s\n' "$*" >> "$TEST_ROOT/service.log"
exit 0
''')
        self.mock('curl', '''#!/usr/bin/env python3
import json, os, pathlib, shutil, sys
root = pathlib.Path(os.environ['TEST_ROOT'])
args = sys.argv[1:]
out = pathlib.Path(args[args.index('-o') + 1])
url = next(x for x in args if x.startswith('https://') or x.startswith('http://'))
scenario = os.environ.get('SCENARIO', '')
if '/commits/' in url:
    assert url.endswith('/commits/master'), url
    out.write_text(json.dumps({'sha': 'a' * 40}))
elif '/download/' in url:
    if scenario == 'missing': sys.exit(22)
    shutil.copyfile(root / 'release.tar.gz', out)
else:
    raise AssertionError('API calls must use the real HTTP test server')
''')
        self.archive()

    def mock(self, name, source):
        path = self.bin / name
        path.write_text(source)
        path.chmod(0o755)

    def archive(self, unsafe=False):
        files = {'spawner/backend/Spawner.dll': 'new',
                 'spawner/backend/appsettings.Production.json': 'release config',
                 'spawner/backend/appsettings.New.json': 'new defaults',
                 'spawner/www/index.html': 'new UI',
                 'spawner/BUILD_INFO.txt': f'commit={SHA}\n'}
        if unsafe:
            files['../escaped'] = 'bad'
        with tarfile.open(self.root / 'release.tar.gz', 'w:gz') as archive:
            for name, value in files.items():
                data = value.encode()
                info = tarfile.TarInfo(name)
                info.size = len(data)
                archive.addfile(info, io.BytesIO(data))

    def run_update(self, scenario='', *args):
        self.scenario = scenario
        return subprocess.run(['bash', str(self.script), *args],
                              env={**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}',
                                   'TEST_ROOT': str(self.root), 'SCENARIO': scenario},
                              text=True, capture_output=True, start_new_session=True)

    def interactive_update(self, scenario, args, answers):
        self.scenario = scenario
        master, slave = pty.openpty()
        # Acquire the controlling terminal in a fresh process, not a threaded fork.
        process = subprocess.Popen([sys.executable, '-c',
            'import fcntl,os,sys,termios; fcntl.ioctl(0, termios.TIOCSCTTY, 0); os.execv("/bin/bash", ["bash", *sys.argv[1:]])',
            str(self.script), *args], stdin=slave, stdout=slave, stderr=slave,
            start_new_session=True, env={**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}',
                                        'TEST_ROOT': str(self.root), 'SCENARIO': scenario})
        os.close(slave)
        output = ''
        pending = list(answers)
        deadline = time.monotonic() + 15
        try:
            while time.monotonic() < deadline:
                if select.select([master], [], [], 0.1)[0]:
                    try:
                        chunk = os.read(master, 65536)
                    except OSError:
                        break
                    if not chunk: break
                    output += chunk.decode()
                    if pending and pending[0][0] in output:
                        _, answer = pending.pop(0)
                        os.write(master, (answer + '\n').encode())
            else:
                process.kill()
                self.fail('Interactive updater timed out: ' + output)
        finally:
            os.close(master)
            code = process.wait(timeout=5)
        self.assertFalse(pending, output)
        return code, output

    def login_answers(self, password='correct-password'):
        return [('Administrator username:', 'admin'), ('Administrator password:', password),
                ('Public HTTPS origin (same as browser address):', 'https://spawner.example.com')]

    def test_authenticated_update(self):
        code, output = self.interactive_update('auth', [], self.login_answers())
        self.assertEqual(code, 0, output)
        self.assertNotIn('correct-password', output)
        self.assertIn('/api/v1/auth/logout', [p for p, _ in self.requests])
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'new')

    def test_failed_health_restores_authenticated_backend(self):
        code, output = self.interactive_update('auth-unhealthy', [], self.login_answers())
        self.assertNotEqual(code, 0, output)
        self.assertIn('Previous application restored', output)
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'old')

    def test_authenticated_busy_update_leaves_service_untouched(self):
        code, output = self.interactive_update('auth-busy', [], self.login_answers())
        self.assertNotEqual(code, 0, output)
        self.assertIn('Stop instances', output)
        self.assertFalse((self.root / 'service.log').exists())

    def test_bad_login_leaves_service_untouched(self):
        code, output = self.interactive_update('auth', [], self.login_answers('wrong-password'))
        self.assertNotEqual(code, 0, output)
        self.assertIn('HTTP Error 403', output)
        self.assertFalse((self.root / 'service.log').exists())

    def test_login_without_terminal_fails_closed(self):
        result = self.run_update('auth')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('run interactively', result.stderr)
        self.assertFalse((self.root / 'service.log').exists())

    def test_reject_remote_plaintext_api(self):
        with (self.root / 'config').open('a') as config:
            config.write('API_URL=http://192.0.2.1:5001\n')
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('HTTPS origin or literal loopback', result.stderr)
        self.assertFalse((self.root / 'service.log').exists())

    def test_configure_auth_preserves_settings_and_hashes_password(self):
        path = self.backend / 'appsettings.Production.json'
        original = {'Settings': {'DefaultJavaArgs': '-Xmx8G'}}
        path.write_text(json.dumps(original))
        password = 'a-long-admin-password'
        code, output = self.interactive_update('', ['--configure-auth'], [
            ('Administrator username [admin]:', ''),
            ('Public HTTPS origin (for example', 'https://spawner.example.com/'),
            ('New administrator password (16–1024 characters):', password),
            ('Confirm password:', password)])
        self.assertEqual(code, 0, output)
        self.assertNotIn(password, output)
        saved = json.loads(path.read_text())
        self.assertEqual(saved['Settings'], original['Settings'])
        self.assertEqual(saved['Authentication']['PublicOrigin'], 'https://spawner.example.com')
        kind, salt, digest = saved['Authentication']['PasswordHash'].split(':')
        self.assertEqual(kind, 'pbkdf2-sha256-600000')
        self.assertEqual(base64.b64decode(digest), hashlib.pbkdf2_hmac(
            'sha256', password.encode(), base64.b64decode(salt), 600000))
        self.assertEqual(path.stat().st_mode & 0o777, 0o640)
        backup, = (self.root / 'backups').glob('*.before-auth-*')
        self.assertEqual(json.loads(backup.read_text()), original)
        self.assertEqual(backup.stat().st_mode & 0o777, 0o600)
        self.assertFalse((self.root / 'service.log').exists())

    def test_configure_auth_rejects_plaintext_public_origin(self):
        code, output = self.interactive_update('', ['--configure-auth'], [
            ('Administrator username [admin]:', 'admin'),
            ('Public HTTPS origin (for example', 'http://spawner.example.com')])
        self.assertNotEqual(code, 0, output)
        self.assert_preserved()

    def assert_preserved(self):
        self.assertEqual((self.backend / 'appsettings.Production.json').read_text(), 'local config')
        self.assertEqual((self.backend / 'Instances/world').read_text(), 'world data')

    def test_success_and_backup(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'new')
        self.assertEqual((self.frontend / 'index.html').read_text(), 'new UI')
        self.assertFalse((self.backend / 'obsolete.dll').exists())
        self.assertEqual((self.backend / 'appsettings.New.json').read_text(), 'new defaults')
        self.assert_preserved()
        backups = list((self.root / 'backups').glob('update-*'))
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / 'backend/Spawner.dll').read_text(), 'old')

    def test_frontend_outside_install_root(self):
        external = self.root / 'nginx-www'
        self.frontend.rename(external)
        with (self.root / 'config').open('a') as config:
            config.write(f'FRONTEND_INSTALL_DIR={external}\n')
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((external / 'index.html').read_text(), 'new UI')
        self.assertFalse(self.frontend.exists())
        self.assert_preserved()

    def test_explicit_tag(self):
        result = self.run_update('', 'commit-' + SHA)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_failed_health_rolls_back(self):
        result = self.run_update('unhealthy')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Previous application restored', result.stderr)
        self.assertIn('API health check failed after 1 attempts', result.stderr)
        self.assertIn('HTTP Error 503', result.stderr)
        self.assertIn('journalctl -u spawner', result.stderr)
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'old')
        self.assertEqual((self.frontend / 'index.html').read_text(), 'old UI')
        self.assertTrue((self.backend / 'obsolete.dll').exists())
        self.assert_preserved()

    def test_startup_connection_failure_is_retried_quietly(self):
        with (self.root / 'config').open('a') as config:
            config.write('HEALTH_ATTEMPTS=2\n')
        result = self.run_update('slow-start')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.root / 'startup-retried').exists())
        self.assertIn('Waiting for spawner API', result.stdout)
        self.assertIn('Updated to commit-', result.stdout)
        self.assertNotIn('Spawner API check failed', result.stderr)
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'new')

    def test_busy_instances_leave_service_untouched(self):
        result = self.run_update('busy')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Stop instances', result.stderr)
        self.assertFalse((self.root / 'service.log').exists())

    def test_missing_release_leaves_service_untouched(self):
        self.assertNotEqual(self.run_update('missing').returncode, 0)
        self.assertFalse((self.root / 'service.log').exists())

    def test_unsafe_archive_leaves_service_untouched(self):
        self.archive(unsafe=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Unsafe release archive', result.stderr)
        self.assertFalse((self.root / 'service.log').exists())

    def test_failed_install_rolls_back(self):
        self.mock('rsync', """#!/bin/sh
if [ ! -f "$TEST_ROOT/rsync-failed" ]; then
  touch "$TEST_ROOT/rsync-failed"
  printf broken > "$TEST_ROOT/install/app/backend/Spawner.dll"
  exit 1
fi
exec /usr/bin/rsync "$@"
""")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Previous application restored', result.stderr)
        self.assertEqual((self.backend / 'Spawner.dll').read_text(), 'old')
        self.assert_preserved()

    def test_concurrent_update_is_rejected(self):
        backups = self.root / 'backups'
        backups.mkdir()
        with (backups / 'update.lock').open('w') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Another update is running', result.stderr)
        self.assertFalse((self.root / 'service.log').exists())

    def test_invalid_tag(self):
        self.assertNotEqual(self.run_update('', '../bad').returncode, 0)
        self.assertFalse((self.root / 'service.log').exists())


if __name__ == '__main__':
    unittest.main()
