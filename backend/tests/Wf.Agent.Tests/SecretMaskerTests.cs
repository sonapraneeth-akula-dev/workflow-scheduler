using System.Text.Json;
using Wf.Agent.Secrets;

namespace Wf.Agent.Tests;

public sealed class SecretMaskerTests
{
    [Theory]
    [InlineData("before token after", "before *** after")]
    [InlineData("token token", "*** ***")]
    [InlineData("tokentoken", "***")]
    [InlineData("TOKEN Token", "TOKEN Token")]
    [InlineData("toke", "toke")]
    [InlineData("", "")]
    public void Complete_text_masks_literal_ordinal_occurrences(string text, string expected) =>
        Assert.Equal(expected, new SecretMasker(["token"]).Redact(text));

    [Fact]
    public void Overlapping_and_prefix_values_redact_the_union_without_exposing_suffixes()
    {
        var masker = new SecretMasker(["aba", "bab", "token", "token-long", "aba"]);
        Assert.Equal("*** ***", masker.Redact("abab token-long"));
    }

    [Fact]
    public void Every_possible_chunk_partition_has_identical_safe_output()
    {
        const string text = "xabab:xyz!";
        var masker = new SecretMasker(["aba", "bab", "xyz"]);
        for (var partition = 0; partition < 1 << (text.Length - 1); partition++)
        {
            var stream = masker.CreateStream();
            var result = "";
            var start = 0;
            for (var end = 1; end <= text.Length; end++)
            {
                if (end < text.Length && (partition & (1 << (end - 1))) == 0) continue;
                result += stream.Append(text[start..end]);
                Assert.DoesNotContain("aba", result);
                Assert.DoesNotContain("bab", result);
                Assert.DoesNotContain("xyz", result);
                start = end;
            }
            result += stream.Complete();
            Assert.Equal("x***:***!", result);
        }
    }

    [Fact]
    public void Sensitive_suffix_is_held_until_completed_or_disambiguated()
    {
        var stream = new SecretMasker(["secret"]).CreateStream();
        Assert.Equal("", stream.Append("sec"));
        Assert.Equal("", stream.Append(""));
        Assert.Equal("", stream.Append("ret"));
        Assert.Equal("***", stream.Complete());
        var unfinished = new SecretMasker(["secret"]).CreateStream();
        Assert.Equal("", unfinished.Append("sec"));
        Assert.Equal("sec", unfinished.Complete());
    }

    [Fact]
    public void Shorter_secret_is_not_lost_behind_longer_value_lookahead()
    {
        var stream = new SecretMasker(["ab", "abcdef"]).CreateStream();
        Assert.Equal("", stream.Append("ab!"));
        Assert.Equal("***!", stream.Complete());
    }

    [Fact]
    public void Unicode_newlines_regex_characters_and_whitespace_are_literal_values()
    {
        var secrets = new[] { "\U0001F511\u96EA", "a\r\nb", ".*[x]", " " };
        var text = $"[{string.Join("|", secrets)}]";
        var stream = new SecretMasker(secrets).CreateStream();
        var result = string.Concat(text.Select(character => stream.Append(character.ToString()))) + stream.Complete();
        Assert.Equal("[***|***|***|***]", result);
    }

    [Fact]
    public void Streams_do_not_mix_stdout_stderr_or_other_attempts()
    {
        var masker = new SecretMasker(["secret"]);
        var first = masker.CreateStream();
        var second = masker.CreateStream();
        Assert.Equal("", first.Append("sec"));
        Assert.Equal("", second.Append("ret"));
        Assert.Equal("ret", second.Complete());
        Assert.Equal("", first.Append("ret"));
        Assert.Equal("***", first.Complete());
        Assert.Equal("ret", masker.Redact("ret"));
    }

    [Fact]
    public void Completion_is_idempotent_and_append_after_eof_fails()
    {
        var stream = new SecretMasker(["secret"]).CreateStream();
        Assert.Equal("", stream.Complete());
        Assert.Equal("", stream.Complete());
        Assert.Throws<InvalidOperationException>(() => stream.Append("text"));
    }

