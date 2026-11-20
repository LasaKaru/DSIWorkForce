using ClosedXML.Excel;
using DSI.WorkForce.Core.Contracts;

namespace DSI.WorkForce.Web.Services;

/// <summary>Writes a report result in the same shape as "Daily Efficiency Report Format.xlsx" (title in B2, headers on row 5).</summary>
public static class ExcelExporter
{
    public static byte[] Export(ReportResultDto report, string? subtitle, DateOnly from, DateOnly to)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(report.Title.Length > 31 ? report.Title[..31] : report.Title);
        const int firstCol = 2, headerRow = 5;

        ws.Cell(2, firstCol).Value = report.Title;
        ws.Cell(2, firstCol).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(3, firstCol).Value = $"{subtitle} {from:dd/MM/yyyy} - {to:dd/MM/yyyy}".Trim();

        for (var c = 0; c < report.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, firstCol + c);
            cell.Value = report.Columns[c].Header;
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DDEBF7"))
                .Alignment.SetWrapText().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                .Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        }

        for (var r = 0; r < report.Rows.Count; r++)
        {
            var row = report.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                var cell = ws.Cell(headerRow + 1 + r, firstCol + c);
                cell.Value = row[c] switch
                {
                    null => Blank.Value,
                    DateTime d => d,
                    decimal m => m,
                    int i => i,
                    long l => l,
                    double db => db,
                    bool b => b,
                    var o => o.ToString(),
                };
                if (report.Columns[c].Format is { } fmt)
                {
                    if (row[c] is DateTime) cell.Style.DateFormat.Format = fmt;
                    else cell.Style.NumberFormat.Format = fmt;
                }
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }
        }

        var lastCol = firstCol + report.Columns.Count - 1;
        ws.Range(headerRow, firstCol, headerRow + Math.Max(report.Rows.Count, 1), lastCol).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns(firstCol, lastCol).AdjustToContents(headerRow, headerRow + Math.Min(report.Rows.Count, 500), 6, 40);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
