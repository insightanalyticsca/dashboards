using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ScottPlot;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    private sealed class ExecutiveMetricDto
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public decimal? Value { get; init; }
        public string Format { get; init; } = "number";
        public string Period { get; init; } = string.Empty;
        public decimal? Mom { get; init; }
        public decimal? Yoy { get; init; }
        public string MomLabel { get; init; } = "MoM";
        public string YoyLabel { get; init; } = "YoY";
        public string DeltaMode { get; init; } = "percent";
        public bool PositiveIsGood { get; init; } = true;
    }

    private sealed class ExecutiveSeriesDto
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "line";
        public string Axis { get; init; } = "left";
        public string Stack { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public bool Smooth { get; init; }
        public List<decimal?> Data { get; init; } = new();
    }

    private sealed class ExecutiveChartDto
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Kind { get; init; } = "line";
        public string Width { get; init; } = string.Empty;
        public string ValueFormat { get; init; } = "number";
        public string LeftAxisTitle { get; init; } = string.Empty;
        public string RightAxisTitle { get; init; } = string.Empty;
        public List<string> Categories { get; init; } = new();
        public List<ExecutiveSeriesDto> Series { get; init; } = new();
    }

    private sealed class ExecutiveColumnGroupDto
    {
        public string Label { get; init; } = string.Empty;
        public List<string> Columns { get; init; } = new();
    }

    private sealed class ExecutiveTableDto
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Width { get; init; } = string.Empty;
        public string Kind { get; init; } = "table";
        public List<string> Columns { get; init; } = new();
        public List<ExecutiveColumnGroupDto> ColumnGroups { get; init; } = new();
        public Dictionary<string, string> Formats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Dictionary<string, object?>> Rows { get; init; } = new();
    }

    private sealed class ExecutiveVersionPayload
    {
        public string Key { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Variant { get; init; } = string.Empty;
        public string AsOfLabel { get; init; } = string.Empty;
        public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
        public List<ExecutiveMetricDto> Metrics { get; init; } = new();
        public List<ExecutiveChartDto> Charts { get; init; } = new();
        public List<ExecutiveTableDto> Tables { get; init; } = new();
        public List<string> Notes { get; init; } = new();
    }

    private sealed class ExecutiveMonthPoint
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public string MonthName { get; init; } = string.Empty;
        public decimal TotalCustomers { get; init; }
        public decimal NewCustomers { get; init; }
        public decimal Bills { get; init; }
        public decimal TotalPercent { get; init; }
        public decimal NewPercent { get; init; }
        public DateTime Period => new(Year, Month, 1);
    }


    // E-Bill only: one SQL load per application process at a time, followed by
    // a short payload cache. A disconnected browser does not cancel the shared
    // SQL operation for every other waiting request.
    private static readonly object ExecutiveEbillCacheSync = new();
    private static ExecutiveVersionPayload? ExecutiveEbillCachedPayload;
    private static DateTimeOffset ExecutiveEbillCacheExpiresUtc = DateTimeOffset.MinValue;
    private static Task<ExecutiveVersionPayload>? ExecutiveEbillInFlightTask;

    private int ExecutiveEbillCacheSeconds()
    {
        var configured = _cfg["Dashboard:Executive:EbillCacheSeconds"];
        return int.TryParse(configured, out var seconds)
            ? Math.Clamp(seconds, 15, 300)
            : 60;
    }

    private int ExecutiveEbillLoadTimeoutSeconds()
    {
        var configured = _cfg["Dashboard:Executive:EbillLoadTimeoutSeconds"];
        return int.TryParse(configured, out var seconds)
            ? Math.Clamp(seconds, 60, 600)
            : 240;
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveEbillCachedAsync(
        CancellationToken cancellationToken)
    {
        Task<ExecutiveVersionPayload> sharedTask;
        ExecutiveVersionPayload? stalePayload;
        var now = DateTimeOffset.UtcNow;

        lock (ExecutiveEbillCacheSync)
        {
            if (ExecutiveEbillCachedPayload is not null &&
                ExecutiveEbillCacheExpiresUtc > now)
            {
                return ExecutiveEbillCachedPayload;
            }

            stalePayload = ExecutiveEbillCachedPayload;
            ExecutiveEbillInFlightTask ??= LoadAndCacheExecutiveEbillAsync();
            sharedTask = ExecutiveEbillInFlightTask;
        }

        try
        {
            // The browser may stop waiting, but the bounded shared load keeps
            // running for other callers and can populate the short cache.
            return await sharedTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (stalePayload is not null)
        {
            _log.LogWarning(
                ex,
                "E-Bill refresh failed; serving the last successful cached payload.");
            return stalePayload;
        }
    }

    private async Task<ExecutiveVersionPayload> LoadAndCacheExecutiveEbillAsync()
    {
        var timeoutSeconds = ExecutiveEbillLoadTimeoutSeconds();
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var payload = await LoadExecutiveEbillAsync(timeoutCts.Token);
            var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(
                ExecutiveEbillCacheSeconds());

            lock (ExecutiveEbillCacheSync)
            {
                ExecutiveEbillCachedPayload = payload;
                ExecutiveEbillCacheExpiresUtc = expiresUtc;
            }

            return payload;
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"E-Bill server load exceeded {timeoutSeconds} seconds.",
                ex);
        }
        finally
        {
            lock (ExecutiveEbillCacheSync)
            {
                ExecutiveEbillInFlightTask = null;
            }
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetExecutiveVersionData(
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await LoadExecutiveVersionAsync(version, cancellationToken);
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Json(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (TimeoutException ex)
        {
            _log.LogError(ex, "Executive version data timed out for {Version}.", version);
            return Problem(
                title: "Executive dashboard data timed out",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (SqlException ex)
        {
            _log.LogError(
                ex,
                "Executive SQL failed for {Version}. Number={Number}, State={State}, Class={Class}.",
                version,
                ex.Number,
                ex.State,
                ex.Class);
            return Problem(
                title: "Executive dashboard SQL failed",
                detail: $"SQL {ex.Number}: {ex.Message}",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Executive version data failed for {Version}.", version);
            return Problem(
                title: "Executive dashboard data failed",
                detail: ex.Message,
                statusCode: 500);
        }
    }

    [HttpGet]
    public Task<IActionResult> ExportEbillPerformance(
        string format = "xlsx",
        bool email = false,
        CancellationToken cancellationToken = default) =>
        ExportExecutiveVersionAsync("ebill", format, email, cancellationToken);

    [HttpGet]
    public Task<IActionResult> ExportArPortfolio(
        string format = "xlsx",
        bool email = false,
        CancellationToken cancellationToken = default) =>
        ExportExecutiveVersionAsync("ar", format, email, cancellationToken);

    [HttpGet]
    public Task<IActionResult> ExportDisconnectsBankruptcies(
        string format = "xlsx",
        bool email = false,
        CancellationToken cancellationToken = default) =>
        ExportExecutiveVersionAsync("disconnects", format, email, cancellationToken);

    [HttpGet]
    public Task<IActionResult> ExportFinalBillRecovery(
        string format = "xlsx",
        bool email = false,
        CancellationToken cancellationToken = default) =>
        ExportExecutiveVersionAsync("finalbill", format, email, cancellationToken);

    [HttpGet]
    public Task<IActionResult> ExportCustomerPaymentsExecutive(
        string format = "xlsx",
        bool email = false,
        CancellationToken cancellationToken = default) =>
        ExportExecutiveVersionAsync("payments", format, email, cancellationToken);

    private async Task<IActionResult> ExportExecutiveVersionAsync(
        string version,
        string format,
        bool email,
        CancellationToken cancellationToken)
    {
        if (!IsExecutiveExportAuthorized(Request))
        {
            return Unauthorized("Missing/invalid X-Job-Key.");
        }

        try
        {
            var payload = await LoadExecutiveVersionAsync(version, cancellationToken);
            var normalizedFormat = (format ?? "xlsx").Trim().ToLowerInvariant();
            byte[] bytes;
            string extension;
            string contentType;

            if (normalizedFormat is "png" or "image")
            {
                bytes = BuildExecutivePng(payload);
                extension = "png";
                contentType = "image/png";
            }
            else if (normalizedFormat is "xlsx" or "excel")
            {
                bytes = BuildExecutiveWorkbook(payload);
                extension = "xlsx";
                contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            }
            else
            {
                return BadRequest("format must be xlsx or png");
            }

            var fileName = $"{ExecutiveFileStem(payload.Key)}_{DateTime.Now:yyyy-MM-dd_HHmm}.{extension}";
            if (email)
            {
                await SendExecutiveExportEmailAsync(payload, bytes, fileName, contentType);
            }

            return File(bytes, contentType, fileName);
        }
        catch (Exception ex)
        {
            var traceId = HttpContext.TraceIdentifier;
            _log.LogError(
                ex,
                "Executive export failed. Version={Version}, Format={Format}, TraceId={TraceId}",
                version,
                format,
                traceId);
            return Problem(
                title: "Executive dashboard export failed",
                detail: $"{ex.GetBaseException().Message} TraceId={traceId}",
                statusCode: 500);
        }
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveVersionAsync(
        string version,
        CancellationToken cancellationToken)
    {
        return NormalizeExecutiveVersionKey(version) switch
        {
            "ebill" => await LoadExecutiveEbillCachedAsync(cancellationToken),
            "ar" => await LoadExecutiveArPortfolioAsync(cancellationToken),
            "disconnects" => await LoadExecutiveDisconnectsAsync(cancellationToken),
            "finalbill" => await LoadExecutiveFinalBillAsync(cancellationToken),
            "payments" => await LoadExecutivePaymentsAsync(cancellationToken),
            _ => throw new InvalidOperationException($"Unknown executive version: {version}")
        };
    }

    private static string NormalizeExecutiveVersionKey(string? version)
    {
        var key = (version ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "ebill" or "e-bill" or "ebill-performance" or "213" => "ebill",
            "ar" or "ar-portfolio" or "214" => "ar",
            "disconnects" or "disconnects-bankruptcies" or "215" => "disconnects",
            "finalbill" or "final-bill" or "final-bill-recovery" or "216" => "finalbill",
            "payments" or "customer-payments" or "217" => "payments",
            _ => key
        };
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveEbillAsync(CancellationToken cancellationToken)
    {
        var rule = ResolveCustomHtmlRuleByKey(MonthlyEbnotesPageKey)
            ?? throw new InvalidOperationException($"Template not found: {MonthlyEbnotesPageKey}");
        var notesSource = RequireMonthlyEbnotesNotesSource(rule);
        var billsSource = RequireMonthlyEbnotesBillsSource(rule);
        var connectionName = MonthlyEbnotesConnectionName(notesSource);
        var notesSql = CsrSourceSql(notesSource);
        var billsSql = CsrSourceSql(billsSource);

        var sql = $"""
            SET NOCOUNT ON;

            WITH notes AS
            (
                SELECT
                    TRY_CONVERT(int, n.[year]) AS [year],
                    TRY_CONVERT(int, n.[month]) AS [month],
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), n.[month_name]))), N''),
                        CASE
                            WHEN TRY_CONVERT(int, n.[year]) IS NOT NULL
                             AND TRY_CONVERT(int, n.[month]) BETWEEN 1 AND 12
                            THEN DATENAME(month, DATEFROMPARTS(TRY_CONVERT(int, n.[year]), TRY_CONVERT(int, n.[month]), 1))
                        END
                    ) AS [month_name],
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), n.[CategoryGroup]))), N''),
                        N'Other') AS [category_group],
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), n.[AccountID]))), N''),
                        CASE
                            WHEN n.[account_no] IS NULL THEN NULL
                            WHEN n.[occupant_code] IS NULL THEN CONVERT(nvarchar(100), n.[account_no])
                            ELSE CONCAT(CONVERT(nvarchar(100), n.[account_no]), N'-', CONVERT(nvarchar(50), n.[occupant_code]))
                        END
                    ) AS [account_id],
                    LOWER(LTRIM(RTRIM(COALESCE(CONVERT(nvarchar(100), n.[IsFirstEBill]), N'')))) AS [is_first]
                FROM {notesSql} AS n
            ),
            category_rows AS
            (
                SELECT
                    [year],
                    [month],
                    MAX([month_name]) AS [month_name],
                    [category_group],
                    COUNT(DISTINCT [account_id]) AS [category_total_customers],
                    COUNT(DISTINCT CASE
                        WHEN [is_first] IN (N'1', N'true', N'yes', N'y', N'new', N'new e-bill', N'first', N'firstebill', N'first ebill')
                        THEN [account_id]
                    END) AS [category_new_customers]
                FROM notes
                WHERE [year] IS NOT NULL
                  AND [month] BETWEEN 1 AND 12
                  AND [account_id] IS NOT NULL
                GROUP BY [year], [month], [category_group]
            ),
            monthly_rows AS
            (
                SELECT
                    [year],
                    [month],
                    MAX([month_name]) AS [month_name],
                    COUNT(DISTINCT [account_id]) AS [total_customers],
                    COUNT(DISTINCT CASE
                        WHEN [is_first] IN (N'1', N'true', N'yes', N'y', N'new', N'new e-bill', N'first', N'firstebill', N'first ebill')
                        THEN [account_id]
                    END) AS [new_customers]
                FROM notes
                WHERE [year] IS NOT NULL
                  AND [month] BETWEEN 1 AND 12
                  AND [account_id] IS NOT NULL
                GROUP BY [year], [month]
            ),
            monthly_bills AS
            (
                SELECT
                    TRY_CONVERT(int, b.[gl_year]) AS [year],
                    TRY_CONVERT(int, b.[gl_month]) AS [month],
                    SUM(TRY_CONVERT(decimal(38, 6), b.[bills])) AS [bills]
                FROM {billsSql} AS b
                GROUP BY TRY_CONVERT(int, b.[gl_year]), TRY_CONVERT(int, b.[gl_month])
            )
            SELECT
                c.[year],
                c.[month],
                c.[month_name],
                c.[category_group],
                CONVERT(decimal(38, 6), c.[category_total_customers]) AS [category_total_customers],
                CONVERT(decimal(38, 6), c.[category_new_customers]) AS [category_new_customers],
                CONVERT(decimal(38, 6), m.[total_customers]) AS [total_customers],
                CONVERT(decimal(38, 6), m.[new_customers]) AS [new_customers],
                COALESCE(b.[bills], 0) AS [bills],
                CONVERT(decimal(18, 4), CASE WHEN COALESCE(b.[bills], 0) = 0 THEN NULL ELSE 100.0 * m.[total_customers] / b.[bills] END) AS [total_percent],
                CONVERT(decimal(18, 4), CASE WHEN COALESCE(b.[bills], 0) = 0 THEN NULL ELSE 100.0 * m.[new_customers] / b.[bills] END) AS [new_percent]
            FROM category_rows AS c
            INNER JOIN monthly_rows AS m
              ON m.[year] = c.[year]
             AND m.[month] = c.[month]
            LEFT JOIN monthly_bills AS b
              ON b.[year] = c.[year]
             AND b.[month] = c.[month]
            ORDER BY c.[year], c.[month], c.[category_group];
            """;

        var loadTimer = Stopwatch.StartNew();
        await using var connection = new SqlConnection(ConnStr(connectionName));

        _log.LogInformation(
            "E-Bill SQL connection opening. Connection={ConnectionName}.",
            connectionName);
        await connection.OpenAsync(cancellationToken);
        _log.LogInformation(
            "E-Bill SQL connection opened in {ElapsedMs} ms. Connection={ConnectionName}.",
            loadTimer.ElapsedMilliseconds,
            connectionName);

        var queryTimer = Stopwatch.StartNew();
        var rows = await ReadCsrRowsAsync(
            connection,
            sql,
            Array.Empty<SqlParameter>(),
            cancellationToken);
        _log.LogInformation(
            "E-Bill SQL query returned {RowCount} rows in {QueryMs} ms; total load {TotalMs} ms.",
            rows.Count,
            queryTimer.ElapsedMilliseconds,
            loadTimer.ElapsedMilliseconds);

        var grouped = rows
            .Where(row => ExecInt(row, "year") > 0 && ExecInt(row, "month") is >= 1 and <= 12)
            .GroupBy(row => new { Year = ExecInt(row, "year"), Month = ExecInt(row, "month") })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .ToList();

        if (grouped.Count == 0)
            throw new InvalidOperationException("The E-Bill sources returned no monthly rows.");

        // Total customer counts are monthly snapshots. For the executive
        // "New" cards and chart, use the movement visible in the rolling
        // total table so all E-Bill visuals reconcile to the same numbers:
        // current month total customers minus previous month total customers.
        var rawPoints = grouped.Select(group =>
        {
            var first = group.First();
            return new ExecutiveMonthPoint
            {
                Year = group.Key.Year,
                Month = group.Key.Month,
                MonthName = ExecString(first, "month_name"),
                TotalCustomers = ExecDecimal(first, "total_customers") ?? 0m,
                NewCustomers = ExecDecimal(first, "new_customers") ?? 0m,
                Bills = ExecDecimal(first, "bills") ?? 0m,
                TotalPercent = ExecDecimal(first, "total_percent") ?? 0m,
                NewPercent = ExecDecimal(first, "new_percent") ?? 0m
            };
        }).ToList();

        var rawByPeriod = rawPoints.ToDictionary(
            point => point.Period,
            point => point);

        var points = rawPoints.Select(point =>
        {
            rawByPeriod.TryGetValue(
                point.Period.AddMonths(-1),
                out var previousPoint);

            var netNewCustomers = previousPoint is null
                ? 0m
                : point.TotalCustomers - previousPoint.TotalCustomers;

            return new ExecutiveMonthPoint
            {
                Year = point.Year,
                Month = point.Month,
                MonthName = point.MonthName,
                TotalCustomers = point.TotalCustomers,
                NewCustomers = netNewCustomers,
                Bills = point.Bills,
                TotalPercent = point.TotalPercent,
                NewPercent = ExecRatio(netNewCustomers, point.Bills)
            };
        }).ToList();

        var categories = rows
            .Select(row => ExecString(row, "category_group"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Equals("Residential", StringComparison.OrdinalIgnoreCase) ? 0
                : value.Contains("Large", StringComparison.OrdinalIgnoreCase) ? 1
                : value.Contains("Small", StringComparison.OrdinalIgnoreCase) ? 2
                : value.Equals("Other", StringComparison.OrdinalIgnoreCase) ? 3
                : 4)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lastCompletedEbillMonth = new DateTime(
                DateTime.Today.Year,
                DateTime.Today.Month,
                1)
            .AddMonths(-1);

        var current = points
            .Where(point => point.Period <= lastCompletedEbillMonth)
            .OrderBy(point => point.Period)
            .LastOrDefault()
            ?? points[^1];

        var previous = points.LastOrDefault(
            point => point.Period == current.Period.AddMonths(-1));

        var priorYear = points.LastOrDefault(
            point => point.Period == current.Period.AddYears(-1));

        var rolling = points
            .Where(point => point.Period <= current.Period)
            .OrderBy(point => point.Period)
            .TakeLast(13)
            .ToList();

        var rollingPeriods = rolling
            .Select(point => point.Period)
            .ToList();

        var currentMonth = current.Month;
        var currentYear = current.Year;
        var previousYear = currentYear - 1;

        decimal Sum(Func<ExecutiveMonthPoint, decimal> selector, int year, int maxMonth) =>
            points.Where(point => point.Year == year && point.Month <= maxMonth).Sum(selector);

        var previousTotalFull = Sum(point => point.TotalCustomers, previousYear, 12);
        var previousTotalYtd = Sum(point => point.TotalCustomers, previousYear, currentMonth);
        var currentTotalYtd = Sum(point => point.TotalCustomers, currentYear, currentMonth);
        var previousNewFull = Sum(point => point.NewCustomers, previousYear, 12);
        var previousNewYtd = Sum(point => point.NewCustomers, previousYear, currentMonth);
        var currentNewYtd = Sum(point => point.NewCustomers, currentYear, currentMonth);
        var previousBillsFull = Sum(point => point.Bills, previousYear, 12);
        var previousBillsYtd = Sum(point => point.Bills, previousYear, currentMonth);
        var currentBillsYtd = Sum(point => point.Bills, currentYear, currentMonth);

        decimal CategoryValue(DateTime period, string category, string field) => rows
            .Where(row => ExecInt(row, "year") == period.Year &&
                          ExecInt(row, "month") == period.Month &&
                          string.Equals(ExecString(row, "category_group"), category, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, field) ?? 0m);

        decimal CategoryNetNew(DateTime period, string category) =>
            CategoryValue(period, category, "category_total_customers") -
            CategoryValue(period.AddMonths(-1), category, "category_total_customers");

        var hierarchyRows = new List<Dictionary<string, object?>>();
        foreach (var yearGroup in rollingPeriods.GroupBy(period => period.Year).OrderBy(group => group.Key))
        {
            hierarchyRows.Add(ExecRow(
                ("Year / Month", yearGroup.Key.ToString(CultureInfo.InvariantCulture)),
                ("__rowType", "group"),
                ("__label", yearGroup.Key.ToString(CultureInfo.InvariantCulture))));

            foreach (var period in yearGroup.OrderBy(value => value))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Year / Month"] = period.ToString("MMM", CultureInfo.InvariantCulture),
                    ["Total"] = points.First(point => point.Period == period).TotalCustomers,
                    ["__indent"] = 1
                };
                foreach (var category in categories)
                    row[category] = CategoryValue(period, category, "category_total_customers");
                hierarchyRows.Add(row);
            }
        }

        var ytdRows = new List<Dictionary<string, object?>>
        {
            ExecRow(
                ("Metric", "Total E-Bill Customers"),
                ($"{previousYear} Total", previousTotalFull),
                ($"{previousYear} YTD", previousTotalYtd),
                ($"{currentYear} YTD", currentTotalYtd),
                ("YTD Compare", ExecPercentChange(currentTotalYtd, previousTotalYtd)),
                ("__formats", ExecFormats(
                    ($"{previousYear} Total", "number"),
                    ($"{previousYear} YTD", "number"),
                    ($"{currentYear} YTD", "number"),
                    ("YTD Compare", "percent")))),
            ExecRow(
                ("Metric", "New Customers"),
                ($"{previousYear} Total", previousNewFull),
                ($"{previousYear} YTD", previousNewYtd),
                ($"{currentYear} YTD", currentNewYtd),
                ("YTD Compare", ExecPercentChange(currentNewYtd, previousNewYtd)),
                ("__formats", ExecFormats(
                    ($"{previousYear} Total", "number"),
                    ($"{previousYear} YTD", "number"),
                    ($"{currentYear} YTD", "number"),
                    ("YTD Compare", "percent")))),
            ExecRow(
                ("Metric", "Total E-Bill %"),
                ($"{previousYear} Total", ExecRatio(previousTotalFull, previousBillsFull)),
                ($"{previousYear} YTD", ExecRatio(previousTotalYtd, previousBillsYtd)),
                ($"{currentYear} YTD", ExecRatio(currentTotalYtd, currentBillsYtd)),
                ("YTD Compare", ExecPointChange(ExecRatio(currentTotalYtd, currentBillsYtd), ExecRatio(previousTotalYtd, previousBillsYtd))),
                ("__formats", ExecFormats(
                    ($"{previousYear} Total", "percent2"),
                    ($"{previousYear} YTD", "percent2"),
                    ($"{currentYear} YTD", "percent2"),
                    ("YTD Compare", "percent2")))),
            ExecRow(
                ("Metric", "New E-Bill %"),
                ($"{previousYear} Total", ExecRatio(previousNewFull, previousBillsFull)),
                ($"{previousYear} YTD", ExecRatio(previousNewYtd, previousBillsYtd)),
                ($"{currentYear} YTD", ExecRatio(currentNewYtd, currentBillsYtd)),
                ("YTD Compare", ExecPointChange(ExecRatio(currentNewYtd, currentBillsYtd), ExecRatio(previousNewYtd, previousBillsYtd))),
                ("__formats", ExecFormats(
                    ($"{previousYear} Total", "percent2"),
                    ($"{previousYear} YTD", "percent2"),
                    ($"{currentYear} YTD", "percent2"),
                    ("YTD Compare", "percent2"))))
        };

        var newChartSeries = categories
            .Select(category => ExecSeries(
                category,
                "stackedBar",
                rollingPeriods.Select(period => (decimal?)CategoryNetNew(period, category)),
                stack: "new-ebill"))
            .ToList();
        newChartSeries.Add(ExecSeries(
            "New E-Bill %",
            "line",
            rolling.Select(point => (decimal?)point.NewPercent),
            axis: "right"));

        return new ExecutiveVersionPayload
        {
            Key = "ebill",
            Title = "E-Bill Performance",
            Variant = "violet",
            AsOfLabel = $"Through {current.Period:MMMM yyyy}",
            Metrics = new List<ExecutiveMetricDto>
            {
                ExecMetric("total", "Total E-Bill Customers", current.TotalCustomers, "number", current.Period, previous?.TotalCustomers, priorYear?.TotalCustomers),
                ExecMetric("new", "New E-Bill Customers", current.NewCustomers, "number", current.Period, previous?.NewCustomers, priorYear?.NewCustomers),
                ExecMetric("total-pct", "Monthly E-Bill % (Total)", current.TotalPercent, "percent2", current.Period, previous?.TotalPercent, priorYear?.TotalPercent, "points"),
                ExecMetric("new-pct", "Monthly E-Bill % (New)", current.NewPercent, "percent2", current.Period, previous?.NewPercent, priorYear?.NewPercent, "points")
            },
            Charts = new List<ExecutiveChartDto>
            {
                new()
                {
                    Id = "ebill-new",
                    Title = "New Monthly E-Bill Customers — Rolling 13 Months",
                    Kind = "combo",
                    Width = "wide",
                    LeftAxisTitle = "New Customers",
                    RightAxisTitle = "New E-Bill %",
                    ValueFormat = "number",
                    Categories = rollingPeriods.Select(period => period.ToString("MMM yy", CultureInfo.InvariantCulture)).ToList(),
                    Series = newChartSeries
                }
            },
            Tables = new List<ExecutiveTableDto>
            {
                new()
                {
                    Id = "ebill-total-matrix",
                    Title = "Total E-Bill Customers — Rolling 13 Months",
                    Width = "wide",
                    Kind = "hierarchy",
                    Columns = new[] { "Year / Month" }.Concat(categories).Concat(new[] { "Total" }).ToList(),
                    Formats = categories.Concat(new[] { "Total" }).ToDictionary(column => column, _ => "number", StringComparer.OrdinalIgnoreCase),
                    Rows = hierarchyRows
                },
                new()
                {
                    Id = "ebill-ytd",
                    Title = "Year-to-Date Comparison",
                    Width = "wide",
                    Kind = "matrix",
                    Columns = new List<string> { "Metric", $"{previousYear} Total", $"{previousYear} YTD", $"{currentYear} YTD", "YTD Compare" },
                    Formats = ExecFormats(("YTD Compare", "percent")),
                    Rows = ytdRows
                }
            }
        };
    }

    private async Task<ExecutiveVersionPayload> LoadExecutivePaymentsAsync(CancellationToken cancellationToken)
    {
        var pageRule = ResolveCustomHtmlRuleByKey(CustomerPaymentsMonthlyPageKey)
            ?? throw new InvalidOperationException($"Template not found: {CustomerPaymentsMonthlyPageKey}");
        var source = RequireCustomerPaymentsSource(pageRule);
        var sourceSql = CsrSourceSql(source);
        var connectionName = CustomerPaymentsConnectionName(source);

        var sql = $"""
            WITH normalized AS
            (
                SELECT
                    COALESCE(
                        TRY_CONVERT(int, c.[Year]),
                        DATEPART(year, TRY_CONVERT(datetime2, c.[TRANS_DATE]))) AS [year],
                    COALESCE(
                        TRY_CONVERT(int, c.[month]),
                        DATEPART(month, TRY_CONVERT(datetime2, c.[TRANS_DATE]))) AS [month],
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(400), c.[DESCRIPTION]))), N''),
                        N'Other') AS [payment_type],
                    COALESCE(TRY_CONVERT(decimal(38, 6), c.[TRANS_AMT]), 0) AS [amount],
                    c.[SEQUENCE_] AS [sequence]
                FROM {sourceSql} AS c
            )
            SELECT
                [year],
                [month],
                DATENAME(month, DATEFROMPARTS([year], [month], 1)) AS [month_name],
                [payment_type],
                SUM([amount]) AS [amount],
                COUNT_BIG([sequence]) AS [transactions]
            FROM normalized
            WHERE [year] IS NOT NULL
              AND [month] BETWEEN 1 AND 12
              AND DATEFROMPARTS([year], [month], 1) < @currentMonthStart
            GROUP BY [year], [month], [payment_type]
            ORDER BY [year], [month], [payment_type];
            """;

        var currentMonthStart = new DateTime(
            DateTime.Today.Year,
            DateTime.Today.Month,
            1);

        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var rows = await ReadCsrRowsAsync(
            connection,
            sql,
            new[]
            {
                new SqlParameter("@currentMonthStart", SqlDbType.Date)
                {
                    Value = currentMonthStart
                }
            },
            cancellationToken);

        var validRows = rows
            .Where(row => ExecInt(row, "year") > 0 && ExecInt(row, "month") is >= 1 and <= 12)
            .ToList();
        if (validRows.Count == 0)
            throw new InvalidOperationException("Customer Payments returned no monthly data.");

        var periods = validRows
            .Select(row => new DateTime(ExecInt(row, "year"), ExecInt(row, "month"), 1))
            .Distinct()
            .OrderBy(date => date)
            .ToList();
        var lastCompletedPaymentsMonth = currentMonthStart.AddMonths(-1);

        var currentPeriod = periods
            .Where(period => period < currentMonthStart)
            .OrderBy(period => period)
            .LastOrDefault();

        if (currentPeriod == default)
        {
            throw new InvalidOperationException(
                $"Customer Payments returned no completed month before {currentMonthStart:MMMM yyyy}.");
        }

        if (currentPeriod > lastCompletedPaymentsMonth)
        {
            throw new InvalidOperationException(
                $"Customer Payments selected an invalid reporting month: {currentPeriod:MMMM yyyy}.");
        }

        _log.LogInformation(
            "Customer Payments executive reporting month selected. CurrentMonthStart={CurrentMonthStart:yyyy-MM-dd}, ReportingMonth={ReportingMonth:yyyy-MM-dd}",
            currentMonthStart,
            currentPeriod);

        var rollingPeriods = periods
            .Where(period => period <= currentPeriod)
            .OrderBy(period => period)
            .TakeLast(13)
            .ToList();

        var paymentTypes = validRows.Select(row => ExecString(row, "payment_type"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        decimal PeriodAmount(DateTime period) => validRows
            .Where(row => ExecInt(row, "year") == period.Year && ExecInt(row, "month") == period.Month)
            .Sum(row => ExecDecimal(row, "amount") ?? 0m);
        decimal PeriodTransactions(DateTime period) => validRows
            .Where(row => ExecInt(row, "year") == period.Year && ExecInt(row, "month") == period.Month)
            .Sum(row => ExecDecimal(row, "transactions") ?? 0m);
        decimal TypeAmount(DateTime period, string type) => validRows
            .Where(row => ExecInt(row, "year") == period.Year && ExecInt(row, "month") == period.Month &&
                          string.Equals(ExecString(row, "payment_type"), type, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, "amount") ?? 0m);

        var priorMonth = currentPeriod.AddMonths(-1);
        var priorYear = currentPeriod.AddYears(-1);

        var paymentsMomLabel = currentPeriod.ToString(
            "MMM yyyy 'MoM'",
            CultureInfo.InvariantCulture);

        var paymentsYoyLabel = currentPeriod.ToString(
            "MMM yyyy 'YoY'",
            CultureInfo.InvariantCulture);

        var currentAmount = PeriodAmount(currentPeriod);
        var currentTransactions = PeriodTransactions(currentPeriod);
        var previousAmount = PeriodAmount(priorMonth);
        var previousTransactions = PeriodTransactions(priorMonth);
        var priorYearAmount = PeriodAmount(priorYear);
        var priorYearTransactions = PeriodTransactions(priorYear);

        var series = paymentTypes
            .Select(type => ExecSeries(type, "stackedBar", rollingPeriods.Select(period => (decimal?)TypeAmount(period, type)), stack: "payments"))
            .ToList();
        series.Add(ExecSeries("Transactions", "line", rollingPeriods.Select(period => (decimal?)PeriodTransactions(period)), axis: "right"));

        var compareRows = new List<Dictionary<string, object?>>
        {
            ExecRow(
                ("Metric", "Payment Value"),
                ($"{priorYear:MMM yyyy}", priorYearAmount),
                ($"{priorMonth:MMM yyyy}", previousAmount),
                ($"{currentPeriod:MMM yyyy}", currentAmount),
                ("YoY Compare", ExecPercentChange(currentAmount, priorYearAmount)),
                ("MoM Compare", ExecPercentChange(currentAmount, previousAmount)),
                ("__formats", ExecFormats(
                    ($"{priorYear:MMM yyyy}", "currency"),
                    ($"{priorMonth:MMM yyyy}", "currency"),
                    ($"{currentPeriod:MMM yyyy}", "currency"),
                    ("YoY Compare", "percent"),
                    ("MoM Compare", "percent")))),
            ExecRow(
                ("Metric", "Transactions"),
                ($"{priorYear:MMM yyyy}", priorYearTransactions),
                ($"{priorMonth:MMM yyyy}", previousTransactions),
                ($"{currentPeriod:MMM yyyy}", currentTransactions),
                ("YoY Compare", ExecPercentChange(currentTransactions, priorYearTransactions)),
                ("MoM Compare", ExecPercentChange(currentTransactions, previousTransactions)),
                ("__formats", ExecFormats(
                    ($"{priorYear:MMM yyyy}", "number"),
                    ($"{priorMonth:MMM yyyy}", "number"),
                    ($"{currentPeriod:MMM yyyy}", "number"),
                    ("YoY Compare", "percent"),
                    ("MoM Compare", "percent"))))
        };

        return new ExecutiveVersionPayload
        {
            Key = "payments",
            Title = "Customer Payments",
            Variant = "blue",
            AsOfLabel = $"Through {currentPeriod:MMMM yyyy}",
            Metrics = new List<ExecutiveMetricDto>
            {
                new()
                {
                    Key = "payment-value",
                    Label = "Payment Value",
                    Value = currentAmount,
                    Format = "currency",
                    Period = currentPeriod.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Mom = ExecPercentChange(currentAmount, previousAmount),
                    Yoy = ExecPercentChange(currentAmount, priorYearAmount),
                    MomLabel = paymentsMomLabel,
                    YoyLabel = paymentsYoyLabel,
                    DeltaMode = "percent"
                },
                new()
                {
                    Key = "transactions",
                    Label = "Transactions",
                    Value = currentTransactions,
                    Format = "number",
                    Period = currentPeriod.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Mom = ExecPercentChange(currentTransactions, previousTransactions),
                    Yoy = ExecPercentChange(currentTransactions, priorYearTransactions),
                    MomLabel = paymentsMomLabel,
                    YoyLabel = paymentsYoyLabel,
                    DeltaMode = "percent"
                }
            },
            Charts = new List<ExecutiveChartDto>
            {
                new()
                {
                    Id = "payments-rolling",
                    Title = "Payment Value and Transactions — Rolling 13 Months",
                    Kind = "combo",
                    Width = "wide",
                    ValueFormat = "currency",
                    LeftAxisTitle = "Payment Value",
                    RightAxisTitle = "Transactions",
                    Categories = rollingPeriods.Select(period => period.ToString("MMM yy", CultureInfo.InvariantCulture)).ToList(),
                    Series = series
                }
            },
            Tables = new List<ExecutiveTableDto>
            {
                new()
                {
                    Id = "payments-compare",
                    Title = "Period Comparison",
                    Width = "wide",
                    Columns = new List<string> { "Metric", $"{priorYear:MMM yyyy}", $"{priorMonth:MMM yyyy}", $"{currentPeriod:MMM yyyy}", "YoY Compare", "MoM Compare" },
                    Formats = ExecFormats(
                        ("YoY Compare", "percent"),
                        ("MoM Compare", "percent")),
                    Rows = compareRows
                }
            }
        };
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveFinalBillAsync(CancellationToken cancellationToken)
    {
        var pageRule = ResolveCustomHtmlRuleByKey("csr_collection-report")
            ?? throw new InvalidOperationException("Template not found: csr_collection-report");
        var source = pageRule.Sources.FirstOrDefault()
            ?? throw new InvalidOperationException("Collection Report has no source.");
        var sourceSql = CsrSourceSql(source);
        var connectionName = string.IsNullOrWhiteSpace(source.ConnectionName)
            ? (_cfg["Dashboard:CsrPbipImport:SourceConnectionName"] ?? "csr_pbip_source")
            : source.ConnectionName;

        var sql = $"""
            SELECT
                TRY_CONVERT(date, c.[DateIn]) AS [date_in],
                CASE
                    WHEN UPPER(CONVERT(nvarchar(200), c.[Category Description])) LIKE N'%COMM%' THEN N'Commercial'
                    WHEN UPPER(CONVERT(nvarchar(200), c.[Category Description])) LIKE N'%RES%' THEN N'Residential'
                    ELSE COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), c.[Category Description]))), N''), N'Other')
                END AS [customer_type],
                COUNT(DISTINCT CONVERT(nvarchar(200), c.[AccountNumber])) AS [accounts],
                SUM(TRY_CONVERT(decimal(38, 6), c.[CurrentBalance])) AS [balance],
                SUM(TRY_CONVERT(decimal(38, 6), c.[Post Paid])) AS [post_paid]
            FROM {sourceSql} AS c
            WHERE TRY_CONVERT(date, c.[DateIn]) IS NOT NULL
              AND TRY_CONVERT(date, c.[DateIn]) < @currentMonthStart
              AND (
                    UPPER(LTRIM(RTRIM(COALESCE(CONVERT(nvarchar(100), c.[utility_type]), N'')))) = N'E'
                 OR UPPER(COALESCE(CONVERT(nvarchar(100), c.[utility_type]), N'')) LIKE N'%ELECTRIC%'
              )
            GROUP BY
                TRY_CONVERT(date, c.[DateIn]),
                CASE
                    WHEN UPPER(CONVERT(nvarchar(200), c.[Category Description])) LIKE N'%COMM%' THEN N'Commercial'
                    WHEN UPPER(CONVERT(nvarchar(200), c.[Category Description])) LIKE N'%RES%' THEN N'Residential'
                    ELSE COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), c.[Category Description]))), N''), N'Other')
                END
            ORDER BY [date_in], [customer_type];
            """;

        var currentMonthStart = new DateTime(
            DateTime.Today.Year,
            DateTime.Today.Month,
            1);

        var reportingMonth = currentMonthStart.AddMonths(-1);
        var reportingMonthStart = new DateTime(
            reportingMonth.Year,
            reportingMonth.Month,
            1);

        var priorMonthStart = reportingMonthStart.AddMonths(-1);

        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var rows = await ReadCsrRowsAsync(
            connection,
            sql,
            new[]
            {
                new SqlParameter("@currentMonthStart", SqlDbType.Date)
                {
                    Value = currentMonthStart
                }
            },
            cancellationToken);

        var dates = rows
            .Select(row => ExecDate(row, "date_in"))
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Date)
            .ToList();

        if (dates.Count == 0)
            throw new InvalidOperationException("Final Bill Collections returned no completed-month dated rows.");

        var currentYear = reportingMonth.Year;
        var previousYear = currentYear - 1;

        var latestDate = dates
            .Where(date => date.Year == currentYear && date < currentMonthStart)
            .DefaultIfEmpty(reportingMonth)
            .Max();

        var currentRows = rows
            .Where(row =>
            {
                var date = ExecDate(row, "date_in");
                return date.HasValue &&
                       date.Value.Year == currentYear &&
                       date.Value.Date < currentMonthStart;
            })
            .ToList();

        var priorMonthYtdRows = rows
            .Where(row =>
            {
                var date = ExecDate(row, "date_in");
                return date.HasValue &&
                       date.Value.Year == currentYear &&
                       date.Value.Date < reportingMonthStart;
            })
            .ToList();

        var previousRows = rows
            .Where(row => ExecDate(row, "date_in")?.Year == previousYear)
            .ToList();

        if (currentRows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Final Bill Collections returned no current-year data through {reportingMonth:MMMM yyyy}.");
        }

        _log.LogInformation(
            "Final Bill executive reporting month selected. CurrentMonthStart={CurrentMonthStart:yyyy-MM-dd}, ReportingMonth={ReportingMonth:yyyy-MM-dd}",
            currentMonthStart,
            reportingMonthStart);

        var finalBillMomLabel = reportingMonth.ToString(
            "MMM yyyy 'MoM'",
            CultureInfo.InvariantCulture);

        var finalBillYoyLabel = reportingMonth.ToString(
            "MMM yyyy 'YoY'",
            CultureInfo.InvariantCulture);

        var finalBillPeriodLabel = reportingMonth.ToString(
            "MMM yyyy",
            CultureInfo.InvariantCulture);

        var customerTypes = new[] { "Commercial", "Residential" };

        decimal Sum(IEnumerable<Dictionary<string, object?>> sourceRows, string customerType, string field) => sourceRows
            .Where(row => string.Equals(ExecString(row, "customer_type"), customerType, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, field) ?? 0m);

        decimal DateSum(DateTime date, string customerType, string field) => currentRows
            .Where(row => ExecDate(row, "date_in")?.Date == date.Date &&
                          string.Equals(ExecString(row, "customer_type"), customerType, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, field) ?? 0m);

        static decimal? PaidRatio(decimal balance, decimal postPaid) => balance == 0m ? null : 100m * (-postPaid) / balance;

        Dictionary<string, object?> MatrixRow(string label, Func<string, string, decimal> value, string? rowType = null)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Date In"] = label
            };
            foreach (var type in customerTypes)
            {
                var accounts = value(type, "accounts");
                var balance = value(type, "balance");
                var postPaid = value(type, "post_paid");
                row[$"{type} Accts"] = accounts;
                row[$"{type} Balance"] = balance;
                row[$"{type} Post Paid"] = postPaid;
                row[$"{type} Paid Ratio"] = PaidRatio(balance, postPaid);
            }
            var totalAccounts = customerTypes.Sum(type => value(type, "accounts"));
            var totalBalance = customerTypes.Sum(type => value(type, "balance"));
            var totalPostPaid = customerTypes.Sum(type => value(type, "post_paid"));
            row["Total Accts"] = totalAccounts;
            row["Total Balance"] = totalBalance;
            row["Total Post Paid"] = totalPostPaid;
            row["Total Paid Ratio"] = PaidRatio(totalBalance, totalPostPaid);
            if (!string.IsNullOrWhiteSpace(rowType)) row["__rowType"] = rowType;
            return row;
        }

        var currentTableRows = currentRows
            .Select(row => ExecDate(row, "date_in"))
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Date)
            .Distinct()
            .OrderBy(date => date)
            .Select(date => MatrixRow(
                date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                (type, field) => DateSum(date, type, field)))
            .ToList();
        currentTableRows.Add(MatrixRow(
            "Total",
            (type, field) => Sum(currentRows, type, field),
            "total"));

        var previousTableRows = new List<Dictionary<string, object?>>
        {
            MatrixRow(
                "Total",
                (type, field) => Sum(previousRows, type, field),
                "total")
        };

        var currentAccounts = customerTypes.Sum(type => Sum(currentRows, type, "accounts"));
        var currentBalance = customerTypes.Sum(type => Sum(currentRows, type, "balance"));
        var currentPostPaid = customerTypes.Sum(type => Sum(currentRows, type, "post_paid"));
        var currentPaidRatio = PaidRatio(currentBalance, currentPostPaid);

        var priorMonthYtdAccounts = customerTypes.Sum(type => Sum(priorMonthYtdRows, type, "accounts"));
        var priorMonthYtdBalance = customerTypes.Sum(type => Sum(priorMonthYtdRows, type, "balance"));
        var priorMonthYtdPostPaid = customerTypes.Sum(type => Sum(priorMonthYtdRows, type, "post_paid"));
        var priorMonthYtdPaidRatio = PaidRatio(priorMonthYtdBalance, priorMonthYtdPostPaid);

        var previousAccounts = customerTypes.Sum(type => Sum(previousRows, type, "accounts"));
        var previousBalance = customerTypes.Sum(type => Sum(previousRows, type, "balance"));
        var previousPostPaid = customerTypes.Sum(type => Sum(previousRows, type, "post_paid"));
        var previousPaidRatio = PaidRatio(
            previousBalance,
            previousPostPaid);

        var columns = new List<string>
        {
            "Date In",
            "Commercial Accts", "Commercial Balance", "Commercial Post Paid", "Commercial Paid Ratio",
            "Residential Accts", "Residential Balance", "Residential Post Paid", "Residential Paid Ratio",
            "Total Accts", "Total Balance", "Total Post Paid", "Total Paid Ratio"
        };
        var formats = ExecFormats(
            ("Commercial Accts", "number"), ("Commercial Balance", "currency2"), ("Commercial Post Paid", "currency2"), ("Commercial Paid Ratio", "percent2"),
            ("Residential Accts", "number"), ("Residential Balance", "currency2"), ("Residential Post Paid", "currency2"), ("Residential Paid Ratio", "percent2"),
            ("Total Accts", "number"), ("Total Balance", "currency2"), ("Total Post Paid", "currency2"), ("Total Paid Ratio", "percent2"));
        var columnGroups = new List<ExecutiveColumnGroupDto>
        {
            new() { Label = "Commercial", Columns = new List<string> { "Commercial Accts", "Commercial Balance", "Commercial Post Paid", "Commercial Paid Ratio" } },
            new() { Label = "Residential", Columns = new List<string> { "Residential Accts", "Residential Balance", "Residential Post Paid", "Residential Paid Ratio" } },
            new() { Label = "Total", Columns = new List<string> { "Total Accts", "Total Balance", "Total Post Paid", "Total Paid Ratio" } }
        };

        return new ExecutiveVersionPayload
        {
            Key = "finalbill",
            Title = "Final Bill Collections Recovery — Electric",
            Variant = "indigo",
            AsOfLabel = $"Current year through {latestDate:MMMM d, yyyy} · LM {reportingMonth:MMM yyyy}",
            Metrics = new List<ExecutiveMetricDto>
            {
                new()
                {
                    Key = "accounts",
                    Label = "Current-Year Accounts",
                    Value = currentAccounts,
                    Format = "number",
                    Period = finalBillPeriodLabel,
                    Mom = ExecPercentChange(currentAccounts, priorMonthYtdAccounts),
                    Yoy = ExecPercentChange(currentAccounts, previousAccounts),
                    MomLabel = finalBillMomLabel,
                    YoyLabel = finalBillYoyLabel,
                    DeltaMode = "percent"
                },
                new()
                {
                    Key = "balance",
                    Label = "Current-Year Balance",
                    Value = currentBalance,
                    Format = "currency",
                    Period = finalBillPeriodLabel,
                    Mom = ExecPercentChange(currentBalance, priorMonthYtdBalance),
                    Yoy = ExecPercentChange(currentBalance, previousBalance),
                    MomLabel = finalBillMomLabel,
                    YoyLabel = finalBillYoyLabel,
                    DeltaMode = "percent"
                },
                new()
                {
                    Key = "postpaid",
                    Label = "Current-Year Post Paid",
                    Value = currentPostPaid,
                    Format = "currency",
                    Period = finalBillPeriodLabel,
                    Mom = ExecPercentChange(currentPostPaid, priorMonthYtdPostPaid),
                    Yoy = ExecPercentChange(currentPostPaid, previousPostPaid),
                    MomLabel = finalBillMomLabel,
                    YoyLabel = finalBillYoyLabel,
                    DeltaMode = "percent"
                },
                new()
                {
                    Key = "ratio",
                    Label = "Current-Year Paid Ratio",
                    Value = currentPaidRatio,
                    Format = "percent2",
                    Period = finalBillPeriodLabel,
                    Mom = currentPaidRatio.HasValue
                        ? ExecPointChange(currentPaidRatio.Value, priorMonthYtdPaidRatio)
                        : null,
                    Yoy = currentPaidRatio.HasValue
                        ? ExecPointChange(currentPaidRatio.Value, previousPaidRatio)
                        : null,
                    MomLabel = finalBillMomLabel,
                    YoyLabel = finalBillYoyLabel,
                    DeltaMode = "points"
                }
            },
            Charts = new List<ExecutiveChartDto>
            {
                new()
                {
                    Id = "finalbill-category",
                    Title = $"{currentYear} Accounts by Customer Type",
                    Kind = "pie",
                    Width = "third",
                    ValueFormat = "number",
                    Categories = customerTypes.ToList(),
                    Series = new List<ExecutiveSeriesDto>
                    {
                        ExecSeries("Accounts", "pie", customerTypes.Select(type => (decimal?)Sum(currentRows, type, "accounts")))
                    }
                }
            },
            Tables = new List<ExecutiveTableDto>
            {
                new()
                {
                    Id = "finalbill-current",
                    Title = $"{currentYear} — Date In Detail",
                    Width = "wide",
                    Kind = "matrix",
                    Columns = columns,
                    ColumnGroups = columnGroups,
                    Formats = formats,
                    Rows = currentTableRows
                },
                new()
                {
                    Id = "finalbill-previous",
                    Title = $"{previousYear} — All Dates Total",
                    Width = "wide",
                    Kind = "matrix",
                    Columns = columns,
                    ColumnGroups = columnGroups,
                    Formats = formats,
                    Rows = previousTableRows
                }
            }
        };
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveDisconnectsAsync(CancellationToken cancellationToken)
    {
        var connectionName = _cfg["Dashboard:CsrPbipImport:SourceConnectionName"] ?? "csr_pbip_source";

        const string disconnectSql = """
        SELECT
            CONVERT(nvarchar(100), d.[account_class]) AS [account_class],
            TRY_CONVERT(date, d.[AsOfEOM]) AS [as_of_eom],
            CONVERT(nvarchar(100), d.[PeriodLabel]) AS [period_label],
            TRY_CONVERT(decimal(38, 6), d.[Disconnected_m]) AS [disconnected_m],
            TRY_CONVERT(decimal(38, 6), d.[Closed_m]) AS [closed_m],
            TRY_CONVERT(decimal(38, 6), d.[Reconnected_m]) AS [reconnected_m],
            TRY_CONVERT(decimal(38, 6), d.[LicoRecon_m]) AS [lico_m],
            TRY_CONVERT(decimal(38, 6), d.[DisconnectedAmt_m]) AS [disconnected_amt_m],
            TRY_CONVERT(decimal(38, 6), d.[ClosedAmt_m]) AS [closed_amt_m],
            TRY_CONVERT(decimal(38, 6), d.[ReconnectedAmt_m]) AS [reconnected_amt_m],
            TRY_CONVERT(decimal(38, 6), d.[LicoReconAmt_m]) AS [lico_amt_m],
            TRY_CONVERT(decimal(38, 6), d.[Disconnected_y]) AS [disconnected_y],
            TRY_CONVERT(decimal(38, 6), d.[Closed_y]) AS [closed_y],
            TRY_CONVERT(decimal(38, 6), d.[Reconnected_y]) AS [reconnected_y],
            TRY_CONVERT(decimal(38, 6), d.[LicoRecon_y]) AS [lico_y],
            TRY_CONVERT(decimal(38, 6), d.[DisconnectedAmt_y]) AS [disconnected_amt_y],
            TRY_CONVERT(decimal(38, 6), d.[ClosedAmt_y]) AS [closed_amt_y],
            TRY_CONVERT(decimal(38, 6), d.[ReconnectedAmt_y]) AS [reconnected_amt_y],
            TRY_CONVERT(decimal(38, 6), d.[LicoReconAmt_y]) AS [lico_amt_y]
        FROM [dbo].[vw_disconnect_reconnect_stats_opt1_wide] AS d
        WHERE TRY_CONVERT(date, d.[AsOfEOM]) IS NOT NULL
        ORDER BY TRY_CONVERT(date, d.[AsOfEOM]), d.[account_class];
        """;

        await using var disconnectConnection = new SqlConnection(ConnStr(connectionName));
        await disconnectConnection.OpenAsync(cancellationToken);
        var disconnectRows = await ReadCsrRowsAsync(
            disconnectConnection,
            disconnectSql,
            Array.Empty<SqlParameter>(),
            cancellationToken);

        if (disconnectRows.Count == 0)
            throw new InvalidOperationException("Disconnect/Reconnect source returned no rows.");

        var latestEom = disconnectRows
            .Select(row => ExecDate(row, "as_of_eom"))
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Date)
            .Max();

        var latestRows = disconnectRows
            .Where(row => ExecDate(row, "as_of_eom")?.Date == latestEom)
            .ToList();

        var previousYear = latestEom.Year - 1;
        var priorYearDate = disconnectRows
            .Select(row => ExecDate(row, "as_of_eom"))
            .Where(date => date.HasValue && date.Value.Year == previousYear)
            .Select(date => date!.Value.Date)
            .DefaultIfEmpty()
            .Max();
        var priorYearRows = priorYearDate == default
            ? new List<Dictionary<string, object?>>()
            : disconnectRows
                .Where(row => ExecDate(row, "as_of_eom")?.Date == priorYearDate)
                .ToList();

        static bool IsAllDisconnectRow(Dictionary<string, object?> row) =>
            string.Equals(
                ExecString(row, "account_class").Trim(),
                "All",
                StringComparison.OrdinalIgnoreCase);

        decimal DisconnectTotal(
            IReadOnlyCollection<Dictionary<string, object?>> sourceRows,
            string field)
        {
            var allRow = sourceRows.FirstOrDefault(IsAllDisconnectRow);
            if (allRow != null)
                return ExecDecimal(allRow, field) ?? 0m;

            return sourceRows
                .Where(row => !IsAllDisconnectRow(row))
                .Sum(row => ExecDecimal(row, field) ?? 0m);
        }

        var comparisonMonthStart = new DateTime(latestEom.Year, latestEom.Month, 1);
        var previousMonthStart = comparisonMonthStart.AddMonths(-1);
        var priorYearMonthStart = comparisonMonthStart.AddYears(-1);
        var priorYearMonthEnd = priorYearMonthStart.AddMonths(1);
        var previousYearStart = new DateTime(previousYear, 1, 1);
        var currentYearStart = previousYearStart.AddYears(1);

        const string disconnectHistorySql = """
        WITH source_rows AS
        (
            SELECT
                NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(255), r.[Account]))), N'') AS [account],
                TRY_CONVERT(date, r.[dc_date]) AS [dc_date],
                TRY_CONVERT(date, r.[rc_date]) AS [rc_date],
                TRY_CONVERT(date, r.[final_pending_date]) AS [closed_date],
                TRY_CONVERT(decimal(38, 6), r.[dc_$_value]) AS [amount],
                CONVERT(nvarchar(100), r.[LowIncomeStatus]) AS [low_income_status]
            FROM dbo.ns_disconnect_report() AS r
        )
        SELECT
            COUNT(DISTINCT CASE
                WHEN [dc_date] >= @previousMonthStart
                 AND [dc_date] <  @comparisonMonthStart
                THEN [account]
            END) AS [disconnected_previous_month],
            COUNT(DISTINCT CASE
                WHEN [dc_date] >= @priorYearMonthStart
                 AND [dc_date] <  @priorYearMonthEnd
                THEN [account]
            END) AS [disconnected_prior_year_month],
            COUNT(DISTINCT CASE
                WHEN [rc_date] >= @previousMonthStart
                 AND [rc_date] <  @comparisonMonthStart
                THEN [account]
            END) AS [reconnected_previous_month],
            COUNT(DISTINCT CASE
                WHEN [rc_date] >= @priorYearMonthStart
                 AND [rc_date] <  @priorYearMonthEnd
                THEN [account]
            END) AS [reconnected_prior_year_month],

            COUNT(DISTINCT CASE
                WHEN [dc_date] >= @previousYearStart
                 AND [dc_date] <  @currentYearStart
                THEN [account]
            END) AS [disconnected_y],
            COUNT(DISTINCT CASE
                WHEN [closed_date] >= @previousYearStart
                 AND [closed_date] <  @currentYearStart
                THEN [account]
            END) AS [closed_y],
            COUNT(DISTINCT CASE
                WHEN [rc_date] >= @previousYearStart
                 AND [rc_date] <  @currentYearStart
                THEN [account]
            END) AS [reconnected_y],
            COUNT(DISTINCT CASE
                WHEN [rc_date] >= @previousYearStart
                 AND [rc_date] <  @currentYearStart
                 AND UPPER(LTRIM(RTRIM(ISNULL([low_income_status], N'')))) = N'LOW INCOME'
                THEN [account]
            END) AS [lico_y],

            SUM(CASE
                WHEN [dc_date] >= @previousYearStart
                 AND [dc_date] <  @currentYearStart
                THEN ISNULL([amount], 0)
                ELSE 0
            END) AS [disconnected_amt_y],
            SUM(CASE
                WHEN [closed_date] >= @previousYearStart
                 AND [closed_date] <  @currentYearStart
                THEN ISNULL([amount], 0)
                ELSE 0
            END) AS [closed_amt_y],
            SUM(CASE
                WHEN [rc_date] >= @previousYearStart
                 AND [rc_date] <  @currentYearStart
                THEN ISNULL([amount], 0)
                ELSE 0
            END) AS [reconnected_amt_y],
            SUM(CASE
                WHEN [rc_date] >= @previousYearStart
                 AND [rc_date] <  @currentYearStart
                 AND UPPER(LTRIM(RTRIM(ISNULL([low_income_status], N'')))) = N'LOW INCOME'
                THEN ISNULL([amount], 0)
                ELSE 0
            END) AS [lico_amt_y]
        FROM source_rows;
        """;

        var disconnectHistoryRows = await ReadCsrRowsAsync(
            disconnectConnection,
            disconnectHistorySql,
            new[]
            {
            new SqlParameter("@comparisonMonthStart", SqlDbType.Date) { Value = comparisonMonthStart },
            new SqlParameter("@previousMonthStart", SqlDbType.Date) { Value = previousMonthStart },
            new SqlParameter("@priorYearMonthStart", SqlDbType.Date) { Value = priorYearMonthStart },
            new SqlParameter("@priorYearMonthEnd", SqlDbType.Date) { Value = priorYearMonthEnd },
            new SqlParameter("@previousYearStart", SqlDbType.Date) { Value = previousYearStart },
            new SqlParameter("@currentYearStart", SqlDbType.Date) { Value = currentYearStart }
            },
            cancellationToken);

        var disconnectHistory = disconnectHistoryRows.FirstOrDefault()
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        decimal PriorYearDisconnectTotal(string field) =>
            priorYearRows.Count > 0
                ? DisconnectTotal(priorYearRows, field)
                : ExecDecimal(disconnectHistory, field) ?? 0m;

        var currentLabel = ExecString(latestRows.First(), "period_label");
        if (string.IsNullOrWhiteSpace(currentLabel))
            currentLabel = latestEom.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        var metricDefinitions = new[]
        {
        new { Label = "Disconnected Accounts", Month = "disconnected_m", Ytd = "disconnected_y", Format = "number" },
        new { Label = "Accounts Closed After Disconnect", Month = "closed_m", Ytd = "closed_y", Format = "number" },
        new { Label = "Reconnected Accounts", Month = "reconnected_m", Ytd = "reconnected_y", Format = "number" },
        new { Label = "Low Income Reconnects", Month = "lico_m", Ytd = "lico_y", Format = "number" },
        new { Label = "$ Disconnected Accounts", Month = "disconnected_amt_m", Ytd = "disconnected_amt_y", Format = "currency2" },
        new { Label = "$ Accounts Closed After Disconnect", Month = "closed_amt_m", Ytd = "closed_amt_y", Format = "currency2" },
        new { Label = "$ Reconnected Accounts", Month = "reconnected_amt_m", Ytd = "reconnected_amt_y", Format = "currency2" },
        new { Label = "$ Low Income Reconnects", Month = "lico_amt_m", Ytd = "lico_amt_y", Format = "currency2" }
    };

        var disconnectTableRows = metricDefinitions.Select(metric => ExecRow(
            ("Metric", metric.Label),
            (currentLabel, DisconnectTotal(latestRows, metric.Month)),
            ("YTD", DisconnectTotal(latestRows, metric.Ytd)),
            ($"{previousYear} Total", PriorYearDisconnectTotal(metric.Ytd)),
            ("__formats", ExecFormats(
                (currentLabel, metric.Format),
                ("YTD", metric.Format),
                ($"{previousYear} Total", metric.Format)))))
            .ToList();

        var disconnectClasses = new[] { "Residential", "Commercial" };
        decimal DisconnectYtdByClass(string customerClass) => latestRows
            .Where(row => string.Equals(ExecArClass(ExecString(row, "account_class")), customerClass, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, "disconnected_y") ?? 0m);

        var pageRule = ResolveCustomHtmlRuleByKey("csr_bankruptcies-report")
            ?? throw new InvalidOperationException("Template not found: csr_bankruptcies-report");
        var bankruptcySource = pageRule.Sources.FirstOrDefault()
            ?? throw new InvalidOperationException("Bankruptcies Report has no source.");
        var bankruptcySourceSql = CsrSourceSql(bankruptcySource);
        var bankruptcyConnectionName = string.IsNullOrWhiteSpace(bankruptcySource.ConnectionName)
            ? connectionName
            : bankruptcySource.ConnectionName;

        var bankruptcyQuery = $"""
        SELECT
            DATEFROMPARTS(
                COALESCE(TRY_CONVERT(int, b.[year]), YEAR(TRY_CONVERT(date, b.[date_in]))),
                MONTH(TRY_CONVERT(date, b.[date_in])),
                1) AS [period],
            COALESCE(TRY_CONVERT(int, b.[year]), YEAR(TRY_CONVERT(date, b.[date_in]))) AS [year],
            MONTH(TRY_CONVERT(date, b.[date_in])) AS [month],
            COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), b.[month_name]))), N''), DATENAME(month, TRY_CONVERT(date, b.[date_in]))) AS [month_name],
            CASE
                WHEN UPPER(CONVERT(nvarchar(200), b.[Category Description])) LIKE N'%COMM%' THEN N'Commercial'
                WHEN UPPER(CONVERT(nvarchar(200), b.[Category Description])) LIKE N'%RES%' THEN N'Residential'
                ELSE N'Other'
            END AS [customer_type],
            COUNT(DISTINCT CONVERT(nvarchar(255), b.[name])) AS [accounts],
            SUM(TRY_CONVERT(decimal(38, 6), b.[amount_in])) AS [amount]
        FROM {bankruptcySourceSql} AS b
        WHERE TRY_CONVERT(date, b.[date_in]) IS NOT NULL
        GROUP BY
            COALESCE(TRY_CONVERT(int, b.[year]), YEAR(TRY_CONVERT(date, b.[date_in]))),
            MONTH(TRY_CONVERT(date, b.[date_in])),
            COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), b.[month_name]))), N''), DATENAME(month, TRY_CONVERT(date, b.[date_in]))),
            CASE
                WHEN UPPER(CONVERT(nvarchar(200), b.[Category Description])) LIKE N'%COMM%' THEN N'Commercial'
                WHEN UPPER(CONVERT(nvarchar(200), b.[Category Description])) LIKE N'%RES%' THEN N'Residential'
                ELSE N'Other'
            END
        ORDER BY [period], [customer_type];
        """;

        await using var bankruptcyConnection = new SqlConnection(ConnStr(bankruptcyConnectionName));
        await bankruptcyConnection.OpenAsync(cancellationToken);
        var bankruptcyRows = await ReadCsrRowsAsync(
            bankruptcyConnection,
            bankruptcyQuery,
            Array.Empty<SqlParameter>(),
            cancellationToken);

        var bankruptcyPeriods = bankruptcyRows
            .Select(row => ExecDate(row, "period"))
            .Where(date => date.HasValue)
            .Select(date => new DateTime(date!.Value.Year, date.Value.Month, 1))
            .Distinct()
            .OrderBy(date => date)
            .TakeLast(13)
            .ToList();
        var bankruptcyTypes = new[] { "Commercial", "Residential" };

        decimal BankruptcyValue(DateTime period, string customerType, string field) => bankruptcyRows
            .Where(row => ExecDate(row, "period") is { } date &&
                          date.Year == period.Year && date.Month == period.Month &&
                          string.Equals(ExecString(row, "customer_type"), customerType, StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, field) ?? 0m);

        var bankruptcyMatrixRows = new List<Dictionary<string, object?>>();
        foreach (var yearGroup in bankruptcyPeriods.GroupBy(period => period.Year).OrderBy(group => group.Key))
        {
            var yearPeriods = yearGroup.OrderBy(period => period).ToList();
            var yearRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Year / Month"] = yearGroup.Key.ToString(CultureInfo.InvariantCulture),
                ["__rowType"] = "group-total"
            };
            foreach (var type in bankruptcyTypes)
            {
                yearRow[$"{type} Accounts"] = yearPeriods.Sum(period => BankruptcyValue(period, type, "accounts"));
                yearRow[$"{type} Amount In"] = yearPeriods.Sum(period => BankruptcyValue(period, type, "amount"));
            }
            yearRow["Total Accounts"] = bankruptcyTypes.Sum(type => yearPeriods.Sum(period => BankruptcyValue(period, type, "accounts")));
            yearRow["Total Amount In"] = bankruptcyTypes.Sum(type => yearPeriods.Sum(period => BankruptcyValue(period, type, "amount")));
            bankruptcyMatrixRows.Add(yearRow);

            foreach (var period in yearPeriods)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Year / Month"] = period.ToString("MMM", CultureInfo.InvariantCulture),
                    ["__indent"] = 1
                };
                foreach (var type in bankruptcyTypes)
                {
                    row[$"{type} Accounts"] = BankruptcyValue(period, type, "accounts");
                    row[$"{type} Amount In"] = BankruptcyValue(period, type, "amount");
                }
                row["Total Accounts"] = bankruptcyTypes.Sum(type => BankruptcyValue(period, type, "accounts"));
                row["Total Amount In"] = bankruptcyTypes.Sum(type => BankruptcyValue(period, type, "amount"));
                bankruptcyMatrixRows.Add(row);
            }
        }

        if (bankruptcyPeriods.Count > 0)
        {
            var grand = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Year / Month"] = "Total",
                ["__rowType"] = "total"
            };
            foreach (var type in bankruptcyTypes)
            {
                grand[$"{type} Accounts"] = bankruptcyPeriods.Sum(period => BankruptcyValue(period, type, "accounts"));
                grand[$"{type} Amount In"] = bankruptcyPeriods.Sum(period => BankruptcyValue(period, type, "amount"));
            }
            grand["Total Accounts"] = bankruptcyTypes.Sum(type => bankruptcyPeriods.Sum(period => BankruptcyValue(period, type, "accounts")));
            grand["Total Amount In"] = bankruptcyTypes.Sum(type => bankruptcyPeriods.Sum(period => BankruptcyValue(period, type, "amount")));
            bankruptcyMatrixRows.Add(grand);
        }

        var lastCompletedCalendarMonth = new DateTime(
                DateTime.Today.Year,
                DateTime.Today.Month,
                1)
            .AddMonths(-1);

        var bankruptcyCurrentPeriod = bankruptcyPeriods
            .Where(period => period <= lastCompletedCalendarMonth)
            .DefaultIfEmpty(
                bankruptcyPeriods.Count > 0
                    ? bankruptcyPeriods.Max()
                    : new DateTime(latestEom.Year, latestEom.Month, 1))
            .Max();
        var bankruptcyPreviousPeriod = bankruptcyCurrentPeriod.AddMonths(-1);
        var bankruptcyPriorYearPeriod = bankruptcyCurrentPeriod.AddYears(-1);
        var bankruptcyCurrentYear = bankruptcyCurrentPeriod.Year;

        decimal BankruptcyYtdThrough(DateTime throughPeriod, string type, string field) => bankruptcyRows
            .Where(row =>
                ExecDate(row, "period") is { } date &&
                date.Year == throughPeriod.Year &&
                date.Month <= throughPeriod.Month &&
                string.Equals(
                    ExecString(row, "customer_type"),
                    type,
                    StringComparison.OrdinalIgnoreCase))
            .Sum(row => ExecDecimal(row, field) ?? 0m);

        decimal BankruptcyYtd(string type, int year, string field) =>
            BankruptcyYtdThrough(
                new DateTime(year, bankruptcyCurrentPeriod.Month, 1),
                type,
                field);

        var bankruptcyCurrentAccounts = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyCurrentPeriod, type, "accounts"));
        var bankruptcyPreviousMonthAccounts = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyPreviousPeriod, type, "accounts"));
        var bankruptcyPriorYearAccounts = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyPriorYearPeriod, type, "accounts"));

        var bankruptcyCurrentAmount = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyCurrentPeriod, type, "amount"));
        var bankruptcyPreviousMonthAmount = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyPreviousPeriod, type, "amount"));
        var bankruptcyPriorYearAmount = bankruptcyTypes.Sum(type =>
            BankruptcyYtdThrough(bankruptcyPriorYearPeriod, type, "amount"));

        var bankruptcyMomLabel = bankruptcyCurrentPeriod.ToString(
            "MMM yyyy 'MoM'",
            CultureInfo.InvariantCulture);
        var bankruptcyYoyLabel = bankruptcyCurrentPeriod.ToString(
            "MMM yyyy 'YoY'",
            CultureInfo.InvariantCulture);
        var bankruptcyPeriodLabel = bankruptcyCurrentPeriod.ToString(
            "MMM yyyy 'YTD'",
            CultureInfo.InvariantCulture);

        var bankruptcyColumns = new List<string>
    {
        "Year / Month",
        "Commercial Accounts", "Commercial Amount In",
        "Residential Accounts", "Residential Amount In",
        "Total Accounts", "Total Amount In"
    };
        var bankruptcyGroups = new List<ExecutiveColumnGroupDto>
    {
        new() { Label = "Commercial", Columns = new List<string> { "Commercial Accounts", "Commercial Amount In" } },
        new() { Label = "Residential", Columns = new List<string> { "Residential Accounts", "Residential Amount In" } },
        new() { Label = "Total", Columns = new List<string> { "Total Accounts", "Total Amount In" } }
    };

        return new ExecutiveVersionPayload
        {
            Key = "disconnects",
            Title = "Disconnects, Reconnects and Bankruptcies",
            Variant = "teal",
            AsOfLabel = $"Through {latestEom:MMMM yyyy}",
            Metrics = new List<ExecutiveMetricDto>
        {
            ExecMetric(
                "disconnect-month",
                "Disconnected Accounts",
                DisconnectTotal(latestRows, "disconnected_m"),
                "number",
                latestEom,
                ExecDecimal(disconnectHistory, "disconnected_previous_month"),
                ExecDecimal(disconnectHistory, "disconnected_prior_year_month")),
            ExecMetric(
                "reconnect-month",
                "Reconnected Accounts",
                DisconnectTotal(latestRows, "reconnected_m"),
                "number",
                latestEom,
                ExecDecimal(disconnectHistory, "reconnected_previous_month"),
                ExecDecimal(disconnectHistory, "reconnected_prior_year_month")),
            new()
            {
                Key = "bankruptcy-accounts",
                Label = "Bankruptcy Accounts YTD",
                Value = bankruptcyCurrentAccounts,
                Format = "number",
                Period = bankruptcyPeriodLabel,
                Mom = ExecPercentChange(
                    bankruptcyCurrentAccounts,
                    bankruptcyPreviousMonthAccounts),
                Yoy = ExecPercentChange(
                    bankruptcyCurrentAccounts,
                    bankruptcyPriorYearAccounts),
                MomLabel = bankruptcyMomLabel,
                YoyLabel = bankruptcyYoyLabel
            },
            new()
            {
                Key = "bankruptcy-amount",
                Label = "Bankruptcy Amount YTD",
                Value = bankruptcyCurrentAmount,
                Format = "currency",
                Period = bankruptcyPeriodLabel,
                Mom = ExecPercentChange(
                    bankruptcyCurrentAmount,
                    bankruptcyPreviousMonthAmount),
                Yoy = ExecPercentChange(
                    bankruptcyCurrentAmount,
                    bankruptcyPriorYearAmount),
                MomLabel = bankruptcyMomLabel,
                YoyLabel = bankruptcyYoyLabel
            }
        },
            Charts = new List<ExecutiveChartDto>
        {
            new()
            {
                Id = "disconnect-ytd",
                Title = "Disconnects YTD by Customer Type",
                Kind = "pie",
                Width = "third",
                Categories = disconnectClasses.ToList(),
                Series = new List<ExecutiveSeriesDto>
                {
                    ExecSeries("Disconnects", "pie", disconnectClasses.Select(type => (decimal?)DisconnectYtdByClass(type)))
                }
            },
            new()
            {
                Id = "bankruptcy-ytd",
                Title = $"Bankruptcies by Customer Type — {bankruptcyCurrentPeriod:MMM yyyy} YTD",
                Kind = "pie",
                Width = "third",
                Categories = bankruptcyTypes.ToList(),
                Series = new List<ExecutiveSeriesDto>
                {
                    ExecSeries("Accounts", "pie", bankruptcyTypes.Select(type => (decimal?)BankruptcyYtdThrough(bankruptcyCurrentPeriod, type, "accounts")))
                }
            }
        },
            Tables = new List<ExecutiveTableDto>
        {
            new()
            {
                Id = "disconnect-summary",
                Title = "Disconnect/Reconnect Stats — Option 1",
                Width = "wide",
                Kind = "matrix",
                Columns = new List<string> { "Metric", currentLabel, "YTD", $"{previousYear} Total" },
                Rows = disconnectTableRows
            },
            new()
            {
                Id = "bankruptcy-rolling-matrix",
                Title = "Bankruptcies — Rolling 13 Months",
                Width = "wide",
                Kind = "hierarchy",
                Columns = bankruptcyColumns,
                ColumnGroups = bankruptcyGroups,
                Formats = ExecFormats(
                    ("Commercial Accounts", "number"), ("Commercial Amount In", "currency2"),
                    ("Residential Accounts", "number"), ("Residential Amount In", "currency2"),
                    ("Total Accounts", "number"), ("Total Amount In", "currency2")),
                Rows = bankruptcyMatrixRows
            }
        }
        };
    }

    private async Task<ExecutiveVersionPayload> LoadExecutiveArPortfolioAsync(CancellationToken cancellationToken)
    {
        // Use the same EOM summary source as the existing aging application. This keeps
        // the bucket totals, matrices, customer counts, and average-bill denominator on
        // one reporting grain instead of mixing the legacy ITS aggregate with upload data.
        var rollingRows = await LoadExecutiveArSummaryRowsAsync(cancellationToken);

        var normalized = rollingRows.Select(row => new
        {
            Date = ExecDate(row, "SelectedDate", "Period", "Month", "Date", "SnapshotDate"),
            Bucket = ExecBucket(ExecString(row, "AgingBucket", "Bucket", "BucketName", "Label")),
            Amount = ExecDecimal(row, "Amount", "Value", "Total", "Balance") ?? 0m,
            Category = ExecArClass(ExecString(row, "CategoryGroup", "Class", "CustomerClass", "Category")),
            Service = ExecString(row, "Service", "Utility", "UtilityType", "ServiceType")
        })
        .Where(row => row.Date.HasValue && !string.IsNullOrWhiteSpace(row.Bucket) && !string.IsNullOrWhiteSpace(row.Category))
        .ToList();

        if (normalized.Count == 0)
            throw new InvalidOperationException("Aging History TrueDebt returned no usable rows.");

        var hasService = normalized.Any(row => !string.IsNullOrWhiteSpace(row.Service));
        var electricRows = hasService
            ? normalized.Where(row => ExecIsElectric(row.Service)).ToList()
            : normalized;
        if (electricRows.Count == 0) electricRows = normalized;

        var periods = electricRows
            .Select(row => new DateTime(row.Date!.Value.Year, row.Date.Value.Month, 1))
            .Distinct()
            .OrderBy(date => date)
            .TakeLast(13)
            .ToList();
        if (periods.Count == 0)
            throw new InvalidOperationException("Aging History TrueDebt returned no reporting periods.");

        var labels = periods.Select(period => period.ToString("MMM yy", CultureInfo.InvariantCulture)).ToList();
        var customerCountsByPeriod = await LoadExecutiveArCustomerCountsAsync(periods, cancellationToken);
        var eomRows = await LoadExecutiveArEomCompareRowsAsync(cancellationToken);
        var commercialCategoryRows = await LoadExecutiveArCommercialCategoryRowsAsync(cancellationToken);
        var waterWastewaterRows = await LoadExecutiveArWaterWastewaterRowsAsync(cancellationToken);

        decimal Amount(DateTime period, string category, string? bucket = null) => electricRows
            .Where(row => row.Date!.Value.Year == period.Year && row.Date.Value.Month == period.Month &&
                          string.Equals(row.Category, category, StringComparison.OrdinalIgnoreCase) &&
                          (bucket == null || string.Equals(row.Bucket, bucket, StringComparison.OrdinalIgnoreCase)))
            .Sum(row => row.Amount);
        decimal Arrears(DateTime period, string category) =>
            new[] { "31-60", "61-90", ">90" }.Sum(bucket => Amount(period, category, bucket));
        decimal TotalAr(DateTime period, string category) =>
            new[] { "0-30", "31-60", "61-90", ">90" }.Sum(bucket => Amount(period, category, bucket));
        decimal? Customers(DateTime period)
        {
            var key = new DateTime(period.Year, period.Month, 1);
            return customerCountsByPeriod.TryGetValue(key, out var count) ? count : null;
        }
        decimal? AverageBill(DateTime period)
        {
            var customers = Customers(period);
            return customers.HasValue && customers.Value > 0m
                ? (Arrears(period, "Residential") + Arrears(period, "Commercial")) / customers.Value
                : null;
        }

        var currentPeriod = periods[^1];
        var previousPeriod = periods.FirstOrDefault(period => period == currentPeriod.AddMonths(-1));
        var priorYearPeriod = periods.FirstOrDefault(period => period == currentPeriod.AddYears(-1));
        DateTime? previousDate = previousPeriod == default ? null : previousPeriod;
        DateTime? priorYearDate = priorYearPeriod == default ? null : priorYearPeriod;

        ExecutiveChartDto AgingChart(string id, string title, string category) => new()
        {
            Id = id,
            Title = title,
            Kind = "combo",
            Categories = labels,
            LeftAxisTitle = "AR Balance",
            Series = new List<ExecutiveSeriesDto>
            {
                ExecSeries("0-30", "stackedBar", periods.Select(period => (decimal?)Amount(period, category, "0-30")), stack: "aging"),
                ExecSeries("31-60", "stackedBar", periods.Select(period => (decimal?)Amount(period, category, "31-60")), stack: "aging"),
                ExecSeries("61-90", "stackedBar", periods.Select(period => (decimal?)Amount(period, category, "61-90")), stack: "aging"),
                ExecSeries(">90", "stackedBar", periods.Select(period => (decimal?)Amount(period, category, ">90")), stack: "aging"),
                ExecSeries("Total", "line", periods.Select(period => (decimal?)TotalAr(period, category)))
            }
        };

        var totalRows = new List<Dictionary<string, object?>>();
        foreach (var yearGroup in periods.GroupBy(period => period.Year).OrderBy(group => group.Key))
        {
            totalRows.Add(ExecRow(
                ("Month", $"{yearGroup.Key} AR Aging Totals"),
                ("__rowType", "group"),
                ("__label", $"{yearGroup.Key} AR Aging Totals")));

            foreach (var period in yearGroup.OrderBy(value => value))
            {
                var res = Arrears(period, "Residential");
                var comm = Arrears(period, "Commercial");
                var customers = Customers(period);
                totalRows.Add(ExecRow(
                    ("Month", period.ToString("MMMM", CultureInfo.InvariantCulture)),
                    ("0-30", Amount(period, "Residential", "0-30") + Amount(period, "Commercial", "0-30")),
                    ("31-60", Amount(period, "Residential", "31-60") + Amount(period, "Commercial", "31-60")),
                    ("61-90", Amount(period, "Residential", "61-90") + Amount(period, "Commercial", "61-90")),
                    (">90", Amount(period, "Residential", ">90") + Amount(period, "Commercial", ">90")),
                    ("Res/Comm Arrears", res + comm),
                    ("Total Arrears Customers", customers),
                    ("Average Bill", customers.HasValue && customers.Value > 0m ? (res + comm) / customers.Value : null),
                    ("__indent", 1)));
            }
        }

        Dictionary<string, string> DeltaTones(decimal zeroThirty, decimal thirtySixty, decimal sixtyNinety, decimal ninetyPlus, decimal total) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["0-30"] = zeroThirty < 0 ? "good" : zeroThirty > 0 ? "bad" : "neutral",
                ["31-60"] = thirtySixty < 0 ? "good" : thirtySixty > 0 ? "bad" : "neutral",
                ["61-90"] = sixtyNinety < 0 ? "good" : sixtyNinety > 0 ? "bad" : "neutral",
                [">90"] = ninetyPlus < 0 ? "good" : ninetyPlus > 0 ? "bad" : "neutral",
                ["Total"] = total < 0 ? "good" : total > 0 ? "bad" : "neutral"
            };

        ExecutiveTableDto EomMatrix(string id, string title, string customerClass)
        {
            var selected = eomRows
                .Where(row => string.Equals(ExecArClass(ExecString(row, "category_group")), customerClass, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (customerClass.Equals("Commercial", StringComparison.OrdinalIgnoreCase))
            {
                selected = eomRows
                    .Where(row => ExecArClass(ExecString(row, "category_group")).Equals("Commercial", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(row => new { Sort = ExecInt(row, "sort"), Period = ExecString(row, "period") })
                    .Select(group => ExecRow(
                        ("sort", group.Key.Sort),
                        ("period", group.Key.Period),
                        ("zero_thirty", group.Sum(row => ExecDecimal(row, "zero_thirty") ?? 0m)),
                        ("thirty_sixty", group.Sum(row => ExecDecimal(row, "thirty_sixty") ?? 0m)),
                        ("sixty_ninety", group.Sum(row => ExecDecimal(row, "sixty_ninety") ?? 0m)),
                        ("ninety_plus", group.Sum(row => ExecDecimal(row, "ninety_plus") ?? 0m)),
                        ("total", group.Sum(row => ExecDecimal(row, "total") ?? 0m))))
                    .ToList();
            }

            var matrixRows = selected
                .OrderBy(row => ExecInt(row, "sort"))
                .Select(row =>
                {
                    var period = ExecString(row, "period");
                    var zeroThirty = ExecDecimal(row, "zero_thirty") ?? 0m;
                    var thirtySixty = ExecDecimal(row, "thirty_sixty") ?? 0m;
                    var sixtyNinety = ExecDecimal(row, "sixty_ninety") ?? 0m;
                    var ninetyPlus = ExecDecimal(row, "ninety_plus") ?? 0m;
                    var total = ExecDecimal(row, "total") ?? zeroThirty + thirtySixty + sixtyNinety + ninetyPlus;
                    var isDelta = period.Equals("Delta", StringComparison.OrdinalIgnoreCase);
                    return ExecRow(
                        ("Period", period),
                        ("0-30", zeroThirty),
                        ("31-60", thirtySixty),
                        ("61-90", sixtyNinety),
                        (">90", ninetyPlus),
                        ("Total", total),
                        ("__rowType", isDelta ? "delta" : string.Empty),
                        ("__cellTones", isDelta ? DeltaTones(zeroThirty, thirtySixty, sixtyNinety, ninetyPlus, total) : new Dictionary<string, string>()));
                })
                .ToList();

            return new ExecutiveTableDto
            {
                Id = id,
                Title = title,
                Kind = "matrix",
                Columns = new List<string> { "Period", "0-30", "31-60", "61-90", ">90", "Total" },
                Formats = ExecFormats(("0-30", "currency"), ("31-60", "currency"), ("61-90", "currency"), (">90", "currency"), ("Total", "currency")),
                Rows = matrixRows
            };
        }

        var commercialPeriodLabel = commercialCategoryRows.Select(row => ExecString(row, "period")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var commercialMatrixRows = commercialCategoryRows
            .OrderBy(row => ExecInt(row, "sort"))
            .ThenBy(row => ExecString(row, "category"), StringComparer.OrdinalIgnoreCase)
            .Select(row => ExecRow(
                ("Category", ExecString(row, "category")),
                ("0-30", ExecDecimal(row, "zero_thirty") ?? 0m),
                ("31-60", ExecDecimal(row, "thirty_sixty") ?? 0m),
                ("61-90", ExecDecimal(row, "sixty_ninety") ?? 0m),
                (">90", ExecDecimal(row, "ninety_plus") ?? 0m),
                ("Total", ExecDecimal(row, "total") ?? 0m)))
            .ToList();
        if (commercialMatrixRows.Count > 0)
        {
            commercialMatrixRows.Add(ExecRow(
                ("Category", "Total"),
                ("0-30", commercialMatrixRows.Sum(row => ExecDecimal(row, "0-30") ?? 0m)),
                ("31-60", commercialMatrixRows.Sum(row => ExecDecimal(row, "31-60") ?? 0m)),
                ("61-90", commercialMatrixRows.Sum(row => ExecDecimal(row, "61-90") ?? 0m)),
                (">90", commercialMatrixRows.Sum(row => ExecDecimal(row, ">90") ?? 0m)),
                ("Total", commercialMatrixRows.Sum(row => ExecDecimal(row, "Total") ?? 0m)),
                ("__rowType", "total")));
        }

        var waterPeriodLabel = waterWastewaterRows.Select(row => ExecString(row, "period")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var waterMatrixRows = waterWastewaterRows
            .OrderBy(row => ExecString(row, "category"), StringComparer.OrdinalIgnoreCase)
            .Select(row => ExecRow(
                ("Category", ExecString(row, "category")),
                ("31-60 Days", ExecDecimal(row, "thirty_sixty") ?? 0m),
                ("61-90 Days", ExecDecimal(row, "sixty_ninety") ?? 0m),
                ("90+ Days", ExecDecimal(row, "ninety_plus") ?? 0m),
                ("Total", ExecDecimal(row, "total") ?? 0m)))
            .ToList();
        if (waterMatrixRows.Count > 0)
        {
            waterMatrixRows.Add(ExecRow(
                ("Category", "Total"),
                ("31-60 Days", waterMatrixRows.Sum(row => ExecDecimal(row, "31-60 Days") ?? 0m)),
                ("61-90 Days", waterMatrixRows.Sum(row => ExecDecimal(row, "61-90 Days") ?? 0m)),
                ("90+ Days", waterMatrixRows.Sum(row => ExecDecimal(row, "90+ Days") ?? 0m)),
                ("Total", waterMatrixRows.Sum(row => ExecDecimal(row, "Total") ?? 0m)),
                ("__rowType", "total")));
        }

        var currentRes = Arrears(currentPeriod, "Residential");
        var currentComm = Arrears(currentPeriod, "Commercial");
        var currentCustomers = Customers(currentPeriod);
        var currentAverage = AverageBill(currentPeriod);

        decimal? PreviousValue(Func<DateTime, decimal> selector, DateTime? period) => period.HasValue ? selector(period.Value) : null;
        decimal? PreviousNullable(Func<DateTime, decimal?> selector, DateTime? period) => period.HasValue ? selector(period.Value) : null;

        var tables = new List<ExecutiveTableDto>
        {
            EomMatrix("ar-res-delta", "Residential — EOM Delta", "Residential"),
            EomMatrix("ar-comm-delta", "Commercial — EOM Delta", "Commercial")
        };
        if (waterMatrixRows.Count > 0)
        {
            tables.Add(new ExecutiveTableDto
            {
                Id = "ar-water-wastewater",
                Title = string.IsNullOrWhiteSpace(waterPeriodLabel) ? "Water & Wastewater — AR" : $"Water & Wastewater — AR ({waterPeriodLabel})",
                Kind = "matrix",
                Columns = new List<string> { "Category", "31-60 Days", "61-90 Days", "90+ Days", "Total" },
                Formats = ExecFormats(("31-60 Days", "currency2"), ("61-90 Days", "currency2"), ("90+ Days", "currency2"), ("Total", "currency2")),
                Rows = waterMatrixRows
            });
        }
        if (commercialMatrixRows.Count > 0)
        {
            tables.Add(new ExecutiveTableDto
            {
                Id = "ar-commercial-category",
                Title = string.IsNullOrWhiteSpace(commercialPeriodLabel) ? "Commercial by Category" : $"Commercial by Category — {commercialPeriodLabel}",
                Kind = "matrix",
                Columns = new List<string> { "Category", "0-30", "31-60", "61-90", ">90", "Total" },
                Formats = ExecFormats(("0-30", "currency"), ("31-60", "currency"), ("61-90", "currency"), (">90", "currency"), ("Total", "currency")),
                Rows = commercialMatrixRows
            });
        }
        tables.Add(new ExecutiveTableDto
        {
            Id = "ar-total",
            Title = "Total AR — Electric Only, True Debt, Active Accounts — Rolling 13 Months",
            Width = "wide",
            Kind = "hierarchy",
            Columns = new List<string> { "Month", "0-30", "31-60", "61-90", ">90", "Res/Comm Arrears", "Total Arrears Customers", "Average Bill" },
            Formats = ExecFormats(("0-30", "currency"), ("31-60", "currency"), ("61-90", "currency"), (">90", "currency"), ("Res/Comm Arrears", "currency"), ("Total Arrears Customers", "number"), ("Average Bill", "currency2")),
            Rows = totalRows
        });

        return new ExecutiveVersionPayload
        {
            Key = "ar",
            Title = "AR Portfolio",
            Variant = "cyan",
            AsOfLabel = $"Through {currentPeriod:MMMM yyyy}",
            Metrics = new List<ExecutiveMetricDto>
            {
                ExecMetric("res", "Residential Arrears", currentRes, "currency", currentPeriod,
                    PreviousValue(period => Arrears(period, "Residential"), previousDate),
                    PreviousValue(period => Arrears(period, "Residential"), priorYearDate)),
                ExecMetric("comm", "Commercial Arrears", currentComm, "currency", currentPeriod,
                    PreviousValue(period => Arrears(period, "Commercial"), previousDate),
                    PreviousValue(period => Arrears(period, "Commercial"), priorYearDate)),
                new()
                {
                    Key = "customers",
                    Label = "Total Arrears Customers",
                    Value = currentCustomers,
                    Format = "number",
                    Period = currentPeriod.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Mom = currentCustomers.HasValue ? ExecPercentChange(currentCustomers.Value, PreviousNullable(Customers, previousDate)) : null,
                    Yoy = currentCustomers.HasValue ? ExecPercentChange(currentCustomers.Value, PreviousNullable(Customers, priorYearDate)) : null
                },
                new()
                {
                    Key = "average",
                    Label = "Average Bill",
                    Value = currentAverage,
                    Format = "currency2",
                    Period = currentPeriod.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Mom = currentAverage.HasValue ? ExecPercentChange(currentAverage.Value, PreviousNullable(AverageBill, previousDate)) : null,
                    Yoy = currentAverage.HasValue ? ExecPercentChange(currentAverage.Value, PreviousNullable(AverageBill, priorYearDate)) : null
                }
            },
            Charts = new List<ExecutiveChartDto>
            {
                AgingChart("ar-res", "Electric Residential — Rolling 13 Months — EoM, True Debt, Active Accounts", "Residential"),
                AgingChart("ar-comm", "Electric Commercial — Rolling 13 Months — EoM, True Debt, Active Accounts", "Commercial")
            },
            Tables = tables,
            Notes = BuildExecutiveArNotes(hasService, waterMatrixRows.Count > 0, currentCustomers.HasValue)
        };
    }

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveArSummaryRowsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH source_rows AS
            (
                SELECT
                    TRY_CONVERT(date, a.[SelectedDate]) AS [SelectedDate],
                    CONVERT(nvarchar(100), a.[Service]) AS [Service],
                    CONVERT(nvarchar(200), a.[CategoryGroup]) AS [CategoryGroup],
                    CONVERT(nvarchar(200), a.[Category]) AS [Category],
                    CONVERT(nvarchar(100), a.[AgingBucket]) AS [AgingBucket],
                    TRY_CONVERT(int, a.[AgingBucketOrder]) AS [AgingBucketOrder],
                    TRY_CONVERT(decimal(38, 6), a.[Amount]) AS [Amount]
                FROM [dbo].[aging_param_history_sum_fast_eom] AS a WITH (NOLOCK)
                WHERE TRY_CONVERT(date, a.[SelectedDate]) IS NOT NULL
            ),
            latest AS
            (
                SELECT MAX([SelectedDate]) AS [SelectedDate]
                FROM source_rows
                WHERE UPPER(LTRIM(RTRIM(COALESCE([Service], N'')))) = N'E'
                   OR UPPER(COALESCE([Service], N'')) LIKE N'%ELECTRIC%'
            )
            SELECT
                s.[SelectedDate],
                s.[Service],
                s.[CategoryGroup],
                s.[Category],
                s.[AgingBucket],
                s.[AgingBucketOrder],
                s.[Amount]
            FROM source_rows AS s
            CROSS JOIN latest AS l
            WHERE l.[SelectedDate] IS NOT NULL
              AND s.[SelectedDate] >= DATEADD(MONTH, -12, l.[SelectedDate])
              AND s.[SelectedDate] <= l.[SelectedDate]
            ORDER BY s.[SelectedDate], s.[CategoryGroup], s.[AgingBucketOrder], s.[AgingBucket];
            """;

        return await LoadExecutiveCorporateRowsAsync(sql, cancellationToken);
    }

    private async Task<Dictionary<DateTime, decimal>> LoadExecutiveArCustomerCountsAsync(
        IReadOnlyList<DateTime> periods,
        CancellationToken cancellationToken)
    {
        if (periods.Count == 0)
            return new Dictionary<DateTime, decimal>();

        var startDate = new DateTime(periods[0].Year, periods[0].Month, 1);
        var lastPeriod = periods[^1];
        var endDateExclusive = new DateTime(lastPeriod.Year, lastPeriod.Month, 1).AddMonths(1);

        // The upload application treats Name as the customer key in the EOM detail view
        // (its drill-through groups and counts distinct Name values). Calculate true-debt
        // customers at that same grain: one customer per month whose combined 31+ balance
        // is positive. This avoids counting one customer once for every populated bucket.
        const string sql = """
            WITH detail AS
            (
                SELECT
                    DATEFROMPARTS(
                        YEAR(TRY_CONVERT(date, d.[SelectedDate])),
                        MONTH(TRY_CONVERT(date, d.[SelectedDate])),
                        1) AS [period],
                    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(255), d.[Name]))), N'') AS [customer_key],
                    UPPER(LTRIM(RTRIM(COALESCE(CONVERT(nvarchar(100), d.[Service]), N'')))) AS [service_code],
                    UPPER(LTRIM(RTRIM(COALESCE(CONVERT(nvarchar(200), d.[CategoryGroup]), N'')))) AS [category_group],
                    UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        COALESCE(CONVERT(nvarchar(100), d.[AgingBucket]), N''),
                        N' ', N''), N'-', N''), N'–', N''), N'+', N'PLUS'), N'>', N'OVER')) AS [bucket_code],
                    COALESCE(TRY_CONVERT(decimal(38, 6), d.[Amount]), 0) AS [amount]
                FROM [dbo].[aging_param_history_det_fast_eom] AS d WITH (NOLOCK)
                WHERE TRY_CONVERT(date, d.[SelectedDate]) >= @startDate
                  AND TRY_CONVERT(date, d.[SelectedDate]) < @endDateExclusive
            ),
            customer_month AS
            (
                SELECT
                    [period],
                    [customer_key],
                    SUM(CASE
                        WHEN [bucket_code] IN
                        (
                            N'THIRTYSIXTYDAYS', N'3160', N'3160DAYS',
                            N'SIXTYNINETYDAYS', N'6190', N'6190DAYS',
                            N'NINETYPLUSDAYS', N'90PLUS', N'90PLUSDAYS',
                            N'OVER90', N'OVER90DAYS', N'91PLUS', N'91PLUSDAYS'
                        )
                        OR [bucket_code] LIKE N'%THIRTYSIXTY%'
                        OR [bucket_code] LIKE N'%SIXTYNINETY%'
                        OR [bucket_code] LIKE N'%NINETYPLUS%'
                        OR [bucket_code] LIKE N'%90PLUS%'
                        OR [bucket_code] LIKE N'%OVER90%'
                        OR [bucket_code] LIKE N'%91PLUS%'
                        THEN [amount]
                        ELSE 0
                    END) AS [arrears]
                FROM detail
                WHERE [period] IS NOT NULL
                  AND [customer_key] IS NOT NULL
                  AND ([service_code] = N'E' OR [service_code] = N'ELECTRIC' OR [service_code] LIKE N'%ELECTRIC%')
                  AND
                  (
                      [category_group] = N'RESIDENTIAL'
                      OR [category_group] = N'LARGE COMMERCIAL'
                      OR [category_group] = N'SMALL COMMERCIAL'
                      OR [category_group] = N'COMMERCIAL'
                  )
                GROUP BY [period], [customer_key]
            )
            SELECT
                [period],
                CONVERT(decimal(38, 6), COUNT_BIG(*)) AS [arrears_customers]
            FROM customer_month
            WHERE [arrears] > 0
            GROUP BY [period]
            ORDER BY [period];
            """;

        var rows = await LoadExecutiveCorporateRowsAsync(
            sql,
            new[]
            {
                new SqlParameter("@startDate", SqlDbType.Date) { Value = startDate.Date },
                new SqlParameter("@endDateExclusive", SqlDbType.Date) { Value = endDateExclusive.Date }
            },
            cancellationToken);

        return rows
            .Select(row => new
            {
                Period = ExecDate(row, "period"),
                Count = ExecDecimal(row, "arrears_customers")
            })
            .Where(row => row.Period.HasValue && row.Count.HasValue)
            .ToDictionary(
                row => new DateTime(row.Period!.Value.Year, row.Period.Value.Month, 1),
                row => row.Count!.Value);
    }

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveArEomCompareRowsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CONVERT(nvarchar(50), e.[Service]) AS [service],
                CONVERT(nvarchar(100), e.[CategoryGroup]) AS [category_group],
                TRY_CONVERT(int, e.[Sort]) AS [sort],
                CONVERT(nvarchar(100), e.[Period]) AS [period],
                TRY_CONVERT(decimal(38, 6), e.[ZeroThirtyDays]) AS [zero_thirty],
                TRY_CONVERT(decimal(38, 6), e.[ThirtySixtyDays]) AS [thirty_sixty],
                TRY_CONVERT(decimal(38, 6), e.[SixtyNinetyDays]) AS [sixty_ninety],
                TRY_CONVERT(decimal(38, 6), e.[NinetyPlusDays]) AS [ninety_plus],
                TRY_CONVERT(decimal(38, 6), e.[Total]) AS [total]
            FROM [dbo].[vw_eom_bucket_compare] AS e
            ORDER BY e.[Sort], e.[CategoryGroup];
            """;
        var rows = await LoadExecutiveCorporateRowsAsync(sql, cancellationToken);
        var electric = rows.Where(row => ExecIsElectric(ExecString(row, "service"))).ToList();
        return electric.Count > 0 ? electric : rows;
    }

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveArCommercialCategoryRowsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CONVERT(nvarchar(50), e.[Service]) AS [service],
                CONVERT(nvarchar(100), e.[Category]) AS [category],
                TRY_CONVERT(int, e.[Sort]) AS [sort],
                CONVERT(nvarchar(100), e.[Period]) AS [period],
                TRY_CONVERT(decimal(38, 6), e.[ZeroThirtyDays]) AS [zero_thirty],
                TRY_CONVERT(decimal(38, 6), e.[ThirtySixtyDays]) AS [thirty_sixty],
                TRY_CONVERT(decimal(38, 6), e.[SixtyNinetyDays]) AS [sixty_ninety],
                TRY_CONVERT(decimal(38, 6), e.[NinetyPlusDays]) AS [ninety_plus],
                TRY_CONVERT(decimal(38, 6), e.[Total]) AS [total]
            FROM [dbo].[vw_eom_bucket_compare_cat] AS e
            ORDER BY e.[Sort], e.[Category];
            """;
        var rows = await LoadExecutiveCorporateRowsAsync(sql, cancellationToken);
        var electric = rows.Where(row => ExecIsElectric(ExecString(row, "service"))).ToList();
        var source = electric.Count > 0 ? electric : rows;
        var commercial = source
            .Where(row => ExecString(row, "category").Contains("comm", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return commercial.Count > 0 ? commercial : source;
    }

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveArWaterWastewaterRowsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            WITH base AS
            (
                SELECT
                    TRY_CONVERT(date, d.[SelectedDate]) AS [selected_date],
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), d.[Category]))), N''),
                        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), d.[CategoryGroup]))), N''),
                        N'(Blank)') AS [category],
                    UPPER(LTRIM(RTRIM(COALESCE(CONVERT(nvarchar(100), d.[Service]), N'')))) AS [service_code],
                    UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        COALESCE(CONVERT(nvarchar(100), d.[AgingBucket]), N''),
                        N' ', N''), N'-', N''), N'–', N''), N'+', N'PLUS'), N'>', N'OVER')) AS [bucket_code],
                    COALESCE(TRY_CONVERT(decimal(38, 6), d.[Amount]), 0) AS [amount]
                FROM [dbo].[aging_param_history_det_fast_eom] AS d
                WHERE TRY_CONVERT(date, d.[SelectedDate]) IS NOT NULL
            ),
            water_rows AS
            (
                SELECT *
                FROM base
                WHERE [service_code] LIKE N'%WATER%'
                   OR [service_code] LIKE N'%SEWER%'
                   OR [service_code] LIKE N'%WASTE%'
            ),
            latest AS
            (
                SELECT MAX([selected_date]) AS [selected_date]
                FROM water_rows
            )
            SELECT
                CONVERT(nvarchar(30), l.[selected_date], 107) AS [period],
                w.[category],
                SUM(CASE WHEN w.[bucket_code] LIKE N'%3160%' OR w.[bucket_code] LIKE N'%THIRTYSIXTY%' THEN w.[amount] ELSE 0 END) AS [thirty_sixty],
                SUM(CASE WHEN w.[bucket_code] LIKE N'%6190%' OR w.[bucket_code] LIKE N'%SIXTYNINETY%' THEN w.[amount] ELSE 0 END) AS [sixty_ninety],
                SUM(CASE WHEN w.[bucket_code] LIKE N'%90PLUS%' OR w.[bucket_code] LIKE N'%NINETYPLUS%' OR w.[bucket_code] LIKE N'%OVER90%' THEN w.[amount] ELSE 0 END) AS [ninety_plus],
                SUM(CASE
                    WHEN w.[bucket_code] LIKE N'%3160%' OR w.[bucket_code] LIKE N'%THIRTYSIXTY%'
                      OR w.[bucket_code] LIKE N'%6190%' OR w.[bucket_code] LIKE N'%SIXTYNINETY%'
                      OR w.[bucket_code] LIKE N'%90PLUS%' OR w.[bucket_code] LIKE N'%NINETYPLUS%' OR w.[bucket_code] LIKE N'%OVER90%'
                    THEN w.[amount] ELSE 0 END) AS [total]
            FROM water_rows AS w
            CROSS JOIN latest AS l
            WHERE w.[selected_date] = l.[selected_date]
            GROUP BY l.[selected_date], w.[category]
            ORDER BY w.[category];
            """;
        return await LoadExecutiveCorporateRowsAsync(sql, cancellationToken);
    }

    private Task<List<Dictionary<string, object?>>> LoadExecutiveCorporateRowsAsync(
        string sql,
        CancellationToken cancellationToken) =>
        LoadExecutiveCorporateRowsAsync(sql, Array.Empty<SqlParameter>(), cancellationToken);

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveCorporateRowsAsync(
        string sql,
        IEnumerable<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var connectionName = (_cfg["Dashboard:CsrPbipImport:SourceConnectionName"] ?? "csr_pbip_source").Trim();
        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        return await ReadCsrRowsAsync(connection, sql, parameters, cancellationToken);
    }

    private static List<string> BuildExecutiveArNotes(
        bool hasService,
        bool hasWaterWastewater,
        bool hasCustomerCounts)
    {
        var notes = new List<string>();
        if (!hasService)
            notes.Add("The existing Aging History TrueDebt object does not expose a Service field; its current scope is retained exactly.");
        if (!hasWaterWastewater)
            notes.Add("Water/Wastewater will render when an existing source exposes those service rows; no replacement data was inferred.");
        if (!hasCustomerCounts)
            notes.Add("Arrears customer count and average bill remain blank when the existing source exposes neither customer counts nor account identifiers.");
        return notes;
    }

    private async Task<List<Dictionary<string, object?>>> LoadExecutiveRuleRowsAsync(
        CustomHtmlRuleConfig rule,
        CancellationToken cancellationToken)
    {
        var connectionName = string.IsNullOrWhiteSpace(rule.ConnectionName) ? "build" : rule.ConnectionName.Trim();
        var schema = string.IsNullOrWhiteSpace(rule.Schema) ? "dbo" : rule.Schema.Trim();
        var obj = (rule.Object ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(obj))
            throw new InvalidOperationException($"Template '{rule.Key}' has no SQL object.");

        var sourceSql = $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}";
        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        return await ReadCsrRowsAsync(
            connection,
            $"SELECT * FROM {sourceSql};",
            Array.Empty<SqlParameter>(),
            cancellationToken);
    }

    private bool IsExecutiveExportAuthorized(HttpRequest request)
    {
        var configured = (_cfg["ExecutiveExports:JobKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configured)) return true;
        var supplied = (request.Headers["X-Job-Key"].FirstOrDefault() ?? string.Empty).Trim();
        return string.Equals(configured, supplied, StringComparison.Ordinal);
    }

    private async Task SendExecutiveExportEmailAsync(
        ExecutiveVersionPayload payload,
        byte[] bytes,
        string fileName,
        string contentType)
    {
        var host = (_cfg["ExecutiveExports:Smtp:Host"] ?? string.Empty).Trim();
        var port = int.TryParse(_cfg["ExecutiveExports:Smtp:Port"], out var parsedPort) ? parsedPort : 25;
        var enableSsl = bool.TryParse(_cfg["ExecutiveExports:Smtp:EnableSsl"], out var parsedSsl) && parsedSsl;
        var user = (_cfg["ExecutiveExports:Smtp:User"] ?? string.Empty).Trim();
        var pass = _cfg["ExecutiveExports:Smtp:Pass"] ?? string.Empty;
        var from = (_cfg["ExecutiveExports:Mail:From"] ?? _cfg["Email:Smtp:FromAddress"] ?? string.Empty).Trim();
        var toText = (_cfg[$"ExecutiveExports:Versions:{payload.Key}:To"] ?? _cfg["ExecutiveExports:Mail:To"] ?? string.Empty).Trim();
        var ccText = (_cfg[$"ExecutiveExports:Versions:{payload.Key}:Cc"] ?? _cfg["ExecutiveExports:Mail:Cc"] ?? string.Empty).Trim();
        var subject = (_cfg[$"ExecutiveExports:Versions:{payload.Key}:Subject"] ?? $"{payload.Title} - {DateTime.Now:yyyy-MM-dd}").Trim()
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", payload.Title, StringComparison.OrdinalIgnoreCase);
        var body = (_cfg[$"ExecutiveExports:Versions:{payload.Key}:Body"] ?? $"Attached: {payload.Title} export generated from the dashboard SQL sources.").Trim()
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", payload.Title, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("ExecutiveExports:Smtp:Host is missing.");
        if (string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("ExecutiveExports:Mail:From is missing.");
        if (string.IsNullOrWhiteSpace(toText)) throw new InvalidOperationException($"ExecutiveExports recipient is missing for {payload.Key}.");

        static IEnumerable<string> SplitEmails(string text) => text
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using var message = new MailMessage { From = new MailAddress(from), Subject = subject, Body = body, IsBodyHtml = false };
        foreach (var address in SplitEmails(toText)) message.To.Add(address);
        foreach (var address in SplitEmails(ccText)) message.CC.Add(address);
        var stream = new MemoryStream(bytes);
        message.Attachments.Add(new Attachment(stream, fileName, contentType));

        using var smtp = new SmtpClient(host, port) { EnableSsl = enableSsl };
        if (!string.IsNullOrWhiteSpace(user))
            smtp.Credentials = new System.Net.NetworkCredential(user, pass);
        else
            smtp.UseDefaultCredentials = true;
        await smtp.SendMailAsync(message);
    }

    private static byte[] BuildExecutiveWorkbook(ExecutiveVersionPayload payload)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = payload.Title;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 16;
        summary.Cell(2, 1).Value = payload.AsOfLabel;
        summary.Cell(2, 1).Style.Font.Italic = true;

        var metricRow = 4;
        summary.Cell(metricRow, 1).Value = "Metric";
        summary.Cell(metricRow, 2).Value = "Value";
        summary.Cell(metricRow, 3).Value = "Period";
        summary.Cell(metricRow, 4).Value = "MoM";
        summary.Cell(metricRow, 5).Value = "YoY";
        summary.Range(metricRow, 1, metricRow, 5).Style.Font.Bold = true;
        summary.Range(metricRow, 1, metricRow, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        summary.Range(metricRow, 1, metricRow, 5).Style.Font.FontColor = XLColor.White;
        var rowNumber = metricRow + 1;
        foreach (var metric in payload.Metrics)
        {
            summary.Cell(rowNumber, 1).Value = metric.Label;
            if (metric.Value.HasValue) summary.Cell(rowNumber, 2).Value = metric.Value.Value;
            summary.Cell(rowNumber, 3).Value = metric.Period;
            if (metric.Mom.HasValue) summary.Cell(rowNumber, 4).Value = metric.Mom.Value;
            if (metric.Yoy.HasValue) summary.Cell(rowNumber, 5).Value = metric.Yoy.Value;
            summary.Cell(rowNumber, 2).Style.NumberFormat.Format = ExecExcelFormat(metric.Format);
            summary.Range(rowNumber, 4, rowNumber, 5).Style.NumberFormat.Format = "0.0\"%\"";
            rowNumber++;
        }
        summary.Columns(1, 5).AdjustToContents(10, 40);

        var imageRow = rowNumber + 2;
        foreach (var chart in payload.Charts.Take(2))
        {
            var png = RenderExecutiveChartPng(chart, 900, 330);
            using var stream = new MemoryStream(png);
            var picture = summary.AddPicture(stream).MoveTo(summary.Cell(imageRow, 1));
            picture.WithSize(900, 330);
            imageRow += 18;
        }

        var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary" };
        foreach (var chart in payload.Charts)
        {
            var sheet = workbook.AddWorksheet(ExecSheetName("Chart " + chart.Title, usedSheetNames));
            sheet.Cell(1, 1).Value = chart.Title;
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontSize = 13;
            WriteExecutiveChartData(sheet, chart, 3);
            var png = RenderExecutiveChartPng(chart, 1200, 440);
            using var stream = new MemoryStream(png);
            var picture = sheet.AddPicture(stream).MoveTo(sheet.Cell(3, Math.Max(4, chart.Series.Count + 3)));
            picture.WithSize(1200, 440);
            sheet.Columns().AdjustToContents(8, 28);
        }

        foreach (var table in payload.Tables)
        {
            var sheet = workbook.AddWorksheet(ExecSheetName(table.Title, usedSheetNames));
            sheet.Cell(1, 1).Value = table.Title;
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontSize = 13;
            WriteExecutiveTable(sheet, table, 3);
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static int ExecutiveTablePanelHeight(ExecutiveTableDto table)
    {
        var headerRows = table.ColumnGroups.Count > 0 ? 2 : 1;
        var bodyRows = Math.Max(1, table.Rows.Count);
        return Math.Clamp(48 + headerRows * 25 + bodyRows * 21, 190, 760);
    }

    private static byte[] BuildExecutivePng(ExecutiveVersionPayload payload)
    {
        const int width = 1600;
        const int outerMargin = 28;
        const int gap = 14;
        const int metricColumns = 4;
        const int metricHeight = 104;
        const int chartPanelHeight = 390;

        var metricRows = Math.Max(1, (int)Math.Ceiling(payload.Metrics.Count / (double)metricColumns));
        var chartRows = (int)Math.Ceiling(payload.Charts.Count / 2d);
        var tableHeights = payload.Tables.Select(ExecutiveTablePanelHeight).ToList();
        var notesHeight = payload.Notes.Count * 17;
        var height = 82
            + metricRows * (metricHeight + gap)
            + chartRows * (chartPanelHeight + gap)
            + tableHeights.Sum(value => value + gap)
            + notesHeight
            + 56;
        height = Math.Max(900, height);

        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var titleFont = new Font("Segoe UI", 22, FontStyle.Bold);
        using var subtitleFont = new Font("Segoe UI", 10, FontStyle.Regular);
        using var metricLabelFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var metricValueFont = new Font("Segoe UI", 20, FontStyle.Bold);
        using var panelTitleFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var tableFont = new Font("Segoe UI", 8, FontStyle.Regular);
        using var tableHeaderFont = new Font("Segoe UI", 8, FontStyle.Bold);
        using var borderPen = new Pen(Color.FromArgb(216, 222, 234), 1);
        using var titleBrush = new SolidBrush(Color.FromArgb(23, 23, 119));
        using var mutedBrush = new SolidBrush(Color.FromArgb(96, 112, 139));
        using var panelBrush = new SolidBrush(Color.FromArgb(248, 250, 253));
        using var headerBrush = new SolidBrush(Color.FromArgb(31, 78, 120));
        using var whiteBrush = new SolidBrush(Color.White);

        graphics.DrawString(payload.Title, titleFont, titleBrush, outerMargin, 16);
        graphics.DrawString(payload.AsOfLabel, subtitleFont, mutedBrush, outerMargin + 2, 52);

        var y = 78;
        var metricWidth = (width - outerMargin * 2 - gap * (metricColumns - 1)) / metricColumns;
        for (var index = 0; index < Math.Max(payload.Metrics.Count, 1); index++)
        {
            var row = index / metricColumns;
            var column = index % metricColumns;
            var x = outerMargin + column * (metricWidth + gap);
            var rect = new Rectangle(x, y + row * (metricHeight + gap), metricWidth, metricHeight);
            graphics.FillRectangle(panelBrush, rect);
            graphics.DrawRectangle(borderPen, rect);

            if (index < payload.Metrics.Count)
            {
                var metric = payload.Metrics[index];
                graphics.DrawString(metric.Label, metricLabelFont, mutedBrush, x + 11, rect.Y + 10);
                graphics.DrawString(ExecDisplay(metric.Value, metric.Format), metricValueFont, titleBrush, x + 11, rect.Y + 34);
                graphics.DrawString(metric.Period, subtitleFont, mutedBrush, x + 11, rect.Y + 78);
            }
        }
        y += metricRows * (metricHeight + gap);

        for (var index = 0; index < payload.Charts.Count; index += 2)
        {
            for (var column = 0; column < 2 && index + column < payload.Charts.Count; column++)
            {
                var chart = payload.Charts[index + column];
                var panelWidth = (width - outerMargin * 2 - gap) / 2;
                var x = outerMargin + column * (panelWidth + gap);
                var rect = new Rectangle(x, y, panelWidth, chartPanelHeight);
                graphics.FillRectangle(whiteBrush, rect);
                graphics.DrawRectangle(borderPen, rect);
                graphics.DrawString(chart.Title, panelTitleFont, titleBrush, x + 10, y + 8);

                var chartBytes = RenderExecutiveChartPng(chart, panelWidth - 18, chartPanelHeight - 38);
                using var chartStream = new MemoryStream(chartBytes);
                using var chartImage = Image.FromStream(chartStream);
                graphics.DrawImage(chartImage, x + 9, y + 30, panelWidth - 18, chartPanelHeight - 38);
            }
            y += chartPanelHeight + gap;
        }

        for (var tableIndex = 0; tableIndex < payload.Tables.Count; tableIndex++)
        {
            var table = payload.Tables[tableIndex];
            var panelHeight = tableHeights[tableIndex];
            var rect = new Rectangle(outerMargin, y, width - outerMargin * 2, panelHeight);
            graphics.FillRectangle(whiteBrush, rect);
            graphics.DrawRectangle(borderPen, rect);
            graphics.DrawString(table.Title, panelTitleFont, titleBrush, rect.X + 10, rect.Y + 8);
            DrawExecutiveTablePreview(
                graphics,
                table,
                new Rectangle(rect.X + 9, rect.Y + 30, rect.Width - 18, rect.Height - 39),
                tableFont,
                tableHeaderFont,
                borderPen,
                headerBrush,
                whiteBrush,
                mutedBrush);
            y += panelHeight + gap;
        }

        foreach (var note in payload.Notes)
        {
            graphics.DrawString(note, subtitleFont, mutedBrush, outerMargin, y);
            y += 17;
        }

        graphics.DrawString($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}", subtitleFont, mutedBrush, outerMargin, height - 32);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static void DrawExecutiveTablePreview(
        Graphics graphics,
        ExecutiveTableDto table,
        Rectangle bounds,
        Font bodyFont,
        Font headerFont,
        Pen borderPen,
        Brush headerBrush,
        Brush headerTextBrush,
        Brush bodyTextBrush)
    {
        var columns = table.Columns.ToList();
        if (columns.Count == 0)
        {
            graphics.DrawString("No columns.", bodyFont, bodyTextBrush, bounds.X + 4, bounds.Y + 4);
            return;
        }

        const int rowHeight = 21;
        const int headerHeight = 25;
        var hasGroups = table.ColumnGroups.Count > 0;
        var headerRows = hasGroups ? 2 : 1;
        var headerTotalHeight = headerRows * headerHeight;
        var columnWidth = Math.Max(56, bounds.Width / columns.Count);
        var visibleRows = Math.Max(1, (bounds.Height - headerTotalHeight) / rowHeight);
        using var centered = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var leftAligned = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };

        Rectangle CellRect(int columnIndex, int top, int height)
        {
            var x = bounds.X + columnIndex * columnWidth;
            var width = columnIndex == columns.Count - 1 ? bounds.Right - x : columnWidth;
            return new Rectangle(x, top, width, height);
        }

        if (hasGroups)
        {
            var groupByColumn = new Dictionary<string, ExecutiveColumnGroupDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in table.ColumnGroups)
            {
                foreach (var column in group.Columns)
                    groupByColumn[column] = group;
            }

            var emitted = new HashSet<ExecutiveColumnGroupDto>();
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                if (!groupByColumn.TryGetValue(column, out var group))
                {
                    var cell = CellRect(columnIndex, bounds.Y, headerTotalHeight);
                    graphics.FillRectangle(headerBrush, cell);
                    graphics.DrawRectangle(borderPen, cell);
                    graphics.DrawString(column, headerFont, headerTextBrush, cell, centered);
                    continue;
                }

                if (!emitted.Add(group))
                    continue;

                var grouped = group.Columns.Where(name => columns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                if (grouped.Count == 0) continue;
                var firstIndex = columns.FindIndex(name => name.Equals(grouped[0], StringComparison.OrdinalIgnoreCase));
                var lastIndex = columns.FindLastIndex(name => grouped.Contains(name, StringComparer.OrdinalIgnoreCase));
                var firstRect = CellRect(firstIndex, bounds.Y, headerHeight);
                var lastRect = CellRect(lastIndex, bounds.Y, headerHeight);
                var groupCell = new Rectangle(firstRect.X, bounds.Y, lastRect.Right - firstRect.X, headerHeight);
                graphics.FillRectangle(headerBrush, groupCell);
                graphics.DrawRectangle(borderPen, groupCell);
                graphics.DrawString(group.Label, headerFont, headerTextBrush, groupCell, centered);
            }

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                if (!groupByColumn.TryGetValue(column, out var group)) continue;
                var label = column.StartsWith(group.Label + " ", StringComparison.OrdinalIgnoreCase)
                    ? column[(group.Label.Length + 1)..]
                    : column;
                var cell = CellRect(columnIndex, bounds.Y + headerHeight, headerHeight);
                graphics.FillRectangle(headerBrush, cell);
                graphics.DrawRectangle(borderPen, cell);
                graphics.DrawString(label, headerFont, headerTextBrush, cell, centered);
            }
        }
        else
        {
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var cell = CellRect(columnIndex, bounds.Y, headerHeight);
                graphics.FillRectangle(headerBrush, cell);
                graphics.DrawRectangle(borderPen, cell);
                graphics.DrawString(columns[columnIndex], headerFont, headerTextBrush, cell, centered);
            }
        }

        var rowsToDraw = Math.Min(table.Rows.Count, visibleRows);
        for (var rowIndex = 0; rowIndex < rowsToDraw; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var rowType = Convert.ToString(ExecRead(row, "__rowType"), CultureInfo.InvariantCulture) ?? string.Empty;
            var rowTop = bounds.Y + headerTotalHeight + rowIndex * rowHeight;

            if (rowType.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                using var groupBrush = new SolidBrush(Color.FromArgb(231, 240, 247));
                var rowRect = new Rectangle(bounds.X, rowTop, bounds.Width, rowHeight);
                graphics.FillRectangle(groupBrush, rowRect);
                graphics.DrawRectangle(borderPen, rowRect);
                var label = Convert.ToString(ExecRead(row, "__label"), CultureInfo.InvariantCulture)
                    ?? Convert.ToString(ExecRead(row, columns[0]), CultureInfo.InvariantCulture)
                    ?? string.Empty;
                graphics.DrawString(label, headerFont, bodyTextBrush, rowRect, leftAligned);
                continue;
            }

            var isStrong = rowType.Equals("total", StringComparison.OrdinalIgnoreCase) ||
                           rowType.Equals("group-total", StringComparison.OrdinalIgnoreCase) ||
                           rowType.Equals("delta", StringComparison.OrdinalIgnoreCase);
            if (isStrong)
            {
                using var strongBrush = new SolidBrush(rowType.Equals("delta", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(243, 246, 250)
                    : Color.FromArgb(217, 234, 247));
                graphics.FillRectangle(strongBrush, bounds.X, rowTop, bounds.Width, rowHeight);
            }

            var rowFormats = ExecRead(row, "__formats") as IReadOnlyDictionary<string, string>;
            var cellTones = ExecRead(row, "__cellTones") as IReadOnlyDictionary<string, string>;
            var indent = ExecInt(row, "__indent");
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                var cell = CellRect(columnIndex, rowTop, rowHeight);
                row.TryGetValue(column, out var raw);
                var format = rowFormats is not null && rowFormats.TryGetValue(column, out var rowFormat)
                    ? rowFormat
                    : table.Formats.TryGetValue(column, out var tableFormat)
                        ? tableFormat
                        : string.Empty;
                var text = ExecDisplayObject(raw, format);
                graphics.DrawRectangle(borderPen, cell);

                Brush textBrush = bodyTextBrush;
                if (cellTones is not null && cellTones.TryGetValue(column, out var tone))
                {
                    if (tone.Equals("good", StringComparison.OrdinalIgnoreCase))
                        textBrush = Brushes.ForestGreen;
                    else if (tone.Equals("bad", StringComparison.OrdinalIgnoreCase))
                        textBrush = Brushes.Firebrick;
                }

                var drawRect = cell;
                if (columnIndex == 0 && indent > 0)
                {
                    drawRect = new Rectangle(cell.X + indent * 12, cell.Y, Math.Max(1, cell.Width - indent * 12), cell.Height);
                }
                graphics.DrawString(text, isStrong ? headerFont : bodyFont, textBrush, drawRect, columnIndex == 0 ? leftAligned : centered);
            }
        }

        if (table.Rows.Count > rowsToDraw)
        {
            graphics.DrawString(
                $"+ {table.Rows.Count - rowsToDraw:N0} more row(s) in Excel",
                bodyFont,
                bodyTextBrush,
                bounds.X + 4,
                bounds.Bottom - 15);
        }
    }

    private static byte[] RenderExecutiveChartPng(ExecutiveChartDto chart, int width, int height)
    {
        var plot = new ScottPlot.Plot(width, height);
        var categories = chart.Categories ?? new List<string>();
        plot.Title(chart.Title);

        if (chart.Kind.Equals("pie", StringComparison.OrdinalIgnoreCase))
        {
            var values = chart.Series.FirstOrDefault()?.Data
                .Select(value => value.HasValue ? Math.Max(0d, (double)value.Value) : 0d)
                .ToArray() ?? Array.Empty<double>();

            if (values.Length > 0 && values.Any(value => value > 0d))
            {
                var labels = categories
                    .Take(values.Length)
                    .Concat(Enumerable.Repeat(string.Empty, Math.Max(0, values.Length - categories.Count)))
                    .ToArray();
                var pie = plot.AddPie(values);
                pie.DonutSize = .55;
                pie.SliceLabels = labels;
                pie.ShowPercentages = true;
                pie.ShowLabels = true;
                plot.Legend();
            }

            using var pieBitmap = plot.GetBitmap();
            using var pieOutput = new MemoryStream();
            pieBitmap.Save(pieOutput, ImageFormat.Png);
            return pieOutput.ToArray();
        }

        var xs = Enumerable.Range(0, categories.Count).Select(index => (double)index).ToArray();
        plot.XTicks(xs, categories.ToArray());
        plot.Grid(enable: true);
        var axisNumberFormat = chart.ValueFormat.StartsWith("percent", StringComparison.OrdinalIgnoreCase)
            ? "0.##'%'"
            : chart.ValueFormat.StartsWith("currency", StringComparison.OrdinalIgnoreCase)
                ? "$#,##0"
                : "N0";
        plot.YAxis.TickLabelFormat(axisNumberFormat, false);
        if (!string.IsNullOrWhiteSpace(chart.LeftAxisTitle)) plot.YLabel(chart.LeftAxisTitle);
        if (chart.Series.Any(series => series.Axis.Equals("right", StringComparison.OrdinalIgnoreCase)))
        {
            plot.YAxis2.Ticks(true);
            if (!string.IsNullOrWhiteSpace(chart.RightAxisTitle)) plot.YAxis2.Label(chart.RightAxisTitle);
        }

        var stackOffsets = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        for (var seriesIndex = 0; seriesIndex < chart.Series.Count; seriesIndex++)
        {
            var series = chart.Series[seriesIndex];
            var values = Enumerable.Range(0, categories.Count)
                .Select(index => index < series.Data.Count && series.Data[index].HasValue ? (double)series.Data[index]!.Value : 0d)
                .ToArray();
            var type = (series.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type is "bar" or "stackedbar")
            {
                var bar = plot.AddBar(values, xs);
                bar.BarWidth = type == "stackedbar" ? 0.62 : Math.Max(0.18, 0.72 / Math.Max(1, chart.Series.Count));
                if (type == "stackedbar")
                {
                    var stackKey = string.IsNullOrWhiteSpace(series.Stack) ? "stack" : series.Stack;
                    if (!stackOffsets.TryGetValue(stackKey, out var offsets))
                    {
                        offsets = new double[categories.Count];
                        stackOffsets[stackKey] = offsets;
                    }
                    bar.ValueOffsets = offsets.ToArray();
                    for (var i = 0; i < offsets.Length; i++) offsets[i] += values[i];
                }
                else if (chart.Series.Count > 1)
                {
                    bar.PositionOffset = (seriesIndex - (chart.Series.Count - 1) / 2d) * bar.BarWidth;
                }
                bar.Label = series.Name;
            }
            else
            {
                var scatter = plot.AddScatter(xs, values, lineWidth: 2, markerSize: 4, label: series.Name);
                if (series.Axis.Equals("right", StringComparison.OrdinalIgnoreCase)) scatter.YAxisIndex = 1;
            }
        }

        if (categories.Count > 0) plot.SetAxisLimits(xMin: -0.55, xMax: categories.Count - 0.45);
        var legend = plot.Legend(location: Alignment.LowerCenter);
        legend.Orientation = Orientation.Horizontal;
        using var bitmap = plot.GetBitmap();
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static void WriteExecutiveChartData(IXLWorksheet sheet, ExecutiveChartDto chart, int startRow)
    {
        sheet.Cell(startRow, 1).Value = "Period";
        for (var index = 0; index < chart.Series.Count; index++)
            sheet.Cell(startRow, index + 2).Value = chart.Series[index].Name;
        sheet.Range(startRow, 1, startRow, chart.Series.Count + 1).Style.Font.Bold = true;
        sheet.Range(startRow, 1, startRow, chart.Series.Count + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        sheet.Range(startRow, 1, startRow, chart.Series.Count + 1).Style.Font.FontColor = XLColor.White;
        for (var rowIndex = 0; rowIndex < chart.Categories.Count; rowIndex++)
        {
            sheet.Cell(startRow + 1 + rowIndex, 1).Value = chart.Categories[rowIndex];
            for (var seriesIndex = 0; seriesIndex < chart.Series.Count; seriesIndex++)
            {
                if (rowIndex < chart.Series[seriesIndex].Data.Count && chart.Series[seriesIndex].Data[rowIndex].HasValue)
                    sheet.Cell(startRow + 1 + rowIndex, seriesIndex + 2).Value = chart.Series[seriesIndex].Data[rowIndex]!.Value;
            }
        }
    }

    private static void WriteExecutiveTable(IXLWorksheet sheet, ExecutiveTableDto table, int startRow)
    {
        var columnCount = Math.Max(1, table.Columns.Count);
        var hasGroups = table.ColumnGroups.Count > 0;
        var headerRows = hasGroups ? 2 : 1;

        if (hasGroups)
        {
            var groupByColumn = new Dictionary<string, ExecutiveColumnGroupDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in table.ColumnGroups)
            {
                foreach (var column in group.Columns)
                    groupByColumn[column] = group;
            }

            var emitted = new HashSet<ExecutiveColumnGroupDto>();
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                var excelColumn = index + 1;
                if (!groupByColumn.TryGetValue(column, out var group))
                {
                    sheet.Cell(startRow, excelColumn).Value = column;
                    sheet.Range(startRow, excelColumn, startRow + 1, excelColumn).Merge();
                    continue;
                }

                if (!emitted.Add(group))
                    continue;

                var groupedColumns = group.Columns
                    .Where(name => table.Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (groupedColumns.Count == 0)
                    continue;

                var firstIndex = table.Columns.FindIndex(name => name.Equals(groupedColumns[0], StringComparison.OrdinalIgnoreCase));
                var lastIndex = table.Columns.FindLastIndex(name => groupedColumns.Contains(name, StringComparer.OrdinalIgnoreCase));
                sheet.Cell(startRow, firstIndex + 1).Value = group.Label;
                sheet.Range(startRow, firstIndex + 1, startRow, lastIndex + 1).Merge();
            }

            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                if (groupByColumn.ContainsKey(column))
                {
                    var label = column;
                    foreach (var group in table.ColumnGroups)
                    {
                        if (group.Columns.Contains(column, StringComparer.OrdinalIgnoreCase) &&
                            label.StartsWith(group.Label + " ", StringComparison.OrdinalIgnoreCase))
                        {
                            label = label[(group.Label.Length + 1)..];
                            break;
                        }
                    }
                    sheet.Cell(startRow + 1, index + 1).Value = label;
                }
            }
        }
        else
        {
            for (var index = 0; index < table.Columns.Count; index++)
                sheet.Cell(startRow, index + 1).Value = table.Columns[index];
        }

        var header = sheet.Range(startRow, 1, startRow + headerRows - 1, columnCount);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        header.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        var rowNumber = startRow + headerRows;
        foreach (var row in table.Rows)
        {
            var rowType = Convert.ToString(ExecRead(row, "__rowType"), CultureInfo.InvariantCulture) ?? string.Empty;
            if (rowType.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                var label = Convert.ToString(ExecRead(row, "__label"), CultureInfo.InvariantCulture)
                    ?? Convert.ToString(ExecRead(row, table.Columns.FirstOrDefault() ?? string.Empty), CultureInfo.InvariantCulture)
                    ?? string.Empty;
                sheet.Cell(rowNumber, 1).Value = label;
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Merge();
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Font.Bold = true;
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Font.FontColor = XLColor.FromHtml("#1F4E78");
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#E7F0F7");
                rowNumber++;
                continue;
            }

            var rowFormats = ExecRead(row, "__formats") as IReadOnlyDictionary<string, string>;
            var cellTones = ExecRead(row, "__cellTones") as IReadOnlyDictionary<string, string>;
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var column = table.Columns[columnIndex];
                row.TryGetValue(column, out var value);
                var cell = sheet.Cell(rowNumber, columnIndex + 1);
                ExecSetCell(cell, value);

                if (rowFormats is not null &&
                    rowFormats.TryGetValue(column, out var rowFormat) &&
                    !string.IsNullOrWhiteSpace(rowFormat))
                {
                    cell.Style.NumberFormat.Format = ExecExcelFormat(rowFormat);
                }
                else if (table.Formats.TryGetValue(column, out var tableFormat) &&
                         !string.IsNullOrWhiteSpace(tableFormat))
                {
                    cell.Style.NumberFormat.Format = ExecExcelFormat(tableFormat);
                }

                if (cellTones is not null && cellTones.TryGetValue(column, out var tone))
                {
                    if (tone.Equals("good", StringComparison.OrdinalIgnoreCase))
                        cell.Style.Font.FontColor = XLColor.FromHtml("#15803D");
                    else if (tone.Equals("bad", StringComparison.OrdinalIgnoreCase))
                        cell.Style.Font.FontColor = XLColor.FromHtml("#B91C1C");
                }
            }

            if (rowType.Equals("total", StringComparison.OrdinalIgnoreCase) ||
                rowType.Equals("group-total", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Font.Bold = true;
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
            }
            else if (rowType.Equals("delta", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Font.Bold = true;
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F6FA");
            }
            else if ((rowNumber - startRow) % 2 == 0)
            {
                sheet.Range(rowNumber, 1, rowNumber, columnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F6FA");
            }
            rowNumber++;
        }

        var lastRow = Math.Max(startRow + headerRows - 1, rowNumber - 1);
        if (!hasGroups)
            sheet.Range(startRow, 1, lastRow, columnCount).SetAutoFilter();
        sheet.Range(startRow, 1, lastRow, columnCount).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sheet.Range(startRow, 1, lastRow, columnCount).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        sheet.SheetView.FreezeRows(startRow + headerRows - 1);
        sheet.Columns(1, columnCount).AdjustToContents(8, 45);
    }

    private static void ExecSetCell(IXLCell cell, object? value)
    {
        if (value == null || value == DBNull.Value) return;
        switch (value)
        {
            case DateTime date: cell.Value = date; break;
            case decimal number: cell.Value = number; break;
            case double number: cell.Value = number; break;
            case float number: cell.Value = number; break;
            case int number: cell.Value = number; break;
            case long number: cell.Value = number; break;
            case bool boolean: cell.Value = boolean; break;
            default: cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; break;
        }
    }

    private static string ExecExcelFormat(string format) => (format ?? string.Empty).ToLowerInvariant() switch
    {
        "currency" => "$#,##0;[Red]-$#,##0",
        "currency2" => "$#,##0.00;[Red]-$#,##0.00",
        "percent" => "0.0\"%\"",
        "percent2" => "0.00\"%\"",
        "decimal2" => "0.00",
        _ => "#,##0"
    };

    private static string ExecDisplay(decimal? value, string format)
    {
        if (!value.HasValue) return "—";
        return (format ?? string.Empty).ToLowerInvariant() switch
        {
            "currency" => value.Value.ToString("C0", CultureInfo.GetCultureInfo("en-CA")),
            "currency2" => value.Value.ToString("C2", CultureInfo.GetCultureInfo("en-CA")),
            "percent" => value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%",
            "percent2" => value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%",
            "decimal2" => value.Value.ToString("N2", CultureInfo.InvariantCulture),
            _ => value.Value.ToString("N0", CultureInfo.InvariantCulture)
        };
    }

    private static string ExecDisplayObject(object? value, string format)
    {
        if (value == null || value == DBNull.Value) return "—";
        try
        {
            if (!string.IsNullOrWhiteSpace(format))
                return ExecDisplay(Convert.ToDecimal(value, CultureInfo.InvariantCulture), format);
        }
        catch
        {
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ExecutiveFileStem(string key) => key switch
    {
        "ebill" => "Ebill_Performance",
        "ar" => "AR_Portfolio",
        "disconnects" => "Disconnects_Bankruptcies",
        "finalbill" => "Final_Bill_Collections_Recovery",
        "payments" => "Customer_Payments",
        _ => "Executive_Dashboard"
    };

    private static string ExecSheetName(string requested, ISet<string> used)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '[', ']', ':', '*', '?', '/', '\\' }).ToHashSet();
        var clean = new string((requested ?? "Sheet").Where(character => !invalid.Contains(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        var index = 2;
        while (!used.Add(candidate))
        {
            var suffix = $" {index++}";
            var prefixLength = Math.Max(1, 31 - suffix.Length);
            candidate = clean[..Math.Min(clean.Length, prefixLength)] + suffix;
        }
        return candidate;
    }

    private static ExecutiveMetricDto ExecMetric(
        string key,
        string label,
        decimal current,
        string format,
        DateTime period,
        decimal? previous,
        decimal? priorYear,
        string deltaMode = "percent") => new()
        {
            Key = key,
            Label = label,
            Value = current,
            Format = format,
            Period = period.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            Mom = deltaMode.Equals("points", StringComparison.OrdinalIgnoreCase)
                ? ExecPointChange(current, previous)
                : ExecPercentChange(current, previous),
            Yoy = deltaMode.Equals("points", StringComparison.OrdinalIgnoreCase)
                ? ExecPointChange(current, priorYear)
                : ExecPercentChange(current, priorYear),
            DeltaMode = deltaMode
        };

    private static ExecutiveChartDto ExecChart(
        string id,
        string title,
        string kind,
        IEnumerable<string> categories,
        params ExecutiveSeriesDto[] series) => new()
        {
            Id = id,
            Title = title,
            Kind = kind,
            Categories = categories.ToList(),
            Series = series.ToList()
        };

    private static ExecutiveSeriesDto ExecSeries(
        string name,
        string type,
        IEnumerable<decimal?> data,
        string axis = "left",
        string stack = "") => new()
        {
            Name = name,
            Type = type,
            Axis = axis,
            Stack = stack,
            Data = data.ToList()
        };

    private static Dictionary<string, object?> ExecRow(params (string Key, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) row[value.Key] = value.Value;
        return row;
    }

    private static Dictionary<string, string> ExecFormats(params (string Key, string Value)[] values)
    {
        var formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) formats[value.Key] = value.Value;
        return formats;
    }

    private static object? ExecRead(Dictionary<string, object?> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var value)) return value;
        }
        return null;
    }

    private static string ExecString(Dictionary<string, object?> row, params string[] names) =>
        Convert.ToString(ExecRead(row, names), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    private static decimal? ExecDecimal(Dictionary<string, object?> row, params string[] names)
    {
        var value = ExecRead(row, names);
        if (value == null || value == DBNull.Value) return null;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch { return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null; }
    }

    private static int ExecInt(Dictionary<string, object?> row, params string[] names)
    {
        var value = ExecDecimal(row, names);
        return value.HasValue ? decimal.ToInt32(decimal.Truncate(value.Value)) : 0;
    }

    private static DateTime? ExecDate(Dictionary<string, object?> row, params string[] names)
    {
        var value = ExecRead(row, names);
        if (value == null || value == DBNull.Value) return null;
        if (value is DateTime date) return date;
        if (value is DateTimeOffset offset) return offset.DateTime;
        return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ExecPercentChange(decimal current, decimal? previous) =>
        previous.HasValue && previous.Value != 0m ? ((current - previous.Value) / previous.Value) * 100m : null;

    private static decimal? ExecPointChange(decimal current, decimal? previous) =>
        previous.HasValue ? current - previous.Value : null;

    private static decimal ExecRatio(decimal numerator, decimal denominator) =>
        denominator == 0m ? 0m : numerator / denominator * 100m;

    private static int ExecMetricOrder(string metric)
    {
        var value = (metric ?? string.Empty).ToLowerInvariant();
        if (value.Contains("month") && !value.Contains('$')) return 1;
        if (value.Contains("ytd") && !value.Contains('$')) return 2;
        if ((value.Contains("prev") || value.Contains("previous")) && !value.Contains('$')) return 3;
        if (value.Contains("month") && value.Contains('$')) return 4;
        if (value.Contains("ytd") && value.Contains('$')) return 5;
        if ((value.Contains("prev") || value.Contains("previous")) && value.Contains('$')) return 6;
        return 20;
    }

    private static string ExecBucket(string value)
    {
        var normalized = new string((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Contains("current") || normalized.Contains("030") || normalized.Contains("zerothirty")) return "0-30";
        if (normalized.Contains("3160") || normalized.Contains("thirtysixty")) return "31-60";
        if (normalized.Contains("6190") || normalized.Contains("sixtyninety")) return "61-90";
        if (normalized.Contains("90plus") || normalized.Contains("ninetyplus") || normalized.Contains("over90") || normalized.Contains("91")) return ">90";
        return (value ?? string.Empty).Trim();
    }

    private static string ExecArClass(string value)
    {
        if (value.Contains("res", StringComparison.OrdinalIgnoreCase)) return "Residential";
        if (value.Contains("comm", StringComparison.OrdinalIgnoreCase)) return "Commercial";
        return (value ?? string.Empty).Trim();
    }

    private static bool ExecIsElectric(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "e" or "electric" or "electricity" || normalized.Contains("electric");
    }

    private static bool ExecIsWater(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Contains("water") || normalized.Contains("waste") || normalized.Contains("sewer");
    }
}
