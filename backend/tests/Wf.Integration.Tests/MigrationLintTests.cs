using Wf.ControlPlane.Data;

namespace Wf.Integration.Tests;

public sealed class MigrationLintTests
{
    [Fact]
    public void Every_embedded_migration_matches_the_checked_in_script_and_is_expand_only()
    {
        var scripts = Migrator.LoadScripts();
        Assert.NotEmpty(scripts);
        Assert.Equal(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Migrations"), "*.sql").Length, scripts.Count);
        foreach (var script in scripts)
        {
            Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", script.Name)), script.Contents);
            Assert.Empty(MigrationLint.Validate(script.Contents));
        }
    }

    [Theory]
    [InlineData("ALTER TABLE runs DROP COLUMN status;")]
    [InlineData("drop table runs cascade;")]
    [InlineData("ALTER TABLE runs ReNaMe COLUMN status TO state;")]
    [InlineData("TRUNCATE runs;")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text NOT NULL;")]
    [InlineData("ALTER TABLE runs ADD owner text NOT /* comment */ NULL;")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text DEFAULT NULL NOT NULL;")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text DEFAULT (NULL) NOT NULL;")]
    [InlineData("ALTER TABLE runs ADD a int DEFAULT 0 NOT NULL, ADD b int NOT NULL;")]
    [InlineData("ALTER TABLE runs ALTER COLUMN owner SET NOT NULL;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN DROP TABLE runs; END $$;")]
    [InlineData("DO $$ BEGIN DROP TABLE runs; END $$;")]
    public void Contract_changes_fail_with_a_source_line(string sql)
    {
        var issues = MigrationLint.Validate("-- header\n" + sql);
        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.Equal(2, issue.Line));
    }

    [Theory]
    [InlineData("CREATE TABLE runs (id uuid NOT NULL, owner text NOT NULL);")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text;")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text DEFAULT 'unknown' NOT NULL;")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text NOT NULL DEFAULT 'unknown';")]
    [InlineData("ALTER TABLE runs ADD COLUMN owner text DEFAULT 'unknown'; ALTER TABLE runs ALTER COLUMN owner SET NOT NULL;")]
    [InlineData("ALTER TABLE runs ADD a int NOT NULL DEFAULT 0, ADD b text;")]
    [InlineData("ALTER TABLE runs ADD a text NOT NULL DEFAULT concat('one','two'), ADD b text;")]
    [InlineData("""ALTER TABLE "DROP" ADD COLUMN "RENAME" text;""")]
    [InlineData("-- DROP TABLE runs;\n/* outer /* RENAME */ ignored */ SELECT 'DROP TABLE';")]
    [InlineData("CREATE FUNCTION f() RETURNS text LANGUAGE sql AS $body$ SELECT 'DROP' $body$;")]
    [InlineData("SELECT E'escaped\\' DROP TABLE runs';")]
    [InlineData("SELECT $$ DROP TABLE runs $$;")]
    public void Expand_changes_and_quoted_or_commented_words_are_not_false_positives(string sql) =>
        Assert.Empty(MigrationLint.Validate(sql));

    [Theory]
    [InlineData("/* unterminated")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT \"unterminated")]
    [InlineData("CREATE FUNCTION f() AS $body$ unterminated")]
    [InlineData("SELECT (1;")]
    public void Malformed_lexical_input_does_not_silently_pass(string sql) =>
        Assert.Throws<InvalidDataException>(() => MigrationLint.Validate(sql));
}
