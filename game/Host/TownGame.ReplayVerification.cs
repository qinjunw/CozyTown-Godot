using System.Diagnostics;
using System.Text.Json;
using CozyTown.Core.Town;

namespace CozyTown.Game;

public partial class TownGame
{
    private long _recordingSmokeInitialMemory;

    private double ReplayRecordingSeconds()
    {
        string? configured = Argument("--record-seconds=");
        if (configured is null) return 2;
        if (!double.TryParse(configured, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 2 || seconds > 360)
            throw new InvalidDataException("Recording smoke duration must be 2..360 seconds.");
        return seconds;
    }

    private void FinishReplayRecordingSmoke()
    {
        _finishing = true;
        FlushWorldEvents();
        var package = _journal.CaptureReplay();
        long managedBeforeReplay = GC.GetTotalMemory(true);
        string export = Path.Combine(_userRoot, "exports", "recording-smoke.zip");
        var watch = Stopwatch.StartNew();
        _journal.ExportSnapshotZip(export);
        double exportSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var replay = TownReplay.Run(TownReplay.ReadPackage(export));
        double replaySeconds = watch.Elapsed.TotalSeconds;
        var report = new
        {
            schemaVersion = 1, test = "ordinary_source_recording", passed = replay.Passed, mode = _mode,
            elapsedSeconds = _elapsed, boundaries = package.BoundaryCount, packageBytes = new FileInfo(Path.Combine(_journal.DirectoryPath, "replay.json")).Length,
            archiveBytes = new FileInfo(export).Length,
            journalBytes = new FileInfo(Path.Combine(_journal.DirectoryPath, "events.jsonl")).Length,
            managedInitialBytes = _recordingSmokeInitialMemory, managedBeforeReplayBytes = managedBeforeReplay,
            managedAfterReplayBytes = GC.GetTotalMemory(true),
            workingSetBytes = Process.GetCurrentProcess().WorkingSet64, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, exportSeconds, replaySeconds,
            replay = replay with { FinalStateJson = null }
        };
        _journal.WriteArtifact("recording-performance.json", report);
        WriteReport(report);
        GetTree().Quit(report.passed ? 0 : 1);
    }
}
