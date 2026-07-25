using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Data;
using System.Text.Json;

namespace corporate_dashboards.Controllers;

[Route("ItsSnapshot")]
public sealed class ItsSnapshotController : Controller
{
    private readonly IConfiguration _cfg;

    public ItsSnapshotController(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    [HttpGet("Ping")]
    public IActionResult Ping()
    {
        return Content("ItsSnapshotController is live.");
    }

    [HttpGet("Workbook")]
    public async Task<IActionResult> Workbook(CancellationToken ct)
    {
        var page = QueryValue("page", "Page");
        if (string.IsNullOrWhiteSpace(page))
            page = "Multi";

        var title = QueryValue("title", "Title", "layoutTitle", "currentLayoutTitle", "currentlayouttitle");
        var layoutId = QueryInt("currentlayoutid", "currentLayoutId", "layoutVersionId", "layoutversionid", "versionId");

        var layout = await LoadLayoutAsync(page, title, layoutId, ct);
        var sources = ExtractTileSources(layout.LayoutJson);

        if (sources.Count == 0)
            return NotFound("No SQL-backed tiles found in LayoutJson.");

        var workbookBytes = await BuildWorkbookAsync(layout, sources, ct);

        var safeTitle = SafeFilePart(layout.Title);
        var fileName = $"{safeTitle}_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

        return File(
            workbookBytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private async Task<LayoutRow> LoadLayoutAsync(string page, string? title, int? layoutId, CancellationToken ct)
    {
        await using var con = new SqlConnection(LayoutConnStr());
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();

        if (layoutId.HasValue && layoutId.Value > 0)
        {
            cmd.CommandText = @"
SELECT TOP (1)
    LayoutVersionId,
    UserName,
    Page,
    Title,
    LayoutJson,
    CreatedUtc
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id
  AND Page = @page;";
            cmd.Parameters.AddWithValue("@id", layoutId.Value);
            cmd.Parameters.AddWithValue("@page", page);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("Provide ?title=... or ?currentlayoutid=...");

            cmd.CommandText = @"
SELECT TOP (1)
    LayoutVersionId,
    UserName,
    Page,
    Title,
    LayoutJson,
    CreatedUtc
FROM dbo.DashboardLayoutVersion
WHERE Page = @page
  AND LOWER(Title) = LOWER(@title)
ORDER BY LayoutVersionId DESC;";
            cmd.Parameters.AddWithValue("@page", page);
            cmd.Parameters.AddWithValue("@title", title);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            if (layoutId.HasValue)
                throw new InvalidOperationException($"LayoutVersionId {layoutId.Value} was not found for page {page}.");

            throw new InvalidOperationException($"Layout title '{title}' was not found for page {page}.");
        }

        return new LayoutRow
        {
            LayoutVersionId = reader.GetInt32(reader.GetOrdinal("LayoutVersionId")),
            UserName = Convert.ToString(reader["UserName"]) ?? "",
            Page = Convert.ToString(reader["Page"]) ?? "",
            Title = Convert.ToString(reader["Title"]) ?? "",
            LayoutJson = Convert.ToString(reader["LayoutJson"]) ?? "{}",
            CreatedUtc = reader["CreatedUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["CreatedUtc"])
        };
    }

    private static List<TileSource> ExtractTileSources(string layoutJson)
    {
        var result = new List<TileSource>();

        using var doc = JsonDocument.Parse(layoutJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var tileProp in tiles.EnumerateObject())
        {
            var tileId = tileProp.Name;
            var tile = tileProp.Value;

            var connection = GetNestedString(tile, "dataset", "connection");
            var schema = GetNestedString(tile, "dataset", "schema");
            var obj = GetNestedString(tile, "dataset", "obj");

            var chartType = GetNestedString(tile, "ui", "chartType");
            var template = GetNestedString(tile, "ui", "customHtmlTemplate");
            var manualTitle = GetNestedString(tile, "ui", "manualTitle");

            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(obj))
                continue;

            result.Add(new TileSource
            {
                TileId = tileId,
                Connection = string.IsNullOrWhiteSpace(connection) ? "build" : connection,
                Schema = schema,
                ObjectName = obj,
                ChartType = chartType,
                Template = template,
                Title = !string.IsNullOrWhiteSpace(manualTitle)
                    ? manualTitle
                    : !string.IsNullOrWhiteSpace(template)
                        ? template
                        : $"{schema}.{obj}"
            });
        }

        return result;
    }

    private async Task<byte[]> BuildWorkbookAsync(LayoutRow layout, List<TileSource> sources, CancellationToken ct)
    {
        using var ms = new MemoryStream();

        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();

            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint sheetId = 1;

            var summaryPart = wbPart.AddNewPart<WorksheetPart>();
            var summaryData = new SheetData();
            summaryPart.Worksheet = new Worksheet(summaryData);

            WriteSummary(summaryData, layout, sources);

            sheets.Append(new Sheet
            {
                Id = wbPart.GetIdOfPart(summaryPart),
                SheetId = sheetId++,
                Name = MakeUniqueSheetName("00 Snapshot", usedSheetNames)
            });

            foreach (var source in sources)
            {
                var table = await QuerySourceAsync(source, ct);

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                wsPart.Worksheet = new Worksheet(sheetData);

                WriteDataTable(sheetData, table);
                wsPart.Worksheet.Save();

                var sheetName = MakeUniqueSheetName(source.Title, usedSheetNames);

                sheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = sheetId++,
                    Name = sheetName
                });
            }

            wbPart.Workbook.Save();
        }

        return ms.ToArray();
    }

