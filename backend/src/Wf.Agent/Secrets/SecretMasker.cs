using System.Text.Json;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Secrets;

namespace Wf.Agent.Secrets;

public sealed class SecretMasker
{
    private readonly string[] values;
    private readonly SecretRedactor redactor;

    public SecretMasker(IEnumerable<string> resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(resolvedValues);
        values = resolvedValues.ToArray();
        redactor = new(values);
    }

    public string Redact(string text) => redactor.Redact(text);
    public JsonElement RedactJson(JsonElement value) => redactor.RedactJson(value);
    public TextStream CreateStream() => new(redactor.CreateStream());
    internal SecretMasker WithValues(IEnumerable<string> additional) => new(values.Concat(additional));
    internal IReadOnlyList<string> Values => values;
    internal TaskCompleted RedactResult(TaskCompleted result) => redactor.RedactResult(result);

    public sealed class TextStream
    {
        private readonly SecretRedactor.TextStream stream;
        internal TextStream(SecretRedactor.TextStream stream) => this.stream = stream;
        public string Append(string chunk) => stream.Append(chunk);
        public string Complete() => stream.Complete();
    }
}
