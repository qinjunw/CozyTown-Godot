using System.Diagnostics;
using System.Text.Json.Serialization;
using CozyTown.Core.Town;

namespace CozyTown.Game;

public interface IPlayerDialogueRetirements
{
    string? Find(string budgetId, string logicalRequestId);
    void Retire(string budgetId, string logicalRequestId, string reason);
}

// Terminal facts survive loading an earlier branch. This file never holds text, memories, fees or slot state.
public sealed class TownPlayerDialogueRegistry : IPlayerDialogueRetirements
{
    private readonly string _path;
    public TownPlayerDialogueRegistry(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try
        {
            using var created = new FileStream(_path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(_path)) TownSaveFile.PublishBytes(_path, System.Text.Encoding.UTF8.GetBytes(TownJson.Serialize(new RegistryDocument())), null);
        }
        catch (IOException) when (File.Exists(_path + ".lock"))
        {
            using var held = Lock();
            _ = Read();
        }
    }
    public string? Find(string budgetId, string logicalRequestId)
    {
        using var held = Lock();
        return Read().Entries.SingleOrDefault(e => e.BudgetId == budgetId && e.LogicalRequestId == logicalRequestId)?.Reason;
    }

    public void Retire(string budgetId, string logicalRequestId, string reason)
    {
        if (!Id(budgetId) || !Id(logicalRequestId) || !Id(reason)) throw new InvalidDataException("Dialogue retirement identity and reason are required.");
        using var held = Lock();
        var document = Read();
        if (document.Entries.Any(e => e.BudgetId == budgetId && e.LogicalRequestId == logicalRequestId)) return;
        document.Entries.Add(new(budgetId, logicalRequestId, reason));
        TownSaveFile.PublishBytes(_path, System.Text.Encoding.UTF8.GetBytes(TownJson.Serialize(document)), null, _path + ".previous");
    }

    private RegistryDocument Read()
    {
        if (!File.Exists(_path)) throw new InvalidDataException("Dialogue retirement registry is missing; earlier terminal facts cannot be recovered.");
        if (new FileInfo(_path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Dialogue retirement registry exceeds 8 MiB.");
        RegistryDocument document;
        try { document = TownJson.Deserialize<RegistryDocument>(File.ReadAllText(_path)); }
        catch (System.Text.Json.JsonException error) { throw new InvalidDataException("Dialogue retirement registry fields are invalid.", error); }
        if (document.SchemaVersion != 1 || document.Entries is null || document.Entries.Any(e => e is null || !Id(e.BudgetId) || !Id(e.LogicalRequestId) || !Id(e.Reason))
            || document.Entries.Select(e => (e.BudgetId, e.LogicalRequestId)).Distinct().Count() != document.Entries.Count)
            throw new InvalidDataException("Dialogue retirement registry identities are invalid.");
        return document;
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (File.Exists(_path + ".lock") && clock.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
    }
    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
    private sealed record Retirement(string BudgetId, string LogicalRequestId, string Reason);
    private sealed class RegistryDocument
    {
        [JsonRequired] public int SchemaVersion { get; set; } = 1;
        [JsonRequired] public List<Retirement> Entries { get; set; } = [];
    }
}
