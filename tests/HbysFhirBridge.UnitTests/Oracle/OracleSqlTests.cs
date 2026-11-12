using HbysFhirBridge.Oracle;

namespace HbysFhirBridge.UnitTests.Oracle;

public sealed class OracleSqlTests
{
    [Theory]
    [InlineData("hbys", "HBYS")]
    [InlineData("HBYS_BRIDGE", "HBYS_BRIDGE")]
    public void Plain_identifiers_are_accepted_and_upper_cased(string input, string expected) =>
        OracleSql.Identifier(input).Should().Be(expected);

    [Theory]
    [InlineData("hbys; DROP TABLE hasta")]
    [InlineData("\"HBYS\"")]
    [InlineData("1hbys")]
    [InlineData("hbys.hasta")]
    [InlineData("a_name_that_is_far_too_long_for_oracle")]
    public void Anything_else_is_refused(string input)
    {
        var act = () => OracleSql.Identifier(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Scripts_split_on_slash_lines_and_drop_comment_only_chunks()
    {
        const string script = "-- header\nCREATE TABLE a (x NUMBER)\n/\n-- only a comment\n/\nBEGIN\n  NULL;\nEND;\n/\n";
        OracleSql.SplitScript(script).Should().Equal("-- header\nCREATE TABLE a (x NUMBER)", "BEGIN\n  NULL;\nEND;");
    }

    [Fact]
    public void Embedded_schema_scripts_parse_into_the_expected_statements()
    {
        var schema = OracleSql.SplitScript(OracleSchemaInstaller.ReadScript("01-hbys-schema.sql")).ToList();
        schema.Count(s => s.Contains("CREATE TABLE", StringComparison.Ordinal)).Should().Be(4);
        schema.Count(s => s.Contains("CREATE OR REPLACE TRIGGER", StringComparison.Ordinal)).Should().Be(6);

        var grants = OracleSql.SplitScript(OracleSchemaInstaller.ReadScript("02-hbys-grants.sql")).ToList();
        grants.Should().HaveCount(4).And.OnlyContain(g => g.Contains("GRANT SELECT", StringComparison.Ordinal));
        grants.Should().NotContain(g => g.Contains("UPDATE", StringComparison.Ordinal) || g.Contains("INSERT", StringComparison.Ordinal),
            "the bridge login must never be able to change clinical data or forge journal entries");
    }
}
