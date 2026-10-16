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

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Catalog_presentation_defaults_are_explicitly_opt_in(string value, bool expected) =>
        Assert.Equal(expected, Parse("defaults", value).UsePresentationDefaults);

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
    [InlineData("defaults", "True")]
    [InlineData("defaults", "1")]
    [InlineData("defaults", "")]
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
        Assert.Throws<ArgumentException>(() => CatalogQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["defaults"] = new(new[] { "true", "false" }) })));
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

    [Fact]
    public void Catalog_metadata_filters_preserve_exact_unicode_empty_values_and_case()
    {
        var query = CatalogQuery.Parse(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["meta.Environment"] = "dev", ["meta.component"] = "\U0001F680", ["meta.optional"] = ""
        }));
        Assert.Equal(3, query.Metadata.Count);
        Assert.Equal("dev", query.Metadata["Environment"]);
        Assert.Equal("\U0001F680", query.Metadata["component"]);
        Assert.Equal("", query.Metadata["optional"]);
    }

    [Theory]
    [InlineData("meta.", "dev")]
    [InlineData("meta. ", "dev")]
    [InlineData("meta.bad\nkey", "dev")]
    [InlineData("meta.environment", "bad\nvalue")]
    [InlineData("Meta.environment", "dev")]
    public void Invalid_catalog_metadata_filters_fail_explicitly(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse(key, value));

    [Fact]
    public void Catalog_metadata_filters_are_bounded_and_reject_duplicate_values()
    {
        Assert.Throws<ArgumentException>(() => Parse("meta." + new string('a', 129), "dev"));
        Assert.Throws<ArgumentException>(() => Parse("meta.environment", new string('a', 1025)));
        Assert.Throws<ArgumentException>(() => CatalogQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["meta.environment"] = new(new[] { "dev", "ppe" }) })));
        var maximum = Enumerable.Range(0, 32).ToDictionary(index => "meta.key" + index,
            _ => new StringValues("dev"), StringComparer.Ordinal);
        Assert.Equal(32, CatalogQuery.Parse(new QueryCollection(maximum)).Metadata.Count);
        maximum.Add("meta.one-too-many", "dev");
        Assert.Throws<ArgumentException>(() => CatalogQuery.Parse(new QueryCollection(maximum)));
        var unicode = string.Concat(Enumerable.Repeat("\U0001F680", 128));
        Assert.True(Parse("meta." + unicode, "").Metadata.ContainsKey(unicode));
    }

    private static CatalogQuery Parse(string key, string value) =>
        CatalogQuery.Parse(new QueryCollection(new Dictionary<string, StringValues> { [key] = value }));
}
