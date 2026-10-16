using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class ReportCompareQueryTests
{
    private static readonly Guid Series = Guid.NewGuid();
    private static readonly Guid Left = Guid.CreateVersion7();
    private static readonly Guid Right = Guid.CreateVersion7();

    [Fact]
    public void ReportCompare_preserves_exact_sources_titles_unicode_and_UTF8_boundaries()
    {
        var source = new string('é', 256) + "/" + new string('é', 256);
        var title = new string('é', 256);
        var query = Valid();
        query["sourceKey"] = source;
        query["title"] = title;
        Assert.Equal((Series, Left, Right, source, title), ReportCompareEndpoints.Parse(Series.ToString("D"), new QueryCollection(query)));
        foreach (var value in new[] { " stage.node/Task ", "$post/report", "stage.node/task", "' OR true--", "a/b/c" })
        {
            query["sourceKey"] = value;
            query["title"] = " Résumé\r\n<script>e\u0301 ";
            var parsed = ReportCompareEndpoints.Parse(Series.ToString("D"), new QueryCollection(query));
            Assert.Equal(value, parsed.SourceKey);
            Assert.Equal(" Résumé\r\n<script>e\u0301 ", parsed.Title);
        }
    }

    [Theory]
    [InlineData("leftRunId")]
    [InlineData("rightRunId")]
    [InlineData("sourceKey")]
    [InlineData("title")]
    public void ReportCompare_requires_exactly_one_nonblank_value_for_each_known_key(string key)
    {
        foreach (var value in new StringValues[] { "", " ", "\t", new(new[] { "a", "a" }), new(new[] { "a", "b" }) })
        {
            var query = Valid();
            query[key] = value;
            Invalid(query);
        }
        var missing = Valid();
        missing.Remove(key);
        Invalid(missing);
        foreach (var unknown in new[] { "attempt", "kind", "Title", "LeftRunId", "blob_uri" })
        {
            var query = Valid();
            query[unknown] = "anything";
            Invalid(query);
        }
    }

    [Theory]
    [InlineData("sourceKey", 1025)]
    [InlineData("title", 512)]
    public void ReportCompare_rejects_oversized_invalid_UTF8_and_NUL_without_normalizing(string key, int bound)
    {
        foreach (var value in new[] { new string('a', bound + 1), new string('é', bound / 2 + 1), "\ud800", "a\0b" })
        {
            var query = Valid();
            query[key] = value;
            Invalid(query);
        }
    }

    [Fact]
    public void ReportCompare_series_accepts_any_nonempty_canonical_UUID_but_runs_require_canonical_RFC_UUIDv7()
    {
        foreach (var series in new[] { "not-a-series", Guid.Empty.ToString("D"), Series.ToString("N"), Series.ToString("D").ToUpperInvariant() })
            Assert.ThrowsAny<ArgumentException>(() => ReportCompareEndpoints.Parse(series, new QueryCollection(Valid())));
        foreach (var key in new[] { "leftRunId", "rightRunId" })
        foreach (var run in new[] { "bad", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("D"), Left.ToString("N"),
            Left.ToString("D").ToUpperInvariant(), Left.ToString("D")[..19] + "7" + Left.ToString("D")[20..] })
        {
            var query = Valid();
            query[key] = run;
            Invalid(query);
        }
        var same = Valid();
        same["rightRunId"] = Left.ToString("D");
        Invalid(same);
    }

    private static Dictionary<string, StringValues> Valid() => new()
    {
        ["leftRunId"] = Left.ToString("D"), ["rightRunId"] = Right.ToString("D"),
        ["sourceKey"] = "$post/report", ["title"] = "Summary"
    };

    private static void Invalid(Dictionary<string, StringValues> query) =>
        Assert.ThrowsAny<ArgumentException>(() => ReportCompareEndpoints.Parse(Series.ToString("D"), new QueryCollection(query)));
}
