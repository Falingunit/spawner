"""Integration checks using real file copies/rsync and mocked host/network commands.
Run: python3 -m unittest discover -s scripts/tests -v
"""
import fcntl
import io
import json
import os
from pathlib import Path
import subprocess
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
        config = self.root / 'config'
        config.write_text(f'INSTALL_ROOT={self.install}\nBACKUP_ROOT={self.root}/backups\nHEALTH_ATTEMPTS=1\n')
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
    if scenario == 'slow-start' and (root / 'install/app/backend/Spawner.dll').read_text() == 'new' and not (root / 'startup-retried').exists():
        (root / 'startup-retried').touch()
        print('curl: (7) Failed to connect', file=sys.stderr)
        sys.exit(7)
    if scenario == 'unhealthy' and (root / 'install/app/backend/Spawner.dll').read_text() == 'new':
        print('curl: (22) HTTP error', file=sys.stderr)
        sys.exit(22)
    out.write_text(json.dumps({'servers': [{'id': 'test', 'status': 'running' if scenario == 'busy' else 'offline'}]}))
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
        return subprocess.run(['bash', str(self.script), *args],
                              env={**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}',
                                   'TEST_ROOT': str(self.root), 'SCENARIO': scenario},
                              text=True, capture_output=True)

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
            config.write(f'FRONTEND_INSTALL_DIR={external}\nAPI_URL=http://127.0.0.1:5001\n')
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
        self.assertIn('curl: (22) HTTP error', result.stderr)
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
        self.assertNotIn('curl:', result.stderr)
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
