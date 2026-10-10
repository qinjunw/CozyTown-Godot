using CozyTown.Replay;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

public sealed class TownReplayCliTests
{
    [Test]
    public void MissingInputProducesMachineReadableFailureAndNonzeroExit()
    {
        using var output = new StringWriter();
        int code = ReplayCommand.Run(["--input=" + Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")], output);
        Assert.That(code, Is.EqualTo(2));
        using var report = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.That(report.RootElement.GetProperty("passed").GetBoolean(), Is.False);
        Assert.That(report.RootElement.GetProperty("code").GetString(), Is.EqualTo("missing_dependency"));
    }
}
