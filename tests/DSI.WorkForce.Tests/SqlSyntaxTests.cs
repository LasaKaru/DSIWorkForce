using System.Text.RegularExpressions;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Data;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit.Abstractions;

namespace DSI.WorkForce.Tests;

/// <summary>
/// Parses every piece of T-SQL in the solution with Microsoft's own T-SQL parser (ScriptDom, as used by SSMS / SqlPackage),
/// so syntax errors are caught in CI without needing a SQL Server instance.
/// </summary>
public partial class SqlSyntaxTests(ITestOutputHelper output)
{
    private static IList<ParseError> Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        return errors;
    }

    private void AssertParses(string name, string sql)
    {
        var errors = Parse(sql);
        foreach (var e in errors) output.WriteLine($"{name} line {e.Line}: {e.Message}");
        Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}"))}");
    }

    [Fact]
    public void Migration_scripts_are_valid_tsql()
    {
        var asm = typeof(DatabaseMigrator).Assembly;
        var scripts = asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order().ToList();
        Assert.True(scripts.Count >= 7);
        foreach (var name in scripts)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            AssertParses(name, new StreamReader(s).ReadToEnd());
        }
    }

    [Fact]
    public void Generated_report_views_and_functions_are_valid_tsql()
    {
        Assert.NotEmpty(Parse("SELECT FROM WHERE"));   // the parser really rejects bad SQL
        foreach (var code in TestData.ReportCodes())
        {
            var sql = ReportViewSqlBuilder.Build(TestData.Report(code), TestData.FieldMap(TestData.PressTemplate()));
            output.WriteLine(sql.ViewSql);
            AssertParses(code + " view", sql.ViewSql);
            AssertParses(code + " function", sql.FunctionSql);
        }
    }

    [Fact]
    public void Repository_queries_are_valid_tsql()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DSI.WorkForce.Data", "Repositories"));
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var snippets = RawLiteral().Matches(code).Select(m => m.Groups[1].Value)
                .Concat(SimpleLiteral().Matches(code).Select(m => m.Groups[1].Value));
            foreach (var sql in snippets)
            {
                // Interpolation holes ({fn}, [{col}]) are always identifiers in this code base.
                var cleaned = Hole().Replace(sql, "x");
                AssertParses($"{Path.GetFileName(file)}: {sql.Trim()[..Math.Min(60, sql.Trim().Length)]}", cleaned);
                count++;
            }
        }
        output.WriteLine($"{count} SQL statements parsed");
        Assert.True(count > 25);
    }

    [GeneratedRegex("\"\"\"(.*?)\"\"\"", RegexOptions.Singleline)] private static partial Regex RawLiteral();
    [GeneratedRegex("\\$?\"((?:SELECT|UPDATE|INSERT|DELETE|IF) [^\"]+)\"")] private static partial Regex SimpleLiteral();
    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_.()]*\}")] private static partial Regex Hole();
}
