using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using CozyTown.Replay;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownReplayParentContinuityExportTests
{
    [TestCase(false, "valid")]
    [TestCase(true, "valid")]
    [TestCase(false, "changed-host")]
    [TestCase(true, "changed-host")]
    [TestCase(false, "missing-saved-host")]
    [TestCase(true, "missing-saved-host")]
    [TestCase(false, "missing-initial-host")]
    [TestCase(true, "missing-initial-host")]
    [TestCase(false, "rewound-budget")]
    [TestCase(true, "rewound-budget")]
    [TestCase(false, "missing-budget")]
    [TestCase(true, "missing-budget")]
    public void ExportedSaveForkAndCliVerifyParentHostAndBudgetContinuity(bool fullDemo, string variant)
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-parent-continuity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string credential = Path.Combine(root, "credential-sentinel.json"), ledger = Path.Combine(root, "budget-sentinel.json");
            File.WriteAllText(credential, "synthetic credential sentinel"); File.WriteAllText(ledger, "synthetic budget sentinel");
            var content = Content();
            var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
            var world = new TownWorld(content, budgetId: profile.BudgetId); world.Advance(.001, 0);
            var host = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, host);
            var empty = new BudgetSnapshot(profile.BudgetId, "CNY", 5_000_000, 0, 0, 0, 0, null, []);
            journal.SetReplayBudget(empty, profile);
            host.RouteTo(new(160, 60)); host.AcceptElapsed(.2); host.TakeStep(); host.Move(.05, new(0, 0));
            var request = world.GetDecisionRequests()[0];
            string provider = TownJson.Serialize(new { messages = new[] { new { role = "user", content = request.InputJson } } });
            var frozen = new FrozenModelRequest(new(world.State.BranchId, world.SessionId, request.LogicalRequestId,
                request.ResidentId, request.ActivityRevision), ModelStage.Baseline, provider, false, TimeSpan.FromSeconds(60));
            host.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(frozen));
            host.Pause("focus", true);
            var stopped = empty with { StopReason = "authentication_failed" };
            journal.RecordReplayInput("budget_stop", new { reason = "authentication_failed", after = stopped });
            var saved = world.CaptureSnapshot();
            string savePath = Path.Combine(root, "save.json");
            TownSaveFile.Write(savePath, new(1, TownJson.Serialize(saved), world.State.BranchId, journal.RecordCount,
                host.State.UnconsumedSeconds, "real", "baseline", new() { [request.LogicalRequestId] = frozen },
                host.State.PlayerRoute, host.State.PlayerRouteCursor), candidate => TownWorld.RestoreCandidate(content, candidate.WorldJson));
            var source = TownSaveFile.Read(savePath);
            var child = TownWorld.RestoreCandidate(content, source.WorldJson);
            var childHost = new TownInputHost(content, child, new(source.UnconsumedSeconds, source.PlayerRoute, source.PlayerRouteCursor,
                ["loading", "menu"], source.ModelRequests.ToDictionary(pair => pair.Key, pair => TownJson.Serialize(pair.Value))));
            var parent = new TownReplayParent(journal.CaptureReplay(), saved, host.State);
            using var childJournal = new TownRunJournal(Path.Combine(root, "runs"), child.State.BranchId, new { schemaVersion = 2 });
            childJournal.StartReplay(content, child, childHost, parent); childJournal.SetReplayBudget(stopped, profile);
            childHost.Pause("loading", false); childHost.Pause("menu", false);
            var package = childJournal.CaptureReplay();
            Assert.That(TownReplay.Run(package).Passed, Is.True);
            string export = Path.Combine(root, "export.zip"); childJournal.ExportSnapshotZip(export);
            Assert.That(TownReplay.Run(TownReplay.ReadPackage(export)).Passed, Is.True);
            using var exportedOutput = new StringWriter();
            Assert.That(ReplayCommand.Run(["--input=" + export], exportedOutput), Is.Zero);

            package = variant switch
            {
                "changed-host" => package with { InitialHost = package.InitialHost! with { UnconsumedSeconds = .7 },
                    Parent = parent with { SavedHost = parent.SavedHost! with { UnconsumedSeconds = .7 } } },
                "missing-saved-host" => package with { Parent = parent with { SavedHost = null } },
                "missing-initial-host" => package with { InitialHost = null },
                "rewound-budget" => package with { InitialBudget = TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(empty)) },
                "missing-budget" => package with { InitialBudget = null },
                _ => package
            };
            string input = Path.Combine(root, "candidate.json"); File.WriteAllText(input, TownJson.Serialize(package));
            using var output = new StringWriter();
            int exit = ReplayCommand.Run(["--input=" + input], output);
            var report = TownJson.Deserialize<TownReplayReport>(output.ToString());
            Assert.That(exit, Is.EqualTo(variant == "valid" ? 0 : 1));
            Assert.That(report.Passed, Is.EqualTo(variant == "valid"));
            if (variant != "valid")
            {
                Assert.That(report.Field, Does.StartWith("parent."));
                Assert.That(report.Command, Is.EqualTo("budget_stop"));
                Assert.That(report.InputJson, Does.Contain("authentication_failed"));
            }
            Assert.That(File.ReadAllText(credential), Is.EqualTo("synthetic credential sentinel"));
            Assert.That(File.ReadAllText(ledger), Is.EqualTo("synthetic budget sentinel"));
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(root).StartsWith("cozytown-parent-continuity-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private static TownContent Content() => TownContent.Create(
        new(256, 256, [new("player.spawn", "Home", new(60, 60), "outside"), new("square", "Square", new(100, 60), "outside")],
            [], [new("outside", new(0, 0, 256, 256))]),
        new([new("mina", "Mina", "square", "square", "apple", new() { ["apple"] = 3 }, 5,
            [new("free", 0, 1440, "square", "free", true)])], [new("apple", "Apple")]));
}
