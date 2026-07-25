using ClosedXML.Excel;
using corporate_dashboards.Models;
using corporate_dashboards.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace corporate_dashboards.Controllers;

public sealed class CxDashboardUploadController : Controller
{
    private static readonly HashSet<string> SupportedManualVisualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "cx_ka_meetings_card",
        "cx_ka_engagements_card",
        "cx_ka_projects_card",
        "cx_engagements_target_chart",
        "cx_soe_portal_table",
        "cx_soe_unique_customer_applicants_pie",
        "cx_soe_applications_by_customer_type_pie"
    };

    private readonly ICxDashboardUploadAccessService _access;
    private readonly CxDashboardUploadOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CxDashboardUploadController> _logger;

    public CxDashboardUploadController(
        ICxDashboardUploadAccessService access,
        IOptions<CxDashboardUploadOptions> options,
        IConfiguration configuration,
        ILogger<CxDashboardUploadController> logger)
    {
        _access = access;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        if (!_access.CanAccess(User))
        {
            return Forbid();
        }

        return View(BuildVm());
    }

    [HttpGet]
    public IActionResult Template()
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        if (!_access.CanAccess(User))
        {
            return Forbid();
        }

        using var workbook = BuildTemplateWorkbook(GetEnabledVisuals());
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "CX_dashboard_upload_empty_template.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> Index(IFormFile? uploadFile, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        if (!_access.CanAccess(User))
        {
            _logger.LogWarning(
                "CX upload POST forbidden. User={User}; FileName={FileName}; FileLength={FileLength}",
                User.Identity?.Name ?? "(anonymous)",
                uploadFile?.FileName ?? "(null)",
                uploadFile?.Length ?? 0);
            return Forbid();
        }

        var vm = BuildVm();

        if (uploadFile is null || uploadFile.Length == 0)
        {
            vm.Errors.Add("Choose a filled .xlsx CX workbook.");
            return View(vm);
        }

        if (!uploadFile.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            vm.Errors.Add("Only .xlsx upload files are supported.");
            return View(vm);
        }

        List<CxParsedVisualUpload> parsed;
        string workbookHash;

        try
        {
            await using var uploadStream = uploadFile.OpenReadStream();
            using var memory = new MemoryStream();
            await uploadStream.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            workbookHash = Sha256Hex(bytes);
            memory.Position = 0;
            parsed = ParseWorkbook(memory, GetEnabledVisuals(), vm.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "CX upload parse failed. User={User}; FileName={FileName}; FileLength={FileLength}",
                User.Identity?.Name ?? "(anonymous)",
                uploadFile.FileName,
                uploadFile.Length);

            vm.Errors.Add("The workbook could not be read. Use the CX template without renaming sheets or headers.");
            vm.Errors.Add(ex.GetBaseException().Message);
            return View(vm);
        }

        if (vm.Errors.Count > 0)
        {
            return View(vm);
        }

        if (parsed.Count == 0)
        {
            vm.Errors.Add("No filled CX visual tabs were found. Blank tabs and untouched example rows from an older template are ignored.");
            return View(vm);
        }

        var currentUser = string.IsNullOrWhiteSpace(User.Identity?.Name)
            ? "unknown"
            : User.Identity!.Name!;

        long? uploadBatchId = null;
        try
        {
            var connectionString = ResolveConnectionString();
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            uploadBatchId = await InsertBatchAsync(
                conn,
                tx,
                uploadFile.FileName,
                workbookHash,
                currentUser,
                cancellationToken);

            foreach (var visual in parsed)
            {
                var pipelineKey = visual.Definition.EffectiveApplyKey;

                foreach (var row in visual.Rows)
                {
                    await InsertStageRowAsync(
                        conn,
                        tx,
                        uploadBatchId.Value,
                        pipelineKey,
                        visual.SourceSheetName,
                        row.RowOrdinal,
                        row.RowJson,
                        row.RowHash,
                        cancellationToken);
                }

                try
                {
                    await ApplyVisualAsync(
                        conn,
                        tx,
                        uploadBatchId.Value,
                        pipelineKey,
                        currentUser,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"CX apply failed for sheet '{visual.SourceSheetName}', visual key '{visual.Definition.Key}', pipeline key '{pipelineKey}'. {ex.GetBaseException().Message}",
                        ex);
                }
            }

            await UpdateBatchStatusAsync(
                conn,
                tx,
                uploadBatchId.Value,
                "Published",
                $"Published {parsed.Count} visual tab(s).",
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

            vm.Success = true;
            vm.UploadBatchId = uploadBatchId;
            vm.SourceFileName = uploadFile.FileName;
            vm.PublishedVisuals = parsed.Count;
            vm.PublishedRows = parsed.Sum(v => v.Rows.Count);
            vm.Results = parsed.Select(v => new CxDashboardUploadResultVm
            {
                VisualKey = v.Definition.Key,
                PipelineKey = v.Definition.EffectiveApplyKey,
                SheetName = v.SourceSheetName,
                RowCount = v.Rows.Count,
                Status = "Published"
            }).ToList();

            return View(vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "CX upload failed. User={User}; Batch={Batch}; FileName={FileName}; Visuals={Visuals}; Rows={Rows}",
                currentUser,
                uploadBatchId,
                uploadFile.FileName,
                parsed.Count,
                parsed.Sum(v => v.Rows.Count));

            vm.Errors.Add("Upload reached the server, but saving or applying the CX pipeline failed.");
            vm.Errors.Add(ex.GetBaseException().Message);
            return View(vm);
        }
    }

    private string ResolveConnectionString()
    {
        var configuredName = string.IsNullOrWhiteSpace(_options.ConnectionName)
            ? "build"
            : _options.ConnectionName.Trim();

        return _configuration.GetConnectionString("CxUpload")
            ?? _configuration.GetConnectionString(configuredName)
            ?? _configuration.GetConnectionString("build")
            ?? throw new InvalidOperationException(
                $"Missing SQL connection string. Configure ConnectionStrings:CxUpload or ConnectionStrings:{configuredName}.");
    }

    private CxDashboardUploadPageVm BuildVm()
    {
        return new CxDashboardUploadPageVm
        {
            Visuals = GetEnabledVisuals().Select(v => new CxDashboardUploadVisualVm
            {
                Key = v.Key,
                ApplyKey = v.EffectiveApplyKey,
                Label = v.Label,
                SheetName = v.SheetName,
                Role = v.Role,
                Target = string.IsNullOrWhiteSpace(v.Object) ? string.Empty : $"{v.Schema}.{v.Object}",
                Headers = v.Headers,
                RequiredHeaders = v.RequiredHeaders
            }).ToList()
        };
    }

    private List<CxDashboardUploadVisualOptions> GetEnabledVisuals()
    {
        return (_options.Visuals ?? new List<CxDashboardUploadVisualOptions>())
            .Where(v => v.Enabled)
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .Where(v => !string.IsNullOrWhiteSpace(v.SheetName))
            .Where(v => SupportedManualVisualKeys.Contains(v.Key))
            .Select(v =>
            {
                if (v.Headers.Count == 0)
                {
                    v.Headers = DefaultHeadersForRole(v.Role).ToList();
                }

                if (v.RequiredHeaders.Count == 0)
                {
                    v.RequiredHeaders = DefaultRequiredHeadersForRole(v.Role).ToList();
                }

                return v;
            })
            .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<CxParsedVisualUpload> ParseWorkbook(
        Stream stream,
        IReadOnlyList<CxDashboardUploadVisualOptions> definitions,
        List<string> errors)
    {
        using var workbook = new XLWorkbook(stream);
        var parsed = new List<CxParsedVisualUpload>();

        foreach (var definition in definitions)
        {
            var worksheet = FindWorksheet(workbook, definition);
            if (worksheet is null)
            {
                continue;
            }

            var sheetErrorStart = errors.Count;
            var headerMap = ReadHeaderMap(worksheet.Row(1));
            var sheetPrefix = $"Sheet '{worksheet.Name}'";
            var hasAnyExpectedHeader = definition.Headers.Any(h => headerMap.ContainsKey(NormalizeHeader(h)));

            if (!hasAnyExpectedHeader && IsWorksheetEmpty(worksheet))
            {
                continue;
            }

            foreach (var required in definition.RequiredHeaders)
            {
                if (!headerMap.ContainsKey(NormalizeHeader(required)))
                {
                    errors.Add($"{sheetPrefix}: missing required column '{required}'.");
                }
            }

            if (errors.Count > sheetErrorStart)
            {
                continue;
            }

            var rows = new List<CxParsedStageRow>();
            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            var orderedHeaders = definition.Headers
                .Where(h => headerMap.ContainsKey(NormalizeHeader(h)))
                .ToList();

            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                var row = worksheet.Row(rowNumber);
                if (IsLegacyInstructionRow(row) || IsDataRowEmpty(row, orderedHeaders, headerMap))
                {
                    continue;
                }

                var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in orderedHeaders)
                {
                    payload[header] = CellValue(row.Cell(headerMap[NormalizeHeader(header)]));
                }

                // Earlier templates contained a prefilled example in row 2. It was
                // presented as an "empty" template but caused every tab to publish.
                // Ignore that untouched example while still accepting a user-edited row 2.
                if (rowNumber == 2 && IsUntouchedLegacySampleRow(definition, payload))
                {
                    continue;
                }

                var rowErrorStart = errors.Count;
                foreach (var required in definition.RequiredHeaders)
                {
                    if (!TryGetPayloadValue(payload, required, out var value) || IsNullOrBlank(value))
                    {
                        errors.Add($"{sheetPrefix}, row {rowNumber}: '{required}' is required.");
                    }
                }

                if (errors.Count != rowErrorStart)
                {
                    continue;
                }

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
                rows.Add(new CxParsedStageRow(rows.Count + 1, json, Sha256Hex(Encoding.UTF8.GetBytes(json))));
            }

            if (rows.Count > 0)
            {
                parsed.Add(new CxParsedVisualUpload(definition, worksheet.Name, rows));
            }
        }

        return parsed;
    }

    private static IXLWorksheet? FindWorksheet(
        XLWorkbook workbook,
        CxDashboardUploadVisualOptions definition)
    {
        var candidates = new List<string>
        {
            definition.SheetName,
            definition.Key,
            definition.Label,
            definition.Object,
            definition.Key.StartsWith("cx_", StringComparison.OrdinalIgnoreCase)
                ? definition.Key[3..]
                : definition.Key
        };
        candidates.AddRange(definition.SheetAliases ?? new List<string>());

        var normalizedCandidates = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => NormalizeHeader(SafeSheetName(x)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return workbook.Worksheets.FirstOrDefault(ws =>
            normalizedCandidates.Contains(NormalizeHeader(ws.Name)));
    }

    private static XLWorkbook BuildTemplateWorkbook(IReadOnlyList<CxDashboardUploadVisualOptions> definitions)
    {
        var workbook = new XLWorkbook();

        foreach (var definition in definitions)
        {
            var worksheet = workbook.Worksheets.Add(SafeSheetName(definition.SheetName));
            var headers = definition.Headers.Count > 0
                ? definition.Headers
                : DefaultHeadersForRole(definition.Role).ToList();

            for (var col = 0; col < headers.Count; col++)
            {
                worksheet.Cell(1, col + 1).Value = headers[col];
            }

            var header = worksheet.Range(1, 1, 1, headers.Count);
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#171777");
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            header.SetAutoFilter();

            worksheet.SheetView.FreezeRows(1);
            worksheet.Row(1).Height = 22;
            worksheet.Columns(1, headers.Count).Width = 19;

        }

        var guide = workbook.Worksheets.Add("Upload Guide");
        guide.Cell("A1").Value = "CX Dashboard Upload";
        guide.Cell("A1").Style.Font.Bold = true;
        guide.Cell("A1").Style.Font.FontSize = 16;
        guide.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#171777");
        guide.Cell("A3").Value = "Fill only the tabs you want to update. Data tabs contain headers only; blank tabs are ignored.";
        guide.Cell("A4").Value = "Do not rename headers. Required fields are listed below on this guide sheet.";
        guide.Cell("A5").Value = "API-driven Call Volume / response-time / call-handling visuals and Ebill Adoption are excluded.";
        guide.Cell("A7").Value = "Sheet";
        guide.Cell("B7").Value = "Visual key";
        guide.Cell("C7").Value = "Pipeline key";
        guide.Cell("D7").Value = "Required fields";
        guide.Range("A7:D7").Style.Font.Bold = true;

        var r = 8;
        foreach (var definition in definitions)
        {
            guide.Cell(r, 1).Value = definition.SheetName;
            guide.Cell(r, 2).Value = definition.Key;
            guide.Cell(r, 3).Value = definition.EffectiveApplyKey;
            guide.Cell(r, 4).Value = string.Join(", ", definition.RequiredHeaders);
            r++;
        }

        guide.Columns(1, 3).AdjustToContents();
        guide.Column(4).Width = 75;
        guide.SheetView.FreezeRows(7);
        return workbook;
    }

    private static bool IsLegacyInstructionRow(IXLRow row)
    {
        var first = CellText(row.Cell(1)).Trim();
        return first.Equals("Notes", StringComparison.OrdinalIgnoreCase)
            || first.Equals("VisualKey", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Visual Key", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Target view", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Target", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Required", StringComparison.OrdinalIgnoreCase)
            || first.StartsWith("Empty tabs are ignored", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUntouchedLegacySampleRow(
        CxDashboardUploadVisualOptions definition,
        IReadOnlyDictionary<string, object?> payload)
    {
        var sample = SampleRowForRole(definition.Role, definition.Label);
        var compared = 0;

        foreach (var pair in sample)
        {
            if (!TryGetPayloadValue(payload, pair.Key, out var actual) || actual is null)
            {
                continue;
            }

            compared++;
            if (!ValuesEquivalent(actual, pair.Value))
            {
                return false;
            }
        }

        return compared >= Math.Min(3, sample.Count);
    }

    private static bool ValuesEquivalent(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        if (decimal.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var ld)
            && decimal.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var rd))
        {
            return ld == rd;
        }

        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture)?.Trim(),
            Convert.ToString(right, CultureInfo.InvariantCulture)?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPayloadValue(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        out object? value)
    {
        if (payload.TryGetValue(key, out value)) return true;
        var normalized = NormalizeHeader(key);
        var found = payload.FirstOrDefault(x => NormalizeHeader(x.Key) == normalized);
        if (found.Key is not null)
        {
            value = found.Value;
            return true;
        }

        value = null;
        return false;
    }

    private async Task<long> InsertBatchAsync(
        DbConnection conn,
        DbTransaction tx,
        string fileName,
        string workbookHash,
        string uploadedBy,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dbo.CxDashboardUploadBatch
            (
                OriginalFileName,
                WorkbookHash,
                UploadedBy,
                Status,
                Message
            )
            OUTPUT INSERTED.UploadBatchId
            VALUES
            (
                @OriginalFileName,
                @WorkbookHash,
                @UploadedBy,
                N'Uploaded',
                NULL
            );
            """;

        AddParam(cmd, "@OriginalFileName", fileName);
        AddParam(cmd, "@WorkbookHash", workbookHash);
        AddParam(cmd, "@UploadedBy", uploadedBy);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task InsertStageRowAsync(
        DbConnection conn,
        DbTransaction tx,
        long uploadBatchId,
        string visualKey,
        string sheetName,
        int rowOrdinal,
        string rowJson,
        string rowHash,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dbo.CxDashboardUploadStageRow
            (
                UploadBatchId,
                VisualKey,
                SheetName,
                RowOrdinal,
                RowJson,
                RowHash
            )
            VALUES
            (
                @UploadBatchId,
                @VisualKey,
                @SheetName,
                @RowOrdinal,
                @RowJson,
                @RowHash
            );
            """;

        AddParam(cmd, "@UploadBatchId", uploadBatchId);
        AddParam(cmd, "@VisualKey", visualKey);
        AddParam(cmd, "@SheetName", sheetName);
        AddParam(cmd, "@RowOrdinal", rowOrdinal);
        AddParam(cmd, "@RowJson", rowJson);
        AddParam(cmd, "@RowHash", rowHash);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ApplyVisualAsync(
        DbConnection conn,
        DbTransaction tx,
        long uploadBatchId,
        string visualKey,
        string uploadedBy,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            EXEC {QuoteSqlMultipartIdentifier(_options.ApplyProcedure)}
                @UploadBatchId = @UploadBatchId,
                @VisualKey = @VisualKey,
                @UploadedBy = @UploadedBy;
            """;
        AddParam(cmd, "@UploadBatchId", uploadBatchId);
        AddParam(cmd, "@VisualKey", visualKey);
        AddParam(cmd, "@UploadedBy", uploadedBy);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteSqlMultipartIdentifier(string name)
    {
        var parts = (name ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3 || parts.Any(x => !Regex.IsMatch(x, "^[A-Za-z_][A-Za-z0-9_]*$")))
        {
            throw new InvalidOperationException($"Invalid CX apply procedure name: '{name}'.");
        }

        return string.Join('.', parts.Select(x => $"[{x}]"));
    }

    private static async Task UpdateBatchStatusAsync(
        DbConnection conn,
        DbTransaction tx,
        long uploadBatchId,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.CxDashboardUploadBatch
            SET Status = @Status,
                Message = @Message
            WHERE UploadBatchId = @UploadBatchId;
            """;
        AddParam(cmd, "@UploadBatchId", uploadBatchId);
        AddParam(cmd, "@Status", status);
        AddParam(cmd, "@Message", message);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static Dictionary<string, int> ReadHeaderMap(IXLRow headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var normalized = NormalizeHeader(CellText(cell));
            if (!string.IsNullOrWhiteSpace(normalized) && !map.ContainsKey(normalized))
            {
                map[normalized] = cell.Address.ColumnNumber;
            }
        }
        return map;
    }

    private static bool IsWorksheetEmpty(IXLWorksheet worksheet)
    {
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow <= 1) return true;
        for (var r = 2; r <= lastRow; r++)
        {
            if (worksheet.Row(r).CellsUsed().Any(c => !string.IsNullOrWhiteSpace(CellText(c))))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDataRowEmpty(
        IXLRow row,
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, int> headerMap)
    {
        return headers.All(header =>
            string.IsNullOrWhiteSpace(CellText(row.Cell(headerMap[NormalizeHeader(header)]))));
    }

    private static object? CellValue(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var dateValue))
        {
            return dateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (cell.TryGetValue<decimal>(out var decimalValue))
        {
            // Excel stores a displayed 1.14% as 0.0114. The CX SQL shapes
            // store percentage points, so preserve what the user sees.
            var numberFormat = cell.Style.NumberFormat.Format ?? string.Empty;
            return numberFormat.Contains('%') ? decimalValue * 100m : decimalValue;
        }

        var text = CellText(cell);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var numericText = text.Replace("%", string.Empty).Trim();
        if (decimal.TryParse(numericText, NumberStyles.Number, CultureInfo.CurrentCulture, out var number)
            || decimal.TryParse(numericText, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static string CellText(IXLCell cell) => cell.GetFormattedString().Trim();

    private static string NormalizeHeader(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool IsNullOrBlank(object? value) =>
        value is null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture));

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string SafeSheetName(string value)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Sheet";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    private static IReadOnlyList<string> DefaultHeadersForRole(string role) => role switch
    {
        "cx-kpi-card" => new[]
        {
            "title", "value", "value_text", "value_type", "period_label", "period_sort",
            "snapshot_date", "delta_pct", "status", "target_value", "narrative_line_a", "narrative_line_b"
        },
        "cx-engagements-target" => new[]
        {
            "period_label", "period_sort", "snapshot_date", "category", "type", "category_sort", "value", "target_value"
        },
        "cx-soe-table" => new[]
        {
            "row_label", "row_sort", "period_label", "snapshot_date", "current_month_label", "current_month_value",
            "prior_month_label", "prior_month_value", "ytd_value", "target_value", "status"
        },
        "cx-soe-pie" => new[]
        {
            "category", "category_sort", "value", "period_label", "snapshot_date"
        },
        _ => new[] { "period_label", "period_sort", "value", "value_text" }
    };

    private static IReadOnlyList<string> DefaultRequiredHeadersForRole(string role) => role switch
    {
        "cx-kpi-card" => new[] { "title", "value", "period_label" },
        "cx-engagements-target" => new[] { "period_label", "category", "value" },
        "cx-soe-table" => new[] { "row_label", "current_month_value" },
        "cx-soe-pie" => new[] { "category", "value" },
        _ => new[] { "period_label", "value" }
    };

    private static Dictionary<string, object?> SampleRowForRole(string role, string label)
    {
        var lastMonthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        var priorMonthStart = lastMonthStart.AddMonths(-1);
        var periodLabel = lastMonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var periodSort = lastMonthStart.Year * 100 + lastMonthStart.Month;

        return role switch
        {
            "cx-kpi-card" => new Dictionary<string, object?>
            {
                ["title"] = label,
                ["value"] = 100,
                ["value_text"] = "100",
                ["value_type"] = "raw",
                ["period_label"] = periodLabel,
                ["period_sort"] = periodSort,
                ["snapshot_date"] = lastMonthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["delta_pct"] = 2.5m,
                ["status"] = "good",
                ["target_value"] = 100
            },
            "cx-engagements-target" => new Dictionary<string, object?>
            {
                ["period_label"] = periodLabel,
                ["period_sort"] = periodSort,
                ["snapshot_date"] = lastMonthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["category"] = "Meeting",
                ["type"] = "Meeting",
                ["category_sort"] = 1,
                ["value"] = 10,
                ["target_value"] = 12
            },
            "cx-soe-table" => new Dictionary<string, object?>
            {
                ["row_label"] = "Residential",
                ["row_sort"] = 1,
                ["period_label"] = periodLabel,
                ["snapshot_date"] = lastMonthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["current_month_label"] = periodLabel,
                ["current_month_value"] = 10,
                ["prior_month_label"] = priorMonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                ["prior_month_value"] = 9,
                ["ytd_value"] = 100,
                ["target_value"] = 100,
                ["status"] = "good"
            },
            "cx-soe-pie" => new Dictionary<string, object?>
            {
                ["category"] = "Residential",
                ["category_sort"] = 1,
                ["value"] = 100,
                ["period_label"] = periodLabel,
                ["snapshot_date"] = lastMonthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            _ => new Dictionary<string, object?>
            {
                ["period_label"] = periodLabel,
                ["period_sort"] = periodSort,
                ["value"] = 100,
                ["value_text"] = "100"
            }
        };
    }

    private sealed record CxParsedVisualUpload(
        CxDashboardUploadVisualOptions Definition,
        string SourceSheetName,
        List<CxParsedStageRow> Rows);

    private sealed record CxParsedStageRow(
        int RowOrdinal,
        string RowJson,
        string RowHash);
}
