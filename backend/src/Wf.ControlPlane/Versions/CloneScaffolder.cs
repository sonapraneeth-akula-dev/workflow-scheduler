using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Wf.Contracts.Manifest;
using Wf.Contracts.Schemas;
using Wf.ControlPlane.Auth;
using Wf.Validation.SemVer;

namespace Wf.ControlPlane.Versions;

public sealed class UnsupportedCloneSourceException() : Exception(
    "Clone requires one literal Workflow.Define declaration with an expression-bodied fluent builder and literal Type.");

public sealed record VersionCloneRequest(string NewName, string TargetType)
{
    public string TargetWorkflow(string sourceWorkflow)
    {
        var target = TargetType + "/" + NewName;
        if (!WorkflowIdentity.TryGetTypeScope(target, out var scope) || scope != TargetType ||
            string.IsNullOrWhiteSpace(NewName) || NewName.Contains('/') || NewName.Trim() != NewName ||
            TargetType.Trim() != TargetType)
            throw new ArgumentException("Use a workflow leaf name and an exact team/type target scope.");
        if (target == sourceWorkflow) throw new ArgumentException("Clone requires a different workflow identity.");
        return target;
    }
}

public static class CloneScaffolder
{
    public static byte[] Create(string source, WorkflowManifest original, Guid versionId, VersionCloneRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = request.TargetWorkflow(original.Name);
        _ = new ApiBaseline(original.SdkVersion, [], []);
        if (versionId == Guid.Empty ||
            Encoding.UTF8.GetByteCount(source) is <= 0 or > VersionSourceReader.MaximumSourceBytes)
            throw new InvalidDataException("Invalid clone source metadata.");
        var lineage = original.Name + "@" + versionId.ToString("D");
        var script = Rewrite(source, original, target, request.TargetType.Split('/')[1], lineage, cancellationToken);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add(archive, "workflow.cs", script);
            Add(archive, "schedules.json", "{\n  \"$schema\": \"schemas/schedules.schema.json\",\n  \"instances\": []\n}\n");
            Add(archive, "schemas/schedules.schema.json", JsonSerializer.Serialize(ScheduleFileSchema.Export()));
            Add(archive, "WorkflowClone.Tests.csproj", TestProject(original.SdkVersion));
            Add(archive, "Tests/CloneTests.cs", Tests(target, request.TargetType.Split('/')[1], lineage));
            Add(archive, "README.md", """
                Clone scaffold: the selected immutable source is retained; only its declared name, version (1.0.0),
                type and ClonedFrom metadata change. No workflow has been registered or activated.
                Schedules start empty: add explicit instances only after reviewing variables and target type metadata.
                Retained package/project directives and authored tasks remain unchanged; restore their dependencies locally.
                Review trusted source, then run `wf validate .` and `dotnet test WorkflowClone.Tests.csproj`.
                Tests validate/describe trusted source; they never run task bodies. Review target registry requirements before publish.
                """);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static string Rewrite(string source, WorkflowManifest manifest, string target, string type, string lineage,
        CancellationToken token)
    {
        var text = SourceText.From(source);
        var directives = text.Lines.Where(line => line.ToString().StartsWith("#:", StringComparison.Ordinal))
            .Select(line => new TextChange(line.Span, new string(' ', line.Span.Length)));
        var tree = CSharpSyntaxTree.ParseText(text.WithChanges(directives),
            new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: token);
        var root = tree.GetCompilationUnitRoot(token);
        if (tree.GetDiagnostics(token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.GetStructure() is ConditionalDirectiveTriviaSyntax))
            throw new UnsupportedCloneSourceException();
        var declarations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression.ToString() is "Workflow.Define" or "Wf.Sdk.Workflow.Define" or
                "global::Wf.Sdk.Workflow.Define").ToArray();
        if (declarations.Length != 1 || root.Usings.Any(item => item.Alias is not null) ||
            root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(item => item.Identifier.ValueText is "Workflow" or "Wf") ||
            root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(item => item.Identifier.ValueText is "Workflow" or "Wf"))
            throw new UnsupportedCloneSourceException();
        var define = declarations[0];
        var args = define.ArgumentList.Arguments;
        if (define.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax
                { Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax { Parent: GlobalStatementSyntax } } } } ||
            args.Count != 3 || args.Any(argument => argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0) ||
            Literal(args[0].Expression) != manifest.Name || Literal(args[1].Expression) != manifest.Version ||
            args[2].Expression is not SimpleLambdaExpressionSyntax { ExpressionBody: { } body } lambda)
            throw new UnsupportedCloneSourceException();
        var calls = new List<InvocationExpressionSyntax>();
        ExpressionSyntax current = body;
        while (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call)
        {
            calls.Add(call);
            current = access.Expression;
        }
        if (current is not IdentifierNameSyntax identifier || identifier.Identifier.ValueText != lambda.Parameter.Identifier.ValueText)
            throw new UnsupportedCloneSourceException();
        var types = calls.Where(call => ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText == "Type").ToArray();
        if (types.Length != 1 || types[0].ArgumentList.Arguments.Count != 1 ||
            Literal(types[0].ArgumentList.Arguments[0].Expression) != manifest.Metadata.Type)
            throw new UnsupportedCloneSourceException();
        var changes = new List<TextChange>
        {
            Replace(args[0].Expression, target), Replace(args[1].Expression, "1.0.0"),
            Replace(types[0].ArgumentList.Arguments[0].Expression, type)
        };
        var metadata = calls.Where(call => ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText == "Metadata").ToArray();
        if (metadata.Any(call => call.ArgumentList.Arguments.Count != 2 ||
            Literal(call.ArgumentList.Arguments[0].Expression) is null))
            throw new UnsupportedCloneSourceException();
        var previous = metadata.Where(call => Literal(call.ArgumentList.Arguments[0].Expression) == "ClonedFrom").ToArray();
        if (previous.Length > 1) throw new UnsupportedCloneSourceException();
        if (previous.Length == 1)
            changes.Add(Replace(previous[0].ArgumentList.Arguments[1].Expression, lineage));
        else
            changes.Add(new(new(body.Span.End, 0), ".Metadata(\"ClonedFrom\", " + Quote(lineage) + ")"));
        token.ThrowIfCancellationRequested();
        return text.WithChanges(changes).ToString();
    }

    private static string? Literal(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText : null;

    private static string Quote(string text) => SyntaxFactory.Literal(text).Text;
    private static TextChange Replace(ExpressionSyntax expression, string value) => new(expression.Span, Quote(value));

    private static void Add(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string TestProject(string sdkVersion) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
            <IsPackable>false</IsPackable>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
            <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
            <TestingPlatformCaptureOutput>false</TestingPlatformCaptureOutput>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Tests\**\*.cs" />
            <PackageReference Include="Wf.Sdk.Testing" Version="{{sdkVersion}}" />
            <PackageReference Include="xunit.v3" Version="3.2.2" />
            <Using Include="Xunit" />
          </ItemGroup>
        </Project>
        """;

    private static string Tests(string name, string type, string lineage) => $$"""
        using System.Runtime.CompilerServices;
        using Wf.Sdk.Testing;
        using Wf.Validation.Build;

        public sealed class CloneTests
        {
            [Fact]
            public async Task Clone_identity_lineage_and_schedules_are_valid()
            {
                var directory = SourceDirectory();
                var result = await WorkflowValidation.ValidateAsync(directory,
                    new ScriptBuildCache(Path.Combine(directory, "bin", "clone-cache"),
                        Path.Combine(directory, "obj", "clone-artifacts")),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors) +
                    result.BuildFailure?.StandardOutput + result.BuildFailure?.StandardError);
                var manifest = result.Build!.Manifest;
                Assert.Equal({{Quote(name)}}, manifest.Name);
                Assert.Equal("1.0.0", manifest.Version);
                Assert.Equal({{Quote(type)}}, manifest.Metadata.Type);
                Assert.Equal({{Quote(lineage)}}, manifest.Metadata.Values["ClonedFrom"]);
            }

            private static string SourceDirectory([CallerFilePath] string file = "") =>
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));
        }
        """;
}
