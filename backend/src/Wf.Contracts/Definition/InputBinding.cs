namespace Wf.Contracts.Definition;

public sealed record InputBinding
{
    public string Node { get; }
    public string Key { get; }

    public InputBinding(string node, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Node = node;
        Key = key;
    }
}
