using System.Text.Json;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownObservedChoiceFailuresTests
{
    private static IEnumerable<JsonElement> Samples()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rejected-samples.json")));
        return document.RootElement.GetProperty("samples").EnumerateArray().Select(s => s.Clone()).ToArray();
    }

    private static IEnumerable<TestCaseData> RejectedSamples() => Samples().Select(sample => new TestCaseData(sample)
        .SetName("ObservedChoice_" + sample.GetProperty("scenario").GetString() + "_" + sample.GetProperty("residentId").GetString()
            + "_" + sample.GetProperty("logicalRequestId").GetString()!.Split(':')[^1]));

    private static DecisionRequest Request(TownWorld world, string resident) => world.GetDecisionRequests().Single(r => r.ResidentId == resident);
    private static void Decide(TownWorld world, string resident, ActionCandidate candidate)
    {
        var request = Request(world, resident);
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "fixed-" + request.LogicalRequestId), Is.True);
        Assert.That(world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "fixed-" + request.LogicalRequestId,
            resident, candidate, "fixed_model", null)), Is.True);
        world.Advance(0, 0);
    }

    private static TownWorld Prepare(JsonElement sample)
    {
        string self = sample.GetProperty("residentId").GetString()!;
        var candidate = TownJson.Deserialize<ActionCandidate>(sample.GetProperty("candidate").GetRawText());
        string partner = candidate.TargetId!;
        var own = sample.GetProperty("frozenResident");
        string[] ids = ["mina", "eli", "ren", "sora"], resources = ["berries", "fish", "grain", "soup"];
        var places = ids.Select((id, i) => new PlaceDefinition(id, id, new(80 + i * 16, 96), "outside"))
            .Concat(new[] { "plaza.north", "plaza.east", "plaza.west", "plaza.south" }
                .Select(id => new PlaceDefinition(id, id, new(240, 160), "outside"))).ToArray();
        var residents = ids.Select(id => new ResidentDefinition(id, id, id, id, "berries",
            id == self ? TownJson.Deserialize<Dictionary<string, int>>(own.GetProperty("inventory").GetRawText())
                : resources.ToDictionary(resource => resource, _ => 8),
            id == self ? own.GetProperty("coins").GetInt32() : 100, [new("free", 0, 1440, id, "free", true)])).ToArray();
        var content = TownContent.Create(new(384, 256, places, [], [new("outside", new(0, 0, 384, 256))]),
            new(residents, resources.Select(id => new ResourceDefinition(id, id)).ToArray(), 540));
        var world = new TownWorld(content); world.Advance(.001, 0);
        string place = sample.GetProperty("frozenCommitment").GetProperty("placeId").GetString()!;
        bool responding = candidate.Action == "respond";
        string initiator = responding ? partner : self, invitee = responding ? self : partner;
        Decide(world, initiator, new("invite", invitee, place, null, 0, 0, null, "一起聊聊。"));
        if (!responding || sample.GetProperty("frozenCommitment").GetProperty("stage").GetString() == "exchange_offered")
        {
            Decide(world, invitee, new("respond", initiator, null, null, 0, 0, true, "好呀。")); world.Advance(8, 0);
            if (responding)
            {
                var offer = sample.GetProperty("frozenCommitment");
                Decide(world, partner, new("propose_exchange", self, null, offer.GetProperty("offerResourceId").GetString(),
                    offer.GetProperty("offerQuantity").GetInt32(), offer.GetProperty("offerPrice").GetInt32(), null, "这份物品按总币价出售。"));
            }
        }
        return world;
    }

    private static void AssertRejectedWithoutAssetChange(TownWorld world, string resident, ActionCandidate candidate, string reason)
    {
        string assets = TownJson.Serialize(world.State.Residents.Select(r => new { r.Id, r.Inventory, r.Coins }));
        string logical = Request(world, resident).LogicalRequestId;
        Decide(world, resident, candidate);
        Assert.That(world.Events.Any(e => e.Kind == "candidate_rejected" && e.LogicalRequestId == logical && e.Reason == reason), Is.True);
        Assert.That(world.Events.Any(e => e.Kind == "candidate_executed" && e.LogicalRequestId == logical), Is.False);
        Assert.That(world.Events.Any(e => e.Kind == "resource_transferred"), Is.False);
        Assert.That(TownJson.Serialize(world.State.Residents.Select(r => new { r.Id, r.Inventory, r.Coins })), Is.EqualTo(assets));
    }

    [TestCaseSource(nameof(RejectedSamples))]
    public void ObservedInvalidCandidateRemainsRejectedWithEveryAssetUnchanged(JsonElement sample)
    {
        var world = Prepare(sample);
        string resident = sample.GetProperty("residentId").GetString()!;
        var request = Request(world, resident);
        using var input = JsonDocument.Parse(request.InputJson);
        var own = sample.GetProperty("frozenResident");
        Assert.That(input.RootElement.GetProperty("resident").GetProperty("coins").GetInt32(), Is.EqualTo(own.GetProperty("coins").GetInt32()));
        var expectedSales = own.GetProperty("inventory").EnumerateObject().Where(p => p.Value.GetInt32() > 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (p.Name, p.Value.GetInt32())).ToArray();
        var sales = input.RootElement.GetProperty("tradeConstraints").GetProperty("availableSales").EnumerateArray()
            .Select(p => (p.GetProperty("resourceId").GetString()!, p.GetProperty("maximumQuantity").GetInt32())).ToArray();
        Assert.That(sales, Is.EqualTo(expectedSales));
        AssertRejectedWithoutAssetChange(world, resident, TownJson.Deserialize<ActionCandidate>(sample.GetProperty("candidate").GetRawText()),
            sample.GetProperty("reason").GetString()!);
    }

    [Test]
    public void BuyerCannotAcceptUnaffordableExistingOfferEvenWithValidFields()
    {
        var sample = Samples().Single(s => s.GetProperty("scenario").GetString() == "baseline"
            && s.GetProperty("residentId").GetString() == "mina");
        var world = Prepare(sample);
        using var input = JsonDocument.Parse(Request(world, "mina").InputJson);
        var offer = input.RootElement.GetProperty("tradeConstraints").GetProperty("pendingOffer");
        Assert.That(offer.GetProperty("sellerId").GetString(), Is.EqualTo("eli"));
        Assert.That(offer.GetProperty("buyerId").GetString(), Is.EqualTo("mina"));
        Assert.That(offer.GetProperty("totalCoinPrice").GetInt32(), Is.EqualTo(10));
        Assert.That(offer.GetProperty("selfCanPay").GetBoolean(), Is.False);
        var candidate = TownJson.Deserialize<ActionCandidate>(sample.GetProperty("candidate").GetRawText()) with { PlaceId = null };
        AssertRejectedWithoutAssetChange(world, "mina", candidate, "insufficient_assets");
    }
}
