"""Offline candidate delivery checks; GitHub operations use a filesystem fixture."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'tools/windows/delivery.ps1'
SHA = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
REPO = 'owner/repo'


class DeliveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'artifacts')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.verification = self.root / 'verification'
        self.run = self.verification / 'fixed'
        self.windows = self.run / 'windows'
        self.windows.mkdir(parents=True)
        self.report = dict(passed=True, sourceDirty=False, sourceCommit=SHA)
        self.write_report()
        for name in ('CozyTown.exe', 'CozyTown.pck',
                     'launcher/CozyTown.Play.exe', 'launcher/CozyTown.Play.runtimeconfig.json',
                     'launcher/launcher.example.json', 'launcher/new-play-entries.ps1', 'launcher/play-entry.ps1',
                     'data_CozyTown.Game_windows_x86_64/System.Private.CoreLib.dll',
                     'data_CozyTown.Game_windows_x86_64/CozyTown.Game.dll'):
            path = self.windows / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        (self.run / 'private.log').write_text('must not ship')
        self.package = self.root / 'candidate with spaces'

    def write_report(self):
        (self.run / 'verification.json').write_text(json.dumps(self.report))

    def command(self, mode, *args, env=None):
        return subprocess.run(['pwsh', '-NoProfile', '-File', str(SCRIPT), '-Mode', mode,
                               '-PackageRoot', str(self.package), *args], cwd=ROOT,
                              env=env, capture_output=True, text=True, encoding='utf-8')

    def package_command(self):
        return self.command('Package', '-VerificationRoot', str(self.verification),
                            '-Repository', REPO, '-RunId', '123', '-RunAttempt', '2', '-SourceCommit', SHA)

    def package_ok(self):
        result = self.package_command()
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads((self.package / 'candidate.json').read_text(encoding='utf-8-sig'))

    def test_package_and_verify_exact_inventory_with_isolated_launcher(self):
        receipt = self.package_ok()
        archive = self.package / receipt['archive']['name']
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(digest, receipt['archive']['sha256'])
        with zipfile.ZipFile(archive) as z:
            self.assertEqual({p.relative_to(self.windows).as_posix() for p in self.windows.rglob('*') if p.is_file()}
                             | {'START-HERE.md', 'Start-Playtest.cmd'}, set(z.namelist()))
            launcher = z.read('Start-Playtest.cmd').decode()
            self.assertIn('%LOCALAPPDATA%\\CozyTown-Godot\\Playtests\\' + SHA[:12], launcher)
            self.assertIn('"%~dp0CozyTown.exe" -- "--data-dir=', launcher)
            self.assertNotIn('--model-config', launcher)
        result = self.command('Verify', '-ExpectedSha256', digest)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotEqual(0, self.command('Verify', '-ExpectedSha256', '0' * 64).returncode)

    def test_invalid_verification_and_incomplete_export_rejected(self):
        for key, value in [('passed', False), ('passed', 'true'), ('sourceDirty', True), ('sourceCommit', '0' * 40)]:
            with self.subTest(key=key, value=value):
                saved = self.report[key]
                self.report[key] = value
                self.write_report()
                self.assertNotEqual(0, self.package_command().returncode)
                self.assertFalse(self.package.exists())
                self.report[key] = saved
        self.write_report()
        (self.windows / 'CozyTown.pck').unlink()
        self.assertNotEqual(0, self.package_command().returncode)

    def test_runtime_createdump_executables_ship_but_other_executables_do_not(self):
        # File locations observed in the 2026-10-18 Windows export, not a release candidate.
        for directory in ('data_CozyTown.Game_windows_x86_64', 'launcher'):
            (self.windows / directory / 'createdump.exe').write_bytes(b'runtime dump utility fixture')
        receipt = self.package_ok()
        with zipfile.ZipFile(self.package / receipt['archive']['name']) as archive:
            for directory in ('data_CozyTown.Game_windows_x86_64', 'launcher'):
                self.assertEqual(b'runtime dump utility fixture', archive.read(directory + '/createdump.exe'))
        result = self.command('Verify', '-ExpectedSha256', receipt['archive']['sha256'])
        self.assertEqual(0, result.returncode, result.stderr)
        shutil.rmtree(self.package)
        for directory in ('data_CozyTown.Game_windows_x86_64', 'launcher'):
            with self.subTest(directory=directory):
                unexpected = self.windows / directory / 'unexpected.exe'
                unexpected.write_bytes(b'not a runtime utility')
                result = self.package_command()
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Unexpected file', result.stderr)
                self.assertFalse(self.package.exists())
                unexpected.unlink()

    def test_duplicate_runs_extra_files_and_existing_output_rejected(self):
        other = self.verification / 'other'
        shutil.copytree(self.run, other)
        self.assertNotEqual(0, self.package_command().returncode)
        shutil.rmtree(other)
        (self.windows / 'save.json').write_text('{}')
        self.assertNotEqual(0, self.package_command().returncode)
        (self.windows / 'save.json').unlink()
        local = self.windows / 'launcher/launcher.local.json'
        local.write_text('{}')
        self.assertNotEqual(0, self.package_command().returncode)
        self.assertFalse(self.package.exists())
        local.unlink()
        self.package_ok()
        before = (self.package / 'candidate.json').read_bytes()
        self.assertNotEqual(0, self.package_command().returncode)
        self.assertEqual(before, (self.package / 'candidate.json').read_bytes())

    def test_verify_rejects_zip_traversal_even_with_matching_outer_digest(self):
        receipt = self.package_ok()
        archive = self.package / receipt['archive']['name']
        with zipfile.ZipFile(archive, 'a') as z:
            z.writestr('../escape', b'unsafe')
        receipt['archive']['sha256'] = hashlib.sha256(archive.read_bytes()).hexdigest()
        receipt['archive']['bytes'] = archive.stat().st_size
        (self.package / 'candidate.json').write_text(json.dumps(receipt))
        (self.package / 'SHA256SUMS.txt').write_text(receipt['archive']['sha256'] + '  ' + archive.name + '\n')
        self.assertNotEqual(0, self.command('Verify', '-ExpectedSha256', receipt['archive']['sha256']).returncode)

    def test_package_rejects_linked_export(self):
        target = self.root / 'external.dll'
        target.write_bytes(b'outside export')
        # Junctions work without Windows developer mode; no external data is read.
        linked = self.windows / 'data_CozyTown.Game_windows_x86_64' / 'linked'
        result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(linked), str(self.root)], capture_output=True)
        self.assertEqual(0, result.returncode)
        try:
            result = self.package_command()
            self.assertNotEqual(0, result.returncode)
            self.assertIn('links', result.stderr)
            self.assertFalse(self.package.exists())
        finally:
            linked.rmdir()

    def prepare_fixture(self):
        receipt = self.package_ok()
        server = self.root / 'server'
        server.mkdir()
        shutil.copytree(self.package, server / 'artifact')
        state = dict(run=dict(id=123, run_attempt=2, workflow_id=99, event='push', head_branch='main',
                              head_sha=SHA, head_repository=dict(full_name=REPO), status='completed', conclusion='success'),
                     artifact=dict(id=456, name='windows-candidate-123-2', expired=False,
                                   workflow_run=dict(id=123, head_sha=SHA)), releases=[], tags=[])
        (server / 'state.json').write_text(json.dumps(state))
        bindir = self.root / 'bin'
        bindir.mkdir()
        (bindir / 'gh.cmd').write_text(f'@"{sys.executable}" "{__file__}" --gh-fixture %*\n')
        self.package = self.root / 'download'
        env = dict(os.environ, PATH=str(bindir) + os.pathsep + os.environ['PATH'], DELIVERY_FIXTURE=str(server),
                   GITHUB_ACTIONS='true', GITHUB_REPOSITORY=REPO, GITHUB_EVENT_NAME='workflow_dispatch',
                   GITHUB_REF='refs/heads/main', GITHUB_WORKFLOW_REF=REPO + '/.github/workflows/windows-prerelease.yml@refs/heads/main')
        return server, state, env, receipt['archive']['sha256']

    def prepare(self, env, digest):
        return self.command('PrepareDraft', '-Repository', REPO, '-RunId', '123', '-Version', 'v0.1.0-alpha.1',
                            '-ExpectedSha256', digest, env=env)

    def test_prepare_draft_uploads_and_downloads_identical_candidate(self):
        server, state, env, digest = self.prepare_fixture()
        result = self.prepare(env, digest)
        self.assertEqual(0, result.returncode, result.stderr)
        request = json.loads((self.package / 'release-request.json').read_text(encoding='utf-8-sig'))
        self.assertIs(True, request['draft'])
        self.assertIs(True, request['prerelease'])
        self.assertEqual(SHA, json.loads((server / 'tag.json').read_text())['sha'])
        for path in (server / 'artifact').iterdir():
            self.assertEqual(path.read_bytes(), (server / 'assets' / path.name).read_bytes())

    def test_prepare_rejects_untrusted_metadata_before_remote_writes(self):
        server, original, env, digest = self.prepare_fixture()
        cases = [('run', 'event', 'pull_request'), ('run', 'head_branch', 'feature'),
                 ('run', 'head_repository', dict(full_name='fork/repo')), ('run', 'workflow_id', 100),
                 ('run', 'conclusion', 'failure'), ('run', 'run_attempt', 1), ('artifact', 'expired', True),
                 ('root', 'releases', [dict(tag_name='v0.1.0-alpha.1')]), ('root', 'tags', [dict(ref='refs/tags/v0.1.0-alpha.1')])]
        for section, key, value in cases:
            with self.subTest(section=section, key=key):
                state = json.loads(json.dumps(original))
                (state if section == 'root' else state[section])[key] = value
                (server / 'state.json').write_text(json.dumps(state))
                result = self.prepare(env, digest)
                self.assertNotEqual(0, result.returncode)
                self.assertRegex(result.stderr, 'successful main push|unexpired artifact|already exists|differs from its successful main run')
                self.assertFalse((server / 'tag.json').exists())
                if self.package.exists():
                    shutil.rmtree(self.package)
        (server / 'state.json').write_text(json.dumps(original))
        self.assertNotEqual(0, self.prepare(env, '0' * 64).returncode)
        self.assertFalse((server / 'tag.json').exists())

    def test_prepare_rejects_non_main_dispatch_and_duplicate_artifacts(self):
        server, state, env, digest = self.prepare_fixture()
        env['GITHUB_REF'] = 'refs/heads/feature'
        self.assertNotEqual(0, self.prepare(env, digest).returncode)
        self.assertFalse((server / 'tag.json').exists())

        env['GITHUB_REF'] = 'refs/heads/main'
        state['duplicate_artifact'] = True
        (server / 'state.json').write_text(json.dumps(state))
        result = self.prepare(env, digest)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('unexpired artifact', result.stderr)
        self.assertFalse((server / 'tag.json').exists())

    def test_draft_download_corruption_fails_without_deleting_partial_release(self):
        server, state, env, digest = self.prepare_fixture()
        state['corrupt_download'] = True
        (server / 'state.json').write_text(json.dumps(state))
        result = self.prepare(env, digest)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('differs from tested candidate bytes', result.stderr)
        self.assertTrue((server / 'tag.json').exists())
        self.assertTrue((server / 'assets/candidate.json').exists())


def gh_fixture(args):
    root = pathlib.Path(os.environ['DELIVERY_FIXTURE'])
    state = json.loads((root / 'state.json').read_text())
    if args[0] == 'api':
        endpoint = next(a for a in args if a.startswith('repos/'))
        if '--method' in args:
            payload = json.loads(pathlib.Path(args[args.index('--input') + 1]).read_text(encoding='utf-8-sig'))
            if endpoint.endswith('/git/refs'):
                (root / 'tag.json').write_text(json.dumps(payload))
                print(json.dumps(payload))
            else:
                assert payload['draft'] is True and payload['prerelease'] is True
                print(json.dumps(dict(id=789, draft=True, prerelease=True)))
        elif '/actions/workflows/' in endpoint:
            print(json.dumps(dict(id=99)))
        elif endpoint.endswith('/actions/runs/123'):
            print(json.dumps(state['run']))
        elif '/artifacts' in endpoint:
            print(json.dumps([dict(artifacts=[state['artifact']] * (2 if state.get('duplicate_artifact') else 1))]))
        elif '/git/matching-refs/' in endpoint:
            print(json.dumps(state['tags']))
        elif '/releases/789' in endpoint:
            print(json.dumps(dict(id=789, tag_name='v0.1.0-alpha.1', draft=True, prerelease=True)))
        elif '/releases?' in endpoint:
            print(json.dumps([state['releases']]))
        else:
            raise RuntimeError(args)
    elif args[:2] == ['run', 'download']:
        shutil.copytree(root / 'artifact', pathlib.Path(args[args.index('--dir') + 1]), dirs_exist_ok=True)
    elif args[:2] == ['release', 'upload']:
        assets = root / 'assets'
        assets.mkdir()
        for arg in args[5:]:
            shutil.copy2(arg, assets / pathlib.Path(arg).name)
    elif args[:2] == ['release', 'download']:
        destination = pathlib.Path(args[args.index('--dir') + 1])
        shutil.copytree(root / 'assets', destination, dirs_exist_ok=True)
        if state.get('corrupt_download'):
            (destination / 'candidate.json').write_bytes(b'corrupted')
    else:
        raise RuntimeError(args)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--gh-fixture':
        gh_fixture(sys.argv[2:])
    else:
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        unittest.main()
