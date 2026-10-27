using ClosedXML.Excel;
using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Services;

internal static class ExcelExporter
{
    public static void Export(IReadOnlyList<WeighingRecord> rows, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Lịch sử cân");

        string[] headers = { "ID", "Mã PO", "PHCD", "PHTX", "PSTX", "Số lượng", "Khối lượng (kg)", "Thời gian" };
        for (int c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0FE");
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int row = i + 2;
            sheet.Cell(row, 1).Value = r.Id;
            sheet.Cell(row, 2).SetValue(r.Aufnr);
            sheet.Cell(row, 3).SetValue(r.Phcd);
            sheet.Cell(row, 4).SetValue(r.Phtx);
            sheet.Cell(row, 5).SetValue(r.Pstx);
            sheet.Cell(row, 6).Value = r.Quantity;
            sheet.Cell(row, 7).Value = r.Kg;
            sheet.Cell(row, 8).Value = r.WeighedAt;
        }

        sheet.Column(6).Style.NumberFormat.Format = "0.###";
        sheet.Column(7).Style.NumberFormat.Format = "0.000";
        sheet.Column(8).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        workbook.SaveAs(path);
    }
}
