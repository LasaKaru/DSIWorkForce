using System.Text;
using System.Text.RegularExpressions;
using DSI.WorkForce.Core.Model;

namespace DSI.WorkForce.Core.Reporting;

/// <summary>Field metadata the builder needs: database id + type (to pick ValueNumber / ValueText / ...).</summary>
public sealed record FieldInfo(int FieldId, string FieldKey, FieldDataType DataType);

/// <summary>
/// Generates "CREATE OR ALTER VIEW rpt.vw_X" from a <see cref="ReportDefinition"/>.
/// This is what makes the Excel layout configurable: add a row to rpt.ReportColumn, rebuild, done.
/// Everything that ends up in SQL is either whitelisted, an integer, or a bracket-escaped identifier,
/// so admin-entered metadata cannot inject SQL.
/// </summary>
public sealed record ReportSql(string ViewSql, string FunctionSql);

public static partial class ReportViewSqlBuilder
{
    private static readonly Dictionary<string, string> HeaderColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WorksheetId"] = "w.WorksheetId",
        ["WorkDate"] = "w.WorkDate",
        ["Shift"] = "w.Shift",
        ["ResourceNo"] = "w.ResourceNo",
        ["EpfNo"] = "w.EpfNo",
        ["ItemCode"] = "w.ItemCode",
        ["Status"] = "w.Status",
        ["TemplateCode"] = "w.TemplateCode",
        ["PageNo"] = "w.PageNo",
        ["MinConfidence"] = "w.MinConfidence",
        ["CreatedAt"] = "w.CreatedAt",
        ["VerifiedBy"] = "w.VerifiedBy",
        ["VerifiedAt"] = "w.VerifiedAt",
        ["SourceFile"] = "sf.FileName",
    };

    /// <summary>Lookup -> (join alias, SQL expression). Joins are only emitted when used.</summary>
    private static readonly Dictionary<string, (string Alias, string Sql)> Lookups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Item.Description"] = ("it", "it.Description"),
        ["Item.StandardRatePerHour"] = ("it", "it.StandardRatePerHour"),
        ["Item.Category"] = ("it", "it.Category"),
        ["Employee.Name"] = ("e", "e.Name"),
        ["Employee.Section"] = ("e", "e.Section"),
        ["Machine.Name"] = ("m", "m.Name"),
        ["Machine.Section"] = ("m", "m.Section"),
        ["Machine.UnitsPerTick"] = ("m", "m.UnitsPerTick"),
        ["Shift.StartTime"] = ("sh", "sh.StartTime"),
        ["Shift.EndTime"] = ("sh", "sh.EndTime"),
        ["Shift.Hours"] = ("sh", "sh.Hours"),
    };

    private static readonly Dictionary<string, string> Joins = new()
    {
        ["sf"] = "LEFT JOIN dsi.ScanFile sf ON sf.ScanFileId = w.ScanFileId",
        ["it"] = "LEFT JOIN dsi.Item it ON it.ItemCode = w.ItemCode",
        ["e"] = "LEFT JOIN dsi.Employee e ON e.EpfNo = w.EpfNo",
        ["m"] = "LEFT JOIN dsi.Machine m ON m.ResourceNo = w.ResourceNo",
        ["sh"] = "LEFT JOIN dsi.ShiftMaster sh ON sh.ShiftCode = w.Shift",
    };

    private static readonly HashSet<string> AllowedExpressionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULLIF", "ISNULL", "COALESCE", "ROUND", "ABS", "CEILING", "FLOOR", "CASE", "WHEN", "THEN", "ELSE", "END",
        "AND", "OR", "NOT", "NULL", "IS", "CAST", "AS", "DECIMAL", "INT", "FLOAT",
    };

    public static IReadOnlyCollection<string> AvailableHeaderColumns => HeaderColumns.Keys;
    public static IReadOnlyCollection<string> AvailableLookups => Lookups.Keys;

    public static IReadOnlyList<string> Validate(ReportDefinition report, IReadOnlyDictionary<string, FieldInfo> fields)
    {
        var errors = new List<string>();
        if (!IdentifierRegex().IsMatch(report.Code)) errors.Add($"Report code '{report.Code}' must be letters/digits/underscore.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in report.Columns)
        {
            var label = $"Column '{c.Header}'";
            if (!IdentifierRegex().IsMatch(c.Key)) errors.Add($"{label}: key '{c.Key}' must be letters/digits/underscore.");
            else if (!keys.Add(c.Key)) errors.Add($"{label}: duplicate key '{c.Key}'.");
            if (string.IsNullOrWhiteSpace(c.Header) || c.Header.Length > 128) errors.Add($"{label}: header is required (max 128 chars).");
            else if (c.Visible && !headers.Add(c.Header)) errors.Add($"{label}: duplicate header.");
            if (string.Equals(c.Header, SheetIdColumn, StringComparison.OrdinalIgnoreCase)) errors.Add($"{label}: '{SheetIdColumn}' is reserved.");

            switch (c.SourceType)
            {
                case ReportSourceType.Header when c.Source is null || !HeaderColumns.ContainsKey(c.Source):
                    errors.Add($"{label}: header source must be one of {string.Join(", ", HeaderColumns.Keys)}."); break;
                case ReportSourceType.Lookup when c.Source is null || !Lookups.ContainsKey(c.Source):
                    errors.Add($"{label}: lookup must be one of {string.Join(", ", Lookups.Keys)}."); break;
                case ReportSourceType.Field or ReportSourceType.FieldAggregate when c.Source is null || !fields.ContainsKey(c.Source):
                    errors.Add($"{label}: unknown field '{c.Source}'."); break;
                case ReportSourceType.FieldAggregate when c.Aggregate is null || !new[] { "SUM", "MIN", "MAX", "AVG", "COUNT", "LIST" }.Contains(c.Aggregate.ToUpperInvariant()):
                    errors.Add($"{label}: aggregate must be SUM, MIN, MAX, AVG, COUNT or LIST."); break;
                case ReportSourceType.Field when c.RowIndex is < 0 or > 255:
                    errors.Add($"{label}: rowIndex must be 0..255."); break;
                case ReportSourceType.Expression:
                    errors.AddRange(ValidateExpression(c, report).Select(e => $"{label}: {e}")); break;
            }
        }
        return errors;
    }

    /// <summary>Name of the always-present last column linking a report row back to its scanned sheet.</summary>
    public const string SheetIdColumn = "Sheet ID";

    /// <summary>
    /// Builds the view (for SSMS / Excel Power Query / Power BI) and an inline table-valued function
    /// rpt.fn_&lt;Code&gt;(@From, @To) used by the apps, which seeks the WorkDate index instead of scanning.
    /// </summary>
    public static ReportSql Build(ReportDefinition report, IReadOnlyDictionary<string, FieldInfo> fields)
    {
        var errors = Validate(report, fields);
        if (errors.Count > 0) throw new InvalidOperationException("Report definition is invalid:\n" + string.Join("\n", errors));

        var header = $"-- Generated by DSI.WorkForce for report {report.Code}. Do not edit by hand:\n" +
                     "-- change rpt.ReportColumn (Admin > Report Columns) and rebuild instead.\n";
        var view = header + $"CREATE OR ALTER VIEW rpt.{Q(report.ViewName)}\nAS\n" + BuildSelect(report, fields, dateFiltered: false);
        var fn = header + $"CREATE OR ALTER FUNCTION rpt.{Q(report.FunctionName)} (@From DATE, @To DATE)\nRETURNS TABLE\nAS\nRETURN\n" +
                 BuildSelect(report, fields, dateFiltered: true);
        return new ReportSql(view, fn);
    }

    private static string BuildSelect(ReportDefinition report, IReadOnlyDictionary<string, FieldInfo> fields, bool dateFiltered)
    {
        var cols = report.Columns.OrderBy(c => c.Order).ToList();
        var usedJoins = new SortedSet<string>(StringComparer.Ordinal);
        var groupBy = new List<string> { "w.WorksheetId" };
        var baseSelect = new List<string> { "w.WorksheetId AS [__WorksheetId]" };

        foreach (var c in cols.Where(c => c.SourceType != ReportSourceType.Expression))
        {
            string sql;
            switch (c.SourceType)
            {
                case ReportSourceType.Header:
                    // Grouping on the raw column (no MAX) lets SQL Server push filters such as
                    // WHERE [Date] = '2026-11-17' down to the index on dsi.Worksheet.
                    sql = HeaderColumns[c.Source!];
                    if (sql.StartsWith("sf.", StringComparison.Ordinal)) usedJoins.Add("sf");
                    if (!groupBy.Contains(sql)) groupBy.Add(sql);
                    break;
                case ReportSourceType.Lookup:
                    sql = LookupExpr(c.Source!, usedJoins);
                    break;
                case ReportSourceType.Field:
                    sql = FieldExpr(fields[c.Source!], c.RowIndex ?? 0);
                    break;
                default:
                    sql = AggregateExpr(fields[c.Source!], c.Aggregate!.ToUpperInvariant());
                    break;
            }
            baseSelect.Add($"{sql} AS {Q(c.Key)}");
        }

        var finalSelect = cols.Where(c => c.Visible).Select(c => c.SourceType == ReportSourceType.Expression
            ? $"{RewriteExpression(c.Expression!)} AS {Q(c.Header)}"
            : $"b.{Q(c.Key)} AS {Q(c.Header)}").ToList();
        finalSelect.Add($"b.[__WorksheetId] AS {Q(SheetIdColumn)}");

        var where = new List<string>
        {
            report.OnlyApproved ? "w.Status IN (N'AutoApproved', N'Verified')" : "w.Status <> N'Rejected'",
        };
        if (dateFiltered) where.Add("w.WorkDate BETWEEN @From AND @To");
        if (report.TemplateCodes.Count > 0)
            where.Add($"w.TemplateCode IN ({string.Join(", ", report.TemplateCodes.Select(Lit))})");

        var sb = new StringBuilder();
        sb.AppendLine("WITH b AS (");
        sb.AppendLine("    SELECT");
        sb.AppendLine("        " + string.Join(",\n        ", baseSelect));
        sb.AppendLine("    FROM dsi.Worksheet w");
        sb.AppendLine("    LEFT JOIN dsi.WorksheetValue v ON v.WorksheetId = w.WorksheetId");
        foreach (var j in usedJoins) sb.AppendLine("    " + Joins[j]);
        sb.AppendLine("    WHERE " + string.Join("\n      AND ", where));
        sb.AppendLine("    GROUP BY " + string.Join(", ", groupBy));
        sb.AppendLine(")");
        sb.AppendLine("SELECT");
        sb.AppendLine("    " + string.Join(",\n    ", finalSelect));
        sb.Append("FROM b;");
        return sb.ToString();
    }

    private static string LookupExpr(string source, ISet<string> joins)
    {
        var (alias, sql) = Lookups[source];
        joins.Add(alias);
        return $"MAX({sql})";
    }

    private static string ValueColumn(FieldDataType t) => t switch
    {
        FieldDataType.Integer or FieldDataType.Decimal => "v.ValueNumber",
        FieldDataType.Date => "v.ValueDate",
        FieldDataType.Checkbox => "CAST(v.ValueBool AS TINYINT)",
        _ => "v.ValueText",
    };

    private static string FieldExpr(FieldInfo f, int row)
    {
        var expr = $"MAX(CASE WHEN v.FieldId = {f.FieldId} AND v.RowIndex = {row} THEN {ValueColumn(f.DataType)} END)";
        return f.DataType == FieldDataType.Checkbox ? $"CAST({expr} AS BIT)" : expr;
    }

    private static string AggregateExpr(FieldInfo f, string agg)
    {
        var col = ValueColumn(f.DataType);
        var cond = $"v.FieldId = {f.FieldId}";
        return agg switch
        {
            "LIST" => $"STRING_AGG(CASE WHEN {cond} THEN NULLIF(LTRIM(RTRIM(v.ValueText)), N'') END, N'; ') WITHIN GROUP (ORDER BY v.RowIndex)",
            "COUNT" => f.DataType is FieldDataType.Integer or FieldDataType.Decimal
                ? $"COUNT(CASE WHEN {cond} AND v.ValueNumber > 0 THEN 1 END)"
                : $"COUNT(CASE WHEN {cond} AND {col} IS NOT NULL THEN 1 END)",
            _ => $"{agg}(CASE WHEN {cond} THEN {col} END)",
        };
    }

    private static IEnumerable<string> ValidateExpression(ReportColumnDefinition c, ReportDefinition report)
    {
        if (string.IsNullOrWhiteSpace(c.Expression)) { yield return "expression is required."; yield break; }
        var e = c.Expression;
        if (e.Contains("--") || e.Contains("/*") || e.Contains(';') || e.Contains('\'') || e.Contains('[') || e.Contains('"'))
        { yield return "expression may not contain quotes, brackets, ';' or comments."; yield break; }

        var baseKeys = report.Columns.Where(x => x.SourceType != ReportSourceType.Expression)
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRegex().Matches(e))
            if (!baseKeys.Contains(m.Groups[1].Value))
                yield return $"{{{m.Groups[1].Value}}} is not a (non-expression) column key of this report.";

        var stripped = TokenRegex().Replace(e, " 0 ");
        foreach (Match w in WordRegex().Matches(stripped))
            if (!AllowedExpressionWords.Contains(w.Value))
                yield return $"'{w.Value}' is not allowed. Allowed: {string.Join(", ", AllowedExpressionWords)}.";
        if (!ExpressionCharsRegex().IsMatch(stripped))
            yield return "expression contains characters that are not allowed.";
    }

    private static string RewriteExpression(string expr) => TokenRegex().Replace(expr, m => $"b.{Q(m.Groups[1].Value)}");

    /// <summary>Bracket-quotes an identifier (same as T-SQL QUOTENAME).</summary>
    public static string Q(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    private static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex IdentifierRegex();
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")] private static partial Regex TokenRegex();
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")] private static partial Regex WordRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_\s.+\-*/(),<>=%]*$")] private static partial Regex ExpressionCharsRegex();
}