    private async Task<DataTable> QuerySourceAsync(TileSource source, CancellationToken ct)
    {
        var connStr = DataConnStr(source.Connection);

        await using var con = new SqlConnection(connStr);
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText = $"SELECT TOP (5000) * FROM {SqlName(source.Schema, source.ObjectName)};";

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var table = new DataTable(source.Title);
        table.Load(reader);

        return table;
    }

    private static void WriteSummary(SheetData sheetData, LayoutRow layout, List<TileSource> sources)
    {
        sheetData.Append(RowOf("Dashboard title", layout.Title));
        sheetData.Append(RowOf("LayoutVersionId", layout.LayoutVersionId.ToString()));
        sheetData.Append(RowOf("Page", layout.Page));
        sheetData.Append(RowOf("Layout owner", layout.UserName));
        sheetData.Append(RowOf("Layout created UTC", layout.CreatedUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""));
        sheetData.Append(RowOf("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        sheetData.Append(new Row());

        sheetData.Append(RowOf(
            "TileId",
            "Title",
            "Connection",
            "Schema",
            "Object",
            "Template",
            "ChartType"));

        foreach (var s in sources)
        {
            sheetData.Append(RowOf(
                s.TileId,
                s.Title,
                s.Connection,
                s.Schema,
                s.ObjectName,
                s.Template,
                s.ChartType));
        }
    }

    private static void WriteDataTable(SheetData sheetData, DataTable table)
    {
        var header = new Row();

        foreach (DataColumn col in table.Columns)
            header.Append(CellText(col.ColumnName));

        sheetData.Append(header);

        foreach (DataRow dataRow in table.Rows)
        {
            var row = new Row();

            foreach (var value in dataRow.ItemArray)
                row.Append(CellText(FormatCell(value)));

            sheetData.Append(row);
        }
    }

    private static Row RowOf(params string[] values)
    {
        var row = new Row();

        foreach (var v in values)
            row.Append(CellText(v));

        return row;
    }

    private static Cell CellText(string value)
    {
        return new Cell
        {
            DataType = CellValues.String,
            CellValue = new CellValue(value ?? "")
        };
    }

    private static string FormatCell(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "";

        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");

        return Convert.ToString(value) ?? "";
    }

    private string LayoutConnStr()
    {
        return _cfg.GetConnectionString("DashboardDb")
            ?? _cfg.GetConnectionString("build")
            ?? _cfg.GetConnectionString("its_dashboard")
            ?? throw new InvalidOperationException("Missing layout DB connection string: DashboardDb/build/its_dashboard.");
    }

    private string DataConnStr(string connectionName)
    {
        return _cfg.GetConnectionString(connectionName)
            ?? _cfg.GetConnectionString("build")
            ?? throw new InvalidOperationException($"Missing data connection string '{connectionName}'.");
    }

    private string? QueryValue(params string[] names)
    {
        foreach (var name in names)
        {
            if (Request.Query.TryGetValue(name, out var value))
            {
                var text = Convert.ToString(value.FirstOrDefault());
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private int? QueryInt(params string[] names)
    {
        var text = QueryValue(names);
        return int.TryParse(text, out var value) ? value : null;
    }

    private static string GetNestedString(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return "";

        if (!p.TryGetProperty(child, out var c))
            return "";

        return c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString();
    }

    private static string SqlName(string schema, string obj)
    {
        return $"[{EscapeSql(schema)}].[{EscapeSql(obj)}]";
    }

    private static string EscapeSql(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SQL identifier cannot be blank.");

        return value.Replace("]", "]]", StringComparison.Ordinal);
    }

    private static string SafeFilePart(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "dashboard" : s;
    }

    private static string MakeUniqueSheetName(string title, HashSet<string> used)
    {
        var baseName = SafeSheetName(title);
        var name = baseName;
        var n = 2;

        while (used.Contains(name))
        {
            var suffix = " " + n++;
            var maxLen = Math.Max(1, 31 - suffix.Length);
            name = baseName[..Math.Min(baseName.Length, maxLen)] + suffix;
        }

        used.Add(name);
        return name;
    }

    private static string SafeSheetName(string title)
    {
        var bad = new HashSet<char>(new[] { '[', ']', '*', '?', '/', '\\', ':' });
        var cleaned = new string((title ?? "Sheet").Where(c => !bad.Contains(c)).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Sheet";

        return cleaned[..Math.Min(31, cleaned.Length)];
    }

    private sealed class LayoutRow
    {
        public int LayoutVersionId { get; set; }
        public string UserName { get; set; } = "";
        public string Page { get; set; } = "";
        public string Title { get; set; } = "";
        public string LayoutJson { get; set; } = "";
        public DateTime? CreatedUtc { get; set; }
    }

    private sealed class TileSource
    {
        public string TileId { get; set; } = "";
        public string Connection { get; set; } = "";
        public string Schema { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string ChartType { get; set; } = "";
        public string Template { get; set; } = "";
        public string Title { get; set; } = "";
    }
}