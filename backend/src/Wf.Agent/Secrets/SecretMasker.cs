using System.Text;
using System.Text.Json;

namespace Wf.Agent.Secrets;

/// <summary>Ordinal literal masking of resolved values. Share the masker, not its per-stream state.</summary>
public sealed class SecretMasker
{
    private readonly string[] _secrets;
    private readonly int _longest;

    public SecretMasker(IEnumerable<string> resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(resolvedValues);
        _secrets = resolvedValues.Select(value =>
        {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(resolvedValues));
            return value;
        }).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal).ToArray();
        _longest = _secrets.Length == 0 ? 0 : _secrets[0].Length;
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TextStream(this).Process(text, complete: true);
    }

    public TextStream CreateStream() => new(this);

    /// <summary>Preserves valid JSON; a secret-bearing non-string scalar becomes a redacted string.</summary>
    public JsonElement RedactJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Output must be a defined JSON value.", nameof(value));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(value, writer);
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        return document.RootElement.Clone();
    }

    private void Write(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var name = Redact(property.Name);
                    if (!names.Add(name))
                        throw new InvalidOperationException("Secret masking would produce duplicate output property names.");
                    writer.WritePropertyName(name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(value.GetString()!));
                break;
            default:
                var raw = value.GetRawText();
                var redacted = Redact(raw);
                if (raw == redacted) value.WriteTo(writer);
                else writer.WriteStringValue(redacted);
                break;
        }
    }

    /// <summary>One decoded text stream (stdout/stderr separately); not thread-safe. Flush before publishing EOF.</summary>
    public sealed class TextStream
    {
        private readonly SecretMasker _masker;
        private string _pending = "";
        private int _redactionRemaining;
        private bool _maskOpen;
        private bool _completed;

        internal TextStream(SecretMasker masker) => _masker = masker;

        public string Append(string chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            if (_completed) throw new InvalidOperationException("The secret-masking stream has completed.");
            return Process(chunk, complete: false);
        }

        public string Complete()
        {
            if (_completed) return "";
            var result = Process("", complete: true);
            _completed = true;
            return result;
        }

        internal string Process(string chunk, bool complete)
        {
            if (_masker._longest == 0) return chunk;
            var text = string.Concat(_pending, chunk);
            var limit = complete ? text.Length : Math.Max(0, text.Length - _masker._longest + 1);
            var output = new StringBuilder(limit);
            for (var index = 0; index < limit; index++)
            {
                foreach (var secret in _masker._secrets)
                {
                    if (!text.AsSpan(index).StartsWith(secret, StringComparison.Ordinal)) continue;
                    _redactionRemaining = Math.Max(_redactionRemaining, secret.Length);
                    break;
                }
                if (_redactionRemaining > 0)
                {
                    if (!_maskOpen) output.Append("***");
                    _maskOpen = true;
                    _redactionRemaining--;
                }
                else
                {
                    output.Append(text[index]);
                    _maskOpen = false;
                }
            }
            _pending = text[limit..];
            return output.ToString();
        }
    }
}
