"""Exercise the CI release command at its verification-output boundary."""

import hashlib
import json
import os
import pathlib
import random
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
COMMAND = ROOT / "tools/windows/ci-release.ps1"
PWSH = shutil.which("pwsh")
COMMIT = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()


def draft_release_fixture(arguments):
    """Model GitHub's published-only by-tag API and ID-addressed drafts."""
    root = pathlib.Path(os.environ["COZYTOWN_RELEASE_FIXTURE_ROOT"])
    release = {"id": 410408646, "draft": True, "author": {"login": "github-actions[bot]"}, "assets": []}
    if arguments[:2] == ["api", "repos/qinjunw/CozyTown-Godot"]:
        print(json.dumps({"private": True}))
    elif arguments[:3] == ["api", "--method", "POST"] and arguments[3].endswith("/git/refs"):
        print(json.dumps({"ref": "refs/tags/ci-windows-12345-2"}))
    elif arguments[:2] == ["release", "create"]:
        print("https://github.com/qinjunw/CozyTown-Godot/releases/tag/untagged-fixture")
    elif arguments[:3] == ["api", "--method", "POST"] and arguments[3].endswith("/releases"):
        payload = json.loads(pathlib.Path(arguments[arguments.index("--input") + 1]).read_text(encoding="utf-8-sig"))
        if payload["draft"] is not True or payload["tag_name"] != "ci-windows-12345-2" or "target_commitish" in payload:
            raise RuntimeError("Draft creation must use its existing unique tag.")
        print(json.dumps(release))
    elif arguments[:2] == ["release", "upload"]:
        for source in arguments[5:]:
            shutil.copy2(source, root / pathlib.Path(source).name)
    elif arguments[0] == "api" and "/releases/tags/" in arguments[1]:
        print("gh: Not Found (HTTP 404)", file=sys.stderr)
        return 1
    elif arguments[:2] == ["api", "repos/qinjunw/CozyTown-Godot/releases/410408646"]:
        for asset in sorted(root.iterdir()):
            if asset.name == "event.json":
                continue
            contents = asset.read_bytes()
            release["assets"].append({
                "name": asset.name, "size": len(contents), "digest": "sha256:" + hashlib.sha256(contents).hexdigest(),
            })
        print(json.dumps(release))
    elif arguments[:2] == ["release", "download"]:
        destination = pathlib.Path(arguments[arguments.index("--dir") + 1])
        for asset in root.iterdir():
            if asset.name != "event.json":
                shutil.copy2(asset, destination / asset.name)
    else:
        raise RuntimeError(f"Unexpected offline GitHub command: {arguments}")
    return 0


class ReleaseCommandTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.verification = self.root / "verification"
        self.run = self.verification / "fixed-run"
        self.run.mkdir(parents=True)
        self.package = self.root / "package"

    def write(self, name, content):
        path = self.run / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def report(self, passed=True, commit=COMMIT):
        self.write("verification.json", json.dumps({
            "passed": passed, "sourceCommit": commit, "sourceDirty": False,
        }).encode())

    def command(self, outcome="success", *extra):
        return subprocess.run([
            PWSH, "-NoProfile", "-File", str(COMMAND), "-Mode", "Package",
            "-VerificationRoot", str(self.verification), "-PackageRoot", str(self.package),
            "-Repository", "qinjunw/CozyTown-Godot", "-RunId", "12345", "-RunAttempt", "2",
            "-HeadSha", COMMIT, "-Event", "pull_request", "-Outcome", outcome, *extra,
        ], cwd=ROOT, text=True, encoding="utf-8", capture_output=True)

    def test_success_collects_complete_windows_and_top_level_reports_only(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        self.write("windows/data/runtime.dll", b"fixed runtime")
        self.write("core.trx", b"fixed test report")
        self.write("process.stderr.log", b"fixed error stream")
        self.write("scene.json", b"{}")
        self.write("data/private-prefix.json", b"large replay prefix")
        result = self.command()
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((self.package / "manifest.json").read_text(encoding="utf-8-sig"))
        expected = {
            "fixed-run/verification.json", "fixed-run/windows/CozyTown.exe",
            "fixed-run/windows/data/runtime.dll", "fixed-run/core.trx",
            "fixed-run/process.stderr.log", "fixed-run/scene.json",
        }
        with zipfile.ZipFile(self.package / "windows-toolchain.zip") as archive:
            self.assertEqual(expected, set(archive.namelist()))
            self.assertIsNone(archive.testzip())
        self.assertEqual(expected, {item["path"] for item in manifest["files"]})
        self.assertEqual(COMMIT, manifest["sourceCommit"])
        self.assertEqual("ci-windows-12345-2", manifest["releaseTag"])
        self.assertEqual(manifest["sourceTreeSha"], manifest["headTreeSha"])
        asset = manifest["assets"][0]
        contents = (self.package / asset["name"]).read_bytes()
        self.assertEqual(len(contents), asset["bytes"])
        self.assertEqual(hashlib.sha256(contents).hexdigest(), asset["sha256"])

    def test_failure_keeps_nested_diagnostics_without_a_completed_report(self):
        self.write("restore.stdout.log", b"restore failed")
        self.write("data/replay/branch.json", b"partial replay")
        self.write("dotnet-export-logs/nested/build.txt", b"compiler diagnostic")
        result = self.command("failure")
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((self.package / "manifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("failure", manifest["outcome"])
        self.assertIsNone(manifest["verificationRuns"][0]["report"])
        with zipfile.ZipFile(self.package / "windows-toolchain-failure.zip") as archive:
            self.assertEqual({
                "fixed-run/restore.stdout.log", "fixed-run/data/replay/branch.json",
                "fixed-run/dotnet-export-logs/nested/build.txt",
            }, set(archive.namelist()))
            self.assertIsNone(archive.testzip())

    def test_large_diagnostics_split_and_reconstruct_without_losing_bytes(self):
        payload = random.Random(123).randbytes(9000)
        self.write("data/large.bin", payload)
        result = self.command("failure", "-MaximumAssetBytes", "1024")
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((self.package / "manifest.json").read_text(encoding="utf-8-sig"))
        self.assertGreater(len(manifest["assets"]), 1)
        reconstructed = self.root / "reconstructed.zip"
        with reconstructed.open("wb") as output:
            for asset in manifest["assets"]:
                part = (self.package / asset["name"]).read_bytes()
                self.assertLessEqual(len(part), 1024)
                self.assertEqual(hashlib.sha256(part).hexdigest(), asset["sha256"])
                output.write(part)
        self.assertEqual(manifest["archive"]["sha256"], hashlib.sha256(reconstructed.read_bytes()).hexdigest())
        with zipfile.ZipFile(reconstructed) as archive:
            self.assertEqual(payload, archive.read("fixed-run/data/large.bin"))
            self.assertIsNone(archive.testzip())

    def test_download_verification_rejects_a_corrupted_asset(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        self.assertEqual(0, self.command().returncode)
        command = [PWSH, "-NoProfile", "-File", str(COMMAND), "-Mode", "Verify", "-PackageRoot", str(self.package)]
        valid = subprocess.run(command, cwd=ROOT, capture_output=True, encoding="utf-8")
        self.assertEqual(0, valid.returncode, valid.stderr)
        with (self.package / "windows-toolchain.zip").open("ab") as output:
            output.write(b"corrupted download")
        invalid = subprocess.run(command, cwd=ROOT, capture_output=True, encoding="utf-8")
        self.assertNotEqual(0, invalid.returncode)

    def test_failure_preserves_diagnostics_at_the_verification_root(self):
        (self.verification / "early-error.log").write_bytes(b"early failure")
        self.write("nested.log", b"run failure")
        result = self.command("failure")
        self.assertEqual(0, result.returncode, result.stderr)
        with zipfile.ZipFile(self.package / "windows-toolchain-failure.zip") as archive:
            self.assertEqual({"early-error.log", "fixed-run/nested.log"}, set(archive.namelist()))

    def test_invalid_asset_limit_is_rejected_before_creating_a_package(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        result = self.command("success", "-MaximumAssetBytes", "2147483648")
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.package.exists())

    def test_success_rejects_a_report_from_another_source(self):
        self.report(commit="0" * 40)
        self.write("windows/CozyTown.exe", b"fixed executable")
        result = self.command()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.package.exists())

    def test_success_rejects_a_missing_windows_export(self):
        self.report()
        self.assertNotEqual(0, self.command().returncode)
        self.assertFalse(self.package.exists())

    def test_existing_package_is_not_overwritten(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        self.assertEqual(0, self.command().returncode)
        original = (self.package / "manifest.json").read_bytes()
        self.assertNotEqual(0, self.command().returncode)
        self.assertEqual(original, (self.package / "manifest.json").read_bytes())

    def test_publish_requires_the_original_actions_context(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        self.assertEqual(0, self.command().returncode)
        result = subprocess.run([
            PWSH, "-NoProfile", "-File", str(COMMAND), "-Mode", "Publish", "-PackageRoot", str(self.package),
        ], cwd=ROOT, capture_output=True, encoding="utf-8")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Publishing requires", result.stderr)

    def test_failure_keeps_an_incomplete_verification_report(self):
        self.write("verification.json", b"{}")
        result = self.command("failure")
        self.assertEqual(0, result.returncode, result.stderr)
        with zipfile.ZipFile(self.package / "windows-toolchain-failure.zip") as archive:
            self.assertEqual(b"{}", archive.read("fixed-run/verification.json"))

    def test_private_draft_publishes_and_downloads_when_by_tag_api_returns_404(self):
        self.report()
        self.write("windows/CozyTown.exe", b"fixed executable")
        self.assertEqual(0, self.command().returncode)
        server = self.root / "server"
        server.mkdir()
        event = server / "event.json"
        event.write_text(json.dumps({"pull_request": {"head": {
            "repo": {"full_name": "qinjunw/CozyTown-Godot"}, "sha": COMMIT,
        }}}), encoding="utf-8")
        executable = self.root / "fixture-bin"
        executable.mkdir()
        (executable / "gh.cmd").write_text(
            f'@"{sys.executable}" "{pathlib.Path(__file__).resolve()}" --gh-fixture %*\n', encoding="utf-8",
        )
        environment = dict(os.environ, PATH=str(executable) + os.pathsep + os.environ["PATH"],
                           COZYTOWN_RELEASE_FIXTURE_ROOT=str(server), GITHUB_ACTIONS="true",
                           GITHUB_REPOSITORY="qinjunw/CozyTown-Godot", GITHUB_RUN_ID="12345",
                           GITHUB_RUN_ATTEMPT="2", GITHUB_EVENT_NAME="pull_request", GITHUB_SHA=COMMIT,
                           GITHUB_EVENT_PATH=str(event), GH_TOKEN="offline-fixture-token")
        result = subprocess.run([
            PWSH, "-NoProfile", "-File", str(COMMAND), "-Mode", "Publish", "-PackageRoot", str(self.package),
        ], cwd=ROOT, env=environment, capture_output=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual((self.package / "manifest.json").read_bytes(),
                         (self.package / "downloaded/manifest.json").read_bytes())
        with zipfile.ZipFile(self.package / "downloaded/windows-toolchain.zip") as archive:
            self.assertEqual(b"fixed executable", archive.read("fixed-run/windows/CozyTown.exe"))
            self.assertIsNone(archive.testzip())


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--gh-fixture":
        sys.exit(draft_release_fixture(sys.argv[2:]))
    unittest.main()
