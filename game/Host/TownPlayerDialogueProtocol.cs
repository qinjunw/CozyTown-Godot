using System.Text.Json;
using CozyTown.Core.Town;

namespace CozyTown.Game;

public static class TownPlayerDialogueProtocol
{
    public const string Version = "town-player-text-v1";
    public static string BuildRequest(PlayerDialogueRequest request) => JsonSerializer.Serialize(new
    {
        model = "deepseek-flash", thinking = new { type = "disabled" }, stream = false, max_tokens = 512,
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new { role = "system", content = "You are the resident identified in the input, speaking to the nearby player. Use only your own state, local observation, memories and schedule. Give ONE natural Chinese paragraph, at most 160 characters, without control characters. Do not claim that speech executes trades, production, delivery or asset changes. Return only JSON with exactly one field: {\"text\":\"你的单段回应\"}." },
            new { role = "user", content = request.InputJson }
        }
    });

    public static string Parse(string json)
    {
        var response = TownJson.Deserialize<PlayerText>(json);
        if (string.IsNullOrWhiteSpace(response.Text) || response.Text.Length > 160 || response.Text.Any(char.IsControl))
            throw new InvalidDataException("Player response requires one nonempty text field of at most 160 characters.");
        return response.Text;
    }

    private sealed record PlayerText(string Text);
    public static string FixedResponse(string residentId) => TownJson.Serialize(new PlayerText(residentId switch
    {
        "mina" => "店里的一天刚刚开始。能见到熟悉的邻居，总让我觉得小镇很温暖。",
        "eli" => "我喜欢慢慢照顾田里的作物。忙完手边的事情，也想看看朋友们过得怎样。",
        "ren" => "走在小镇里，总能遇见一些新鲜的小事。今天也想按自己的节奏过。",
        "sora" => "好好吃饭，也好好生活。等空下来，我想和附近的朋友聊聊近况。",
        _ => throw new InvalidDataException("Unknown fixed dialogue resident.")
    }));
}
