using System.Text.Json;

namespace ModelProbe;

internal static class CredentialReader
{
    public static string Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new ProbeException("credential_file_too_large");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProbeException("credential_root_invalid");
            var matches = document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                .Select(p => p.Value)
                .Where(v => v.EnumerateObject().Any(p => p.Name == "Todo"
                    && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "CozyTown"))
                .ToArray();
            if (matches.Length != 1) throw new ProbeException("credential_match_count_invalid");
            var fields = matches[0].EnumerateObject().ToArray();
            if (fields.Count(p => p.Name == "Todo") != 1 || fields.Count(p => p.Name == "testingAPIKey") != 1)
                throw new ProbeException("credential_fields_ambiguous");
            var value = matches[0].GetProperty("testingAPIKey");
            if (value.ValueKind != JsonValueKind.String) throw new ProbeException("credential_key_invalid");
            string key = value.GetString()!;
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
                throw new ProbeException("credential_key_invalid");
            return key;
        }
        catch (ProbeException) { throw; }
        catch { throw new ProbeException("credential_read_failed"); }
    }
}
