using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace corporate_dashboards.Controllers;

public sealed class DashboardController : Controller
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<DashboardController> _log;

    // Reuse single static readonly array for the "Count" sentinel to avoid allocating new arrays repeatedly (CA1861)
    private static readonly string[] CountFieldArray = new[] { "Count" };

    public DashboardController(IConfiguration cfg, ILogger<DashboardController> log)
    {
        _cfg = cfg;
        _log = log;
    }

    [HttpGet]
    public IActionResult Multi()
    {
        ViewBag.DefaultSchema = (_cfg["Dashboard:DefaultSchema"] ?? "").Trim();
        return View();
    }


private sealed class CustomHtmlRuleConfig
{
    public string Key { get; set; } = "";
    public string Schema { get; set; } = "*";
    public string Object { get; set; } = "*";
    public string ChartType { get; set; } = "customHtml";
    public string HtmlFile { get; set; } = "";
    public string PayloadMode { get; set; } = "";
    public int RefreshSeconds { get; set; }
    public string TrendSchema { get; set; } = "";
    public string TrendObject { get; set; } = "";
    public string TrendTimeField { get; set; } = "";
    public string TrendValueField { get; set; } = "";
    public int TrendMaxPoints { get; set; }
    public string SummarySchema { get; set; } = "";
    public string SummaryObject { get; set; } = "";
    public string PointsSchema { get; set; } = "";
    public string PointsObject { get; set; } = "";
}

public sealed class CustomHtmlLiveDataRequest
{
    public string Schema { get; set; } = "";
    public string Obj { get; set; } = "";
    public string PayloadMode { get; set; } = "";
    public Dictionary<string, FilterSpec> Filters { get; set; } = new();
    public string TrendSchema { get; set; } = "";
    public string TrendObject { get; set; } = "";
    public string TrendTimeField { get; set; } = "";
    public string TrendValueField { get; set; } = "";
    public int TrendMaxPoints { get; set; } = 12;
    public string SummarySchema { get; set; } = "";
    public string SummaryObject { get; set; } = "";
    public string PointsSchema { get; set; } = "";
    public string PointsObject { get; set; } = "";
}

[HttpGet]
public IActionResult GetCustomHtmlConfig(string schema, string obj, string chartType = "customHtml")
{
    if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(obj))
    {
        return Json(new { found = false });
    }

    var rule = ResolveCustomHtmlRule(
        NormalizeCustomHtmlToken(schema.Trim()),
        NormalizeCustomHtmlToken(obj.Trim()),
        NormalizeCustomHtmlToken((chartType ?? "customHtml").Trim()));
    if (rule == null || string.IsNullOrWhiteSpace(rule.HtmlFile))
    {
        return Json(new { found = false });
    }

    var safeFile = Path.GetFileName(rule.HtmlFile.Trim());
    if (string.IsNullOrWhiteSpace(safeFile))
    {
        return Json(new { found = false });
    }

    var basePath = (_cfg["Dashboard:CustomHtml:BasePath"] ?? "/custom-html").Trim();
    var htmlUrl = BuildStaticHtmlUrl(basePath, safeFile);

    return Json(new
    {
        found = true,
        key = rule.Key,
        schema = rule.Schema,
        obj = rule.Object,
        chartType = rule.ChartType,
        htmlUrl,
        payloadMode = rule.PayloadMode,
        refreshSeconds = rule.RefreshSeconds,
        trendSchema = rule.TrendSchema,
        trendObject = rule.TrendObject,
        trendTimeField = rule.TrendTimeField,
        trendValueField = rule.TrendValueField,
        trendMaxPoints = rule.TrendMaxPoints,
        summarySchema = rule.SummarySchema,
        summaryObject = rule.SummaryObject,
        pointsSchema = rule.PointsSchema,
        pointsObject = rule.PointsObject
    });
}


[HttpPost]
[IgnoreAntiforgeryToken]
public async Task<IActionResult> GetCustomHtmlLiveData([FromBody] CustomHtmlLiveDataRequest req)
{
    if (req == null) return BadRequest("missing request");
    if (string.IsNullOrWhiteSpace(req.Schema) || string.IsNullOrWhiteSpace(req.Obj)) return BadRequest("schema/obj required");

    req.Schema = NormalizeCustomHtmlToken(req.Schema ?? "");
    req.Obj = NormalizeCustomHtmlToken(req.Obj ?? "");
    req.TrendSchema = NormalizeCustomHtmlToken(req.TrendSchema ?? "");
    req.TrendObject = NormalizeCustomHtmlToken(req.TrendObject ?? "");
    req.SummarySchema = NormalizeCustomHtmlToken(req.SummarySchema ?? "");
    req.SummaryObject = NormalizeCustomHtmlToken(req.SummaryObject ?? "");
    req.PointsSchema = NormalizeCustomHtmlToken(req.PointsSchema ?? "");
    req.PointsObject = NormalizeCustomHtmlToken(req.PointsObject ?? "");

    var payloadMode = (req.PayloadMode ?? "").Trim();
    if (payloadMode.Equals("remoteHealthMonitor", StringComparison.OrdinalIgnoreCase))
    {
        return await GetRemoteHealthLiveDataAsync(req);
    }

    if (payloadMode.Equals("agingForecastMonitor", StringComparison.OrdinalIgnoreCase))
    {
        return await GetAgingForecastLiveDataAsync(req);
    }

    return BadRequest("unsupported custom html live mode");
}

private async Task<IActionResult> GetRemoteHealthLiveDataAsync(CustomHtmlLiveDataRequest req)
{
    await using var con = new SqlConnection(ConnStr());
    await con.OpenAsync();

    var schema = req.Schema.Trim();
    var obj = req.Obj.Trim();

    var (snapshotOid, _) = await ResolveObjectAsync(con, schema, obj);
    if (snapshotOid == 0) return NotFound("snapshot object not found");

    var snapshotCols = await LoadColumnMapAsync(con, snapshotOid);
    var snapshotTimeField = PickExistingColumn(snapshotCols, "snapshot_time", "start_time", "inserted_at");
    var serverField = PickExistingColumn(snapshotCols, "remote_server", "server_name");
    var databaseField = PickExistingColumn(snapshotCols, "remote_database", "database_name");

    var latest = await QueryLatestRowAsync(
        con,
        schema,
        obj,
        snapshotCols,
        req.Filters,
        snapshotTimeField,
        descending: true);

    if (latest.Count == 0)
    {
        return Json(new
        {
            found = false,
            mode = "remoteHealthMonitor",
            model = new { }
        });
    }

    var remoteServer = FirstNonBlank(latest, serverField, "remote_server", "server_name");
    var remoteDatabase = FirstNonBlank(latest, databaseField, "remote_database", "database_name");

    var trendSchema = string.IsNullOrWhiteSpace(req.TrendSchema) ? "its" : req.TrendSchema.Trim();
    var trendObject = string.IsNullOrWhiteSpace(req.TrendObject) ? "vw_RemoteDbHealth_history" : req.TrendObject.Trim();
    var trendMaxPoints = Math.Clamp(req.TrendMaxPoints <= 0 ? 12 : req.TrendMaxPoints, 2, 240);

    List<object> trendLabels = new();
    List<decimal> trendValues = new();

    var (trendOid, _) = await ResolveObjectAsync(con, trendSchema, trendObject);
    if (trendOid != 0)
    {
        var trendCols = await LoadColumnMapAsync(con, trendOid);
        var trendTimeField = PickExistingColumn(
            trendCols,
            string.IsNullOrWhiteSpace(req.TrendTimeField) ? "" : req.TrendTimeField.Trim(),
            "snapshot_time",
            "start_time",
            "inserted_at");
        var trendValueField = PickExistingColumn(
            trendCols,
            string.IsNullOrWhiteSpace(req.TrendValueField) ? "" : req.TrendValueField.Trim(),
            "health_score");

        if (!string.IsNullOrWhiteSpace(trendTimeField) && !string.IsNullOrWhiteSpace(trendValueField))
        {
            var trendFilters = new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase);

            var trendServerField = PickExistingColumn(trendCols, "remote_server", "server_name");
            var trendDatabaseField = PickExistingColumn(trendCols, "remote_database", "database_name");

            if (!string.IsNullOrWhiteSpace(trendServerField) && !string.IsNullOrWhiteSpace(remoteServer))
            {
                trendFilters[trendServerField] = new FilterSpec
                {
                    Mode = "in",
                    Values = new List<string?> { remoteServer }
                };
            }

            if (!string.IsNullOrWhiteSpace(trendDatabaseField) && !string.IsNullOrWhiteSpace(remoteDatabase))
            {
                trendFilters[trendDatabaseField] = new FilterSpec
                {
                    Mode = "in",
                    Values = new List<string?> { remoteDatabase }
                };
            }

            var trendRows = await QueryProjectedRowsAsync(
                con,
                trendSchema,
                trendObject,
                trendCols,
                trendFilters,
                new[] { trendTimeField, trendValueField },
                trendTimeField,
                descending: true,
                top: trendMaxPoints);

            trendRows.Reverse();

            trendLabels = trendRows
                .Select(row => FormatTrendLabel(ReadDate(row, trendTimeField)))
                .Cast<object>()
                .ToList();

            trendValues = trendRows
                .Select(row => ReadDecimal(row, trendValueField))
                .ToList();
        }
    }

    var snapshotTime = ReadDate(latest, snapshotTimeField);
    var model = new
    {
        health_score = ReadDecimal(latest, "health_score"),
        health_status = FirstNonBlank(latest, "health_status") ?? "Snapshot",
        remote_server = remoteServer ?? "",
        remote_database = remoteDatabase ?? "",
        snapshot_time = snapshotTime?.ToString("MM-dd HH:mm:ss") ?? "",
        blocker_sessions = ReadInt(latest, "blocker_sessions"),
        blocked_sessions = ReadInt(latest, "blocked_sessions"),
        long_running_requests = ReadInt(latest, "long_running_requests"),
        waiting_sessions = ReadInt(latest, "waiting_sessions"),
        max_wait_ms = ReadInt(latest, "max_wait_ms"),
        avg_wait_ms = ReadInt(latest, "avg_wait_ms"),
        max_elapsed_ms = ReadInt(latest, "max_elapsed_ms"),
        avg_elapsed_ms = ReadInt(latest, "avg_elapsed_ms"),
        total_user_sessions = ReadInt(latest, "total_user_sessions"),
        active_requests = ReadInt(latest, "active_requests"),
        sessions_with_open_txn = ReadInt(latest, "sessions_with_open_txn"),
        top_wait_type = FirstNonBlank(latest, "top_wait_type") ?? "",
        top_wait_count = ReadInt(latest, "top_wait_count"),
        score_history = trendValues,
        score_labels = trendLabels
    };

    return Json(new
    {
        found = true,
        mode = "remoteHealthMonitor",
        model,
        rows = new[] { latest }
    });
}



