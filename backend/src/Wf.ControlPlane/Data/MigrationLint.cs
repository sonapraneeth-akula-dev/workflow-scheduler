using System.Text.RegularExpressions;

namespace Wf.ControlPlane.Data;

public sealed record MigrationIssue(int Line, string Message);

public static partial class MigrationLint
{
    public static IReadOnlyList<MigrationIssue> Validate(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = Tokenize(sql, inspectBodies: true);
        var errors = new List<MigrationIssue>();
        var defaults = new Dictionary<(string Table, string Column), bool>();
        foreach (var token in tokens.Where(token => token.Text is "DROP" or "RENAME" or "TRUNCATE"))
            Error(token, $"{token.Text} is a contract change; ship it in a later contract release.");
        foreach (var statement in tokens.SplitOn(";"))
        {
            if (statement.Count < 4 || statement[0].Text != "ALTER" || statement[1].Text != "TABLE") continue;
            var start = 2;
            while (start < statement.Count && statement[start].Text is "ONLY" or "IF" or "EXISTS") start++;
            var action = start;
            while (action < statement.Count && statement[action].Text is not ("ADD" or "ALTER")) action++;
            var table = string.Concat(statement.Skip(start).Take(action - start).Select(token => token.Text));
            foreach (var clause in statement.Skip(action).ToList().SplitOn(","))
            {
                var columnIndex = clause.Count > 1 && clause[1].Text == "COLUMN" ? 2 : 1;
                if (columnIndex >= clause.Count) continue;
                if (clause[columnIndex].Text == "IF") columnIndex += 3;
                if (columnIndex >= clause.Count || clause[columnIndex].Text is "CONSTRAINT" or "CHECK" or "FOREIGN" or "UNIQUE") continue;
                var key = (table, clause[columnIndex].Text);
                var tail = clause.Skip(columnIndex + 1).ToArray();
                var defaultIndex = Array.FindIndex(tail, token => token.Text == "DEFAULT");
                if (defaultIndex >= 0)
                    defaults[key] = tail.Skip(defaultIndex + 1).FirstOrDefault(token => token.Text != "(")?.Text is not (null or "NULL" or ")");
                for (var index = 0; index + 1 < tail.Length; index++)
                    if (tail[index].Text == "NOT" && tail[index + 1].Text == "NULL" &&
                        !defaults.GetValueOrDefault(key))
                        Error(tail[index], "Adding NOT NULL requires a non-null default for old writers.");
            }
        }
        return errors;

        void Error(SqlToken token, string message) =>
            errors.Add(new(1 + sql.AsSpan(0, token.Start).Count('\n'), message));
    }

    internal static string Unwrap(string sql)
    {
        var tokens = Tokenize(sql);
        if (tokens.Count >= 4 && tokens[0].Text == "BEGIN" && tokens[1].Text == ";" &&
            tokens[^2].Text == "COMMIT" && tokens[^1].Text == ";")
            sql = sql[tokens[1].End..tokens[^2].Start];
        if (Tokenize(sql).Any(token => token.Text is "BEGIN" or "COMMIT" or "ROLLBACK"))
            throw new InvalidDataException("Migration transaction control must be a single outer BEGIN/COMMIT wrapper.");
        return sql;
    }

    private sealed record SqlToken(string Text, int Start, int End);

    private static List<SqlToken> Tokenize(string sql, bool inspectBodies = false)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length;)
        {
            var start = index;
            if (char.IsWhiteSpace(sql[index])) { index++; continue; }
            if (sql.AsSpan(index).StartsWith("--"))
            {
                while (index < sql.Length && sql[index] != '\n') index++;
                continue;
            }
            if (sql.AsSpan(index).StartsWith("/*"))
            {
                var depth = 1;
                index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (sql.AsSpan(index).StartsWith("/*")) { depth++; index += 2; }
                    else if (sql.AsSpan(index).StartsWith("*/")) { depth--; index += 2; }
                    else index++;
                }
                if (depth != 0) throw new InvalidDataException("Unterminated migration comment.");
                continue;
            }
            if (sql[index] is '\'' or '"')
            {
                var quote = sql[index++];
                var escaped = quote == '\'' && start > 0 && sql[start - 1] is 'e' or 'E' &&
                    (start < 2 || !char.IsAsciiLetterOrDigit(sql[start - 2]));
                var closed = false;
                while (index < sql.Length)
                {
                    if (escaped && sql[index] == '\\') { index += Math.Min(2, sql.Length - index); continue; }
                    if (sql[index++] != quote) continue;
                    if (index < sql.Length && sql[index] == quote) { index++; continue; }
                    closed = true;
                    break;
                }
                if (!closed) throw new InvalidDataException("Unterminated migration quoted value.");
                tokens.Add(new(quote == '"' ? sql[start..index] : "\0", start, index));
                continue;
            }
            if (sql[index] == '$' && DollarTag().Match(sql, index) is { Success: true, Index: var matchIndex } tag &&
                matchIndex == index)
            {
                var end = sql.IndexOf(tag.Value, index + tag.Length, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unterminated migration dollar-quoted body.");
                if (inspectBodies && tokens.LastOrDefault()?.Text is "AS" or "DO")
                    tokens.AddRange(Tokenize(sql[(index + tag.Length)..end], inspectBodies: true)
                        .Select(token => token with { Start = token.Start + index + tag.Length, End = token.End + index + tag.Length }));
                index = end + tag.Length;
                tokens.Add(new("\0", start, index));
                continue;
            }
            if (char.IsAsciiLetter(sql[index]) || sql[index] == '_')
            {
                while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_')) index++;
                tokens.Add(new(sql[start..index].ToUpperInvariant(), start, index));
            }
            else tokens.Add(new(sql[index++].ToString(), start, index));
        }
        return tokens;
    }

    private static IEnumerable<List<SqlToken>> SplitOn(this List<SqlToken> tokens, string delimiter)
    {
        var depth = 0;
        var part = new List<SqlToken>();
        foreach (var token in tokens)
        {
            if (token.Text == "(") depth++;
            if (token.Text == ")") depth--;
            if (depth < 0) throw new InvalidDataException("Unbalanced migration parentheses.");
            if (token.Text == delimiter && depth == 0) { yield return part; part = []; }
            else part.Add(token);
        }
        if (depth != 0) throw new InvalidDataException("Unbalanced migration parentheses.");
        if (part.Count > 0) yield return part;
    }

    [GeneratedRegex(@"\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$", RegexOptions.CultureInvariant)]
    private static partial Regex DollarTag();
}
