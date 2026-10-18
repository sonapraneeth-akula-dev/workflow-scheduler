using System.Text;
using System.Text.Json;

namespace Wf.Contracts.Secrets;

public static class SecretPatterns
{
    public static string[] Expand(IEnumerable<string> resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(resolvedValues);
        return resolvedValues.SelectMany(value =>
        {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(resolvedValues));
            return new[] { value, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) };
        }).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public static string[] ForJson(IEnumerable<string> resolvedValues) =>
        Expand(resolvedValues).SelectMany(value => new[]
        {
            value, JsonEncodedText.Encode(value).ToString(),
            JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString()
        }).Distinct(StringComparer.Ordinal).ToArray();

    public static bool Contains(JsonElement value, IReadOnlyList<string> patterns)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property =>
                Matches(property.Name, patterns) || Contains(property.Value, patterns)),
            JsonValueKind.Array => value.EnumerateArray().Any(item => Contains(item, patterns)),
            JsonValueKind.String => Matches(value.GetString()!, patterns),
            _ => Matches(value.GetRawText(), patterns)
        };
    }

    private static bool Matches(string value, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.Ordinal));
}
