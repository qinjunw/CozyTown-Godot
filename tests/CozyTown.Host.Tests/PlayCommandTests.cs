extern alias PlayTool;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using CozyTown.Core.Town;
using CozyTown.Model;
using PlayCommand = PlayTool::CozyTown.Play.PlayCommand;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

public sealed class PlayCommandTests
{
    [TestCase("full-demo-v2", "cozytown-godot-full-demo-20261011-v2", 8)]
    [TestCase("manual-play-v1", "cozytown-godot-manual-play-20261017-v1", 96)]
    public void ManualCheckUsesRegisteredProfileAndPreservesSyntheticLedgerWithoutStartingGame(string profile, string budgetId, int manualLimit)
    {
        using var fixture = new LaunchFixture(profile);
        byte[] before = File.ReadAllBytes(fixture.Ledger);
        var result = fixture.Check("--real");
        Assert.That(result.Exit, Is.Zero, result.Output + result.Error);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Multiple(() =>
        {
            Assert.That(report.RootElement.GetProperty("passed").GetBoolean(), Is.True);
            Assert.That(report.RootElement.GetProperty("packageFiles").GetInt32(), Is.EqualTo(2));
            Assert.That(report.RootElement.GetProperty("gameStarted").GetBoolean(), Is.False);
            Assert.That(report.RootElement.GetProperty("budget").GetProperty("manualUsed").GetInt32(), Is.Zero);
            Assert.That(report.RootElement.GetProperty("budget").GetProperty("profile").GetString(), Is.EqualTo(profile));
            Assert.That(report.RootElement.GetProperty("budget").GetProperty("budgetId").GetString(), Is.EqualTo(budgetId));
            Assert.That(report.RootElement.GetProperty("budget").GetProperty("totalLimit").GetInt32(), Is.EqualTo(96));
            Assert.That(report.RootElement.GetProperty("budget").GetProperty("manualLimit").GetInt32(), Is.EqualTo(manualLimit));
            Assert.That(report.RootElement.GetProperty("commandArguments").EnumerateArray().Select(a => a.GetString()),
                Is.EqualTo(new[] { "--", "--model-config=<local-config>", "--execution-purpose=manual", "--data-dir=<separate-data>" }));
            Assert.That(result.Output + result.Error, Does.Not.Contain(fixture.Root).And.Not.Contain(LaunchFixture.Key));
            Assert.That(File.ReadAllBytes(fixture.Ledger), Is.EqualTo(before));
            Assert.That(Directory.Exists(Path.Combine(fixture.Root, "real-data")), Is.False);
        });
    }