private async Task<IActionResult> GetAgingForecastLiveDataAsync(CustomHtmlLiveDataRequest req)
{
    await using var con = new SqlConnection(ConnStr());
    await con.OpenAsync();

    var pointsSchema = string.IsNullOrWhiteSpace(req.PointsSchema) ? req.Schema.Trim() : req.PointsSchema.Trim();
    var pointsObject = string.IsNullOrWhiteSpace(req.PointsObject) ? req.Obj.Trim() : req.PointsObject.Trim();
    var summarySchema = string.IsNullOrWhiteSpace(req.SummarySchema) ? req.Schema.Trim() : req.SummarySchema.Trim();
    var summaryObject = string.IsNullOrWhiteSpace(req.SummaryObject) ? req.Obj.Trim() : req.SummaryObject.Trim();

    var (pointsOid, _) = await ResolveObjectAsync(con, pointsSchema, pointsObject);
    if (pointsOid == 0) return NotFound("points object not found");

    var pointsCols = await LoadColumnMapAsync(con, pointsOid);
    var pointDateField = PickExistingColumn(pointsCols, "PointDate", "point_date", "SelectedDate", "snapshot_time");
    if (string.IsNullOrWhiteSpace(pointDateField))
    {
        return BadRequest("points object does not expose a point date field");
    }

    var pointProjection = new[]
    {
        PickExistingColumn(pointsCols, "RunId", "run_id"),
        PickExistingColumn(pointsCols, "RunDateTime", "run_date_time"),
        PickExistingColumn(pointsCols, "Service", "service"),
        PickExistingColumn(pointsCols, "Category", "category"),
        PickExistingColumn(pointsCols, "CategoryGroup", "category_group"),
        PickExistingColumn(pointsCols, "AgingBucket", "aging_bucket"),
        pointDateField,
        PickExistingColumn(pointsCols, "ActualAmount", "actual_amount"),
        PickExistingColumn(pointsCols, "PredictedAmountWithoutS2", "predicted_amount_without_s2"),
        PickExistingColumn(pointsCols, "PredictedAmountWithS2", "predicted_amount_with_s2"),
        PickExistingColumn(pointsCols, "LowerWithoutS2", "lower_without_s2"),
        PickExistingColumn(pointsCols, "UpperWithoutS2", "upper_without_s2"),
        PickExistingColumn(pointsCols, "LowerWithS2", "lower_with_s2"),
        PickExistingColumn(pointsCols, "UpperWithS2", "upper_with_s2"),
        PickExistingColumn(pointsCols, "ConfidenceWithoutS2", "confidence_without_s2"),
        PickExistingColumn(pointsCols, "ConfidenceWithS2", "confidence_with_s2"),
        PickExistingColumn(pointsCols, "IsForecast", "is_forecast"),
        PickExistingColumn(pointsCols, "IsHoldout", "is_holdout"),
        PickExistingColumn(pointsCols, "HorizonDay", "horizon_day"),
        PickExistingColumn(pointsCols, "Year", "year"),
        PickExistingColumn(pointsCols, "MonthNumeric", "month_numeric"),
        PickExistingColumn(pointsCols, "MonthName", "month_name"),
        PickExistingColumn(pointsCols, "DayOfMonth", "day_of_month"),
        PickExistingColumn(pointsCols, "DateLabel", "date_label"),
        PickExistingColumn(pointsCols, "DateHierarchyLabel", "date_hierarchy_label"),
        PickExistingColumn(pointsCols, "S2StrategyChosen", "s2_strategy_chosen")
    }
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Cast<string>()
    .Distinct(StringComparer.OrdinalIgnoreCase);

    var pointRowsRaw = await QueryProjectedRowsAsync(
        con,
        pointsSchema,
        pointsObject,
        pointsCols,
        filters: new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase),
        projectedFields: pointProjection,
        orderByField: pointDateField,
        descending: false,
        top: 200000);

    var pointRows = TrimAgingRawPointRowsToTrailingYear(pointRowsRaw, pointDateField, AgingForecastTrailingWindowDays);

    List<Dictionary<string, object?>> summaryRows = new();
    var (summaryOid, _) = await ResolveObjectAsync(con, summarySchema, summaryObject);
    if (summaryOid != 0)
    {
        var summaryCols = await LoadColumnMapAsync(con, summaryOid);
        var runTimeField = PickExistingColumn(summaryCols, "RunDateTime", "run_date_time", "LastActualDate", "last_actual_date");
        summaryRows = await QueryProjectedRowsAsync(
            con,
            summarySchema,
            summaryObject,
            summaryCols,
            filters: new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase),
            projectedFields: Array.Empty<string>(),
            orderByField: runTimeField,
            descending: true,
            top: 1);
    }

    return Json(new
    {
        found = true,
        mode = "agingForecastMonitor",
        summaryRows,
        pointRows
    });
}


private const int AgingForecastTrailingWindowDays = 365;

private static List<Dictionary<string, object?>> TrimAgingRawPointRowsToTrailingYear(
    List<Dictionary<string, object?>> rows,
    string pointDateField,
    int maxTotalDays)
{
    if (rows == null || rows.Count == 0 || maxTotalDays <= 0)
    {
        return rows ?? new List<Dictionary<string, object?>>();
    }

    static bool ReadBool(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value) || value == null) continue;
            if (value is bool b) return b;
            if (value is byte bt) return bt != 0;
            if (value is short s) return s != 0;
            if (value is int i) return i != 0;
            if (bool.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            if (int.TryParse(Convert.ToString(value), out var asInt)) return asInt != 0;
        }
        return false;
    }

    static DateTime ReadDateValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value) && value != null && DateTime.TryParse(Convert.ToString(value), out var parsed))
        {
            return parsed.Date;
        }
        return DateTime.MinValue;
    }

    var ordered = rows
        .Select(row => new
        {
            Row = row,
            Date = ReadDateValue(row, pointDateField),
            IsForecast = ReadBool(row, "IsForecast", "is_forecast")
        })
        .Where(x => x.Date != DateTime.MinValue)
        .OrderBy(x => x.Date)
        .ToList();

    if (ordered.Count == 0)
    {
        return new List<Dictionary<string, object?>>();
    }

    var lastActualDate = ordered
        .Where(x => !x.IsForecast)
        .Select(x => x.Date)
        .DefaultIfEmpty(ordered.Last().Date)
        .Max();

    var minHistoryDate = lastActualDate.AddDays(-(maxTotalDays - 1));

    return ordered
        .Where(x => x.IsForecast || x.Date >= minHistoryDate)
        .Select(x =>
        {
            if (x.IsForecast)
            {
                if (x.Row.ContainsKey("ActualAmount")) x.Row["ActualAmount"] = null;
                if (x.Row.ContainsKey("actual_amount")) x.Row["actual_amount"] = null;
            }
            return x.Row;
        })
        .ToList();
}

private static string? PickExistingColumn(
    IReadOnlyDictionary<string, (string systemTypeLower, string category)> colMap,
    params string[] candidates)
{
    foreach (var candidate in candidates ?? Array.Empty<string>())
    {
        var trimmed = (candidate ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(trimmed) && colMap.ContainsKey(trimmed))
        {
            return trimmed;
        }
    }

    return null;
}

