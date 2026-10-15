using System.Text;

namespace Wf.Validation;

public static class TypeNameSimilarity
{
    public static string Normalize(string? name)
    {
        var normalized = new StringBuilder();
        foreach (var character in name ?? "")
            if (char.IsLetterOrDigit(character))
                normalized.Append(char.ToLowerInvariant(character));
        if (normalized.Length > 1 && normalized[^1] == 's')
            normalized.Length--;
        return normalized.ToString();
    }

    // Banded Levenshtein on already-normalized names; transposition counts as two edits.
    public static bool WithinTwoEdits(string left, string right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        const int limit = 2;
        if (Math.Abs(left.Length - right.Length) > limit)
            return false;
        if (left.Length == 0 || right.Length == 0)
            return true;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        Array.Fill(previous, limit + 1);
        for (var column = 0; column <= Math.Min(right.Length, limit); column++)
            previous[column] = column;
        for (var row = 1; row <= left.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current[0] = row;
            var start = Math.Max(1, row - limit);
            var end = Math.Min(right.Length, row + limit);
            if (start > 1)
                current[start - 1] = limit + 1;
            var minimum = limit + 1;
            for (var column = start; column <= end; column++)
            {
                current[column] = Math.Min(Math.Min(current[column - 1], previous[column]) + 1,
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                minimum = Math.Min(minimum, current[column]);
            }
            if (minimum > limit)
                return false;
            if (end < right.Length)
                current[end + 1] = limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[right.Length] <= limit;
    }
}