    [TestCase("full-demo-v2")]
    [TestCase("manual-play-v1")]
    public void OfflineCheckDoesNotReadModelConfigurationCredentialsOrLedger(string profile)
    {
        using var fixture = new LaunchFixture(profile);
        File.Delete(Path.Combine(fixture.Root, "model.json"));
        File.Delete(Path.Combine(fixture.Root, "credential.json"));
        File.Delete(fixture.Ledger);
        File.Delete(fixture.Ledger + ".lock");
        var result = fixture.Check("--offline");
        Assert.That(result.Exit, Is.Zero, result.Output + result.Error);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Multiple(() =>
        {
            Assert.That(report.RootElement.GetProperty("budget").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(report.RootElement.GetProperty("commandArguments").EnumerateArray().Select(a => a.GetString()),
                Is.EqualTo(new[] { "--", "--data-dir=<separate-data>" }));
            Assert.That(Directory.Exists(Path.Combine(fixture.Root, "offline-data")), Is.False);
        });
    }

    [TestCase("full-demo-v2")]
    [TestCase("manual-play-v1")]
    public void PublishedCommandBoundaryEmitsRedactedCheckReportWithoutStartingSyntheticExecutable(string profile)
    {
        using var fixture = new LaunchFixture(profile);
        string dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(PlayCommand).Assembly.Location, "--real", "--check-only", "--settings=" + fixture.Settings })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        Assert.That(process.WaitForExit(30_000), Is.True);
        Assert.That(process.ExitCode, Is.Zero, output + error);
        using var report = JsonDocument.Parse(output);
        Assert.That(report.RootElement.GetProperty("gameStarted").GetBoolean(), Is.False);
        Assert.That(output + error, Does.Not.Contain(fixture.Root).And.Not.Contain(LaunchFixture.Key));
    }

    [Test]
    public void StoppedLedgerReportsConcreteCauseAndUnknownReservationWithoutChangingIt()
    {
        using var fixture = new LaunchFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.Ledger, ModelBudgetProfile.FullDemoV2Version);
        var attempt = ledger.Reserve(ModelStage.Manual, "synthetic-request");
        ledger.MarkUnknown(attempt.AttemptId, "fingerprint_not_registered", true, true);
        byte[] before = File.ReadAllBytes(fixture.Ledger);
        var result = fixture.Check("--real");
        Assert.That(result.Exit, Is.EqualTo(2));
        using var report = JsonDocument.Parse(result.Output);
        var budget = report.RootElement.GetProperty("budget");
        Assert.Multiple(() =>
        {
            Assert.That(report.RootElement.GetProperty("code").GetString(), Is.EqualTo("ledger_stopped"));
            Assert.That(budget.GetProperty("stopReason").GetString(), Is.EqualTo("fingerprint_not_registered"));
            Assert.That(budget.GetProperty("reservedMicros").GetInt64(), Is.EqualTo(2_110_000));
            Assert.That(budget.GetProperty("settledMicros").GetInt64(), Is.Zero);
            Assert.That(budget.GetProperty("slotsHeld").GetInt32(), Is.Zero);
            Assert.That(File.ReadAllBytes(fixture.Ledger), Is.EqualTo(before));
            Assert.That(result.Error, Does.Contain("停止派发").And.Not.Contain("次数已到上限"));
        });
    }

    [TestCase("exhausted", "stage_limit")]
    [TestCase("slots", "concurrency_limit")]
    [TestCase("money", "money_limit")]
    public void AdmissionFailuresRemainDistinctAndDoNotReserveAnotherRequest(string setup, string expected)
    {
        using var fixture = new LaunchFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.Ledger, ModelBudgetProfile.FullDemoV2Version);
        int count = setup == "exhausted" ? 8 : 2;
        for (int i = 0; i < count; i++)
        {
            var attempt = ledger.Reserve(ModelStage.Manual, "synthetic-" + i);
            if (setup == "exhausted") ledger.Settle(attempt.AttemptId, 10, 10);
            else if (setup == "money") ledger.MarkUnknown(attempt.AttemptId, "synthetic_timeout", true);
        }
        byte[] before = File.ReadAllBytes(fixture.Ledger);
        var result = fixture.Check("--real");
        Assert.That(result.Exit, Is.EqualTo(2));
        using var report = JsonDocument.Parse(result.Output);
        Assert.That(report.RootElement.GetProperty("code").GetString(), Is.EqualTo(expected));
        Assert.That(File.ReadAllBytes(fixture.Ledger), Is.EqualTo(before));
    }

    [Test]
    public void SameModeInstanceRefusesStartBeforeCreatingDataOrExecutingGame()
    {
        using var fixture = new LaunchFixture();
        using var mutex = new Mutex(true, "Local\\CozyTown.Godot.Play.real");
        try
        {
            var start = FixtureCommand(fixture.Settings);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            Assert.That(process.WaitForExit(30_000), Is.True);
            Assert.That(process.ExitCode, Is.EqualTo(2));
            using var report = JsonDocument.Parse(output);
            Assert.That(report.RootElement.GetProperty("code").GetString(), Is.EqualTo("launcher_already_running"));
            Assert.That(Directory.Exists(Path.Combine(fixture.Root, "real-data")), Is.False);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static ProcessStartInfo FixtureCommand(string settings)
    {
        string dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(PlayCommand).Assembly.Location, "--real", "--settings=" + settings })
            start.ArgumentList.Add(argument);
        return start;
    }

    [TestCase("same-data", "data_directories_overlap")]
    [TestCase("nested-data", "data_directories_overlap")]
    [TestCase("package-data", "data_directories_overlap")]
    [TestCase("drive-root-data", "data_directories_overlap")]
    [TestCase("changed-file", "package_file_missing_or_changed")]
    [TestCase("missing-file", "package_file_missing_or_changed")]
    [TestCase("extra-file", "package_manifest_incomplete")]
    [TestCase("changed-manifest", "package_identity_invalid")]
    [TestCase("wrong-source", "package_identity_invalid")]
    [TestCase("invalid-json", "launcher_json_invalid")]
    [TestCase("invalid-credential", "credential_match_count_invalid")]
    [TestCase("missing-ledger", "ledger_missing")]
    [TestCase("purpose", "execution_purpose_invalid")]
    [TestCase("duplicate-property", "launcher_json_invalid")]
    [TestCase("invalid-ledger", "ledger_invalid")]
    [TestCase("invalid-profile", "budget_profile_not_registered")]
    public void InvalidLocalInputFailsWithoutPrivatePathsOrSecretValues(string setup, string expected)
    {
        using var fixture = new LaunchFixture();
        switch (setup)
        {
            case "same-data": fixture.WriteSettings("data", "data"); break;
            case "nested-data": fixture.WriteSettings("data", "data/nested"); break;
            case "package-data": fixture.WriteSettings("package/data"); break;
            case "drive-root-data": fixture.WriteSettings(Path.GetPathRoot(fixture.Root)!); break;
            case "changed-file": File.AppendAllText(Path.Combine(fixture.Root, "package", "CozyTown.exe"), "changed"); break;
            case "missing-file": File.Delete(Path.Combine(fixture.Root, "package", "CozyTown.pck")); break;
            case "extra-file": File.WriteAllText(Path.Combine(fixture.Root, "package", "extra.dll"), "unlisted"); break;
            case "changed-manifest": File.AppendAllText(Path.Combine(fixture.Root, "manifest.json"), " "); break;
            case "wrong-source": File.WriteAllText(fixture.Settings, File.ReadAllText(fixture.Settings).Replace(new string('a', 40), new string('b', 40))); break;
            case "invalid-json": File.WriteAllText(fixture.Settings, "{" + LaunchFixture.Key); break;
            case "invalid-credential": File.WriteAllText(Path.Combine(fixture.Root, "credential.json"), "{}"); break;
            case "missing-ledger": File.Delete(fixture.Ledger); break;
            case "purpose": File.WriteAllText(fixture.Settings, File.ReadAllText(fixture.Settings).Replace("manual", "experiment")); break;
            case "duplicate-property": File.WriteAllText(fixture.Settings, File.ReadAllText(fixture.Settings).Replace("{", "{\"executionPurpose\":\"manual\",")); break;
            case "invalid-ledger": File.WriteAllText(fixture.Ledger, "invalid"); break;
            case "invalid-profile": File.WriteAllText(Path.Combine(fixture.Root, "model.json"), File.ReadAllText(Path.Combine(fixture.Root, "model.json")).Replace("full-demo-v2", "unregistered")); break;
        }
        var result = fixture.Check("--real");
        Assert.That(result.Exit, Is.EqualTo(2), result.Output);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Multiple(() =>
        {
            Assert.That(report.RootElement.GetProperty("code").GetString(), Is.EqualTo(expected));
            Assert.That(result.Output + result.Error, Does.Not.Contain(fixture.Root).And.Not.Contain(LaunchFixture.Key));
            Assert.That(Directory.Exists(Path.Combine(fixture.Root, "real-data")), Is.False);
        });
    }

    private sealed class LaunchFixture : IDisposable
    {
        public const string Key = "synthetic-secret-never-output";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cozytown-play-" + Guid.NewGuid().ToString("N"));
        public string Ledger => Path.Combine(Root, "ledger.json");
        public string Settings => Path.Combine(Root, "launcher.json");
        public LaunchFixture(string profileVersion = ModelBudgetProfile.FullDemoV2Version)
        {
            Directory.CreateDirectory(Path.Combine(Root, "package"));
            File.WriteAllText(Path.Combine(Root, "package", "CozyTown.exe"), "synthetic executable; must never run");
            File.WriteAllText(Path.Combine(Root, "package", "CozyTown.pck"), "synthetic content");
            File.WriteAllText(Path.Combine(Root, "credential.json"), JsonSerializer.Serialize(new
                { fixture = new { Todo = "CozyTown", testingAPIKey = Key } }));
            PersistentBudgetLedger.InitializeNew(Ledger, profileVersion);
            File.WriteAllText(Path.Combine(Root, "model.json"), TownJson.Serialize(new
            {
                credentialFile = Path.Combine(Root, "credential.json"), ledgerFile = Ledger,
                expectedSystemFingerprint = "another-registered-fingerprint", budgetProfileVersion = profileVersion
            }));
            File.WriteAllText(Path.Combine(Root, "manifest.json"), TownJson.Serialize(new
            {
                sourceCommit = new string('a', 40), files = new[] { "CozyTown.exe", "CozyTown.pck" }.Select(name =>
                {
                    string path = Path.Combine(Root, "package", name);
                    return new { path = name, bytes = new FileInfo(path).Length,
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
                }).ToArray()
            }));
            WriteSettings();
        }
        public void WriteSettings(string realData = "real-data", string offlineData = "offline-data") =>
            File.WriteAllText(Settings, TownJson.Serialize(new
            {
                gameExecutable = "package/CozyTown.exe", modelConfig = "model.json", executionPurpose = "manual",
                realDataDirectory = realData, offlineDataDirectory = offlineData, packageManifest = "manifest.json",
                expectedSourceCommit = new string('a', 40),
                packageManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, "manifest.json"))))
            }));
        public (int Exit, string Output, string Error) Check(string mode)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = PlayCommand.Run([mode, "--check-only", "--settings=" + Settings], output, error);
            return (exit, output.ToString(), error.ToString());
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