private async Task<Dictionary<string, object?>> QueryLatestRowAsync(
    SqlConnection con,
    string schema,
    string obj,
    IReadOnlyDictionary<string, (string systemTypeLower, string category)> colMap,
    Dictionary<string, FilterSpec>? filters,
    string? orderByField,
    bool descending)
{
    var rows = await QueryProjectedRowsAsync(
        con,
        schema,
        obj,
        colMap,
        filters,
        Array.Empty<string>(),
        orderByField,
        descending,
        top: 1);

    return rows.FirstOrDefault() ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}

private async Task<List<Dictionary<string, object?>>> QueryProjectedRowsAsync(
    SqlConnection con,
    string schema,
    string obj,
    IReadOnlyDictionary<string, (string systemTypeLower, string category)> colMap,
    Dictionary<string, FilterSpec>? filters,
    IEnumerable<string> projectedFields,
    string? orderByField,
    bool descending,
    int top)
{
    var fields = (projectedFields ?? Array.Empty<string>())
        .Where(f => !string.IsNullOrWhiteSpace(f) && colMap.ContainsKey(f.Trim()))
        .Select(f => f.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var schemaQ = Q(schema);
    var objQ = Q(obj);

    var selectSql = fields.Count == 0
        ? "*"
        : string.Join(", ", fields.Select(f => $"{schemaQ}.{objQ}.{Q(f)} AS {Q(f)}"));

    var ps = new List<SqlParameter>();
    var where = BuildWhereClauses(filters, schemaQ, objQ, colMap, ps);

    var sql = new StringBuilder();
    sql.Append("SELECT TOP (@top) ").Append(selectSql).AppendLine();
    sql.AppendLine($"FROM {schemaQ}.{objQ}");
    if (where.Count > 0)
    {
        sql.AppendLine("WHERE " + string.Join(" AND ", where));
    }
    if (!string.IsNullOrWhiteSpace(orderByField) && colMap.ContainsKey(orderByField))
    {
        sql.AppendLine($"ORDER BY {schemaQ}.{objQ}.{Q(orderByField)} {(descending ? "DESC" : "ASC")}");
    }

    await using var cmd = CreateCommand(con);
    cmd.CommandText = sql.ToString();
    cmd.Parameters.Add(new SqlParameter("@top", Math.Max(1, top)));
    foreach (var p in ps) cmd.Parameters.Add(p);

    var data = new List<Dictionary<string, object?>>();
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rdr.FieldCount; i++)
        {
            row[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
        }
        data.Add(row);
    }

    return data;
}

private List<string> BuildWhereClauses(
    Dictionary<string, FilterSpec>? filters,
    string schemaQ,
    string objQ,
    IReadOnlyDictionary<string, (string systemTypeLower, string category)> colMap,
    List<SqlParameter> ps)
{
    var where = new List<string>();
    var pi = 0;

    string AddParam(object? value)
    {
        var name = "@fp" + pi++;
        ps.Add(new SqlParameter(name, value ?? DBNull.Value));
        return name;
    }

    if (filters == null)
    {
        return where;
    }

    foreach (var kv in filters)
    {
        var field = kv.Key?.Trim();
        if (string.IsNullOrWhiteSpace(field) || !colMap.ContainsKey(field))
        {
            continue;
        }

        var spec = kv.Value ?? new FilterSpec();
        var mode = (spec.Mode ?? "in").Trim().ToLowerInvariant();
        var col = $"{schemaQ}.{objQ}.{Q(field)}";
        var isDate = IsDateType(colMap[field].systemTypeLower);

        if (mode == "isnull")
        {
            where.Add($"{col} IS NULL");
            continue;
        }

        if (mode == "notnull")
        {
            where.Add($"{col} IS NOT NULL");
            continue;
        }

        if (mode == "range" && isDate)
        {
            if (!string.IsNullOrWhiteSpace(spec.FromUtc) && DateTime.TryParse(spec.FromUtc, out var fromDt))
            {
                where.Add($"{col} >= {AddParam(DateTime.SpecifyKind(fromDt, DateTimeKind.Utc))}");
            }

            if (!string.IsNullOrWhiteSpace(spec.ToUtc) && DateTime.TryParse(spec.ToUtc, out var toDt))
            {
                where.Add($"{col} < {AddParam(DateTime.SpecifyKind(toDt, DateTimeKind.Utc))}");
            }

            continue;
        }

        if (mode == "in" || mode == "notin")
        {
            var values = (spec.Values ?? new List<string?>())
                .Where(v => v != null)
                .Select(v => v!)
                .Take(500)
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            var inParams = values.Select(AddParam).ToList();
            where.Add($"{col} {(mode == "notin" ? "NOT IN" : "IN")} ({string.Join(",", inParams)})");
        }
    }

    return where;
}

private static string? FirstNonBlank(Dictionary<string, object?> row, params string?[] keys)
{
    foreach (var key in keys)
    {
        if (string.IsNullOrWhiteSpace(key)) continue;
        if (!row.TryGetValue(key, out var value) || value == null) continue;
        var s = Convert.ToString(value)?.Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            return s;
        }
    }

    return null;
}

private static DateTime? ReadDate(Dictionary<string, object?> row, string? key)
{
    if (string.IsNullOrWhiteSpace(key) || !row.TryGetValue(key, out var value) || value == null)
    {
        return null;
    }

    if (value is DateTime dt) return dt;
    return DateTime.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
}

private static int ReadInt(Dictionary<string, object?> row, string key)
{
    if (!row.TryGetValue(key, out var value) || value == null)
    {
        return 0;
    }

    try
    {
        return Convert.ToInt32(value);
    }
    catch
    {
        return int.TryParse(Convert.ToString(value), out var parsed) ? parsed : 0;
    }
}

private static decimal ReadDecimal(Dictionary<string, object?> row, string key)
{
    if (!row.TryGetValue(key, out var value) || value == null)
    {
        return 0m;
    }

    try
    {
        return Convert.ToDecimal(value);
    }
    catch
    {
        return decimal.TryParse(Convert.ToString(value), out var parsed) ? parsed : 0m;
    }
}

private static string FormatTrendLabel(DateTime? value)
{
    return value.HasValue ? value.Value.ToString("MM-dd HH:mm") : "";
}

private CustomHtmlRuleConfig? ResolveCustomHtmlRule(string schema, string obj, string chartType)
{
    var rules = new List<CustomHtmlRuleConfig>();
    foreach (var section in _cfg.GetSection("Dashboard:CustomHtml:Rules").GetChildren())
    {
        var rule = new CustomHtmlRuleConfig
        {
            Key = (section["Key"] ?? "").Trim(),
            Schema = string.IsNullOrWhiteSpace(section["Schema"]) ? "*" : section["Schema"]!.Trim(),
            Object = string.IsNullOrWhiteSpace(section["Object"]) ? "*" : section["Object"]!.Trim(),
            ChartType = string.IsNullOrWhiteSpace(section["ChartType"]) ? "customHtml" : section["ChartType"]!.Trim(),
            HtmlFile = (section["HtmlFile"] ?? "").Trim(),
            PayloadMode = (section["PayloadMode"] ?? "").Trim(),
            RefreshSeconds = int.TryParse(section["RefreshSeconds"], out var refreshSeconds) ? Math.Clamp(refreshSeconds, 0, 3600) : 0,
            TrendSchema = (section["TrendSchema"] ?? "").Trim(),
            TrendObject = (section["TrendObject"] ?? "").Trim(),
            TrendTimeField = (section["TrendTimeField"] ?? "").Trim(),
            TrendValueField = (section["TrendValueField"] ?? "").Trim(),
            TrendMaxPoints = int.TryParse(section["TrendMaxPoints"], out var trendMaxPoints) ? Math.Clamp(trendMaxPoints, 1, 240) : 12,
            SummarySchema = (section["SummarySchema"] ?? "").Trim(),
            SummaryObject = (section["SummaryObject"] ?? "").Trim(),
            PointsSchema = (section["PointsSchema"] ?? "").Trim(),
            PointsObject = (section["PointsObject"] ?? "").Trim()
        };

        if (!string.IsNullOrWhiteSpace(rule.HtmlFile))
        {
            rules.Add(rule);
        }
    }

    CustomHtmlRuleConfig? best = null;
    var bestScore = int.MinValue;

    foreach (var rule in rules)
    {
        var score = MatchRule(rule, schema, obj, chartType);
        if (score > bestScore)
        {
            bestScore = score;
            best = rule;
        }
    }

    return bestScore >= 0 ? best : null;
}

private static int MatchRule(CustomHtmlRuleConfig rule, string schema, string obj, string chartType)
{
    static int ScorePart(string ruleValue, string actualValue, int exactScore)
    {
        if (CustomHtmlTokenEquals(ruleValue, "*")) return 1;
        return CustomHtmlTokenEquals(ruleValue, actualValue) ? exactScore : -1000;
    }

    var score = 0;
    score += ScorePart(rule.Schema, schema, 8);
    score += ScorePart(rule.Object, obj, 12);
    score += ScorePart(rule.ChartType, chartType, 6);
    return score < 0 ? -1 : score;
}

private static string BuildStaticHtmlUrl(string basePath, string safeFile)
{
    if (Uri.TryCreate(basePath, UriKind.Absolute, out var absoluteBase))
    {
        return absoluteBase.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(safeFile);
    }

    var prefix = string.IsNullOrWhiteSpace(basePath) ? "/custom-html" : "/" + basePath.Trim().Trim('/');
    return prefix + "/" + Uri.EscapeDataString(safeFile);
}

