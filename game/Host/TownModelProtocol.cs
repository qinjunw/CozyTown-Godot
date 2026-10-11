using System.Text.Encodings.Web;
using System.Text.Json;
using CozyTown.Core.Town;

namespace CozyTown.Game;

public static class TownModelProtocol
{
    public const string Version = "town-choice-v1";
    private static readonly JsonSerializerOptions RequestJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string BuildRequest(DecisionRequest request) => JsonSerializer.Serialize(new
    {
        model = "deepseek-flash", thinking = new { type = "disabled" }, stream = false, max_tokens = 512,
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new { role = "system", content = "You are one independent resident in CozyTown, not a director for other residents. Make ONE proposed choice using your own state, local observations, memories and schedule window. Choose the partner and action yourself from allowedTargets and allowedActions; you may refuse or choose another allowed action. Do not assume consent or invent assets or facts. Follow actionConstraints: every nullFields entry MUST be null, every zeroFields entry MUST be 0. Only invite uses placeId; all other actions MUST use placeId=null, even when speaking about a place. For respond, use targetId and accept=true/false only: resourceId/placeId=null and quantity/price=0; do not repeat or replace the offer. tradeConstraints describes one-way goods for total coins, NOT barter. For propose_exchange YOU are the seller: choose your own availableSales resource and quantity no greater than maximumQuantity. The target is the buyer and pays YOU the total price in coins; price is not a requested item or a per-unit price. The target must independently accept. To buy another person's goods, you may speak to request their offer; never propose selling goods you do not own. For a pendingOffer, sellerId gives goods to buyerId and buyerId pays totalCoinPrice to sellerId. If you are the buyer, check your current coins and selfCanPay before accepting. Only exchange_offered has a pending offer; historical offer fields during conversation are not pending and do not replenish inventory. Current resident.inventory and coins outrank personality, old dialogue and memories. When no sale is possible, choose an allowed non-sale action. Text never creates an agreement, barter or transfer. World rules independently validate every choice. Write short natural Chinese text, maximum 100 characters. Return only JSON with exactly these eight fields: action, targetId, placeId, resourceId, quantity, price, accept, text. Example of format only, not a preferred action: {\"action\":\"wait\",\"targetId\":null,\"placeId\":null,\"resourceId\":null,\"quantity\":0,\"price\":0,\"accept\":null,\"text\":\"我先想一想。\"}" },
            new { role = "user", content = request.InputJson }
        }
    }, RequestJson);

    public static ActionCandidate Parse(string json)
    {
        var candidate = TownJson.Deserialize<ActionCandidate>(json);
        if (candidate.Text == null || candidate.Text.Length > 200 || candidate.Text.Any(char.IsControl))
            throw new InvalidDataException("Candidate dialogue must be a single short line.");
        return candidate;
    }
}
