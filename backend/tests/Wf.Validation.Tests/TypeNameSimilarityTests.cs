using System.Globalization;

namespace Wf.Validation.Tests;

public sealed class TypeNameSimilarityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("-_!.", "")]
    [InlineData("Load-Test", "loadtest")]
    [InlineData("load_tests", "loadtest")]
    [InlineData("LOAD. TESTS!", "loadtest")]
    [InlineData(" Release 123 ", "release123")]
    [InlineData("S", "s")]
    [InlineData("Boss", "bos")]
    [InlineData("\u00c4Tests", "\u00e4test")]
    public void Normalization_preserves_existing_case_punctuation_and_single_plural_rules(string? name, string expected) =>
        Assert.Equal(expected, TypeNameSimilarity.Normalize(name));

    [Fact]
    public void Case_normalization_is_not_affected_by_Turkish_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("integration", TypeNameSimilarity.Normalize("INTEGRATIONS"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "a", true)]
    [InlineData("", "ab", true)]
    [InlineData("", "abc", false)]
    [InlineData("same", "same", true)]
    [InlineData("abc", "abd", true)]
    [InlineData("abc", "axy", true)]
    [InlineData("abc", "xyz", false)]
    [InlineData("release", "releazeq", true)]
    [InlineData("release", "releaseabc", false)]
    [InlineData("abcdef", "badcef", false)]
    [InlineData("abcdef", "abdcef", true)]
    public void Ordinary_Levenshtein_has_an_inclusive_two_edit_boundary(string left, string right, bool expected)
    {
        Assert.Equal(expected, TypeNameSimilarity.WithinTwoEdits(left, right, Token));
        Assert.Equal(expected, TypeNameSimilarity.WithinTwoEdits(right, left, Token));
    }

    [Fact]
    public void Load_Test_and_load_tests_have_identical_normalized_names() =>
        Assert.Equal(TypeNameSimilarity.Normalize("Load-Test"), TypeNameSimilarity.Normalize("load_tests"));

    [Fact]
    public void Banded_implementation_matches_independent_full_matrix_for_every_short_binary_string()
    {
        var words = new List<string> { "" };
        for (var length = 1; length <= 5; length++)
            for (var bits = 0; bits < 1 << length; bits++)
                words.Add(new string(Enumerable.Range(0, length)
                    .Select(index => (bits & (1 << index)) == 0 ? 'a' : 'b').ToArray()));
        foreach (var left in words)
            foreach (var right in words)
                Assert.Equal(Distance(left, right) <= 2, TypeNameSimilarity.WithinTwoEdits(left, right, Token));
    }

    [Fact]
    public void Long_names_use_the_bounded_band_and_do_not_truncate_the_comparison()
    {
        var prefix = new string('a', 100_000);
        Assert.True(TypeNameSimilarity.WithinTwoEdits(prefix + "bc", prefix + "de", Token));
        Assert.False(TypeNameSimilarity.WithinTwoEdits(prefix + "bcd", prefix + "efg", Token));
    }

    [Fact]
    public void Cancellation_is_observed_even_on_equal_or_length_rejected_inputs()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => TypeNameSimilarity.WithinTwoEdits("abc", "abc", source.Token));
        Assert.Throws<OperationCanceledException>(() => TypeNameSimilarity.WithinTwoEdits("abc", "", source.Token));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void Null_distance_inputs_throw_instead_of_becoming_empty(string? left, string? right) =>
        Assert.Throws<ArgumentNullException>(() => TypeNameSimilarity.WithinTwoEdits(left!, right!, Token));

    private static int Distance(string left, string right)
    {
        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var row = 0; row <= left.Length; row++) matrix[row, 0] = row;
        for (var column = 0; column <= right.Length; column++) matrix[0, column] = column;
        for (var row = 1; row <= left.Length; row++)
            for (var column = 1; column <= right.Length; column++)
                matrix[row, column] = Math.Min(Math.Min(matrix[row - 1, column], matrix[row, column - 1]) + 1,
                    matrix[row - 1, column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
        return matrix[left.Length, right.Length];
    }
}
