using System.Text.Json;
using CozyTown.Core;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class ToolchainProbeTests
{
    [Test]
    public void NewProbeStartsAtZero()
    {
        Assert.That(new ToolchainProbe().TotalTicks, Is.Zero);
    }

    [Test]
    public void RestoredProbeResumesTheSameSequenceAsUninterruptedProbe()
    {
        var uninterrupted = new ToolchainProbe();
        foreach (long ticks in new long[] { 2, 7, 4096 })
        {
            uninterrupted.Advance(ticks);
        }

        var restored = ToolchainProbe.RestoreSnapshotJson(uninterrupted.CaptureSnapshotJson());
        Assert.That(restored.TotalTicks, Is.EqualTo(4105));

        foreach (long ticks in new long[] { 3, 1000 })
        {
            uninterrupted.Advance(ticks);
            restored.Advance(ticks);
        }

        Assert.Multiple(() =>
        {
            Assert.That(restored.TotalTicks, Is.EqualTo(5108));
            Assert.That(restored.TotalTicks, Is.EqualTo(uninterrupted.TotalTicks));
        });
    }

    [Test]
    public void SnapshotIsIndependentOfLaterChangesToTheOriginalProbe()
    {
        var original = new ToolchainProbe();
        original.Advance(12);
        string snapshot = original.CaptureSnapshotJson();
        original.Advance(10);
        var restored = ToolchainProbe.RestoreSnapshotJson(snapshot);
        restored.Advance(1);

        Assert.Multiple(() =>
        {
            Assert.That(original.TotalTicks, Is.EqualTo(22));
            Assert.That(restored.TotalTicks, Is.EqualTo(13));
        });
    }

    [TestCase(0L)]
    [TestCase(9_007_199_254_740_993L)]
    [TestCase(long.MaxValue)]
    public void JsonRoundTripPreservesExactInt64Ticks(long ticks)
    {
        var probe = new ToolchainProbe();
        if (ticks > 0)
        {
            probe.Advance(ticks);
        }

        var restored = ToolchainProbe.RestoreSnapshotJson(probe.CaptureSnapshotJson());

        Assert.That(restored.TotalTicks, Is.EqualTo(ticks));
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(long.MinValue)]
    public void NonpositiveAdvanceLeavesStateUnchanged(long ticks)
    {
        var probe = new ToolchainProbe();
        probe.Advance(12);

        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Advance(ticks));
        Assert.That(probe.TotalTicks, Is.EqualTo(12));
    }

    [Test]
    public void OverflowLeavesStateUnchanged()
    {
        var probe = new ToolchainProbe();
        probe.Advance(1);

        Assert.Throws<OverflowException>(() => probe.Advance(long.MaxValue));
        Assert.That(probe.TotalTicks, Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase("not-json")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"schemaVersion\":1}")]
    [TestCase("{\"totalTicks\":0}")]
    [TestCase("{\"schemaVersion\":2,\"totalTicks\":0}")]
    [TestCase("{\"schemaVersion\":1,\"totalTicks\":-1}")]
    [TestCase("{\"schemaVersion\":1,\"totalTicks\":1.5}")]
    [TestCase("{\"schemaVersion\":1,\"totalTicks\":9223372036854775808}")]
    public void InvalidSnapshotCannotCreateAProbe(string json)
    {
        Assert.Throws<JsonException>(() => ToolchainProbe.RestoreSnapshotJson(json));
    }

    [Test]
    public void NullSnapshotArgumentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ToolchainProbe.RestoreSnapshotJson(null!));
    }
}
