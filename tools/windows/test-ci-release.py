"""Exercise the CI release command at its verification-output boundary."""

import hashlib
import json
import pathlib
import random
import shutil
import subprocess
import tempfile
import unittest
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
COMMAND = ROOT / "tools/windows/ci-release.ps1"
PWSH = shutil.which("pwsh")
COMMIT = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()


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


if __name__ == "__main__":
    unittest.main()