    [Fact]
    public void No_resolved_values_is_a_zero_delay_passthrough()
    {
        var masker = new SecretMasker([]);
        Assert.Equal("text", masker.Redact("text"));
        var stream = masker.CreateStream();
        Assert.Equal("a", stream.Append("a"));
        Assert.Equal("b", stream.Append("b"));
        Assert.Equal("", stream.Complete());
    }

    [Theory]
    [InlineData("token")]
    [InlineData("p@ss\n\u96EA")]
    [InlineData(" ")]
    public void Standard_utf8_base64_values_are_masked_even_when_split_one_character_at_a_time(string secret)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));
        var text = $"raw:{secret}|encoded:{encoded}";
        var masker = new SecretMasker([secret]);
        Assert.Equal("raw:***|encoded:***", masker.Redact(text));
        var stream = masker.CreateStream();
        var result = string.Concat(text.Select(character => stream.Append(character.ToString()))) + stream.Complete();
        Assert.Equal("raw:***|encoded:***", result);
    }

    [Fact]
    public void Constructor_copies_values_and_rejects_invalid_configuration_without_echoing_values()
    {
        var values = new[] { "secret" };
        var masker = new SecretMasker(values);
        values[0] = "replacement";
        Assert.Equal("*** replacement", masker.Redact("secret replacement"));
        Assert.Throws<ArgumentNullException>(() => new SecretMasker(null!));
        Assert.Throws<ArgumentException>(() => new SecretMasker([""]));
        Assert.Throws<ArgumentNullException>(() => new SecretMasker([null!]));
        Assert.Throws<ArgumentNullException>(() => masker.Redact(null!));
        Assert.Throws<ArgumentNullException>(() => masker.CreateStream().Append(null!));
    }

    [Fact]
    public void Structured_outputs_redact_decoded_values_keys_and_nested_arrays_and_keep_valid_json()
    {
        using var source = JsonDocument.Parse("""
            {"secret-key":"before se\u0063ret\n after","nested":[{"value":"secret"},7,true,null],"safe":"public"}
            """);
        var result = new SecretMasker(["secret", "secret\n"]).RedactJson(source.RootElement);
        Assert.False(result.TryGetProperty("secret-key", out _));
        Assert.Equal("before *** after", result.GetProperty("***-key").GetString());
        Assert.Equal("***", result.GetProperty("nested")[0].GetProperty("value").GetString());
        Assert.Equal(7, result.GetProperty("nested")[1].GetInt32());
        Assert.True(result.GetProperty("nested")[2].GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nested")[3].ValueKind);
        Assert.Equal("public", result.GetProperty("safe").GetString());
        Assert.DoesNotContain("secret", result.GetRawText());
        Assert.True(source.RootElement.TryGetProperty("secret-key", out _));
    }

    [Fact]
    public void Secret_bearing_non_string_scalars_become_strings_rather_than_invalid_json()
    {
        using var source = JsonDocument.Parse("[12345,true,false,null,42]");
        var result = new SecretMasker(["234", "true", "false", "null"]).RedactJson(source.RootElement);
        Assert.Equal("1***5", result[0].GetString());
        Assert.Equal("***", result[1].GetString());
        Assert.Equal("***", result[2].GetString());
        Assert.Equal("***", result[3].GetString());
        Assert.Equal(42, result[4].GetInt32());
    }

    [Fact]
    public void Masked_key_collisions_fail_explicitly_instead_of_losing_output_fields()
    {
        using var source = JsonDocument.Parse("""{"first-secret":1,"second-secret":2}""");
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SecretMasker(["first", "second"]).RedactJson(source.RootElement));
        Assert.DoesNotContain("first", error.Message);
        Assert.DoesNotContain("second", error.Message);
        Assert.Throws<ArgumentException>(() => new SecretMasker([]).RedactJson(default));
    }
}
