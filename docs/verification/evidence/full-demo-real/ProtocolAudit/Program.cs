using System.Text.Json;
using CozyTown.Game;

using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var row in document.RootElement.EnumerateArray())
{
    string id = row.GetProperty("attemptId").GetString()!;
    string kind = row.GetProperty("kind").GetString()!;
    bool passed = false;
    string? error = null;
    try
    {
        string candidate = row.GetProperty("candidateJson").GetString() ?? "";
        if (kind == "npc_action") _ = TownModelProtocol.Parse(candidate);
        else if (kind == "player_text") _ = TownPlayerDialogueProtocol.Parse(candidate);
        else throw new InvalidDataException("Unregistered request kind.");
        passed = true;
    }
    catch (Exception failure) { error = failure.GetType().Name; }
    results.Add(new { attemptId = id, kind, passed, error });
}
File.WriteAllText(args[1], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
