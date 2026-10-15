using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Engine.Registry;

namespace Wf.ControlPlane.Tests.Registry;

public sealed class ActiveVersionTests
{
    [Fact]
    public void New_cache_is_unavailable_instead_of_returning_a_stale_or_default_version()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var cache = new ActiveVersionCache(source, NullLogger<ActiveVersionCache>.Instance, TimeProvider.System);
        Assert.False(cache.IsReady);
        var error = Assert.Throws<InvalidOperationException>(() => cache.GetActive("team/type/workflow"));
        Assert.Contains("not synchronized", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_workflow_identity_is_an_explicit_caller_error(string? identity)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var cache = new ActiveVersionCache(source, NullLogger<ActiveVersionCache>.Instance, TimeProvider.System);
        Assert.ThrowsAny<ArgumentException>(() => cache.GetActive(identity!));
    }
}
