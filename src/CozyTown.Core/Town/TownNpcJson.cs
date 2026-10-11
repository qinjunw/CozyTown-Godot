using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CozyTown.Core.Town;

public static class TownNpcJson
{
    private static readonly JsonSerializerOptions Options = new(TownJson.Options)
        { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        var output = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\') { output.Append(json[i]); continue; }
            if (UnicodeEscape(json, i, out char character) && character >= 0x20 && character is not ('"' or '\\'))
            {
                if (!char.IsSurrogate(character))
                {
                    output.Append(character); i += 5; continue;
                }
                if (char.IsHighSurrogate(character) && UnicodeEscape(json, i + 6, out char low) && char.IsLowSurrogate(low))
                {
                    output.Append(character).Append(low); i += 11; continue;
                }
            }
            // Consume an existing escape together so literal backslash-u text stays literal.
            output.Append(json[i]).Append(json[++i]);
        }
        return output.ToString();
    }

    private static bool UnicodeEscape(string json, int index, out char character)
    {
        character = default;
        if (index + 6 > json.Length || json[index] != '\\' || json[index + 1] != 'u'
            || !ushort.TryParse(json.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
            return false;
        character = (char)code; return true;
    }
}