private static string NormalizeCustomHtmlToken(string value)
{
    var s = (value ?? "").Trim();
    if (s.EndsWith(" (view)", StringComparison.OrdinalIgnoreCase))
    {
        s = s[..^7].TrimEnd();
    }
    else if (s.EndsWith(" (table)", StringComparison.OrdinalIgnoreCase))
    {
        s = s[..^8].TrimEnd();
    }

    return s;
}

private static bool CustomHtmlTokenEquals(string left, string right)
{
    return string.Equals(
        NormalizeCustomHtmlToken(left),
        NormalizeCustomHtmlToken(right),
        StringComparison.OrdinalIgnoreCase);
}


    // ----------------------------
    // DTOs
    // ----------------------------
    public sealed class DbObjectDto
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = ""; // table | view
    }

    public sealed class ColumnMetaDto
    {
        public string Name { get; set; } = "";
        public string SystemType { get; set; } = ""; // e.g. nvarchar
        public string UserType { get; set; } = "";   // same as system usually
        public bool IsNullable { get; set; }
        public string Category { get; set; } = "";   // dimension | date | measure
    }

    public sealed class FilterSpec
    {
        public string Mode { get; set; } = "in"; // in, notin, range, isnull, notnull
        public List<string?> Values { get; set; } = new();
        public string? FromUtc { get; set; }
        public string? ToUtc { get; set; }
    }

    public sealed class AggregateRequest
    {
        public string Schema { get; set; } = "";
        public string Obj { get; set; } = "";

        public List<string> Rows { get; set; } = new();
        public List<string> Cols { get; set; } = new();
        public List<string> Values { get; set; } = new(); // multi

        public string Agg { get; set; } = "Sum"; // Sum, Average, Count, Minimum, Maximum
        public Dictionary<string, string> DateGroups { get; set; } = new(); // field -> Year/Quarter/Month/Date
        public Dictionary<string, FilterSpec> Filters { get; set; } = new();

        public int MaxCells { get; set; } = 100000;
    }

    public sealed class NarrateRequest
    {
        public string Schema { get; set; } = "";
        public string Obj { get; set; } = "";
        public string ChartType { get; set; } = "";
        public string Agg { get; set; } = "";

        public List<string> Rows { get; set; } = new();
        public List<string> Cols { get; set; } = new();
        public List<string> Values { get; set; } = new();

        // already aggregated data from server (client sends sample)
        public List<Dictionary<string, object?>> Data { get; set; } = new();
    }

    // ----------------------------
    // Helpers
    // ----------------------------
    private string ConnStr()
    {
        return _cfg.GetConnectionString("build")
               ?? throw new InvalidOperationException("Missing connection string (build/DefaultConnection/DashboardDb).");
    }

    private int SqlCommandTimeoutSeconds()
    {
        var raw = _cfg["Timeouts:SqlServerCommandTimeoutSeconds"];
        return int.TryParse(raw, out var seconds) ? Math.Clamp(seconds, 30, 1800) : 300;
    }

    private SqlCommand CreateCommand(SqlConnection con)
    {
        var cmd = con.CreateCommand();
        cmd.CommandTimeout = SqlCommandTimeoutSeconds();
        return cmd;
    }

    private static string Q(string ident) => $"[{(ident ?? "").Replace("]", "]]")}]";

    private string SchemaLike() => (_cfg["Dashboard:SchemaLike"] ?? "%").Trim();

    private static string MapToCategory(string sqlTypeLower)
    {
        if (sqlTypeLower.Contains("int")
            || sqlTypeLower.Contains("decimal")
            || sqlTypeLower.Contains("numeric")
            || sqlTypeLower.Contains("money")
            || sqlTypeLower.Contains("float")
            || sqlTypeLower.Contains("real"))
            return "measure";

        if (sqlTypeLower.Contains("date") || sqlTypeLower.Contains("time"))
            return "date";

        return "dimension";
    }

    private static bool IsDateType(string sqlTypeLower)
        => sqlTypeLower.Contains("date") || sqlTypeLower.Contains("time");

    private static string AggFn(string agg)
    {
        agg = (agg ?? "Sum").Trim();
        return agg switch
        {
            "Sum" => "SUM",
            "Average" => "AVG",
            "Minimum" => "MIN",
            "Maximum" => "MAX",
            _ => "SUM"
        };
    }

    private static string DateExpr(string quotedCol, string group)
    {
        group = (group ?? "Date").Trim();
        return group switch
        {
            "Year" => $"DATEFROMPARTS(YEAR({quotedCol}),1,1)",
            "Quarter" => $"DATEFROMPARTS(YEAR({quotedCol}), ((DATEPART(QUARTER,{quotedCol})-1)*3)+1, 1)",
            "Month" => $"DATEFROMPARTS(YEAR({quotedCol}), MONTH({quotedCol}), 1)",
            "Date" => $"CAST({quotedCol} as date)",
            _ => $"CAST({quotedCol} as date)"
        };
    }

    private async Task<(int objectId, string objectType)> ResolveObjectAsync(SqlConnection con, string schema, string obj)
    {
        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT TOP 1 o.object_id,
       CASE WHEN o.type = 'V' THEN 'view' ELSE 'table' END as objectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @obj AND o.type IN ('U','V');";
        cmd.Parameters.Add(new SqlParameter("@schema", schema));
        cmd.Parameters.Add(new SqlParameter("@obj", obj));

        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync()) return (0, "");
        return (rdr.GetInt32(0), rdr.GetString(1));
    }

    private async Task<Dictionary<string, (string systemTypeLower, string category)>> LoadColumnMapAsync(SqlConnection con, int objectId)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT c.name,
       LOWER(t.name) as systemType
FROM sys.columns c
JOIN sys.types t ON c.system_type_id = t.system_type_id AND t.user_type_id = t.system_type_id
WHERE c.object_id = @oid;";
        cmd.Parameters.Add(new SqlParameter("@oid", objectId));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var name = rdr.GetString(0);
            var sysType = rdr.GetString(1);
            map[name] = (sysType, MapToCategory(sysType));
        }
        return map;
    }

    // ----------------------------
    // API: Schemas
    // ----------------------------
    [HttpGet]
    public async Task<IActionResult> GetSchemas()
    {
        var like = SchemaLike();
        var schemas = new List<string>();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT s.name
FROM sys.schemas s
WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA')
  AND s.name LIKE @like
ORDER BY s.name;";
        cmd.Parameters.Add(new SqlParameter("@like", like));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));

        return Json(schemas);
    }

    // ----------------------------
    // API: Objects (tables/views)
    // ----------------------------
    [HttpGet]
    public async Task<IActionResult> GetObjects(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        schema = schema.Trim();

        var objs = new List<DbObjectDto>();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT o.name,
       CASE WHEN o.type = 'V' THEN 'view' ELSE 'table' END as objectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.type IN ('U','V')
ORDER BY o.type, o.name;";
        cmd.Parameters.Add(new SqlParameter("@schema", schema));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            objs.Add(new DbObjectDto
            {
                Name = rdr.GetString(0),
                Type = rdr.GetString(1)
            });
        }

        return Json(objs);
    }

    // Optional back-compat alias if anything still calls GetViews
    [HttpGet]
    public Task<IActionResult> GetViews(string schema)
        => GetObjects(schema);

    // ----------------------------
    // API: Columns (metadata + category)
    // ----------------------------
    [HttpGet]
    public async Task<IActionResult> GetColumns(string schema, string obj)
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        if (string.IsNullOrWhiteSpace(obj)) return BadRequest("obj required");
        schema = schema.Trim();
        obj = obj.Trim();

        var cols = new List<ColumnMetaDto>();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        var (oid, _) = await ResolveObjectAsync(con, schema, obj);
        if (oid == 0) return NotFound("object not found");

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT c.name,
       LOWER(st.name) as systemType,
       LOWER(ut.name) as userType,
       c.is_nullable
