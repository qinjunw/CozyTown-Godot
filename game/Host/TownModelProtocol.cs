using System.Text.Json;
using CozyTown.Core.Town;

namespace CozyTown.Game;

public static class TownModelProtocol
{
    public const string Version = "town-choice-v1";
    public static string BuildRequest(DecisionRequest request) => JsonSerializer.Serialize(new
    {
        model = "deepseek-flash", thinking = new { type = "disabled" }, stream = false, max_tokens = 512,
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new { role = "system", content = "You are one independent resident in CozyTown, not a director for other residents. Make ONE proposed choice based only on your own state, local observations, public offers, existing conversation, and schedule window in the input. Pursue your own stated desire when a lawful social opportunity helps; you may also refuse or wait. Do not assume another resident consents. Choose the actual partner and action yourself from allowedActions and allowedTargets. Never invent stock, facts, locations or agreements. World rules will separately validate and execute the choice. For invite choose an observed placeId. For respond use accept=true or false to answer the existing offer. For propose_exchange YOU sell your own available resourceId, quantity and price; the other person must accept. A dialogue statement never transfers resources. Write short natural Chinese dialogue in text, maximum 100 characters. Return only json with exactly these eight fields: action (one allowed action), targetId (visible ID or null), placeId (observed ID or null), resourceId (your resource ID or null), quantity (integer, 0 when unused), price (nonnegative integer, 0 when unused), accept (boolean for respond, otherwise null), text (string). JSON format example only, not a preferred action: {\"action\":\"wait\",\"targetId\":null,\"placeId\":null,\"resourceId\":null,\"quantity\":0,\"price\":0,\"accept\":null,\"text\":\"我先想一想。\"}" },
            new { role = "user", content = request.InputJson }
        }
    });

    public static ActionCandidate Parse(string json)
    {
        var candidate = TownJson.Deserialize<ActionCandidate>(json);
        if (candidate.Text == null || candidate.Text.Length > 200 || candidate.Text.Any(char.IsControl))
            throw new InvalidDataException("Candidate dialogue must be a single short line.");
        return candidate;
    }
}
