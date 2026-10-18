using System.IO.Compression;
using System.Collections.Immutable;
using System.Text;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Versions;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Tests;

public sealed class CloneScaffolderTests
{
    private static readonly Guid Version = Guid.Parse("018f15f0-2e46-7000-8000-000000000001");
    private static readonly WorkflowManifest Manifest = new("1.0.0", "team/release/original", "2.3.4",
        new("release", ImmutableSortedDictionary<string, string>.Empty), [], [], [], TimeSpan.FromHours(24));

    private const string Body = """"
        .Node("work", n => n.Task("echo", ctx => {
            // Workflow.Define("team/release/original", "2.3.4", w => w.Type("release"))
            var text = """
        team/release/original release Workflow.Define("fake", "fake", w => w.Type("fake"))
        """;
            ctx.Outputs["answer"] = text + 42;
        }))
        """";

    private static string Script(string builder = "w.Type(\"release\")", string name = "\"team/release/original\"",
        string version = "\"2.3.4\"") => $$"""
        #:package Wf.Sdk@1.0.0
        #:property PublishAot=false
        using Wf.Sdk;
        var workflow = Workflow.Define({{name}}, {{version}}, w => {{builder}}
            {{Body}});
        return await workflow.RunAsync(args);
        """;

    [Fact]
    public void Clone_changes_only_declaration_literals_and_adds_meaningful_metadata_preserving_authored_bodies()
    {
        var original = Script();
        var bytes = Create(original, new("copy", "other/analysis"));
        var files = Files(bytes);
        Assert.Equal(["workflow.cs", "schedules.json", "schemas/schedules.schema.json",
            "WorkflowClone.Tests.csproj", "Tests/CloneTests.cs", "README.md"], files.Keys);
        const string declaration = "Workflow.Define(\"team/release/original\", \"2.3.4\", w => w.Type(\"release\")";
        var start = original.IndexOf(declaration, StringComparison.Ordinal);
        var expected = original.Remove(start, declaration.Length).Insert(start,
            "Workflow.Define(\"other/analysis/copy\", \"1.0.0\", w => w.Type(\"analysis\")");
        // Change only the builder's outer closing span, not matching text in comments, strings or task bodies.
        var end = expected.LastIndexOf(");", expected.IndexOf("return", StringComparison.Ordinal), StringComparison.Ordinal);
        expected = expected.Insert(end, ".Metadata(\"ClonedFrom\", \"team/release/original@" + Version + "\")");
        Assert.Equal(expected, files["workflow.cs"]);
        Assert.Contains(Body, files["workflow.cs"]);
        Assert.Contains("Assert.Equal(\"other/analysis/copy\", manifest.Name)", files["Tests/CloneTests.cs"]);
        Assert.Contains("manifest.Metadata.Values[\"ClonedFrom\"]", files["Tests/CloneTests.cs"]);
        var schedules = ScheduleFileValidator.Validate(Encoding.UTF8.GetBytes(files["schedules.json"]), [],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(schedules.IsValid, string.Join('\n', schedules.Errors));
        Assert.Empty(schedules.Instances);
        Assert.Equal(bytes, Create(original, new("copy", "other/analysis")));
    }

    [Fact]
    public void Clone_replaces_previous_lineage_once_and_quotes_new_identity_without_interpolation_or_injection()
    {
        var files = Files(Create(Script("w.Type(\"release\").Metadata(\"ClonedFrom\", \"old/old/old@old\")"),
            new("copy\";λ", "other/analysis")));
        Assert.Contains("Workflow.Define(\"other/analysis/copy\\\";λ\", \"1.0.0\"", files["workflow.cs"]);
        Assert.DoesNotContain("old/old/old@old", files["workflow.cs"]);
        Assert.Single(files["workflow.cs"].Split(".Metadata(\"ClonedFrom\",").Skip(1));
        Assert.Contains("team/release/original@" + Version, files["workflow.cs"]);
    }

    [Theory]
    [InlineData("w.Type(kind)")]
    [InlineData("w.Type(\"different\")")]
    [InlineData("w.Type(\"release\").Type(\"release\")")]
    [InlineData("Configure(w)")]
    [InlineData("w.Metadata(key, \"value\").Type(\"release\")")]
    [InlineData("w.Type(\"release\").Metadata(\"ClonedFrom\", \"a\").Metadata(\"ClonedFrom\", \"b\")")]
    public void Clone_rejects_unsupported_builder_without_silently_changing_a_different_declaration(string builder) =>
        Assert.Throws<UnsupportedCloneSourceException>(() => Create(Script(builder), new("copy", "team/release")));

    [Theory]
    [InlineData("name", "\"2.3.4\"")]
    [InlineData("\"other/release/wrong\"", "\"2.3.4\"")]
    [InlineData("\"team/release/original\"", "version")]
    [InlineData("\"team/release/original\"", "\"9.0.0\"")]
    public void Clone_requires_exact_literal_source_identity_and_version(string name, string version) =>
        Assert.Throws<UnsupportedCloneSourceException>(() => Create(Script(name: name, version: version), new("copy", "team/release")));

    [Theory]
    [InlineData("")]
    [InlineData("var workflow =")]
    [InlineData("var workflow = Other.Define(\"team/release/original\", \"2.3.4\", w => w.Type(\"release\"));")]
    [InlineData("using Workflow = Other.Workflow;\nvar workflow = Workflow.Define(\"team/release/original\", \"2.3.4\", w => w.Type(\"release\"));")]
    [InlineData("var workflow = Workflow.Define(\"team/release/original\", \"2.3.4\", w => { w.Type(\"release\"); });")]
    [InlineData("#if DEBUG\nvar workflow = Workflow.Define(\"team/release/original\", \"2.3.4\", w => w.Type(\"release\"));\n#endif")]
    public void Clone_malformed_or_ambiguous_source_is_explicit(string source)
    {
        if (source.Length == 0)
            Assert.Throws<InvalidDataException>(() => Create(source, new("copy", "team/release")));
        else
            Assert.Throws<UnsupportedCloneSourceException>(() => Create(source, new("copy", "team/release")));
    }

    [Theory]
    [InlineData("original", "team/release")]
    [InlineData("", "team/release")]
    [InlineData("../copy", "team/release")]
    [InlineData("copy", "release")]
    [InlineData("copy", "team/release/other")]
    [InlineData("copy", "team/..")]
    [InlineData("copy", "team\\other/release")]
    [InlineData("copy", "team/release\n")]
    [InlineData(" copy", "team/release")]
    public void Clone_validates_target_identity_without_filename_or_scope_escape(string name, string type) =>
        Assert.Throws<ArgumentException>(() => Create(Script(), new(name, type)));

    [Fact]
    public void Clone_bounds_source_and_propagates_cancellation()
    {
        Assert.Throws<InvalidDataException>(() => Create(new('a', VersionSourceReader.MaximumSourceBytes + 1), new("copy", "team/release")));
        Assert.Throws<OperationCanceledException>(() => CloneScaffolder.Create(Script(), Manifest, Version,
            new("copy", "team/release"), new(true)));
    }

    private static byte[] Create(string source, VersionCloneRequest request) =>
        CloneScaffolder.Create(source, Manifest, Version, request, TestContext.Current.CancellationToken);

    private static Dictionary<string, string> Files(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var zip = new ZipArchive(input);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }, StringComparer.Ordinal);
    }
}
