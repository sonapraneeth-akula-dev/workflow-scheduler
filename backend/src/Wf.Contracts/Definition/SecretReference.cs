using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Definition;

public sealed record SecretReference
{
    private const string Prefix = "secret://";
    public string Name { get; }

    [JsonIgnore]
    public string Store => Name.StartsWith(Prefix, StringComparison.Ordinal)
        ? Name[Prefix.Length..Name.IndexOf('/', Prefix.Length)] : "local";

    [JsonIgnore]
    public string SecretName => Name.StartsWith(Prefix, StringComparison.Ordinal)
        ? Name[(Name.IndexOf('/', Prefix.Length) + 1)..] : Name;

    public SecretReference(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException("A secret reference must be a safe name or secret://<store>/<name>, never a literal value.", nameof(name));
        Name = name;
    }

    public static SecretReference Parse(string reference)
    {
        if (reference is null || !reference.StartsWith(Prefix, StringComparison.Ordinal))
            throw new ArgumentException("A qualified secret reference must use secret://<store>/<name>.", nameof(reference));
        return new(reference);
    }

    public static bool IsValidName(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 273) return false;
        var name = reference;
        if (reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var slash = reference.IndexOf('/', Prefix.Length);
            if (slash < 0) return false;
            var store = reference.AsSpan(Prefix.Length, slash - Prefix.Length);
            if (store.Length is < 1 or > 63 || store[0] is < 'a' or > 'z' ||
                store[^1] == '-' || store.ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-"))
                return false;
            name = reference[(slash + 1)..];
        }
        if (name.Length is < 1 or > 200) return false;
        return name.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
    }

    public static bool IsValidValue(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == 1 && value.TryGetProperty("Name", out var name) &&
        name.ValueKind == JsonValueKind.String && IsValidName(name.GetString());

    public override string ToString() => Name;
}
