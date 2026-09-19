using System.Text.Json;

namespace ModelProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" }))
                return await SelfTests.RunAsync();
            if (args.Length == 1 && args[0] == "--help")
            {
                Console.WriteLine("ModelProbe --credential-file <path> --ledger-file <path> --output-dir <new directory>");
                Console.WriteLine("ModelProbe --self-test  (offline; fake credentials only)");
                return 0;
            }
            var options = ParseArguments(args);
            return await ProbeRunner.RunAsync(options);
        }
        catch (ProbeException error)
        {
            Console.Error.WriteLine("probe_failed:" + error.Code);
            return 1;
        }
        catch
        {
            // Credential and provider exceptions are deliberately not printed.
            Console.Error.WriteLine("probe_failed:unexpected_failure");
            return 1;
        }
    }

    private static ProbeOptions ParseArguments(string[] args)
    {
        string[] allowed = ["--credential-file", "--ledger-file", "--output-dir"];
        if (args.Length != 6) throw new ProbeException("arguments_required");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i]) || string.IsNullOrWhiteSpace(args[i + 1])
                || !values.TryAdd(args[i], Path.GetFullPath(args[i + 1])))
                throw new ProbeException("arguments_invalid");
        return new(values[allowed[0]], values[allowed[1]], values[allowed[2]]);
    }
}

internal sealed record ProbeOptions(string CredentialFile, string LedgerFile, string OutputDirectory);
internal sealed class ProbeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ProbeException("duplicate_json_property");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
    }
}