FROM sys.columns c
JOIN sys.types st ON c.system_type_id = st.system_type_id AND st.user_type_id = st.system_type_id
JOIN sys.types ut ON c.user_type_id = ut.user_type_id
WHERE c.object_id = @oid
ORDER BY c.column_id;";
        cmd.Parameters.Add(new SqlParameter("@oid", oid));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var name = rdr.GetString(0);
            var sysType = rdr.GetString(1);
            var userType = rdr.GetString(2);
            var nullable = rdr.GetBoolean(3);

            cols.Add(new ColumnMetaDto
            {
                Name = name,
                SystemType = sysType,
                UserType = userType,
                IsNullable = nullable,
                Category = MapToCategory(sysType)
            });
        }

        return Json(cols);
    }

    // ----------------------------
    // API: Distinct values (lazy, capped)
    // ----------------------------
    [HttpGet]
    public async Task<IActionResult> GetDistinctValues(string schema, string obj, string field, int take = 500, string? search = null)
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        if (string.IsNullOrWhiteSpace(obj)) return BadRequest("obj required");
        if (string.IsNullOrWhiteSpace(field)) return BadRequest("field required");

        schema = schema.Trim();
        obj = obj.Trim();
        field = field.Trim();
        take = Math.Clamp(take <= 0 ? 500 : take, 10, 500);

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        var (oid, _) = await ResolveObjectAsync(con, schema, obj);
        if (oid == 0) return NotFound("object not found");

        var colMap = await LoadColumnMapAsync(con, oid);
        if (!colMap.ContainsKey(field)) return BadRequest("unknown field");

        // identifiers are safe ONLY after validation
        var sqlSchema = Q(schema);
        var sqlObj = Q(obj);
        var sqlCol = Q(field);

        // Cast to nvarchar for broad compatibility & stable ordering
        var sb = new StringBuilder();
        sb.AppendLine($"SELECT DISTINCT TOP (@take) CAST({sqlCol} AS nvarchar(4000)) AS [v]");
        sb.AppendLine($"FROM {sqlSchema}.{sqlObj}");
        sb.Append("WHERE 1=1");

        var p = new List<SqlParameter> { new("@take", take) };

        if (!string.IsNullOrWhiteSpace(search))
        {
            sb.AppendLine();
            sb.Append($"  AND CAST({sqlCol} AS nvarchar(4000)) LIKE @like");
            p.Add(new SqlParameter("@like", "%" + search.Trim() + "%"));
        }

        sb.AppendLine();
        sb.AppendLine("ORDER BY [v];");

        var values = new List<string?>();
        await using var cmd = CreateCommand(con);
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddRange(p.ToArray());

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (rdr.IsDBNull(0)) values.Add(null);
            else values.Add(rdr.GetString(0));
        }

        return Json(new { values });
    }

    // ----------------------------
    // API: Aggregate (server-side pivot)
    // ----------------------------
    [HttpPost]
    public async Task<IActionResult> Aggregate([FromBody] AggregateRequest req)
    {
        if (req == null) return BadRequest("missing request");
        if (string.IsNullOrWhiteSpace(req.Schema)) return BadRequest("schema required");
        if (string.IsNullOrWhiteSpace(req.Obj)) return BadRequest("obj required");

        var schema = req.Schema.Trim();
        var obj = req.Obj.Trim();
        var agg = (req.Agg ?? "Sum").Trim();
        var max = Math.Clamp(req.MaxCells <= 0 ? 50000 : req.MaxCells, 100, 200000);

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        var (oid, _) = await ResolveObjectAsync(con, schema, obj);
        if (oid == 0) return NotFound("object not found");

        var colMap = await LoadColumnMapAsync(con, oid);

        // validate all requested fields exist
        bool HasCol(string f) => !string.IsNullOrWhiteSpace(f) && colMap.ContainsKey(f.Trim());

        var rows = (req.Rows ?? new()).Where(HasCol).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cols = (req.Cols ?? new()).Where(HasCol).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var vals = (req.Values ?? new()).Where(HasCol).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Count mode ignores vals
        if (agg.Equals("Count", StringComparison.OrdinalIgnoreCase)) vals = new();

        // Build SELECT/GROUP BY
        var select = new List<string>();
        var groupBy = new List<string>();
        var orderBy = new List<string>();

        string schemaQ = Q(schema);
        string objQ = Q(obj);

        string ColExpr(string f)
        {
            var colQ = Q(f);
            var quoted = $"{schemaQ}.{objQ}.{colQ}";

            // date grouping if date
            if (req.DateGroups != null && req.DateGroups.TryGetValue(f, out var g) && IsDateType(colMap[f].systemTypeLower))
                return DateExpr(quoted, g);

            return quoted;
        }

        // group columns
        foreach (var f in rows)
        {
            var expr = ColExpr(f);
            select.Add($"{expr} AS {Q(f)}");
            groupBy.Add(expr);
            orderBy.Add(Q(f));
        }
        foreach (var f in cols)
        {
            // avoid duplicates if same field in rows+cols
            if (rows.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;

            var expr = ColExpr(f);
            select.Add($"{expr} AS {Q(f)}");
            groupBy.Add(expr);
            orderBy.Add(Q(f));
        }

        // aggregations (force 0.00 in controller via decimal(38,2))
        var selectAgg = new List<string>();
        var ps = new List<SqlParameter>();
        var where = new List<string>();
        int pi = 0;

        string AddParam(object? value)
        {
            var name = "@p" + pi++;
            ps.Add(new SqlParameter(name, value ?? DBNull.Value));
            return name;
        }

        // filters -> WHERE
        if (req.Filters != null)
        {
            foreach (var kv in req.Filters)
            {
                var f = kv.Key?.Trim();
                if (string.IsNullOrWhiteSpace(f) || !colMap.ContainsKey(f)) continue;

                var spec = kv.Value ?? new FilterSpec();
                var mode = (spec.Mode ?? "in").Trim().ToLowerInvariant();
                var col = $"{schemaQ}.{objQ}.{Q(f)}";
                var isDate = IsDateType(colMap[f].systemTypeLower);

                if (mode == "isnull") { where.Add($"{col} IS NULL"); continue; }
                if (mode == "notnull") { where.Add($"{col} IS NOT NULL"); continue; }

                if (mode == "range" && isDate)
                {
                    if (!string.IsNullOrWhiteSpace(spec.FromUtc) && DateTime.TryParse(spec.FromUtc, out var fromDt))
                    {
                        var pFrom = AddParam(DateTime.SpecifyKind(fromDt, DateTimeKind.Utc));
                        where.Add($"{col} >= {pFrom}");
                    }
                    if (!string.IsNullOrWhiteSpace(spec.ToUtc) && DateTime.TryParse(spec.ToUtc, out var toDt))
                    {
                        var pTo = AddParam(DateTime.SpecifyKind(toDt, DateTimeKind.Utc));
                        where.Add($"{col} < {pTo}");
                    }
                    continue;
                }

                if (mode == "in" || mode == "notin")
                {
                    var values = (spec.Values ?? new()).Where(v => v != null).Select(v => v!).ToList();
                    if (values.Count == 0) continue;

                    // cap IN list
                    if (values.Count > 500) values = values.Take(500).ToList();

                    var inParams = values.Select(v => AddParam(v)).ToList();
                    where.Add($"{col} {(mode == "notin" ? "NOT IN" : "IN")} ({string.Join(",", inParams)})");
                    continue;
                }
            }
        }

        if (agg.Equals("Count", StringComparison.OrdinalIgnoreCase))
        {
            // user asked for decimal(38,2)
            selectAgg.Add($"CAST(COUNT_BIG(1) AS decimal(38,2)) AS {Q("Count")}");
        }
        else
        {
            var fn = AggFn(agg);
            foreach (var vf in vals)
            {
                var col = $"{schemaQ}.{objQ}.{Q(vf)}";
                // tolerate nvarchar numerics (TRY_CONVERT avoids SQL 8114)
                var conv = $"TRY_CONVERT(decimal(38,10), {col})";
                // cast final to 2 dp
                selectAgg.Add($"CAST({fn}({conv}) AS decimal(38,2)) AS {Q(vf)}");
            }
        }

        if (selectAgg.Count == 0)
        {
            if (rows.Count == 0 && cols.Count == 0)
            {
                var rawSql = new StringBuilder();
                rawSql.Append("SELECT TOP (@max) * ");
                rawSql.AppendLine($"FROM {schemaQ}.{objQ}");
                if (where.Count > 0)
                    rawSql.AppendLine("WHERE " + string.Join(" AND ", where));

                await using var rawCmd = CreateCommand(con);
                rawCmd.CommandText = rawSql.ToString();
                rawCmd.Parameters.Add(new SqlParameter("@max", max));
                foreach (var p in ps) rawCmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value ?? DBNull.Value));

                var rawData = new List<Dictionary<string, object?>>();
                await using (var rdr = await rawCmd.ExecuteReaderAsync())
                {
                    while (await rdr.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < rdr.FieldCount; i++)
                        {
                            var k = rdr.GetName(i);
                            row[k] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                        }
                        rawData.Add(row);
                    }
                }

                return Json(new
                {
                    schema,
                    obj,
                    agg,
                    rowFields = rows,
                    colFields = cols,
                    valueFields = Array.Empty<string>(),
                    data = rawData
                });
            }

            return BadRequest("No measures selected (or set Agg=Count).");
        }

        var sql = new StringBuilder();
        sql.Append("SELECT TOP (@max) ");
        sql.Append(string.Join(", ", select.Concat(selectAgg)));
        sql.AppendLine();
        sql.AppendLine($"FROM {schemaQ}.{objQ}");

        if (where.Count > 0)
            sql.AppendLine("WHERE " + string.Join(" AND ", where));

        if (groupBy.Count > 0)
            sql.AppendLine("GROUP BY " + string.Join(", ", groupBy));

        if (orderBy.Count > 0)
            sql.AppendLine("ORDER BY " + string.Join(", ", orderBy));

        await using var cmd = CreateCommand(con);
        cmd.CommandText = sql.ToString();
        cmd.Parameters.Add(new SqlParameter("@max", max));
        foreach (var p in ps) cmd.Parameters.Add(p);

        var data = new List<Dictionary<string, object?>>();
        await using (var rdr = await cmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rdr.FieldCount; i++)
                {
                    var k = rdr.GetName(i);
                    var v = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    row[k] = v;
                }
                data.Add(row);
            }
        }

        // Fix CS0173 by ensuring both branches share a common type (IEnumerable<string>).
        // Use the static readonly CountFieldArray to avoid repeated array allocations (addresses CA1861).
        var valueFields = agg.Equals("Count", StringComparison.OrdinalIgnoreCase)
            ? (IEnumerable<string>)CountFieldArray
            : vals;

        return Json(new
        {
            schema,
            obj,
            agg,
            rowFields = rows,
            colFields = cols,
            valueFields,
            data
        });
    }

    // ----------------------------
    // AI Narrative (Gemma via Ollama)
    // ----------------------------
    // ----------------------------
    // AI Narrative (Hybrid: deterministic facts -> Gemma via Ollama for wording)
    // Facts-only, 3–5 short bullets, no new numbers, no speculation.
    // ----------------------------
    [HttpPost]
    public async Task<IActionResult> Narrate([FromBody] NarrateRequest req)
    {
        if (req == null) return BadRequest("missing request");
        if (req.Data == null || req.Data.Count == 0) return Json(new { text = "" });

        var ollamaUrl = (_cfg["Dashboard:OllamaUrl"] ?? "http://localhost:11434/api/generate").Trim();
        var model = (_cfg["Dashboard:OllamaModel"] ?? "gemma3:1b").Trim();

        // ---- strict timeout ----
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // ---- helpers ----
        static decimal? ToDec(object? v)
        {
            if (v == null) return null;

            try
            {
                if (v is decimal d) return d;
                if (v is double db) return (decimal)db;
                if (v is float f) return (decimal)f;
                if (v is long l) return l;
                if (v is int i) return i;

                if (v is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number)
                    {
                        if (je.TryGetDecimal(out var dd)) return dd;
                        if (je.TryGetDouble(out var ddb)) return (decimal)ddb;
                    }
                    if (je.ValueKind == JsonValueKind.String)
                    {
                        var sn = je.GetString();
                        if (decimal.TryParse(sn, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pd))
                            return pd;
                    }
                    return null;
                }

                if (v is string s1)
                {
                    if (string.IsNullOrWhiteSpace(s1)) return null;
                    s1 = s1.Replace(",", "");
                    if (decimal.TryParse(s1, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pd))
                        return pd;

                    if (decimal.TryParse(s1, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.CurrentCulture, out var pd2))
                        return pd2;

                    return null;
                }

                var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Replace(",", "");
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var p))
                    return p;
            }
            catch { }

            return null;
        }

        static string? ToStr(object? v)
        {
            if (v == null) return null;
            if (v is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Null) return null;
                if (je.ValueKind == JsonValueKind.String) return je.GetString();
                return je.ToString();
            }
            return Convert.ToString(v);
        }

        static bool TryParseDate(string? s, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Try invariant then current culture. Accept YYYY-MM(-DD) and typical formats.
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
                return true;

            if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
                return true;

            // Handle common "YYYY-MM" or "YYYY/MM"
            if (s.Length == 7 && (s[4] == '-' || s[4] == '/'))
            {
                if (int.TryParse(s[..4], out var y) && int.TryParse(s[5..7], out var m) && m is >= 1 and <= 12)
                {
                    dt = new DateTime(y, m, 1);
                    return true;
                }
            }

            return false;
        }

        static string FmtN(decimal v)
            => v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

        static string FmtPct(decimal v)
            => (v * 100m).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";

        static string FmtDate(DateTime dt)
            => dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        static decimal StdDev(IReadOnlyList<decimal> xs)
        {
            if (xs.Count == 0) return 0m;
            decimal mean = 0m;
            for (int i = 0; i < xs.Count; i++) mean += xs[i];
            mean /= xs.Count;

            decimal var = 0m;
            for (int i = 0; i < xs.Count; i++)
            {
                var d = xs[i] - mean;
                var += d * d;
            }
            var /= xs.Count;
            // decimal sqrt: use double safely for display-only stats
            return (decimal)Math.Sqrt((double)var);
        }

        static decimal? PctChange(decimal from, decimal to)
        {
            var denom = Math.Abs(from);
            if (denom <= 0m) return null;
            return (to - from) / denom;
        }

        static string JoinLabel(Dictionary<string, object?> row, List<string> dims)
        {
            if (dims.Count == 0) return "(row)";
            var parts = new List<string>();
            foreach (var f in dims.Take(3))
            {
                if (!row.TryGetValue(f, out var raw)) continue;
                var s = ToStr(raw);
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s!.Trim());
            }
            return parts.Count == 0 ? "(row)" : string.Join(" / ", parts);
        }

        // ---- select measure(s) ----
        var measures = new List<string>();
        if (string.Equals(req.Agg, "Count", StringComparison.OrdinalIgnoreCase))
            measures.Add("Count");
        else if (req.Values != null)
            measures.AddRange(req.Values.Where(x => !string.IsNullOrWhiteSpace(x)));

        // If "Count" key isn't exact, try to find it
        if (measures.Count == 1 && string.Equals(measures[0], "Count", StringComparison.OrdinalIgnoreCase) && req.Data.Count > 0)
        {
            var keys = req.Data[0].Keys;
            var countKey = keys.FirstOrDefault(k => string.Equals(k, "Count", StringComparison.OrdinalIgnoreCase))
                        ?? keys.FirstOrDefault(k => k.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(countKey))
                measures[0] = countKey!;
        }

        if (measures.Count == 0) return Json(new { text = "" });
        var m0 = measures[0];

        // ---- dimension fields ----
        var dimFields = new List<string>();
        if ((req.Rows?.Count ?? 0) > 0) dimFields.AddRange(req.Rows!);
        if ((req.Cols?.Count ?? 0) > 0) dimFields.AddRange(req.Cols!);
        dimFields = dimFields
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dimFields.Count == 0 && req.Data.Count > 0)
        {
            var keys = req.Data[0].Keys.ToList();
            var measSet = new HashSet<string>(measures, StringComparer.OrdinalIgnoreCase);
            dimFields = keys.Where(k => !measSet.Contains(k)).ToList();
        }

        // ---- detect a time-like dimension (optional) ----
        string? timeField = null;
        if (dimFields.Count > 0)
        {
            var probe = req.Data.Take(Math.Min(200, req.Data.Count)).ToList();
            foreach (var f in dimFields)
            {
                int ok = 0, tot = 0;
                foreach (var r in probe)
                {
                    if (!r.TryGetValue(f, out var raw)) continue;
                    var s = ToStr(raw);
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    tot++;
                    if (TryParseDate(s, out _)) ok++;
                }
                if (tot >= 10 && ok >= (int)Math.Ceiling(tot * 0.7))
                {
                    timeField = f;
                    break;
                }
            }
        }

        // ---- build numeric points for primary measure ----
        var pts = new List<(decimal v, Dictionary<string, object?> row)>();
        int nonNum = 0;

        foreach (var r in req.Data)
        {
            if (!r.TryGetValue(m0, out var raw) || raw == null) { nonNum++; continue; }
            var dv = ToDec(raw);
            if (dv == null) { nonNum++; continue; }
            pts.Add((dv.Value, r));
        }

        if (pts.Count == 0)
            return Json(new { text = "" });

        // Basic stats
        decimal sum = 0m, min = pts[0].v, max = pts[0].v;
        var minRow = pts[0].row;
        var maxRow = pts[0].row;

        for (int i = 0; i < pts.Count; i++)
        {
            var v = pts[i].v;
            sum += v;
            if (v < min) { min = v; minRow = pts[i].row; }
            if (v > max) { max = v; maxRow = pts[i].row; }
        }
        var avg = sum / pts.Count;
        var sd = StdDev(pts.Select(x => x.v).ToList());

        // Sorted (desc)
        var topDesc = pts.OrderByDescending(x => x.v).ToList();
        var top1 = topDesc[0].v;
        var top1Label = JoinLabel(topDesc[0].row, dimFields);

        var top3Sum = topDesc.Take(Math.Min(3, topDesc.Count)).Sum(x => x.v);
        var top2Sum = topDesc.Take(Math.Min(2, topDesc.Count)).Sum(x => x.v);

        // ---- deterministic bullets (facts only) ----
        var bullets = new List<string>();

        // (A) Time trend if we have a time field
        if (!string.IsNullOrWhiteSpace(timeField))
        {
            var tp = new List<(DateTime t, decimal v)>();
            foreach (var (v, row) in pts)
            {
                if (!row.TryGetValue(timeField!, out var raw)) continue;
                var s = ToStr(raw);
                if (!TryParseDate(s, out var dt)) continue;
                tp.Add((dt.Date, v));
            }

            tp = tp.OrderBy(x => x.t).ToList();
            if (tp.Count >= 2)
            {
                var first = tp[0];
                var last = tp[^1];
                var delta = last.v - first.v;
                var pct = PctChange(first.v, last.v);

                if (pct.HasValue)
                    bullets.Add($"• {m0}: {FmtDate(first.t)} → {FmtDate(last.t)}: {FmtN(first.v)} → {FmtN(last.v)} (Δ {FmtN(delta)}; {FmtPct(pct.Value)})");
                else
                    bullets.Add($"• {m0}: {FmtDate(first.t)} → {FmtDate(last.t)}: {FmtN(first.v)} → {FmtN(last.v)} (Δ {FmtN(delta)})");

                // last vs previous
                var prev = tp[^2];
                var d2 = last.v - prev.v;
                var p2 = PctChange(prev.v, last.v);
                if (p2.HasValue)
                    bullets.Add($"• Last vs previous: {FmtDate(prev.t)} → {FmtDate(last.t)}: {FmtN(prev.v)} → {FmtN(last.v)} (Δ {FmtN(d2)}; {FmtPct(p2.Value)})");
                else
                    bullets.Add($"• Last vs previous: {FmtDate(prev.t)} → {FmtDate(last.t)}: {FmtN(prev.v)} → {FmtN(last.v)} (Δ {FmtN(d2)})");

                // volatility: last 7 vs prior 7 (if enough)
                if (tp.Count >= 14)
                {
                    var last7 = tp.TakeLast(7).Select(x => x.v).ToList();
                    var prev7 = tp.Skip(Math.Max(0, tp.Count - 14)).Take(7).Select(x => x.v).ToList();
                    var sdLast = StdDev(last7);
                    var sdPrev = StdDev(prev7);
                    var ratio = sdPrev > 0m ? (sdLast / sdPrev) : (decimal?)null;

                    if (ratio.HasValue)
                        bullets.Add($"• Volatility (std dev): last 7 = {FmtN(sdLast)} vs prior 7 = {FmtN(sdPrev)} ({FmtN(ratio.Value)}×)");
                    else
                        bullets.Add($"• Volatility (std dev): last 7 = {FmtN(sdLast)}; prior 7 = {FmtN(sdPrev)}");
                }
            }
        }

        // (B) Concentration / contributors (works best when not time-series)
        if (bullets.Count < 5 && sum != 0m)
        {
            var topShare = top1 / sum;
            bullets.Add($"• Top item: “{top1Label}” = {FmtN(top1)} ({FmtPct(topShare)}) of total {FmtN(sum)}");
        }

        if (bullets.Count < 5 && sum != 0m && topDesc.Count >= 3)
        {
            var share3 = top3Sum / sum;
            bullets.Add($"• Concentration: top 3 sum = {FmtN(top3Sum)} ({FmtPct(share3)}) of total {FmtN(sum)}");
        }

        // (C) Range / dispersion
        if (bullets.Count < 5)
        {
            if (min != 0m)
            {
                var ratio = max / min;
                bullets.Add($"• Range & spread: min {FmtN(min)} (“{JoinLabel(minRow, dimFields)}”), max {FmtN(max)} (“{JoinLabel(maxRow, dimFields)}”), avg {FmtN(avg)}, std dev {FmtN(sd)}, max/min {FmtN(ratio)}×");
            }
            else
            {
                bullets.Add($"• Range & spread: min {FmtN(min)} (“{JoinLabel(minRow, dimFields)}”), max {FmtN(max)} (“{JoinLabel(maxRow, dimFields)}”), avg {FmtN(avg)}, std dev {FmtN(sd)}");
            }
        }

        // Ensure 3–5 bullets (trim extras, or add a factual coverage line)
        if (bullets.Count > 5) bullets = bullets.Take(5).ToList();
        if (bullets.Count < 3)
        {
            bullets.Add($"• Computed from {req.Data.Count} aggregated rows; {nonNum} rows were null/non-numeric for “{m0}”.");
        }
        if (bullets.Count > 5) bullets = bullets.Take(5).ToList();

        var deterministic = string.Join("\n", bullets);

        // ---- Send facts to Gemma only to rewrite wording (numbers must remain identical) ----
        // Build an allow-list of numeric tokens present in deterministic bullets.
        static HashSet<string> ExtractNumbers(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var rx = new System.Text.RegularExpressions.Regex(@"[-+]?\d{1,3}(?:,\d{3})*(?:\.\d+)?%?|[-+]?\d+(?:\.\d+)?×", System.Text.RegularExpressions.RegexOptions.Compiled);
            foreach (System.Text.RegularExpressions.Match mm in rx.Matches(s))
                set.Add(mm.Value);
            return set;
        }

        var allowedNums = ExtractNumbers(deterministic);

        var prompt = new StringBuilder();
        prompt.AppendLine("Rewrite the bullet list for an executive dashboard.");
        prompt.AppendLine("Hard rules:");
        prompt.AppendLine("  - Output 3–5 short bullets ONLY. Each bullet starts with '• ' (bullet + space).");
        prompt.AppendLine("  - FACTS ONLY. No speculation. Avoid modal language (no: likely, suggests, may, might, could, probably).");
        prompt.AppendLine("  - DO NOT add or remove any numbers. All numbers must match exactly what appears in the input bullets.");
        prompt.AppendLine("  - Keep all numeric tokens exactly as-is (commas/decimals/%/× unchanged).");
        prompt.AppendLine("  - Do not spell out numbers as words.");
        prompt.AppendLine();
        prompt.AppendLine("INPUT BULLETS:");
        prompt.AppendLine(deterministic);

        try
        {
            using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            var ollamaReq = new
            {
                model,
                prompt = prompt.ToString(),
                stream = false,
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.9,
                    top_k = 40,
                    repeat_penalty = 1.12,
                    num_predict = 180,
                    seed = 42
                }
            };

            using var resp = await http.PostAsJsonAsync(ollamaUrl, ollamaReq, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
                return Json(new { text = deterministic });

            using var doc = JsonDocument.Parse(raw);
            var outText = doc.RootElement.TryGetProperty("response", out var r)
                ? (r.GetString() ?? "")
                : raw;

            outText = (outText ?? "").Trim();

            // ---- Validate: 3–5 bullets, no new numbers, no speculation language ----
            if (string.IsNullOrWhiteSpace(outText))
                return Json(new { text = deterministic });

            var lines = outText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            // Normalize bullet marker: accept '-' as bullet, convert to '•'
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("- ")) lines[i] = "• " + lines[i].Substring(2).TrimStart();
                else if (!lines[i].StartsWith("• ")) lines[i] = "• " + lines[i].TrimStart('•', ' ', '\t');
            }

            // Keep first 5 bullets max
            if (lines.Count > 5) lines = lines.Take(5).ToList();
            if (lines.Count < 3) return Json(new { text = deterministic });

            var validated = string.Join("\n", lines);

            // Speculation guard
            var badWords = new[]
            {
                "likely","suggests","may","might","could","probably","approx","approximately","around","estimate","seems","appears"
            };
            var lower = validated.ToLowerInvariant();
            if (badWords.Any(w => lower.Contains(w)))
                return Json(new { text = deterministic });

            // Numbers guard
            var outNums = ExtractNumbers(validated);
            foreach (var n in outNums)
            {
                if (!allowedNums.Contains(n))
                    return Json(new { text = deterministic });
            }

            // Ensure at least one numeric token remains (so we didn't lose the values)
            if (outNums.Count == 0 && allowedNums.Count > 0)
                return Json(new { text = deterministic });

            return Json(new { text = validated });
        }
        catch (OperationCanceledException)
        {
            return Json(new { text = deterministic });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Narrate failed");
            return Json(new { text = deterministic });
        }
    }


    // ============================================================
    // API: Dashboard layout versions (SAVE + HISTORY)
    // ============================================================

    private string LayoutUserKey()
        => (User?.Identity?.Name ?? "anonymous").Trim();

    private async Task EnsureLayoutTablesAsync(SqlConnection con)
    {
        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
IF OBJECT_ID('dbo.DashboardLayoutVersion','U') IS NULL
BEGIN
    CREATE TABLE dbo.DashboardLayoutVersion
    (
        LayoutVersionId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashboardLayoutVersion PRIMARY KEY,
        UserName        nvarchar(256) NOT NULL,
        Page            nvarchar(128) NOT NULL,
        Title           nvarchar(256) NULL,
        LayoutJson      nvarchar(max) NOT NULL,
        CreatedUtc      datetime2(3) NOT NULL CONSTRAINT DF_DashboardLayoutVersion_CreatedUtc DEFAULT (SYSUTCDATETIME())
    );

    CREATE INDEX IX_DashboardLayoutVersion_User_Page_Created
        ON dbo.DashboardLayoutVersion(UserName, Page, CreatedUtc DESC, LayoutVersionId DESC);
END

IF OBJECT_ID('dbo.DashboardLayoutState','U') IS NULL
BEGIN
    CREATE TABLE dbo.DashboardLayoutState
    (
        UserName          nvarchar(256) NOT NULL,
        Page              nvarchar(128) NOT NULL,
        CurrentVersionId  bigint NULL,
        UpdatedUtc        datetime2(3) NOT NULL CONSTRAINT DF_DashboardLayoutState_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashboardLayoutState PRIMARY KEY(UserName, Page)
    );

    CREATE INDEX IX_DashboardLayoutState_CurrentVersionId
        ON dbo.DashboardLayoutState(CurrentVersionId);
END
";
        await cmd.ExecuteNonQueryAsync();
    }

    public sealed class SaveLayoutVersionRequest
    {
        public string Page { get; set; } = "Multi";
        public string? Title { get; set; }
        public bool IsFavorite { get; set; }

        // Client sends GridStack layout (grid.save()) as JSON.
        // We store it exactly as JSON text.
        public JsonElement Layout { get; set; }
    }

    public sealed class LayoutVersionInfoDto
    {
        public long Id { get; set; }
        public int VersionNo { get; set; }   // 1..Total
        public int Total { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string? Title { get; set; }
        public bool IsFavorite { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> GetLayoutHistory(string page = "Multi", int take = 200)
    {
        page = (page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";
        take = Math.Clamp(take <= 0 ? 200 : take, 1, 500);

        var user = LayoutUserKey();
        var items = new List<LayoutVersionInfoDto>();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
;WITH v AS
(
    SELECT
        LayoutVersionId,
        CreatedUtc,
        Title,
        ISNULL(Favorite, 0) AS Favorite,
        CAST(ROW_NUMBER() OVER (ORDER BY CreatedUtc ASC, LayoutVersionId ASC) AS int) AS VersionNo,
        CAST(COUNT_BIG(1) OVER () AS int) AS Total
    FROM dbo.DashboardLayoutVersion
    WHERE UserName = @u AND Page = @p
)
SELECT TOP (@take)
    LayoutVersionId,
    CreatedUtc,
    Title,
    Favorite,
    VersionNo,
    Total
FROM v
ORDER BY VersionNo DESC;";
        cmd.Parameters.Add(new SqlParameter("@u", user));
        cmd.Parameters.Add(new SqlParameter("@p", page));
        cmd.Parameters.Add(new SqlParameter("@take", take));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            items.Add(new LayoutVersionInfoDto
            {
                Id = rdr.GetInt64(0),
                CreatedUtc = rdr.GetDateTime(1),
                Title = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                IsFavorite = rdr.GetBoolean(3),
                VersionNo = rdr.GetInt32(4), // CAST in SQL
                Total = rdr.GetInt32(5)      // CAST in SQL
            });
        }

        return Json(new { page, user, versions = items });
    }

    [HttpGet]
    public async Task<IActionResult> GetLayoutVersion(long id)
    {
        if (id <= 0) return BadRequest("id required");

        var user = LayoutUserKey();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT LayoutJson
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id AND UserName = @u;";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@u", user));

        var json = (string?)await cmd.ExecuteScalarAsync();
        if (string.IsNullOrWhiteSpace(json)) return NotFound("version not found");

        return Content(json, "application/json");
    }

    [HttpPost]
    public async Task<IActionResult> SaveLayoutVersion([FromBody] SaveLayoutVersionRequest req)
    {
        if (req == null) return BadRequest("missing request");

        var page = (req.Page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        if (req.Layout.ValueKind == JsonValueKind.Undefined || req.Layout.ValueKind == JsonValueKind.Null)
            return BadRequest("layout required");

        // store exact JSON text (no re-serialization)
        var layoutJson = req.Layout.GetRawText();
        if (string.IsNullOrWhiteSpace(layoutJson) || string.Equals(layoutJson.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return BadRequest("layout required");

        var user = LayoutUserKey();
        var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title!.Trim();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        long newId;
        await using (var cmd = CreateCommand(con))
        {
            cmd.CommandText = @"
INSERT INTO dbo.DashboardLayoutVersion(UserName, Page, Title, LayoutJson, Favorite)
VALUES (@u, @p, @t, @j, @fav);
SELECT CAST(SCOPE_IDENTITY() AS bigint);";
            cmd.Parameters.Add(new SqlParameter("@u", user));
            cmd.Parameters.Add(new SqlParameter("@p", page));
            cmd.Parameters.Add(new SqlParameter("@t", (object?)title ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@j", layoutJson));
            cmd.Parameters.Add(new SqlParameter("@fav", req.IsFavorite ? (object)true : DBNull.Value));

            newId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        if (newId <= 0) return StatusCode(500, "insert failed");

        LayoutVersionInfoDto? info = null;

        await using (var cmd = CreateCommand(con))
        {
            cmd.CommandText = @"
;WITH v AS
(
    SELECT
        LayoutVersionId,
        CreatedUtc,
        Title,
        ISNULL(Favorite, 0) AS Favorite,
        CAST(ROW_NUMBER() OVER (ORDER BY CreatedUtc ASC, LayoutVersionId ASC) AS int) AS VersionNo,
        CAST(COUNT_BIG(1) OVER () AS int) AS Total
    FROM dbo.DashboardLayoutVersion
    WHERE UserName = @u AND Page = @p
)
SELECT LayoutVersionId, CreatedUtc, Title, Favorite, VersionNo, Total
FROM v
WHERE LayoutVersionId = @id;";
            cmd.Parameters.Add(new SqlParameter("@u", user));
            cmd.Parameters.Add(new SqlParameter("@p", page));
            cmd.Parameters.Add(new SqlParameter("@id", newId));

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                info = new LayoutVersionInfoDto
                {
                    Id = rdr.GetInt64(0),
                    CreatedUtc = rdr.GetDateTime(1),
                    Title = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    IsFavorite = rdr.GetBoolean(3),
                    VersionNo = rdr.GetInt32(4), // CAST in SQL
                    Total = rdr.GetInt32(5)      // CAST in SQL
                };
            }
        }

        return Json(new
        {
            page,
            user,
            saved = info ?? new LayoutVersionInfoDto
            {
                Id = newId,
                CreatedUtc = DateTime.UtcNow,
                Title = title,
                VersionNo = 0,
                Total = 0
            }
        });
    }

    // Optional (matches the optional dbo.DashboardLayoutState table)
    public sealed class SetCurrentLayoutRequest
    {
        public string Page { get; set; } = "Multi";
        public long? CurrentVersionId { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> SetCurrentLayout([FromBody] SetCurrentLayoutRequest req)
    {
        if (req == null) return BadRequest("missing request");

        var page = (req.Page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        var user = LayoutUserKey();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
MERGE dbo.DashboardLayoutState AS tgt
USING (SELECT @u AS UserName, @p AS Page) AS src
ON (tgt.UserName = src.UserName AND tgt.Page = src.Page)
WHEN MATCHED THEN
    UPDATE SET CurrentVersionId = @vid, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (UserName, Page, CurrentVersionId) VALUES (@u, @p, @vid);";
        cmd.Parameters.Add(new SqlParameter("@u", user));
        cmd.Parameters.Add(new SqlParameter("@p", page));
        cmd.Parameters.Add(new SqlParameter("@vid", (object?)req.CurrentVersionId ?? DBNull.Value));

        await cmd.ExecuteNonQueryAsync();
        return Json(new { page, user, currentVersionId = req.CurrentVersionId });
    }

    [HttpGet]
    public async Task<IActionResult> GetCurrentLayout(string page = "Multi")
    {
        page = (page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        var user = LayoutUserKey();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT CurrentVersionId
FROM dbo.DashboardLayoutState
WHERE UserName = @u AND Page = @p;";
        cmd.Parameters.Add(new SqlParameter("@u", user));
        cmd.Parameters.Add(new SqlParameter("@p", page));

        var v = await cmd.ExecuteScalarAsync();
        long? id = (v == null || v == DBNull.Value) ? null : Convert.ToInt64(v);

        return Json(new { page, user, currentVersionId = id });
    }

    [HttpPost]
    public async Task<IActionResult> SetLayoutFavorite([FromBody] SetLayoutFavoriteRequest req)
    {
        if (req == null || req.Id <= 0) return BadRequest("id required");

        var user = LayoutUserKey();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
UPDATE dbo.DashboardLayoutVersion
SET Favorite = @fav
WHERE LayoutVersionId = @id AND UserName = @u;";
        cmd.Parameters.Add(new SqlParameter("@fav", req.IsFavorite ? (object)true : DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", req.Id));
        cmd.Parameters.Add(new SqlParameter("@u", user));

        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) return NotFound("version not found");

        return Json(new { id = req.Id, isFavorite = req.IsFavorite });
    }

    public sealed class SetLayoutFavoriteRequest
    {
        public long Id { get; set; }
        public bool IsFavorite { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteLayoutVersion([FromBody] DeleteLayoutVersionRequest req)
    {
        if (req == null || req.Id <= 0) return BadRequest("id required");

        var user = LayoutUserKey();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();

        // Clear any current-layout pointer that references this version
        await using (var cmd = CreateCommand(con))
        {
            cmd.CommandText = @"
UPDATE dbo.DashboardLayoutState
SET CurrentVersionId = NULL
WHERE UserName = @u AND CurrentVersionId = @id;";
            cmd.Parameters.Add(new SqlParameter("@u", user));
            cmd.Parameters.Add(new SqlParameter("@id", req.Id));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = CreateCommand(con))
        {
            cmd.CommandText = @"
DELETE FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id AND UserName = @u;";
            cmd.Parameters.Add(new SqlParameter("@id", req.Id));
            cmd.Parameters.Add(new SqlParameter("@u", user));

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0) return NotFound("version not found");
        }

        return Json(new { id = req.Id, deleted = true });
    }

    public sealed class DeleteLayoutVersionRequest
    {
        public long Id { get; set; }
    }
}