using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api.Series;

namespace Wf.ControlPlane.Tests;

public sealed class TrendsEndpointTests
{
    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    [InlineData("90d", 90)]
    public void Window_uses_UTC_creation_days_including_current_partial_day(string name, int days)
    {
        var window = TrendWindow.Parse(new QueryCollection(new Dictionary<string, StringValues> { ["window"] = name }),
            new(2026, 10, 17, 1, 0, 0, TimeSpan.FromHours(5.5)));
        Assert.Equal(name, window.Name);
        Assert.Equal(days, window.Days);
        Assert.Equal(new DateOnly(2026, 10, 17), window.To);
        Assert.Equal(window.To.AddDays(-days), window.From);
    }

    [Theory]
    [InlineData("7D")]
    [InlineData("7")]
    [InlineData("1d")]
    [InlineData("7d,30d,90d")]
    [InlineData("")]
    [InlineData(" 7d")]
    [InlineData("90d ")]
    public void Noncanonical_windows_are_not_silently_defaulted(string name) =>
        Assert.Throws<ArgumentException>(() => TrendWindow.Parse(
            new QueryCollection(new Dictionary<string, StringValues> { ["window"] = name }), DateTimeOffset.UtcNow));

    [Fact]
    public void Default_is_30_days_but_unknown_or_duplicate_parameters_fail()
    {
        Assert.Equal("30d", TrendWindow.Parse(QueryCollection.Empty, DateTimeOffset.UtcNow).Name);
        foreach (var query in new[]
        {
            new Dictionary<string, StringValues> { ["window"] = new(["7d", "90d"]) },
            new Dictionary<string, StringValues> { ["Window"] = "7d" },
            new Dictionary<string, StringValues> { ["secret"] = "private" }
        })
            Assert.Throws<ArgumentException>(() => TrendWindow.Parse(new QueryCollection(query), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Generated_contract_preserves_null_measurements_and_empty_streak_without_private_run_data()
    {
        var response = new SeriesTrendsResponse(Guid.CreateVersion7(), "7d", new(2026, 10, 11), new(2026, 10, 18),
            new(0, 0, 0, 0, null, null, null), new(null, 0), []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, SeriesTrendsJson.Default.SeriesTrendsResponse));
        Assert.Equal("2026-10-11", json.RootElement.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("totals").GetProperty("successRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("totals").GetProperty("p95Ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("streak").GetProperty("outcome").ValueKind);
        Assert.Equal(["days", "from", "seriesId", "streak", "to", "totals", "window"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }
}
