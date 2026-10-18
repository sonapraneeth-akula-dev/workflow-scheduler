using Microsoft.AspNetCore.Http;
using Npgsql;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class IdempotencyFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("nonascii-\u00e9")]
    [InlineData("\u0000")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\u007fb")]
    public async Task Invalid_key_fails_before_storage_or_handler(string value)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var filter = new IdempotencyFilter(source, TimeProvider.System);
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = value;
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context),
            _ => throw new InvalidOperationException("Invalid keys must not reach storage or the handler."));
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Optional_key_passes_through_without_storage()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var filter = new IdempotencyFilter(source, TimeProvider.System);
        var expected = Results.Accepted();
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()),
            _ => ValueTask.FromResult<object?>(expected));
        Assert.Same(expected, result);
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyFilter.Retention);
    }
}
