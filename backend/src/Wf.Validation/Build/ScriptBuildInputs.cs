using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wf.Validation.Build;

internal static class ScriptBuildInputs
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", "node_modules" };
    private static readonly string[] AncestorFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
            "NuGet.Config", "nuget.config", ".editorconfig", ".globalconfig", "Directory.Build.rsp"];

    internal static async Task<string> HashAsync(ScriptBuildRequest request, string sdk, string dotnet,
        string cacheDirectory, string artifactsDirectory, CancellationToken cancellationToken)
    {
        var script = Path.GetFullPath(request.ScriptPath);
        var root = Path.GetFullPath(request.SourceRoot);
        if (!File.Exists(script))
            throw new FileNotFoundException("Workflow script does not exist.", script);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        if (!Inside(script, root) || !script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ScriptPath must be a .cs file inside SourceRoot.", nameof(request));
        if (Inside(root, cacheDirectory) || Inside(root, artifactsDirectory))
            throw new ArgumentException("SourceRoot cannot be inside cache or build artifacts.", nameof(request));

        var inputs = new SortedSet<string>(StringComparer.Ordinal) { script };
        AddTree(root);
        AddAncestors(Path.GetDirectoryName(script)!);
        foreach (var input in request.AdditionalInputs ?? [])
        {
            var path = Path.GetFullPath(input);
            if (Directory.Exists(path))
                AddTree(path);
            else if (File.Exists(path))
                inputs.Add(path);
            else
                throw new FileNotFoundException("Additional build input does not exist.", path);
            AddAncestors(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add("wf-script-cache-v1");
        Add(script);
        Add(dotnet);
        Add(sdk);
        Add(RuntimeInformation.OSDescription);
        Add(RuntimeInformation.OSArchitecture.ToString());
        Add(RuntimeInformation.ProcessArchitecture.ToString());
        Add(ScriptBuildCache.BuildSettings);
        // MSBuild can read arbitrary environment properties. Hash values; never store their plaintext.
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                     .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal))
        {
            Add((string)variable.Key);
            Add((string?)variable.Value ?? "");
        }
        foreach (var path in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(path);
            await using var stream = File.OpenRead(path);
            hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        void AddTree(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Inside(directory, cacheDirectory) || Inside(directory, artifactsDirectory))
                return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links/reparse points are not supported build inputs: {directory}");
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links/reparse points are not supported build inputs: {file}");
                inputs.Add(Path.GetFullPath(file));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
                    AddTree(child);
            AddAncestors(directory);
        }

        void AddAncestors(string directory)
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
                foreach (var name in AncestorFiles)
                {
                    var file = Path.Combine(current.FullName, name);
                    if (File.Exists(file))
                        inputs.Add(file);
                }
        }
    }

    internal static bool Inside(string path, string directory) =>
        string.Equals(path, directory, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
