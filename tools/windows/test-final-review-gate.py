"""Exercise the final review preflight through its public PowerShell command."""

import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
COMMAND = ROOT / "tools/windows/verify-full-demo.ps1"
PWSH = shutil.which("pwsh")


class FinalReviewGateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.repo = self.root / "repo"
        self.script = self.repo / "tools/windows/verify-full-demo.ps1"
        self.script.parent.mkdir(parents=True)
        shutil.copy2(COMMAND, self.script)
        self.git("init", "--quiet")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "user.name", "Offline fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Fixture baseline")
        self.base = self.git("rev-parse", "HEAD")
        (self.repo / "source.txt").write_text("reviewed source\n", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Fixture reviewed change")
        self.commit = self.git("rev-parse", "HEAD")
        self.tree = self.git("rev-parse", "HEAD^{tree}")
        self.evidence = self.root / "evidence"
        self.evidence.mkdir()
        self.receipt_path = self.evidence / "review-receipt.json"
        self.receipt = {
            "schemaVersion": 1, "sourceCommit": self.commit, "sourceTreeSha": self.tree,
            "baseCommit": self.base, "completedAtUtc": "2026-01-01T00:00:00Z", "reviews": [],
        }
        for axis in ("standards", "spec"):
            report = self.evidence / f"{axis}.md"
            report.write_text(f"# Offline {axis} fixture\nNo blocking findings.\n", encoding="utf-8")
            self.receipt["reviews"].append({
                "axis": axis, "status": "completed", "blockingFindings": 0,
                "reviewerId": f"offline-{axis}-reviewer", "reportPath": report.name,
                "reportSha256": hashlib.sha256(report.read_bytes()).hexdigest(),
            })

    def git(self, *arguments):
        return subprocess.check_output(["git", *arguments], cwd=self.repo, text=True).strip()

    def command(self, *, preflight=True, receipt=True):
        self.receipt_path.write_text(json.dumps(self.receipt), encoding="utf-8")
        arguments = [PWSH, "-NoProfile", "-NonInteractive", "-File", str(self.script),
                     "-ExpectedCommit", self.commit]
        if receipt:
            arguments += ["-ReviewReceipt", str(self.receipt_path)]
        if preflight:
            arguments.append("-ReviewPreflightOnly")
        return subprocess.run(arguments, cwd=self.repo, capture_output=True, encoding="utf-8", timeout=20)

    def test_completed_reviews_pass_without_toolchain_or_game_inputs(self):
        result = self.command()
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        self.assertTrue(report["passed"])
        self.assertEqual("review_preflight", report["mode"])
        self.assertEqual(self.commit, report["sourceCommit"])
        self.assertEqual(self.tree, report["sourceTreeSha"])
        self.assertFalse((self.repo / "artifacts").exists())

    def test_both_independent_axes_must_be_completed_without_blocking_findings(self):
        original = json.dumps(self.receipt)
        changes = {
            "missing axis": lambda: self.receipt["reviews"].pop(),
            "duplicate axis": lambda: self.receipt["reviews"][1].update(axis="standards"),
            "pending": lambda: self.receipt["reviews"][1].update(status="pending"),
            "blocking": lambda: self.receipt["reviews"][0].update(blockingFindings=1),
            "text count": lambda: self.receipt["reviews"][0].update(blockingFindings="0"),
            "same reviewer": lambda: self.receipt["reviews"][1].update(reviewerId="offline-standards-reviewer"),
            "blank reviewer": lambda: self.receipt["reviews"][0].update(reviewerId=" "),
            "wrong schema": lambda: self.receipt.update(schemaVersion=2),
            "missing completion": lambda: self.receipt.update(completedAtUtc=""),
        }
        for label, change in changes.items():
            with self.subTest(label=label):
                self.receipt = json.loads(original)
                change()
                result = self.command(preflight=False)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Independent review receipt", result.stderr)
                self.assertFalse((self.repo / "artifacts").exists())

    def test_receipt_must_match_commit_tree_and_changed_ancestor_base(self):
        original = json.dumps(self.receipt)
        changes = {
            "other source": {"sourceCommit": self.base},
            "other tree": {"sourceTreeSha": "0" * 40},
            "empty diff": {"baseCommit": self.commit},
            "missing base": {"baseCommit": "0" * 40},
        }
        for label, change in changes.items():
            with self.subTest(label=label):
                self.receipt = json.loads(original)
                self.receipt.update(change)
                result = self.command()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Independent review receipt", result.stderr)
                self.assertFalse((self.repo / "artifacts").exists())

    def test_review_reports_must_exist_be_nonempty_and_match_their_hashes(self):
        report = self.evidence / "standards.md"
        original = report.read_bytes()
        for label, contents in (("changed", b"changed report"), ("empty", b""), ("whitespace", b" \n\t")):
            with self.subTest(label=label):
                report.write_bytes(contents)
                if label != "changed":
                    self.receipt["reviews"][0]["reportSha256"] = hashlib.sha256(contents).hexdigest()
                result = self.command()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Independent review report", result.stderr)
                self.assertFalse((self.repo / "artifacts").exists())
                report.write_bytes(original)
        report.unlink()
        result = self.command()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Independent review report", result.stderr)

    def test_normal_final_command_requires_receipt_before_looking_for_tools(self):
        missing = self.command(preflight=False, receipt=False)
        self.assertNotEqual(0, missing.returncode)
        self.assertIn("ReviewReceipt is required", missing.stderr)
        accepted = self.command(preflight=False)
        self.assertNotEqual(0, accepted.returncode)
        self.assertIn("Missing verification input", accepted.stderr)
        self.assertFalse((self.repo / "artifacts").exists())

    def test_preflight_preserves_the_frozen_source_checks(self):
        source = self.repo / "source.txt"
        source.write_text("unreviewed source\n", encoding="utf-8")
        dirty = self.command()
        self.assertNotEqual(0, dirty.returncode)
        self.assertIn("Candidate has uncommitted files", dirty.stderr)
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Fixture source changed after review")
        moved = self.command()
        self.assertNotEqual(0, moved.returncode)
        self.assertIn("Git HEAD differs from ExpectedCommit", moved.stderr)
        self.assertFalse((self.repo / "artifacts").exists())


if __name__ == "__main__":
    unittest.main()
