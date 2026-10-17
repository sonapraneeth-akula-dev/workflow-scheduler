using Wf.Contracts.Checkpoints;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Tests.Storage;

public sealed class CheckpointBlobTests
{
    private static readonly Guid Run = Guid.Parse("0199ea00-0000-7000-8000-000000000001");
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Named_checkpoint_content_keeps_the_verified_canonical_run_scoped_key()
    {
        var key = CheckpointProtocol.Key(Run, "work/task", long.MaxValue, Sha);
        Assert.Equal(key, new BlobContent(BlobKind.Checkpoints, Sha, CheckpointProtocol.MaximumBytes, key).Key);
    }

    [Theory]
    [InlineData("run-case")]
    [InlineData("run-version")]
    [InlineData("hash-case")]
    [InlineData("sequence-zero")]
    [InlineData("sequence-leading-zero")]
    [InlineData("sequence-overflow")]
    [InlineData("digest")]
    [InlineData("prefix")]
    [InlineData("traversal")]
    public void Noncanonical_checkpoint_keys_cannot_reach_a_blob_adapter(string invalid)
    {
        var key = CheckpointProtocol.Key(Run, "work/task", 1, Sha);
        var parts = key.Split('/');
        key = invalid switch
        {
            "run-case" => key.Replace(Run.ToString("D"), Run.ToString("D").ToUpperInvariant(), StringComparison.Ordinal),
            "run-version" => key.Replace(Run.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            "hash-case" => string.Join('/', parts[0], parts[1], parts[2].ToUpperInvariant(), parts[3]),
            "sequence-zero" => key.Replace("/1-", "/0-", StringComparison.Ordinal),
            "sequence-leading-zero" => key.Replace("/1-", "/01-", StringComparison.Ordinal),
            "sequence-overflow" => key.Replace("/1-", "/9223372036854775808-", StringComparison.Ordinal),
            "digest" => key.Replace(Sha + ".json", new string('b', 64) + ".json", StringComparison.Ordinal),
            "prefix" => key.Replace("checkpoints/", "reports/", StringComparison.Ordinal),
            _ => "checkpoints/" + Run + "/../1-" + Sha + ".json"
        };
        Assert.Throws<ArgumentException>(() => new BlobContent(BlobKind.Checkpoints, Sha, 2, key));
    }
}
