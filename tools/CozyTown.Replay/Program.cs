using CozyTown.Core.Town;
using System.Text.Json;

namespace CozyTown.Replay;

public static class ReplayCommand
{
    public static int Main(string[] args)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int exit = Run(args, Console.Out);
        string? metrics = args.SingleOrDefault(a => a.StartsWith("--metrics=", StringComparison.Ordinal))?[10..];
        if (metrics is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metrics))!);
            File.WriteAllText(metrics, TownJson.Serialize(new { elapsedSeconds = watch.Elapsed.TotalSeconds,
                peakWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,
                managedAfterCollectionBytes = GC.GetTotalMemory(true), exitCode = exit }));
        }
        return exit;
    }

    public static int Run(string[] args, TextWriter output)
    {
        string? Argument(string name) => args.SingleOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        TownReplayReport report;
        int exit;
        try
        {
            if (args.Contains("--help"))
            {
                output.WriteLine("Usage: dotnet run --project tools/CozyTown.Replay -- --input=<package.json|run-directory|export.zip> [--report=<report.json>] [--metrics=<metrics.json>]");
                return 0;
            }
            if (args.Contains("--generate-fixtures"))
            {
                FixtureGenerator.Generate(Argument("--content") ?? throw new InvalidDataException("Fixture generation requires --content=<content.json>."),
                    Argument("--output") ?? throw new InvalidDataException("Fixture generation requires --output=<directory>."));
                output.WriteLine("{\"passed\":true,\"code\":\"fixtures_generated\"}");
                return 0;
            }
            string path = Argument("--input") ?? throw new InvalidDataException("Specify --input=<recording path>. Use --help for accepted formats.");
            report = TownReplay.Run(TownReplay.ReadPackage(path));
            exit = report.Passed ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string code = error is FileNotFoundException or DirectoryNotFoundException ? "missing_dependency"
                : error.Message.Contains("legacy records", StringComparison.Ordinal) ? "unsupported_recording" : "invalid_recording";
            report = new(false, code, 0, "package", "complete versioned run package", error.Message, TownWorld.RulesVersion, TownWorld.NumericVersion);
            exit = 2;
        }
        // The report contains a full final state only for the library caller, to keep CLI reports bounded.
        string json = TownJson.Serialize(report with { FinalStateJson = null });
        output.WriteLine(json);
        string? destination = Argument("--report");
        if (destination is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, json);
        }
        return exit;
    }
}
