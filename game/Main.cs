using System.Globalization;
using System.Text.Json;
using CozyTown.Core;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class Main : Control
{
    private ToolchainProbe _probe = new();
    private Label _tickValue = null!;
    private Label _status = null!;
    private Label _snapshot = null!;
    private Button _advanceButton = null!;
    private Button _saveReloadButton = null!;
    private string _snapshotPath = string.Empty;
    private string? _lastSnapshotJson;
    private Exception? _lastActionError;
    private bool _restoredNewInstance;

    public override void _Ready() => _ = InitializeAsync();

    private async Task InitializeAsync()
    {
        string[] args = OS.GetCmdlineUserArgs();
        bool smoke = args.Contains("--toolchain-smoke", StringComparer.Ordinal);
        string? reportPath = ArgumentValue(args, "--report=");
        string? screenshotPath = ArgumentValue(args, "--screenshot=");
        var report = new SmokeReport
        {
            EngineVersion = Engine.GetVersionInfo()["string"].AsString(),
            RuntimeVersion = System.Environment.Version.ToString(),
            RuntimeAssemblyLocation = typeof(object).Assembly.Location,
            DisplayServer = DisplayServer.GetName(),
            ScreenshotPath = screenshotPath
        };
        report.Headless = report.DisplayServer == "headless";

        try
        {
            if (smoke)
            {
                RequireAbsolutePath(reportPath, "--report");
                if (screenshotPath != null)
                {
                    RequireAbsolutePath(screenshotPath, "--screenshot");
                    if (string.Equals(reportPath, screenshotPath, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Report and screenshot paths must differ.");
                }
            }

            _snapshotPath = smoke
                ? reportPath + ".snapshot.json"
                : ProjectSettings.GlobalizePath("user://toolchain-probe.json");
            _tickValue = GetNode<Label>("%TickValue");
            _status = GetNode<Label>("%Status");
            _snapshot = GetNode<Label>("%Snapshot");
            _advanceButton = GetNode<Button>("%AdvanceButton");
            _saveReloadButton = GetNode<Button>("%SaveReloadButton");
            _advanceButton.Pressed += AdvanceFromButton;
            _saveReloadButton.Pressed += SaveReloadFromButton;
            GetNode<Label>("%Runtime").Text =
                $"Godot {report.EngineVersion}  |  .NET {report.RuntimeVersion}  |  {report.DisplayServer}";
            RefreshCounter();
            _advanceButton.GrabFocus();
            if (!smoke) return;

            report.SnapshotPath = _snapshotPath;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            report.InitialTicks = _probe.TotalTicks;
            Require(report.InitialTicks == 0, "The core must start at zero ticks.");

            _advanceButton.EmitSignal(Button.SignalName.Pressed);
            Require(_lastActionError == null, "Advance button failed.", _lastActionError);
            report.AfterAdvanceTicks = _probe.TotalTicks;
            Require(report.AfterAdvanceTicks == 120, "Advance button must produce 120 ticks.");
            report.AdvanceButtonVerified = true;

            _saveReloadButton.EmitSignal(Button.SignalName.Pressed);
            Require(_lastActionError == null, "Save/reload button failed.", _lastActionError);
            report.AfterReloadTicks = _probe.TotalTicks;
            Require(_restoredNewInstance && report.AfterReloadTicks == 120,
                "Save/reload must restore 120 ticks into a new core instance.");
            report.SaveReloadButtonVerified = true;
            report.SnapshotJson = _lastSnapshotJson;

            _probe.Advance(1);
            report.FinalTicks = _probe.TotalTicks;
            Require(report.FinalTicks == 121, "The restored core must continue to tick 121.");
            RefreshCounter();
            _status.Text = "Smoke passed: advance, disk snapshot, reload, then advance again.";

            if (screenshotPath == null)
            {
                report.ScreenshotStatus = "not_requested";
            }
            else if (report.Headless)
            {
                report.ScreenshotStatus = "skipped_headless";
            }
            else
            {
                report.ScreenshotStatus = "failed";
                for (int frame = 0; frame < 3; frame++)
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                EnsureParentDirectory(screenshotPath);
                using Image image = GetViewport().GetTexture().GetImage();
                Require(!image.IsEmpty(), "The rendered viewport image is empty.");
                Error imageError = image.SavePng(screenshotPath);
                Require(imageError == Error.Ok, $"Screenshot save failed: {imageError}.");
                report.ScreenshotStatus = "captured";
            }

            report.Passed = true;
            report.Status = "passed";
            WriteReport(reportPath!, report);
            GD.Print($"Toolchain smoke passed; report: {reportPath}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            report.Passed = false;
            report.Status = "failed";
            report.Error = error.ToString();
            GD.PushError(error.ToString());
            if (_status != null) _status.Text = "Failed: " + error.Message;
            if (!smoke) return;
            if (!string.IsNullOrWhiteSpace(reportPath) && Path.IsPathFullyQualified(reportPath))
            {
                try { WriteReport(reportPath, report); }
                catch (Exception reportError) { GD.PushError("Could not write smoke report: " + reportError); }
            }
            GetTree().Quit(1);
        }
    }

    private void AdvanceFromButton() => RunUiAction(() =>
    {
        _probe.Advance(120);
        RefreshCounter();
        _status.Text = "Advanced the independent C# core by 120 ticks.";
    });

    private void SaveReloadFromButton() => RunUiAction(() =>
    {
        ToolchainProbe previous = _probe;
        string json = previous.CaptureSnapshotJson();
        EnsureParentDirectory(_snapshotPath);
        File.WriteAllText(_snapshotPath, json);
        string storedJson = File.ReadAllText(_snapshotPath);
        ToolchainProbe restored = ToolchainProbe.RestoreSnapshotJson(storedJson);
        Require(restored.TotalTicks == previous.TotalTicks, "Snapshot round-trip changed the tick count.");
        _restoredNewInstance = !ReferenceEquals(previous, restored);
        _probe = restored;
        _lastSnapshotJson = storedJson;
        _snapshot.Text = "Snapshot: " + storedJson;
        _status.Text = $"Saved and restored {_probe.TotalTicks} ticks from disk.";
        RefreshCounter();
    });

    private void RunUiAction(Action action)
    {
        _lastActionError = null;
        try { action(); }
        catch (Exception error)
        {
            _lastActionError = error;
            _status.Text = "Failed: " + error.Message;
            GD.PushError(error.ToString());
        }
    }

    private void RefreshCounter() => _tickValue.Text = _probe.TotalTicks.ToString(CultureInfo.InvariantCulture);

    public override void _ExitTree()
    {
        if (_advanceButton != null) _advanceButton.Pressed -= AdvanceFromButton;
        if (_saveReloadButton != null) _saveReloadButton.Pressed -= SaveReloadFromButton;
    }

    private static string? ArgumentValue(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static void RequireAbsolutePath(string? path, string argument)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException($"{argument}=<absolute path> is required for this output.");
    }

    private static void Require(bool condition, string message, Exception? inner = null)
    {
        if (!condition) throw new InvalidOperationException(message, inner);
    }

    private static void EnsureParentDirectory(string path) =>
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The output requires a parent directory.", nameof(path)));

    private static void WriteReport(string path, SmokeReport report)
    {
        EnsureParentDirectory(path);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private sealed class SmokeReport
    {
        public int ReportSchemaVersion { get; } = 1;
        public string Test { get; } = "godot_toolchain_smoke";
        public bool Passed { get; set; }
        public string Status { get; set; } = "failed";
        public string EngineVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string RuntimeAssemblyLocation { get; set; } = string.Empty;
        public string DisplayServer { get; set; } = string.Empty;
        public bool Headless { get; set; }
        public string InputMethod { get; } = "internal_button_signals";
        public long InitialTicks { get; set; }
        public long AfterAdvanceTicks { get; set; }
        public long AfterReloadTicks { get; set; }
        public long FinalTicks { get; set; }
        public bool AdvanceButtonVerified { get; set; }
        public bool SaveReloadButtonVerified { get; set; }
        public string? SnapshotPath { get; set; }
        public string? SnapshotJson { get; set; }
        public string ScreenshotStatus { get; set; } = "not_requested";
        public string? ScreenshotPath { get; set; }
        public string? Error { get; set; }
    }
}
