using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Core.Normalization;

/// <summary>
/// Cleans raw recognizer text into a typed value and adjusts confidence.
/// Handles the classic handwriting confusions (O→0, l→1, S→5 ...) for numeric boxes and
/// the yy/MM/dd date style used on PC-011-A ("26/11/17" = 17-Nov-2026).
/// </summary>
public static partial class ValueNormalizer
{
    private static readonly Dictionary<char, char> DigitConfusions = new()
    {
        ['O'] = '0', ['o'] = '0', ['D'] = '0', ['Q'] = '0',
        ['I'] = '1', ['l'] = '1', ['|'] = '1', ['i'] = '1', ['!'] = '1', ['L'] = '1',
        ['Z'] = '2', ['z'] = '2',
        ['S'] = '5', ['s'] = '5',
        ['B'] = '8',
        ['G'] = '6', ['b'] = '6',
        ['g'] = '9', ['q'] = '9',
        ['T'] = '7',
    };

    /// <param name="referenceDate">Usually the scan date; used to choose between ambiguous date readings.</param>
    public static ExtractedValue Normalize(FieldDefinition field, int rowIndex, string? raw, decimal recognizerConfidence,
        ValueSource source, DateOnly referenceDate)
    {
        var v = new ExtractedValue
        {
            FieldKey = field.Key,
            RowIndex = rowIndex,
            RawText = raw,
            Source = source,
            Confidence = Clamp(recognizerConfidence),
        };

        var text = CollapseWhitespace(raw);
        if (string.IsNullOrEmpty(text) || text is "-" or "—" or "_" or "/" or "N/A")
        {
            v.Confidence = field.Required ? 0m : v.Confidence;
            return v;
        }

        switch (field.Type)
        {
            case FieldDataType.Integer:
            case FieldDataType.Decimal:
                var (num, substituted) = ParseNumber(text, field.Type == FieldDataType.Integer);
                if (num is null) { v.ValueText = text; v.Confidence = 0m; }
                else
                {
                    v.ValueNumber = num;
                    v.ValueText = num.Value.ToString(CultureInfo.InvariantCulture);
                    if (substituted) v.Confidence = Math.Min(v.Confidence, 0.85m);
                }
                break;

            case FieldDataType.Date:
                var (date, ambiguous) = ParseDate(text, field.DateFormats, referenceDate);
                if (date is null) { v.ValueText = text; v.Confidence = 0m; }
                else
                {
                    v.ValueDate = date;
                    v.ValueText = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var daysAway = Math.Abs(date.Value.DayNumber - referenceDate.DayNumber);
                    if (daysAway > 45) v.Confidence = Math.Min(v.Confidence, 0.40m);
                    else if (ambiguous) v.Confidence = Math.Min(v.Confidence, 0.92m);
                }
                break;

            case FieldDataType.Time:
                v.ValueText = NormalizeTime(text) ?? text;
                break;

            case FieldDataType.Checkbox:
                v.ValueBool = text is "1" or "true" or "True" or "✓" or "✔" or "x" or "X";
                v.ValueText = v.ValueBool.Value ? "1" : "0";
                break;

            default:
                v.ValueText = text;
                break;
        }

        if (field.AllowedValues is { Count: > 0 } && v.ValueText is not null)
            ApplyAllowedValues(field, v);

        if (v.ValueNumber is not null && (field.Min is not null && v.ValueNumber < field.Min || field.Max is not null && v.ValueNumber > field.Max))
            v.Confidence = Math.Min(v.Confidence, 0.30m);

        return v;
    }

    public static (decimal? Value, bool Substituted) ParseNumber(string text, bool integerOnly)
    {
        var sb = new StringBuilder(text.Length);
        var substituted = false;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch is '.' or ',' && !integerOnly) sb.Append('.');
            else if (ch is ' ' or '\'' or ',') continue;
            else if (DigitConfusions.TryGetValue(ch, out var d)) { sb.Append(d); substituted = true; }
            else return (null, substituted);
        }
        var s = sb.ToString();
        if (s.Length == 0 || s.Count(c => c == '.') > 1) return (null, substituted);
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? (n, substituted) : (null, substituted);
    }

    public static (DateOnly? Date, bool Ambiguous) ParseDate(string text, IReadOnlyList<string>? formats, DateOnly referenceDate)
    {
        // Unify separators: 26.11.17, 26-11-17, 26 11 17 -> 26/11/17
        var cleaned = SeparatorRegex().Replace(text.Trim(), "/");
        cleaned = new string(cleaned.Select(c => DigitConfusions.TryGetValue(c, out var d) ? d : c).ToArray());
        var fmts = formats is { Count: > 0 } ? formats : new[] { "yy/M/d", "d/M/yy", "d/M/yyyy", "yyyy/M/d" };

        var candidates = new List<DateOnly>();
        foreach (var f in fmts)
            if (DateOnly.TryParseExact(cleaned, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                candidates.Add(d);

        if (candidates.Count == 0) return (null, false);
        var distinct = candidates.Distinct().ToList();
        // Closest to the scan date wins: "26/11/17" -> 2026-11-17, not 2017-11-26.
        var best = distinct.OrderBy(d => Math.Abs(d.DayNumber - referenceDate.DayNumber)).First();
        return (best, distinct.Count > 1);
    }

    public static string? NormalizeTime(string text)
    {
        var m = TimeRegex().Match(text);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return h is >= 0 and < 24 && min is >= 0 and < 60 ? $"{h:00}:{min:00}" : null;
    }

    private static void ApplyAllowedValues(FieldDefinition field, ExtractedValue v)
    {
        static string Canon(string s) => SeparatorRegex().Replace(s.Replace(" ", ""), "-").ToUpperInvariant();
        var input = Canon(v.ValueText!);
        var exact = field.AllowedValues!.FirstOrDefault(a => Canon(a) == input);
        if (exact is not null) { v.ValueText = exact; return; }

        var (best, score) = FuzzyMatcher.Best(input, field.AllowedValues!, Canon);
        if (best is not null && score >= 0.6)
        {
            v.ValueText = best;
            v.Confidence = Math.Min(v.Confidence, (decimal)Math.Round(score, 2));
        }
        else v.Confidence = 0m;
    }

    private static string? CollapseWhitespace(string? s) =>
        s is null ? null : WhitespaceRegex().Replace(s, " ").Trim();

    private static decimal Clamp(decimal c) => c < 0 ? 0 : c > 1 ? 1 : c;

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"\s*[./\\\-]\s*|\s+")] private static partial Regex SeparatorRegex();
    [GeneratedRegex(@"^\s*(\d{1,2})(?:\s*[.:]\s*(\d{2}))?\s*$")] private static partial Regex TimeRegex();
}
