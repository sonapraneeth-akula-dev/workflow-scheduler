namespace Wf.ControlPlane.Storage;

internal static class BlobRetention
{
    internal static readonly string[] RunPrefixes = ["logs", "resources", "checkpoints", "reports", "perf", "parity"];

    internal static bool IsRun(string value, out Guid run) =>
        Guid.TryParseExact(value, "D", out run) && run.Version == 7 && value == run.ToString("D");

    internal static void ValidateRun(Guid run)
    {
        if (run.Version != 7) throw new ArgumentException("Run retention requires a version-7 run.");
    }

    internal static void ValidateArtifact(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            throw new ArgumentException("Artifact retention requires a canonical SHA-256 digest.");
    }
}
