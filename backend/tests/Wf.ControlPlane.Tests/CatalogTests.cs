using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class CatalogTests
{
    [Theory]
    [InlineData(typeof(CatalogTeam[]))]
    [InlineData(typeof(CatalogType[]))]
    [InlineData(typeof(CatalogWorkflowPage))]
    public void Catalog_response_contracts_export_without_optional_immutable_array_defaults(Type type) =>
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default.GetTypeInfo(type)));

    [Fact]
    public void Default_catalog_page_is_bounded_and_has_no_scope_filter()
    {
        Assert.Equal(new(null, null, null, 50), CatalogQuery.Parse(QueryCollection.Empty));
    }

    [Fact]
    public void Exact_team_type_and_opaque_cursor_preserve_unicode_and_case()
    {
        var team = "Team-\U0001F680";
        var id = team + "/release/Work Flow";
        var query = CatalogQuery.Parse(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["team"] = team, ["type"] = "release", ["limit"] = "100", ["cursor"] = CatalogQuery.Cursor(id)
        }));
        Assert.Equal(new(team, "release", id, 100), query);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("")]
    [InlineData("99999999999999999999")]
    public void Invalid_catalog_limits_fail_explicitly(string value) =>
        Assert.Throws<ArgumentException>(() => Parse("limit", value));

    [Theory]
    [InlineData("team", "")]
    [InlineData("team", "..")]
    [InlineData("team", "other/team")]
    [InlineData("team", "bad\\team")]
    [InlineData("team", "bad\nteam")]
    [InlineData("type", "release")]
    [InlineData("status", "Running")]
    [InlineData("cursor", "")]
    [InlineData("cursor", "not a cursor")]
    public void Invalid_or_unsupported_catalog_filters_are_not_silently_ignored(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse(key, value));

    [Fact]
    public void Catalog_cursor_rejects_invalid_utf8_noncanonical_padding_and_nonworkflow_identity()
    {
        foreach (var cursor in new[]
        {
            WebEncoders.Base64UrlEncode([0xff]),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("team/type/a")),
            CatalogQuery.Cursor("team/type"),
            CatalogQuery.Cursor("team/type/" + new string('a', 129)),
            new string('a', 2052)
        })
            Assert.Throws<ArgumentException>(() => Parse("cursor", cursor));
        Assert.Throws<ArgumentException>(() => CatalogQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["team"] = new(new[] { "a", "b" }) })));
    }

    [Fact]
    public void Maximum_scalar_unicode_identity_fits_catalog_cursor_budget()
    {
        var segment = string.Concat(Enumerable.Repeat("\U0001F680", 128));
        var id = string.Join("/", segment, segment, segment);
        var cursor = CatalogQuery.Cursor(id);
        Assert.Equal(2051, cursor.Length);
        Assert.Equal(id, Parse("cursor", cursor).AfterId);
    }

    private static CatalogQuery Parse(string key, string value) =>
        CatalogQuery.Parse(new QueryCollection(new Dictionary<string, StringValues> { [key] = value }));
}
