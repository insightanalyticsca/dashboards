using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
    public IActionResult Multi(string? layoutTitle = null, string page = "Multi")
    {
        ViewBag.DefaultSchema = (_cfg["Dashboard:DefaultSchema"] ?? "").Trim();

        page = string.IsNullOrWhiteSpace(page) ? "Multi" : page.Trim();
        var requestedTitle = (layoutTitle ?? "").Trim();

        if (string.IsNullOrWhiteSpace(requestedTitle))
        {
            ClearLayoutLaunchCookies();
            return View();
        }

        if (IsAllowedTitleOverride(requestedTitle))
        {
            SetTitleOverrideCookie(requestedTitle, page);
            ViewBag.LayoutTitle = requestedTitle;
            ViewBag.LayoutTitlePage = page;
        }
        else
        {
            ClearLayoutLaunchCookies();
        }

        return View();
    }


private sealed class CustomHtmlRuleConfig
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Schema { get; set; } = "*";
    public string Object { get; set; } = "*";
    public string ChartType { get; set; } = "customHtml";
    public string HtmlFile { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string PayloadMode { get; set; } = "";
    public int RefreshSeconds { get; set; }
    public string Role { get; set; } = "";
    public List<string> RowFields { get; set; } = new();
    public List<string> ColFields { get; set; } = new();
    public List<string> ValueFields { get; set; } = new();
    public string Agg { get; set; } = "Sum";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public Dictionary<string, string> DateGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FilterSpec> Filters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TrendSchema { get; set; } = "";
    public string TrendObject { get; set; } = "";
    public string TrendDatabase { get; set; } = "";
    public string TrendTimeField { get; set; } = "";
    public string TrendValueField { get; set; } = "";
    public int TrendMaxPoints { get; set; }
    public string SummarySchema { get; set; } = "";
    public string SummaryObject { get; set; } = "";
    public string SummaryDatabase { get; set; } = "";
    public string PointsSchema { get; set; } = "";
    public string PointsObject { get; set; } = "";
    public string PointsDatabase { get; set; } = "";
    public string DefaultMode { get; set; } = "";
    public string NormalPointsSchema { get; set; } = "";
    public string NormalPointsObject { get; set; } = "";
    public string NormalPointsDatabase { get; set; } = "";
    public string NormalSummarySchema { get; set; } = "";
    public string NormalSummaryObject { get; set; } = "";
    public string NormalSummaryDatabase { get; set; } = "";
    public string FastPointsSchema { get; set; } = "";
    public string FastPointsObject { get; set; } = "";
    public string FastPointsDatabase { get; set; } = "";
    public string FastSummarySchema { get; set; } = "";
    public string FastSummaryObject { get; set; } = "";
    public string FastSummaryDatabase { get; set; } = "";
}

public sealed class SqlConnectionDto
{
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public sealed class CustomHtmlLiveDataRequest
{
    public string Schema { get; set; } = "";
    public string Obj { get; set; } = "";
    public string PayloadMode { get; set; } = "";
    public string ConnectionName { get; set; } = "build";
    public Dictionary<string, FilterSpec> Filters { get; set; } = new();
    public List<string> Rows { get; set; } = new();
    public List<string> Cols { get; set; } = new();
    public List<string> Values { get; set; } = new();
    public string Agg { get; set; } = "Sum";
    public Dictionary<string, string> DateGroups { get; set; } = new();
    public int MaxCells { get; set; } = 50000;
    public string TemplateId { get; set; } = "";
    public string Role { get; set; } = "";
    public string TrendSchema { get; set; } = "";
    public string TrendObject { get; set; } = "";
    public string TrendDatabase { get; set; } = "";
    public string TrendTimeField { get; set; } = "";
    public string TrendValueField { get; set; } = "";
    public int TrendMaxPoints { get; set; } = 12;
    public string SummarySchema { get; set; } = "";
    public string SummaryObject { get; set; } = "";
    public string SummaryDatabase { get; set; } = "";
    public string PointsSchema { get; set; } = "";
    public string PointsObject { get; set; } = "";
    public string PointsDatabase { get; set; } = "";
    public string DefaultMode { get; set; } = "";
    public string NormalPointsSchema { get; set; } = "";
    public string NormalPointsObject { get; set; } = "";
    public string NormalPointsDatabase { get; set; } = "";
    public string NormalSummarySchema { get; set; } = "";
    public string NormalSummaryObject { get; set; } = "";
    public string NormalSummaryDatabase { get; set; } = "";
    public string FastPointsSchema { get; set; } = "";
    public string FastPointsObject { get; set; } = "";
    public string FastPointsDatabase { get; set; } = "";
    public string FastSummarySchema { get; set; } = "";
    public string FastSummaryObject { get; set; } = "";
    public string FastSummaryDatabase { get; set; } = "";
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
    var appBasePath = Request.PathBase.HasValue ? Request.PathBase.Value!.TrimEnd('/') : "";
    var htmlUrl = BuildStaticHtmlUrl(basePath, safeFile, appBasePath);
    var customHtmlBasePath = BuildStaticHtmlBasePath(basePath, appBasePath);
    var dashboardBaseUrl = string.IsNullOrWhiteSpace(appBasePath) ? "/Dashboard" : appBasePath + "/Dashboard";

    return Json(new
    {
        found = true,
        key = rule.Key,
        schema = rule.Schema,
        obj = rule.Object,
        chartType = rule.ChartType,
        htmlUrl,
        appBasePath,
        pathBase = appBasePath,
        customHtmlBasePath,
        htmlBasePath = customHtmlBasePath,
        dashboardBaseUrl,
        liveDataBaseUrl = dashboardBaseUrl,
        connectionName = rule.ConnectionName,
        payloadMode = rule.PayloadMode,
        refreshSeconds = rule.RefreshSeconds,
        role = rule.Role,
        rowFields = rule.RowFields,
        colFields = rule.ColFields,
        valueFields = rule.ValueFields,
        agg = string.IsNullOrWhiteSpace(rule.Agg) ? "Sum" : rule.Agg,
        title = rule.Title,
        icon = rule.Icon,
        dateGroups = rule.DateGroups,
        filters = rule.Filters,
        trendSchema = rule.TrendSchema,
        trendObject = rule.TrendObject,
        trendDatabase = rule.TrendDatabase,
        trendTimeField = rule.TrendTimeField,
        trendValueField = rule.TrendValueField,
        trendMaxPoints = rule.TrendMaxPoints,
        summarySchema = rule.SummarySchema,
        summaryObject = rule.SummaryObject,
        summaryDatabase = rule.SummaryDatabase,
        pointsSchema = rule.PointsSchema,
        pointsObject = rule.PointsObject,
        pointsDatabase = rule.PointsDatabase,
        defaultMode = rule.DefaultMode,
        normalPointsSchema = rule.NormalPointsSchema,
        normalPointsObject = rule.NormalPointsObject,
        normalPointsDatabase = rule.NormalPointsDatabase,
        normalSummarySchema = rule.NormalSummarySchema,
        normalSummaryObject = rule.NormalSummaryObject,
        normalSummaryDatabase = rule.NormalSummaryDatabase,
        fastPointsSchema = rule.FastPointsSchema,
        fastPointsObject = rule.FastPointsObject,
        fastPointsDatabase = rule.FastPointsDatabase,
        fastSummarySchema = rule.FastSummarySchema,
        fastSummaryObject = rule.FastSummaryObject,
        fastSummaryDatabase = rule.FastSummaryDatabase
    });
}

[HttpGet]
public IActionResult GetCustomHtmlTemplates()
{
    var basePath = (_cfg["Dashboard:CustomHtml:BasePath"] ?? "/custom-html").Trim();
    var appBasePath = Request.PathBase.HasValue ? Request.PathBase.Value!.TrimEnd('/') : "";
    var htmlBasePath = BuildStaticHtmlBasePath(basePath, appBasePath);

    var templates = LoadCustomHtmlTemplates()
        .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.HtmlFile))
        .Select(t => new
        {
            id = t.Key,
            key = t.Key,
            label = string.IsNullOrWhiteSpace(t.Label) ? (string.IsNullOrWhiteSpace(t.Title) ? t.Key : t.Title) : t.Label,
            schema = t.Schema,
            @object = t.Object,
            chartType = t.ChartType,
            htmlFile = Path.GetFileName(t.HtmlFile.Trim()),
            htmlUrl = BuildStaticHtmlUrl(basePath, Path.GetFileName(t.HtmlFile.Trim()), appBasePath),
            htmlBasePath,
            connectionName = t.ConnectionName,
            payloadMode = t.PayloadMode,
            refreshSeconds = t.RefreshSeconds,
            role = t.Role,
            rowFields = t.RowFields,
            colFields = t.ColFields,
            valueFields = t.ValueFields,
            agg = string.IsNullOrWhiteSpace(t.Agg) ? "Sum" : t.Agg,
            title = t.Title,
            icon = t.Icon,
            dateGroups = t.DateGroups,
            filters = t.Filters,
            trendSchema = t.TrendSchema,
            trendObject = t.TrendObject,
            trendDatabase = t.TrendDatabase,
            trendTimeField = t.TrendTimeField,
            trendValueField = t.TrendValueField,
            trendMaxPoints = t.TrendMaxPoints,
            summarySchema = t.SummarySchema,
            summaryObject = t.SummaryObject,
            summaryDatabase = t.SummaryDatabase,
            pointsSchema = t.PointsSchema,
            pointsObject = t.PointsObject,
            pointsDatabase = t.PointsDatabase,
            defaultMode = t.DefaultMode,
            normalPointsSchema = t.NormalPointsSchema,
            normalPointsObject = t.NormalPointsObject,
            normalPointsDatabase = t.NormalPointsDatabase,
            normalSummarySchema = t.NormalSummarySchema,
            normalSummaryObject = t.NormalSummaryObject,
            normalSummaryDatabase = t.NormalSummaryDatabase,
            fastPointsSchema = t.FastPointsSchema,
            fastPointsObject = t.FastPointsObject,
            fastPointsDatabase = t.FastPointsDatabase,
            fastSummarySchema = t.FastSummarySchema,
            fastSummaryObject = t.FastSummaryObject,
            fastSummaryDatabase = t.FastSummaryDatabase
        })
        .ToList();

    return Json(new { templates });
}

[HttpPost]
public async Task<IActionResult> GetCustomHtmlLiveData([FromBody] CustomHtmlLiveDataRequest req)
{
    if (req == null) return BadRequest("missing request");

    var configuredRule = ResolveRequestedCustomHtmlRule(req);
    if (configuredRule != null)
    {
        ApplyConfiguredCustomHtmlRule(req, configuredRule);
    }

    if (string.IsNullOrWhiteSpace(req.Schema) || string.IsNullOrWhiteSpace(req.Obj)) return BadRequest("schema/obj required");

    req.Schema = NormalizeCustomHtmlToken(req.Schema ?? "");
    req.Obj = NormalizeCustomHtmlToken(req.Obj ?? "");
    req.TrendSchema = NormalizeCustomHtmlToken(req.TrendSchema ?? "");
    req.TrendObject = NormalizeCustomHtmlToken(req.TrendObject ?? "");
    req.SummarySchema = NormalizeCustomHtmlToken(req.SummarySchema ?? "");
    req.SummaryObject = NormalizeCustomHtmlToken(req.SummaryObject ?? "");
    req.PointsSchema = NormalizeCustomHtmlToken(req.PointsSchema ?? "");
    req.PointsObject = NormalizeCustomHtmlToken(req.PointsObject ?? "");
    req.NormalPointsSchema = NormalizeCustomHtmlToken(req.NormalPointsSchema ?? "");
    req.NormalPointsObject = NormalizeCustomHtmlToken(req.NormalPointsObject ?? "");
    req.NormalSummarySchema = NormalizeCustomHtmlToken(req.NormalSummarySchema ?? "");
    req.NormalSummaryObject = NormalizeCustomHtmlToken(req.NormalSummaryObject ?? "");
    req.FastPointsSchema = NormalizeCustomHtmlToken(req.FastPointsSchema ?? "");
    req.FastPointsObject = NormalizeCustomHtmlToken(req.FastPointsObject ?? "");
    req.FastSummarySchema = NormalizeCustomHtmlToken(req.FastSummarySchema ?? "");
    req.FastSummaryObject = NormalizeCustomHtmlToken(req.FastSummaryObject ?? "");

    var payloadMode = (req.PayloadMode ?? "").Trim();
    if (payloadMode.Equals("remoteHealthMonitor", StringComparison.OrdinalIgnoreCase))
    {
        return await GetRemoteHealthLiveDataAsync(req);
    }

    if (payloadMode.Equals("agingForecastMonitor", StringComparison.OrdinalIgnoreCase))
    {
        return await GetAgingForecastLiveDataAsync(req);
    }

    if (payloadMode.Equals("sdpRequests", StringComparison.OrdinalIgnoreCase))
    {
        return await GetServiceDeskPlusRequestsDataAsync(req);
    }

    if (payloadMode.Equals("rawRows", StringComparison.OrdinalIgnoreCase))
    {
        return await GetTemplateRawRowsDataAsync(req);
    }

    if (payloadMode.Equals("templateAggregate", StringComparison.OrdinalIgnoreCase))
    {
        return await GetTemplateAggregateDataAsync(req);
    }

    return BadRequest("unsupported custom html live mode: " + payloadMode);
}



private async Task<IActionResult> GetServiceDeskPlusRequestsDataAsync(CustomHtmlLiveDataRequest req)
{
    var role = (req.Role ?? "").Trim();
    var rows = await LoadServiceDeskPlusRequestRowsAsync(req, HttpContext?.RequestAborted ?? CancellationToken.None);
    var data = BuildItsTicketVisualRows(rows, role);

    return Json(new
    {
        found = true,
        mode = "sdpRequests",
        connectionName = string.IsNullOrWhiteSpace(req.ConnectionName) ? "sdpcloud" : req.ConnectionName,
        schema = req.Schema,
        obj = req.Obj,
        role,
        rowFields = Array.Empty<string>(),
        colFields = Array.Empty<string>(),
        valueFields = Array.Empty<string>(),
        data,
        debug = new
        {
            source = "SDPCloud.Contents(sdpondemand.manageengine.com) equivalent: itdesk -> request -> request",
            fetchedRows = rows.Count,
            returnedRows = data.Count,
            role
        }
    });
}

private async Task<List<Dictionary<string, object?>>> LoadServiceDeskPlusRequestRowsAsync(CustomHtmlLiveDataRequest req, CancellationToken ct)
{
    var baseUrl = (_cfg["ServiceDeskPlus:BaseUrl"] ?? "https://sdpondemand.manageengine.com").Trim().TrimEnd('/');
    var path = (_cfg["ServiceDeskPlus:RequestsPath"] ?? "/api/v3/requests").Trim();
    if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

    var authHeaderName = (_cfg["ServiceDeskPlus:AuthHeaderName"] ?? "Authorization").Trim();
    var authHeaderValue = (_cfg["ServiceDeskPlus:AuthHeaderValue"] ?? "").Trim();
    if (string.IsNullOrWhiteSpace(authHeaderValue))
    {
        authHeaderValue = Environment.GetEnvironmentVariable("SDP_AUTH_HEADER_VALUE") ?? "";
    }

    if (string.IsNullOrWhiteSpace(authHeaderValue))
    {
        throw new InvalidOperationException("ServiceDeskPlus AuthHeaderValue is not configured. Set ServiceDeskPlus:AuthHeaderValue or SDP_AUTH_HEADER_VALUE.");
    }

    var pageSize = Math.Clamp(int.TryParse(_cfg["ServiceDeskPlus:PageSize"], out var ps) ? ps : 100, 1, 1000);
    var maxPages = Math.Clamp(int.TryParse(_cfg["ServiceDeskPlus:MaxPages"], out var mp) ? mp : 50, 1, 10000);
    var inputParam = (_cfg["ServiceDeskPlus:InputDataParameterName"] ?? "input_data").Trim();
    if (string.IsNullOrWhiteSpace(inputParam)) inputParam = "input_data";

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    http.DefaultRequestHeaders.Accept.Clear();
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    TryAddConfiguredAuthHeader(http, authHeaderName, authHeaderValue);

    var all = new List<Dictionary<string, object?>>();

    for (var page = 0; page < maxPages; page++)
    {
        var startIndex = page * pageSize + 1;
        var input = new
        {
            list_info = new
            {
                row_count = pageSize,
                start_index = startIndex,
                sort_field = "created_time",
                sort_order = "desc"
            }
        };

        var inputJson = JsonSerializer.Serialize(input);
        var url = baseUrl + path + "?" + Uri.EscapeDataString(inputParam) + "=" + Uri.EscapeDataString(inputJson);
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ServiceDeskPlus request failed HTTP {(int)resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var pageRows = ExtractFirstObjectArray(doc.RootElement, "requests", "request", "data", "rows", "items")
            .Select(FlattenJsonElement)
            .ToList();
        if (pageRows.Count == 0) break;

        all.AddRange(pageRows);
        if (pageRows.Count < pageSize) break;
    }

    return all;
}

private static void TryAddConfiguredAuthHeader(HttpClient http, string headerName, string headerValue)
{
    if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(headerValue)) return;

    if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
    {
        var parts = headerValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(parts[0], parts[1]);
            return;
        }
    }

    http.DefaultRequestHeaders.TryAddWithoutValidation(headerName, headerValue);
}

private static IEnumerable<JsonElement> ExtractFirstObjectArray(JsonElement root, params string[] preferredNames)
{
    foreach (var name in preferredNames)
    {
        if (TryFindNamedArray(root, name, out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
            yield break;
        }
    }
}

private static bool TryFindNamedArray(JsonElement element, string name, out JsonElement array)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                array = prop.Value;
                return true;
            }
            if (TryFindNamedArray(prop.Value, name, out array)) return true;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            if (TryFindNamedArray(item, name, out array)) return true;
        }
    }

    array = default;
    return false;
}

private static Dictionary<string, object?> FlattenJsonElement(JsonElement element)
{
    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    void Walk(JsonElement node, string prefix)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in node.EnumerateObject())
            {
                var name = string.IsNullOrWhiteSpace(prefix) ? ToTitleToken(p.Name) : prefix + "." + ToTitleToken(p.Name);
                Walk(p.Value, name);
            }
            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            row[prefix] = node.GetRawText();
            return;
        }

        row[prefix] = JsonScalar(node);
    }
    Walk(element, "");
    return row;
}

private static string ToTitleToken(string value)
{
    return string.Join(" ", (value ?? "").Replace("_", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}

private static object? JsonScalar(JsonElement e)
{
    return e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDouble(out var d) ? d : e.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText()
    };
}

private static List<Dictionary<string, object?>> BuildItsTicketVisualRows(List<Dictionary<string, object?>> sourceRows, string role)
{
    role = (role ?? "").Trim().ToLowerInvariant();
    var today = DateTime.Today;
    var currStart = new DateTime(today.Year, today.Month, 1);
    var nextStart = currStart.AddMonths(1);
    var lastStart = currStart.AddMonths(-1);
    var currEnd = today.AddDays(1) < nextStart ? today.AddDays(1) : nextStart;
    var daysElapsed = Math.Max(1, today.Day);
    var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);

    var tickets = sourceRows
        .Select(r => new ItsTicketRow(r))
        .Where(t => t.CreatedDate.HasValue)
        .GroupBy(t => string.IsNullOrWhiteSpace(t.RequestId) ? Guid.NewGuid().ToString("N") : t.RequestId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderBy(x => x.CreatedDate).First())
        .ToList();

    var baselineMonths = Enumerable.Range(1, 12)
        .Select(i => new { Start = currStart.AddMonths(-i), End = currStart.AddMonths(-i + 1) })
        .ToList();

    if (role.Contains("priority") || role.Contains("status"))
    {
        IEnumerable<ItsTicketRow> scoped = tickets;
        if (role.Contains("closed"))
        {
            scoped = scoped.Where(t => t.CloseDate.HasValue && t.CloseDate.Value >= currStart && t.CloseDate.Value < currEnd);
        }
        else
        {
            scoped = scoped.Where(t => t.CreatedDate.HasValue && t.CreatedDate.Value >= currStart && t.CreatedDate.Value < currEnd);
        }

        var labelSelector = role.Contains("status")
            ? new Func<ItsTicketRow, string>(t => string.IsNullOrWhiteSpace(t.Status) ? "Unknown" : t.Status)
            : new Func<ItsTicketRow, string>(t => string.IsNullOrWhiteSpace(t.Priority) ? "Unknown" : t.Priority);

        return scoped
            .GroupBy(labelSelector, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Dictionary<string, object?>
            {
                [role.Contains("status") ? "Status.Name" : "Priority.Name"] = g.Key,
                ["Count"] = g.Count(),
                ["__Value"] = g.Count()
            })
            .OrderByDescending(r => Convert.ToInt32(r["Count"]))
            .ToList();
    }

    if (role.Contains("sla"))
    {
        var current = role.Contains("response")
            ? SlaRate(tickets.Where(t => t.CreatedDate >= lastStart && t.CreatedDate < currStart), t => !t.FirstResponseOverdue)
            : SlaRate(tickets.Where(t => t.CloseDate >= lastStart && t.CloseDate < currStart), t => !t.Overdue);
        var prior = role.Contains("response")
            ? SlaRate(tickets.Where(t => t.CreatedDate >= lastStart.AddMonths(-1) && t.CreatedDate < lastStart), t => !t.FirstResponseOverdue)
            : SlaRate(tickets.Where(t => t.CloseDate >= lastStart.AddMonths(-1) && t.CloseDate < lastStart), t => !t.Overdue);
        var delta = prior == 0 ? 0 : current - prior;
        return new List<Dictionary<string, object?>>
        {
            new()
            {
                ["CurrentPeriod"] = lastStart,
                ["CurrentPct"] = current,
                ["PriorPct"] = prior,
                ["DeltaPct"] = delta,
                ["ResponseSlaCurrentPct"] = current,
                ["ResponseSlaPreviousPct"] = prior,
                ["ResponseSlaMoMDeltaPct"] = delta,
                ["ClosureSlaCurrentPct"] = current,
                ["ClosureSlaPreviousPct"] = prior,
                ["ClosureSlaMoMDeltaPct"] = delta
            }
        };
    }

    var lastOpen = tickets.Count(t => t.CreatedDate >= lastStart && t.CreatedDate < currStart);
    var currMtdOpen = tickets.Count(t => t.CreatedDate >= currStart && t.CreatedDate < currEnd);
    var avgOpen = baselineMonths.Average(m => tickets.Count(t => t.CreatedDate >= m.Start && t.CreatedDate < m.End));
    var openDelta = avgOpen == 0 ? 0 : (lastOpen - avgOpen) / avgOpen;

    var lastClosed = tickets.Count(t => t.CloseDate >= lastStart && t.CloseDate < currStart);
    var currMtdClosed = tickets.Count(t => t.CloseDate >= currStart && t.CloseDate < currEnd);
    var avgClosed = baselineMonths.Average(m => tickets.Count(t => t.CloseDate >= m.Start && t.CloseDate < m.End));
    var closedDelta = avgClosed == 0 ? 0 : (lastClosed - avgClosed) / avgClosed;

    var row = new Dictionary<string, object?>
    {
        ["LastMonthLabel"] = lastStart,
        ["CurrentMonthLabel"] = currStart,
        ["LMOpenTickets"] = lastOpen,
        ["open_12m_avg"] = avgOpen,
        ["open_vs_12m_avg_pct"] = openDelta,
        ["LMOpenStatus"] = avgOpen == 0 ? "Open Volume" : openDelta > 0.05 ? "High" : openDelta < -0.05 ? "Low" : "Stable",
        ["CurrentMtdOpen"] = currMtdOpen,
        ["CurrentMtdOpenProratedPct"] = avgOpen == 0 ? 0 : ((currMtdOpen * 30.0 / daysElapsed) - avgOpen) / avgOpen,
        ["LMClosedTickets"] = lastClosed,
        ["closed_12m_avg"] = avgClosed,
        ["closed_vs_12m_avg_pct"] = closedDelta,
        ["LMClosedStatus"] = avgClosed == 0 ? "Close Volume" : closedDelta > 0.05 ? "High" : closedDelta < -0.05 ? "Low" : "Stable",
        ["CurrentMtdClosed"] = currMtdClosed,
        ["CurrentMtdClosedProratedPct"] = avgClosed == 0 ? 0 : ((currMtdClosed * daysInMonth * 1.0 / daysElapsed) - avgClosed) / avgClosed
    };
    return new List<Dictionary<string, object?>> { row };
}

private static double SlaRate(IEnumerable<ItsTicketRow> rows, Func<ItsTicketRow, bool> pass)
{
    var list = rows.ToList();
    if (list.Count == 0) return 0;
    return list.Count(pass) * 1.0 / list.Count;
}

private sealed class ItsTicketRow
{
    private readonly Dictionary<string, object?> _row;
    public ItsTicketRow(Dictionary<string, object?> row) { _row = row; }
    public string RequestId => ReadString("Request ID", "Id", "id");
    public string Status => ReadString("Status.Name", "Status Name", "Status");
    public string Priority => ReadString("Priority.Name", "Priority Name", "Priority");
    public DateTime? CreatedDate => ReadDate("Created Date", "Created Time", "CreatedTime", "Created");
    public DateTime? CloseDate => ReadDate("Resolved Time", "Completed Time", "ResolvedTime", "CompletedTime", "Closed Date");
    public bool Overdue => ReadBool("Overdue Status", "OverdueStatus", "Is Overdue");
    public bool FirstResponseOverdue => ReadBool("First Response Overdue Status", "FirstResponseOverdueStatus", "First Response Overdue");

    private string ReadString(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (_row.TryGetValue(key, out var v) && v != null && v != DBNull.Value) return Convert.ToString(v) ?? "";
        }
        return "";
    }
    private bool ReadBool(params string[] keys)
    {
        var s = ReadString(keys).Trim();
        if (bool.TryParse(s, out var b)) return b;
        if (int.TryParse(s, out var i)) return i != 0;
        return s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
    private DateTime? ReadDate(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!_row.TryGetValue(key, out var v) || v == null || v == DBNull.Value) continue;
            if (v is DateTime dt) return dt;
            if (long.TryParse(Convert.ToString(v), out var epoch))
            {
                if (epoch > 999999999999) return DateTimeOffset.FromUnixTimeMilliseconds(epoch).LocalDateTime;
                if (epoch > 999999999) return DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
            }
            if (DateTime.TryParse(Convert.ToString(v), out var parsed)) return parsed;
        }
        return null;
    }
}


private async Task<IActionResult> GetTemplateRawRowsDataAsync(CustomHtmlLiveDataRequest req)
{
    await using var con = new SqlConnection(ConnStr(req.ConnectionName));
    await con.OpenAsync();

    var schema = (req.Schema ?? "").Trim();
    var obj = (req.Obj ?? "").Trim();
    var max = Math.Clamp(req.MaxCells <= 0 ? 50000 : req.MaxCells, 100, 200000);

    var (oid, objectType) = await ResolveObjectAsync(con, schema, obj);
    if (oid == 0)
    {
        return NotFound($"object not found: {schema}.{obj} on connector '{(string.IsNullOrWhiteSpace(req.ConnectionName) ? "build" : req.ConnectionName)}'");
    }

    var colMap = await LoadColumnMapAsync(con, oid);
    var data = await QueryProjectedRowsAsync(
        con,
        schema,
        obj,
        colMap,
        req.Filters ?? new Dictionary<string, FilterSpec>(),
        Array.Empty<string>(),
        null,
        descending: false,
        top: max);

    return Json(new
    {
        found = true,
        mode = "rawRows",
        connectionName = string.IsNullOrWhiteSpace(req.ConnectionName) ? "build" : req.ConnectionName,
        schema,
        obj,
        objectType,
        agg = "RawRows",
        rowFields = Array.Empty<string>(),
        colFields = Array.Empty<string>(),
        valueFields = Array.Empty<string>(),
        data,
        debug = new
        {
            source = $"{schema}.{obj}",
            returnedRows = data.Count,
            requestedMaxRows = max
        }
    });
}

private async Task<IActionResult> GetTemplateAggregateDataAsync(CustomHtmlLiveDataRequest req)
{
    // Keep template aggregation separate from the public /Dashboard/Aggregate route so
    // file-backed HTML templates do not depend on the constructor's current wells.
    var rows = req.Rows ?? new List<string>();
    var cols = req.Cols ?? new List<string>();
    var values = req.Values ?? new List<string>();
    var agg = string.IsNullOrWhiteSpace(req.Agg) ? "Sum" : req.Agg.Trim();

    return await Aggregate(new AggregateRequest
    {
        ConnectionName = req.ConnectionName,
        Schema = req.Schema,
        Obj = req.Obj,
        Rows = rows,
        Cols = cols,
        Values = values,
        Agg = agg,
        DateGroups = req.DateGroups ?? new Dictionary<string, string>(),
        Filters = req.Filters ?? new Dictionary<string, FilterSpec>(),
        MaxCells = req.MaxCells <= 0 ? 50000 : req.MaxCells
    });
}

private async Task<IActionResult> GetRemoteHealthLiveDataAsync(CustomHtmlLiveDataRequest req)
{
    var snapshotConnectionName = ResolveConnectionNameFromDatabase(req.ConnectionName, "");
    await using var con = new SqlConnection(ConnStr(snapshotConnectionName));
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

    var trendConnectionName = ResolveConnectionNameFromDatabase(snapshotConnectionName, req.TrendDatabase);
    SqlConnection? trendCon = null;
    if (!string.Equals(trendConnectionName, snapshotConnectionName, StringComparison.OrdinalIgnoreCase))
    {
        trendCon = new SqlConnection(ConnStr(trendConnectionName));
        await trendCon.OpenAsync();
    }

    await using var trendConDispose = trendCon;
    var trendDb = trendCon ?? con;
    var (trendOid, _) = await ResolveObjectAsync(trendDb, trendSchema, trendObject);
    if (trendOid != 0)
    {
        var trendCols = await LoadColumnMapAsync(trendDb, trendOid);
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
                trendDb,
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
    var baseConnectionName = string.IsNullOrWhiteSpace(req.ConnectionName) ? "build" : req.ConnectionName.Trim();
    var pointsConnectionName = ResolveConnectionNameFromDatabase(baseConnectionName, req.PointsDatabase);
    var summaryConnectionName = ResolveConnectionNameFromDatabase(baseConnectionName, req.SummaryDatabase);

    await using var pointsCon = new SqlConnection(ConnStr(pointsConnectionName));
    await pointsCon.OpenAsync();

    var pointsSchema = string.IsNullOrWhiteSpace(req.PointsSchema) ? req.Schema.Trim() : req.PointsSchema.Trim();
    var pointsObject = string.IsNullOrWhiteSpace(req.PointsObject) ? req.Obj.Trim() : req.PointsObject.Trim();
    var summarySchema = string.IsNullOrWhiteSpace(req.SummarySchema) ? req.Schema.Trim() : req.SummarySchema.Trim();
    var summaryObject = string.IsNullOrWhiteSpace(req.SummaryObject) ? req.Obj.Trim() : req.SummaryObject.Trim();

    var (pointsOid, _) = await ResolveObjectAsync(pointsCon, pointsSchema, pointsObject);
    if (pointsOid == 0) return NotFound("points object not found");

    var pointsCols = await LoadColumnMapAsync(pointsCon, pointsOid);
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
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    var noFilters = new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase);

    var pointRows = await QueryProjectedRowsAsync(
        pointsCon,
        pointsSchema,
        pointsObject,
        pointsCols,
        noFilters,
        pointProjection,
        pointDateField,
        descending: false,
        top: 0);

    // Important: return raw point rows to the custom HTML.
    // The HTML is responsible for aggregating by the active filters so actuals,
    // forecast nulls, confidence bands, and seasonality match the original visual.
    // Server-side daily aggregation collapses dimensions and can distort ActualAmount.
    var visiblePointRows = pointRows;

    List<Dictionary<string, object?>> summaryRows = new();
    SqlConnection? summaryCon = null;
    if (!string.Equals(summaryConnectionName, pointsConnectionName, StringComparison.OrdinalIgnoreCase))
    {
        summaryCon = new SqlConnection(ConnStr(summaryConnectionName));
        await summaryCon.OpenAsync();
    }

    await using var summaryConDispose = summaryCon;
    var summaryDb = summaryCon ?? pointsCon;
    var (summaryOid, _) = await ResolveObjectAsync(summaryDb, summarySchema, summaryObject);
    if (summaryOid != 0)
    {
        var summaryCols = await LoadColumnMapAsync(summaryDb, summaryOid);
        var runTimeField = PickExistingColumn(summaryCols, "RunDateTime", "run_date_time", "LastActualDate", "last_actual_date");
        summaryRows = await QueryProjectedRowsAsync(
            summaryDb,
            summarySchema,
            summaryObject,
            summaryCols,
            noFilters,
            Array.Empty<string>(),
            runTimeField,
            descending: true,
            top: 1);
    }

    return Json(new
    {
        found = true,
        mode = "agingForecastMonitor",
        summaryRows,
        pointRows = visiblePointRows,
        debug = new
        {
            pointsConnectionName,
            summaryConnectionName,
            pointsSchema,
            pointsObject,
            summarySchema,
            summaryObject,
            rawPointRows = pointRows.Count,
            returnedPointRows = visiblePointRows.Count
        }
    });
}


private static List<Dictionary<string, object?>> TrimAgingRawPointRowsToTrailingWindow(
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
            if (string.IsNullOrWhiteSpace(key) || !row.TryGetValue(key, out var value) || value == null) continue;
            if (value is bool b) return b;
            if (value is byte bt) return bt != 0;
            if (value is short s) return s != 0;
            if (value is int i) return i != 0;
            if (bool.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            if (int.TryParse(Convert.ToString(value), out var asInt)) return asInt != 0;
        }
        return false;
    }

    static DateTime ReadDate(Dictionary<string, object?> row, string dateField)
    {
        if (!string.IsNullOrWhiteSpace(dateField)
            && row.TryGetValue(dateField, out var value)
            && value != null
            && DateTime.TryParse(Convert.ToString(value), out var parsed))
        {
            return parsed.Date;
        }
        if (row.TryGetValue("PointDate", out var pointDate)
            && pointDate != null
            && DateTime.TryParse(Convert.ToString(pointDate), out var parsedPointDate))
        {
            return parsedPointDate.Date;
        }
        return DateTime.MinValue;
    }

    var dated = rows
        .Select(row => new
        {
            Row = row,
            Date = ReadDate(row, pointDateField),
            IsForecast = ReadBool(row, "IsForecast", "is_forecast")
        })
        .Where(x => x.Date != DateTime.MinValue)
        .ToList();

    if (dated.Count == 0)
    {
        return rows;
    }

    var uniqueDays = dated.Select(x => x.Date).Distinct().OrderBy(x => x).ToList();
    if (uniqueDays.Count <= maxTotalDays)
    {
        return rows;
    }

    var lastActualDate = dated
        .Where(x => !x.IsForecast)
        .Select(x => x.Date)
        .DefaultIfEmpty(uniqueDays.Last())
        .Max();

    var forecastDayCount = dated.Where(x => x.IsForecast).Select(x => x.Date).Distinct().Count();
    var historyBudget = Math.Max(30, maxTotalDays - forecastDayCount);
    var minHistoryDate = lastActualDate.AddDays(-(historyBudget - 1));

    return dated
        .Where(x => x.IsForecast || x.Date >= minHistoryDate)
        .OrderBy(x => x.Date)
        .Select(x => x.Row)
        .ToList();
}

private const int AgingForecastTrailingWindowDays = 365;

private sealed class AgingPointAccumulator
{
    public DateTime Date { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal PredictedAmountWithoutS2 { get; set; }
    public decimal PredictedAmountWithS2 { get; set; }
    public decimal LowerWithoutS2 { get; set; }
    public decimal UpperWithoutS2 { get; set; }
    public decimal LowerWithS2 { get; set; }
    public decimal UpperWithS2 { get; set; }
    public decimal ConfidenceWithoutS2Sum { get; set; }
    public decimal ConfidenceWithS2Sum { get; set; }
    public int ConfidenceWithoutS2Count { get; set; }
    public int ConfidenceWithS2Count { get; set; }
    public bool IsForecast { get; set; }
    public bool IsHoldout { get; set; }
    public int HorizonDay { get; set; }
    public string S2StrategyChosen { get; set; } = "";
}

private static List<Dictionary<string, object?>> AggregateAgingPointRows(
    List<Dictionary<string, object?>> rows,
    string pointDateField)
{
    static decimal ReadDecimalSafe(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !row.TryGetValue(key, out var value) || value == null) continue;
            if (value is decimal dec) return dec;
            if (value is double dbl) return (decimal)dbl;
            if (value is float flt) return (decimal)flt;
            if (value is int i) return i;
            if (value is long l) return l;
            if (decimal.TryParse(Convert.ToString(value), out var parsed)) return parsed;
        }
        return 0m;
    }

    static int ReadIntSafe(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !row.TryGetValue(key, out var value) || value == null) continue;
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is short s) return s;
            if (int.TryParse(Convert.ToString(value), out var parsed)) return parsed;
        }
        return 0;
    }

    static bool ReadBoolSafe(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !row.TryGetValue(key, out var value) || value == null) continue;
            if (value is bool b) return b;
            if (value is byte bt) return bt != 0;
            if (value is short s) return s != 0;
            if (value is int i) return i != 0;
            if (bool.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            if (int.TryParse(Convert.ToString(value), out var asInt)) return asInt != 0;
        }
        return false;
    }

    var map = new SortedDictionary<DateTime, AgingPointAccumulator>();
    foreach (var row in rows ?? new List<Dictionary<string, object?>>())
    {
        if (!row.TryGetValue(pointDateField, out var pointDateRaw) || pointDateRaw == null) continue;
        if (!DateTime.TryParse(Convert.ToString(pointDateRaw), out var pointDate)) continue;
        var date = pointDate.Date;

        if (!map.TryGetValue(date, out var acc))
        {
            acc = new AgingPointAccumulator
            {
                Date = date,
                HorizonDay = ReadIntSafe(row, "HorizonDay", "horizon_day"),
                S2StrategyChosen = Convert.ToString(row.TryGetValue("S2StrategyChosen", out var s2) ? s2 : "") ?? "",
            };
            map[date] = acc;
        }

        acc.ActualAmount += ReadDecimalSafe(row, "ActualAmount", "actual_amount");
        acc.PredictedAmountWithoutS2 += ReadDecimalSafe(row, "PredictedAmountWithoutS2", "predicted_amount_without_s2");
        acc.PredictedAmountWithS2 += ReadDecimalSafe(row, "PredictedAmountWithS2", "predicted_amount_with_s2");
        acc.LowerWithoutS2 += ReadDecimalSafe(row, "LowerWithoutS2", "lower_without_s2");
        acc.UpperWithoutS2 += ReadDecimalSafe(row, "UpperWithoutS2", "upper_without_s2");
        acc.LowerWithS2 += ReadDecimalSafe(row, "LowerWithS2", "lower_with_s2");
        acc.UpperWithS2 += ReadDecimalSafe(row, "UpperWithS2", "upper_with_s2");

        var c0 = ReadDecimalSafe(row, "ConfidenceWithoutS2", "confidence_without_s2");
        var c1 = ReadDecimalSafe(row, "ConfidenceWithS2", "confidence_with_s2");
        if (c0 != 0m || row.ContainsKey("ConfidenceWithoutS2") || row.ContainsKey("confidence_without_s2"))
        {
            acc.ConfidenceWithoutS2Sum += c0;
            acc.ConfidenceWithoutS2Count += 1;
        }
        if (c1 != 0m || row.ContainsKey("ConfidenceWithS2") || row.ContainsKey("confidence_with_s2"))
        {
            acc.ConfidenceWithS2Sum += c1;
            acc.ConfidenceWithS2Count += 1;
        }

        acc.IsForecast = acc.IsForecast || ReadBoolSafe(row, "IsForecast", "is_forecast");
        acc.IsHoldout = acc.IsHoldout || ReadBoolSafe(row, "IsHoldout", "is_holdout");
        var horizon = ReadIntSafe(row, "HorizonDay", "horizon_day");
        if (acc.HorizonDay == 0 || horizon < acc.HorizonDay) acc.HorizonDay = horizon;

        var strategy = Convert.ToString(row.TryGetValue("S2StrategyChosen", out var strategyRaw) ? strategyRaw : "") ?? "";
        if (!string.IsNullOrWhiteSpace(strategy)) acc.S2StrategyChosen = strategy;
    }

    return map.Values.Select(acc => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["PointDate"] = acc.Date.ToString("yyyy-MM-dd"),
        ["ActualAmount"] = acc.ActualAmount,
        ["PredictedAmountWithoutS2"] = acc.PredictedAmountWithoutS2,
        ["PredictedAmountWithS2"] = acc.PredictedAmountWithS2,
        ["LowerWithoutS2"] = acc.LowerWithoutS2,
        ["UpperWithoutS2"] = acc.UpperWithoutS2,
        ["LowerWithS2"] = acc.LowerWithS2,
        ["UpperWithS2"] = acc.UpperWithS2,
        ["ConfidenceWithoutS2"] = acc.ConfidenceWithoutS2Count > 0 ? acc.ConfidenceWithoutS2Sum / acc.ConfidenceWithoutS2Count : 0m,
        ["ConfidenceWithS2"] = acc.ConfidenceWithS2Count > 0 ? acc.ConfidenceWithS2Sum / acc.ConfidenceWithS2Count : 0m,
        ["IsForecast"] = acc.IsForecast,
        ["IsHoldout"] = acc.IsHoldout,
        ["HorizonDay"] = acc.HorizonDay,
        ["Year"] = acc.Date.Year,
        ["MonthNumeric"] = acc.Date.Month,
        ["MonthName"] = acc.Date.ToString("MMM"),
        ["DayOfMonth"] = acc.Date.Day,
        ["DateLabel"] = acc.Date.ToString("MMM dd"),
        ["DateHierarchyLabel"] = acc.Date.ToString("dd MMM yyyy"),
        ["S2StrategyChosen"] = acc.S2StrategyChosen ?? ""
    }).ToList();
}




private static List<Dictionary<string, object?>> TrimAgingPointRowsToTrailingYear(
    List<Dictionary<string, object?>> rows,
    int maxTotalDays)
{
    if (rows == null || rows.Count == 0 || maxTotalDays <= 0)
    {
        return rows ?? new List<Dictionary<string, object?>>();
    }

    static bool ReadBool(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return false;
        if (value is bool b) return b;
        if (value is byte bt) return bt != 0;
        if (value is short s) return s != 0;
        if (value is int i) return i != 0;
        if (bool.TryParse(Convert.ToString(value), out var parsed)) return parsed;
        if (int.TryParse(Convert.ToString(value), out var asInt)) return asInt != 0;
        return false;
    }

    static DateTime ReadDate(Dictionary<string, object?> row)
    {
        if (row.TryGetValue("PointDate", out var value) && value != null && DateTime.TryParse(Convert.ToString(value), out var parsed))
        {
            return parsed.Date;
        }
        return DateTime.MinValue;
    }

    var ordered = rows
        .Select(row => new { Row = row, Date = ReadDate(row), IsForecast = ReadBool(row, "IsForecast") })
        .Where(x => x.Date != DateTime.MinValue)
        .OrderBy(x => x.Date)
        .ToList();

    if (ordered.Count <= maxTotalDays)
    {
        return ordered.Select(x => x.Row).ToList();
    }

    var lastActualDate = ordered
        .Where(x => !x.IsForecast)
        .Select(x => x.Date)
        .DefaultIfEmpty(ordered.Last().Date)
        .Max();

    var forecastRows = ordered.Where(x => x.IsForecast).ToList();
    var forecastCount = forecastRows.Count;
    var historyBudget = Math.Max(30, maxTotalDays - forecastCount);
    var minHistoryDate = lastActualDate.AddDays(-(historyBudget - 1));

    var trimmed = ordered
        .Where(x => x.IsForecast || x.Date >= minHistoryDate)
        .Select(x => x.Row)
        .ToList();

    if (trimmed.Count > maxTotalDays)
    {
        trimmed = trimmed.Skip(trimmed.Count - maxTotalDays).ToList();
    }

    return trimmed;
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
    if (top > 0)
    {
        sql.Append("SELECT TOP (@top) ").Append(selectSql).AppendLine();
    }
    else
    {
        sql.Append("SELECT ").Append(selectSql).AppendLine();
    }
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
    if (top > 0)
    {
        cmd.Parameters.Add(new SqlParameter("@top", top));
    }
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


private static List<string> ReadStringList(IConfigurationSection section, string key)
{
    var list = section.GetSection(key)
        .GetChildren()
        .Select(x => (x.Value ?? "").Trim())
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToList();

    if (list.Count > 0) return list;

    var raw = (section[key] ?? "").Trim();
    return string.IsNullOrWhiteSpace(raw)
        ? new List<string>()
        : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

private static Dictionary<string, string> ReadStringDictionary(IConfigurationSection section, string key)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var child in section.GetSection(key).GetChildren())
    {
        var name = (child.Key ?? "").Trim();
        var value = (child.Value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
        {
            dict[name] = value;
        }
    }
    return dict;
}

private static Dictionary<string, FilterSpec> ReadFilterSpecs(IConfigurationSection section, string key)
{
    var filters = new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase);

    foreach (var child in section.GetSection(key).GetChildren())
    {
        var field = (child.Key ?? "").Trim();
        if (string.IsNullOrWhiteSpace(field)) continue;

        var spec = new FilterSpec
        {
            Mode = string.IsNullOrWhiteSpace(child["Mode"]) ? "in" : child["Mode"]!.Trim(),
            Values = ReadStringList(child, "Values").Select(x => (string?)x).ToList(),
            FromUtc = string.IsNullOrWhiteSpace(child["FromUtc"]) ? null : child["FromUtc"]!.Trim(),
            ToUtc = string.IsNullOrWhiteSpace(child["ToUtc"]) ? null : child["ToUtc"]!.Trim()
        };

        filters[field] = spec;
    }

    return filters;
}


private CustomHtmlRuleConfig? ResolveRequestedCustomHtmlRule(CustomHtmlLiveDataRequest req)
{
    var allRules = LoadCustomHtmlTemplates()
        .Concat(LoadCustomHtmlRules())
        .Where(x => x.Enabled)
        .ToList();

    var templateId = NormalizeCustomHtmlToken(req.TemplateId ?? "");
    if (!string.IsNullOrWhiteSpace(templateId))
    {
        var byKey = allRules.FirstOrDefault(x => CustomHtmlTokenEquals(x.Key, templateId));
        if (byKey != null) return byKey;
    }

    var schema = NormalizeCustomHtmlToken(req.Schema ?? "");
    var obj = NormalizeCustomHtmlToken(req.Obj ?? "");
    if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(obj))
    {
        return null;
    }

    var matches = allRules
        .Where(x => CustomHtmlTokenEquals(x.Schema, schema) && CustomHtmlTokenEquals(x.Object, obj))
        .ToList();

    if (!string.IsNullOrWhiteSpace(req.Role))
    {
        var roleMatches = matches
            .Where(x => string.Equals((x.Role ?? "").Trim(), (req.Role ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (roleMatches.Count > 0) matches = roleMatches;
    }

    if (!string.IsNullOrWhiteSpace(req.PayloadMode))
    {
        var modeMatches = matches
            .Where(x => string.Equals((x.PayloadMode ?? "").Trim(), (req.PayloadMode ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modeMatches.Count > 0) matches = modeMatches;
    }

    return matches.FirstOrDefault();
}

private void ApplyConfiguredCustomHtmlRule(CustomHtmlLiveDataRequest req, CustomHtmlRuleConfig rule)
{
    req.TemplateId = string.IsNullOrWhiteSpace(req.TemplateId) ? rule.Key : req.TemplateId;
    req.Schema = string.IsNullOrWhiteSpace(rule.Schema) || CustomHtmlTokenEquals(rule.Schema, "*") ? req.Schema : rule.Schema;
    req.Obj = string.IsNullOrWhiteSpace(rule.Object) || CustomHtmlTokenEquals(rule.Object, "*") ? req.Obj : rule.Object;
    req.ConnectionName = string.IsNullOrWhiteSpace(rule.ConnectionName) ? req.ConnectionName : rule.ConnectionName;
    req.PayloadMode = string.IsNullOrWhiteSpace(rule.PayloadMode) ? req.PayloadMode : rule.PayloadMode;
    req.Role = string.IsNullOrWhiteSpace(rule.Role) ? req.Role : rule.Role;
    req.Rows = rule.RowFields?.ToList() ?? new List<string>();
    req.Cols = rule.ColFields?.ToList() ?? new List<string>();
    req.Values = string.Equals(rule.Agg ?? "", "Count", StringComparison.OrdinalIgnoreCase)
        ? new List<string>()
        : (rule.ValueFields?.ToList() ?? new List<string>());
    req.Agg = string.IsNullOrWhiteSpace(rule.Agg) ? (string.IsNullOrWhiteSpace(req.Agg) ? "Sum" : req.Agg) : rule.Agg;
    req.DateGroups = MergeDateGroups(rule.DateGroups, req.DateGroups);
    req.Filters = MergeFilters(rule.Filters, req.Filters);

    req.TrendSchema = string.IsNullOrWhiteSpace(rule.TrendSchema) ? req.TrendSchema : rule.TrendSchema;
    req.TrendObject = string.IsNullOrWhiteSpace(rule.TrendObject) ? req.TrendObject : rule.TrendObject;
    req.TrendDatabase = string.IsNullOrWhiteSpace(rule.TrendDatabase) ? req.TrendDatabase : rule.TrendDatabase;
    req.TrendTimeField = string.IsNullOrWhiteSpace(rule.TrendTimeField) ? req.TrendTimeField : rule.TrendTimeField;
    req.TrendValueField = string.IsNullOrWhiteSpace(rule.TrendValueField) ? req.TrendValueField : rule.TrendValueField;
    req.TrendMaxPoints = rule.TrendMaxPoints > 0 ? rule.TrendMaxPoints : req.TrendMaxPoints;

    req.SummarySchema = string.IsNullOrWhiteSpace(rule.SummarySchema) ? req.SummarySchema : rule.SummarySchema;
    req.SummaryObject = string.IsNullOrWhiteSpace(rule.SummaryObject) ? req.SummaryObject : rule.SummaryObject;
    req.SummaryDatabase = string.IsNullOrWhiteSpace(rule.SummaryDatabase) ? req.SummaryDatabase : rule.SummaryDatabase;

    req.PointsSchema = string.IsNullOrWhiteSpace(rule.PointsSchema) ? req.PointsSchema : rule.PointsSchema;
    req.PointsObject = string.IsNullOrWhiteSpace(rule.PointsObject) ? req.PointsObject : rule.PointsObject;
    req.PointsDatabase = string.IsNullOrWhiteSpace(rule.PointsDatabase) ? req.PointsDatabase : rule.PointsDatabase;

    req.DefaultMode = string.IsNullOrWhiteSpace(rule.DefaultMode) ? req.DefaultMode : rule.DefaultMode;
    req.NormalPointsSchema = string.IsNullOrWhiteSpace(rule.NormalPointsSchema) ? req.NormalPointsSchema : rule.NormalPointsSchema;
    req.NormalPointsObject = string.IsNullOrWhiteSpace(rule.NormalPointsObject) ? req.NormalPointsObject : rule.NormalPointsObject;
    req.NormalPointsDatabase = string.IsNullOrWhiteSpace(rule.NormalPointsDatabase) ? req.NormalPointsDatabase : rule.NormalPointsDatabase;
    req.NormalSummarySchema = string.IsNullOrWhiteSpace(rule.NormalSummarySchema) ? req.NormalSummarySchema : rule.NormalSummarySchema;
    req.NormalSummaryObject = string.IsNullOrWhiteSpace(rule.NormalSummaryObject) ? req.NormalSummaryObject : rule.NormalSummaryObject;
    req.NormalSummaryDatabase = string.IsNullOrWhiteSpace(rule.NormalSummaryDatabase) ? req.NormalSummaryDatabase : rule.NormalSummaryDatabase;
    req.FastPointsSchema = string.IsNullOrWhiteSpace(rule.FastPointsSchema) ? req.FastPointsSchema : rule.FastPointsSchema;
    req.FastPointsObject = string.IsNullOrWhiteSpace(rule.FastPointsObject) ? req.FastPointsObject : rule.FastPointsObject;
    req.FastPointsDatabase = string.IsNullOrWhiteSpace(rule.FastPointsDatabase) ? req.FastPointsDatabase : rule.FastPointsDatabase;
    req.FastSummarySchema = string.IsNullOrWhiteSpace(rule.FastSummarySchema) ? req.FastSummarySchema : rule.FastSummarySchema;
    req.FastSummaryObject = string.IsNullOrWhiteSpace(rule.FastSummaryObject) ? req.FastSummaryObject : rule.FastSummaryObject;
    req.FastSummaryDatabase = string.IsNullOrWhiteSpace(rule.FastSummaryDatabase) ? req.FastSummaryDatabase : rule.FastSummaryDatabase;
}

private static Dictionary<string, string> MergeDateGroups(Dictionary<string, string>? configured, Dictionary<string, string>? incoming)
{
    var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (configured != null)
    {
        foreach (var kv in configured)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            merged[kv.Key.Trim()] = kv.Value.Trim();
        }
    }

    if (incoming != null)
    {
        foreach (var kv in incoming)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            merged[kv.Key.Trim()] = kv.Value.Trim();
        }
    }

    return merged;
}

private static Dictionary<string, FilterSpec> MergeFilters(Dictionary<string, FilterSpec>? configured, Dictionary<string, FilterSpec>? incoming)
{
    var merged = new Dictionary<string, FilterSpec>(StringComparer.OrdinalIgnoreCase);

    void CopyInto(Dictionary<string, FilterSpec>? source)
    {
        if (source == null) return;
        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
            merged[kv.Key.Trim()] = new FilterSpec
            {
                Mode = string.IsNullOrWhiteSpace(kv.Value.Mode) ? "in" : kv.Value.Mode,
                Values = kv.Value.Values?.ToList() ?? new List<string?>(),
                FromUtc = kv.Value.FromUtc,
                ToUtc = kv.Value.ToUtc
            };
        }
    }

    CopyInto(configured);
    CopyInto(incoming);
    return merged;
}

private string ResolveConnectionNameFromDatabase(string requestedConnection, string preferredDatabase)
{
    var requested = string.IsNullOrWhiteSpace(requestedConnection) ? "build" : requestedConnection.Trim();
    var preferred = (preferredDatabase ?? "").Trim();
    if (string.IsNullOrWhiteSpace(preferred))
    {
        return requested;
    }

    var exactConnectionString = _cfg.GetConnectionString(preferred);
    if (!string.IsNullOrWhiteSpace(exactConnectionString))
    {
        return preferred;
    }

    foreach (var child in _cfg.GetSection("ConnectionStrings").GetChildren())
    {
        var key = child.Key?.Trim();
        var value = child.Value?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;

        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            var databaseName = (builder.InitialCatalog ?? "").Trim();
            if (string.Equals(databaseName, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }
        catch
        {
        }
    }

    return requested;
}

private static CustomHtmlRuleConfig BindCustomHtmlRule(IConfigurationSection section)
{
    return new CustomHtmlRuleConfig
    {
        Key = (section["Key"] ?? "").Trim(),
        Label = (section["Label"] ?? "").Trim(),
        Enabled = !string.Equals(section["Enabled"], "false", StringComparison.OrdinalIgnoreCase),
        Schema = string.IsNullOrWhiteSpace(section["Schema"]) ? "*" : section["Schema"]!.Trim(),
        Object = string.IsNullOrWhiteSpace(section["Object"]) ? "*" : section["Object"]!.Trim(),
        ChartType = string.IsNullOrWhiteSpace(section["ChartType"]) ? "customHtml" : section["ChartType"]!.Trim(),
        HtmlFile = (section["HtmlFile"] ?? "").Trim(),
        ConnectionName = (section["ConnectionName"] ?? "").Trim(),
        PayloadMode = (section["PayloadMode"] ?? "").Trim(),
        RefreshSeconds = int.TryParse(section["RefreshSeconds"], out var refreshSeconds) ? Math.Clamp(refreshSeconds, 0, 3600) : 0,
        Role = (section["Role"] ?? "").Trim(),
        RowFields = ReadStringList(section, "RowFields"),
        ColFields = ReadStringList(section, "ColFields"),
        ValueFields = ReadStringList(section, "ValueFields"),
        Agg = string.IsNullOrWhiteSpace(section["Agg"]) ? "Sum" : section["Agg"]!.Trim(),
        Title = (section["Title"] ?? "").Trim(),
        Icon = (section["Icon"] ?? "").Trim(),
        DateGroups = ReadStringDictionary(section, "DateGroups"),
        Filters = ReadFilterSpecs(section, "Filters"),
        TrendSchema = (section["TrendSchema"] ?? "").Trim(),
        TrendObject = (section["TrendObject"] ?? "").Trim(),
        TrendDatabase = (section["TrendDatabase"] ?? "").Trim(),
        TrendTimeField = (section["TrendTimeField"] ?? "").Trim(),
        TrendValueField = (section["TrendValueField"] ?? "").Trim(),
        TrendMaxPoints = int.TryParse(section["TrendMaxPoints"], out var trendMaxPoints) ? Math.Clamp(trendMaxPoints, 1, 240) : 12,
        SummarySchema = (section["SummarySchema"] ?? "").Trim(),
        SummaryObject = (section["SummaryObject"] ?? "").Trim(),
        SummaryDatabase = (section["SummaryDatabase"] ?? "").Trim(),
        PointsSchema = (section["PointsSchema"] ?? "").Trim(),
        PointsObject = (section["PointsObject"] ?? "").Trim(),
        PointsDatabase = (section["PointsDatabase"] ?? "").Trim(),
        DefaultMode = (section["DefaultMode"] ?? "").Trim(),
        NormalPointsSchema = (section["NormalPointsSchema"] ?? "").Trim(),
        NormalPointsObject = (section["NormalPointsObject"] ?? "").Trim(),
        NormalPointsDatabase = (section["NormalPointsDatabase"] ?? "").Trim(),
        NormalSummarySchema = (section["NormalSummarySchema"] ?? "").Trim(),
        NormalSummaryObject = (section["NormalSummaryObject"] ?? "").Trim(),
        NormalSummaryDatabase = (section["NormalSummaryDatabase"] ?? "").Trim(),
        FastPointsSchema = (section["FastPointsSchema"] ?? "").Trim(),
        FastPointsObject = (section["FastPointsObject"] ?? "").Trim(),
        FastPointsDatabase = (section["FastPointsDatabase"] ?? "").Trim(),
        FastSummarySchema = (section["FastSummarySchema"] ?? "").Trim(),
        FastSummaryObject = (section["FastSummaryObject"] ?? "").Trim(),
        FastSummaryDatabase = (section["FastSummaryDatabase"] ?? "").Trim()
    };
}

private List<CustomHtmlRuleConfig> LoadCustomHtmlTemplates()
{
    return _cfg.GetSection("Dashboard:CustomHtml:Templates")
        .GetChildren()
        .Select(BindCustomHtmlRule)
        .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.HtmlFile))
        .ToList();
}

private List<CustomHtmlRuleConfig> LoadCustomHtmlRules()
{
    return _cfg.GetSection("Dashboard:CustomHtml:Rules")
        .GetChildren()
        .Select(BindCustomHtmlRule)
        .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.HtmlFile))
        .ToList();
}

private CustomHtmlRuleConfig? ResolveCustomHtmlRule(string schema, string obj, string chartType)
{
    var rules = LoadCustomHtmlTemplates()
        .Concat(LoadCustomHtmlRules())
        .ToList();

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
        ruleValue = NormalizeCustomHtmlToken(ruleValue);
        actualValue = NormalizeCustomHtmlToken(actualValue);
        if (CustomHtmlTokenEquals(ruleValue, "*")) return 1;
        return CustomHtmlWildcardMatches(ruleValue, actualValue) ? exactScore : -1000;
    }

    var score = 0;
    score += ScorePart(rule.Schema, schema, 8);
    score += ScorePart(rule.Object, obj, 12);
    score += ScorePart(rule.ChartType, chartType, 6);
    return score < 0 ? -1 : score;
}

private static string BuildStaticHtmlUrl(string basePath, string safeFile, string appBasePath)
{
    if (Uri.TryCreate(basePath, UriKind.Absolute, out var absoluteBase))
    {
        return absoluteBase.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(safeFile);
    }

    var prefix = BuildStaticHtmlBasePath(basePath, appBasePath);
    return prefix.TrimEnd('/') + "/" + Uri.EscapeDataString(safeFile);
}

private static string BuildStaticHtmlBasePath(string basePath, string appBasePath)
{
    var configured = string.IsNullOrWhiteSpace(basePath) ? "/custom-html" : basePath.Trim();

    if (Uri.TryCreate(configured, UriKind.Absolute, out var absoluteBase))
    {
        return absoluteBase.ToString().TrimEnd('/');
    }

    if (configured.StartsWith("~/", StringComparison.Ordinal))
    {
        configured = "/" + configured[2..];
    }
    else if (!configured.StartsWith("/", StringComparison.Ordinal))
    {
        configured = "/" + configured.Trim('/');
    }

    var normalizedAppBase = string.IsNullOrWhiteSpace(appBasePath) ? "" : appBasePath.TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(normalizedAppBase) &&
        (configured.Equals(normalizedAppBase, StringComparison.OrdinalIgnoreCase) ||
         configured.StartsWith(normalizedAppBase + "/", StringComparison.OrdinalIgnoreCase)))
    {
        return configured.TrimEnd('/');
    }

    return (normalizedAppBase + configured).TrimEnd('/');
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

private static bool CustomHtmlWildcardMatches(string ruleValue, string actualValue)
{
    var rule = NormalizeCustomHtmlToken(ruleValue);
    var actual = NormalizeCustomHtmlToken(actualValue);
    if (string.Equals(rule, "*", StringComparison.OrdinalIgnoreCase)) return true;
    if (!rule.Contains('*')) return string.Equals(rule, actual, StringComparison.OrdinalIgnoreCase);

    var parts = rule.Split('*', StringSplitOptions.None);
    var pos = 0;

    if (parts.Length > 0 && parts[0].Length > 0)
    {
        if (!actual.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        pos = parts[0].Length;
    }

    for (var i = 1; i < parts.Length; i++)
    {
        var part = parts[i];
        if (part.Length == 0) continue;
        var found = actual.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
        if (found < 0) return false;
        pos = found + part.Length;
    }

    var last = parts[^1];
    if (last.Length > 0 && !actual.EndsWith(last, StringComparison.OrdinalIgnoreCase)) return false;
    return true;
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
        public string ConnectionName { get; set; } = "build";
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
    private string ConnStr(string? connectionName = null)
    {
        var requested = (connectionName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = (Request?.Query["connection"].FirstOrDefault() ?? "build").Trim();
        }

        // Keep the application metadata store separate from report data connectors.
        if (requested.Equals("AppDb", StringComparison.OrdinalIgnoreCase)) requested = "build";

        var cs = _cfg.GetConnectionString(requested);
        if (!string.IsNullOrWhiteSpace(cs)) return cs!;

        // Universal ad-hoc SQL connector: allow the dashboard selector to accept
        // server.database without requiring appsettings.json edits. Authentication
        // follows the app pool / current Windows identity via Integrated Security.
        if (TryBuildServerDatabaseConnectionString(requested, out var typedConnectionString))
            return typedConnectionString;

        return _cfg.GetConnectionString("build")
               ?? throw new InvalidOperationException("Missing connection string (build/DefaultConnection/DashboardDb).");
    }

    private static bool TryBuildServerDatabaseConnectionString(string requested, out string connectionString)
    {
        connectionString = "";
        requested = (requested ?? "").Trim();
        if (string.IsNullOrWhiteSpace(requested)) return false;
        if (requested.Contains(";")) return false; // do not accept raw connection strings from the browser

        var lastDot = requested.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= requested.Length - 1) return false;

        var server = requested[..lastDot].Trim();
        var database = requested[(lastDot + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database)) return false;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true,
            ConnectTimeout = 15
        };
        connectionString = builder.ConnectionString;
        return true;
    }

    [HttpGet]
    public IActionResult GetConnections()
    {
        var list = _cfg.GetSection("ConnectionStrings")
            .GetChildren()
            .Select(x => new SqlConnectionDto { Name = x.Key, IsDefault = x.Key.Equals("build", StringComparison.OrdinalIgnoreCase) })
            .Where(x => !x.Name.Equals("AppDb", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToList();

        if (list.Count == 0) list.Add(new SqlConnectionDto { Name = "build", IsDefault = true });
        return Json(list);
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
    public async Task<IActionResult> GetSchemas(string connection = "build")
    {
        var like = SchemaLike();
        var schemas = new List<string>();

        await using var con = new SqlConnection(ConnStr(connection));
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
    public async Task<IActionResult> GetObjects(string schema, string connection = "build")
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        schema = schema.Trim();

        var objs = new List<DbObjectDto>();

        await using var con = new SqlConnection(ConnStr(connection));
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
    public Task<IActionResult> GetViews(string schema, string connection = "build")
        => GetObjects(schema, connection);

    // ----------------------------
    // API: Columns (metadata + category)
    // ----------------------------
    [HttpGet]
    public async Task<IActionResult> GetColumns(string schema, string obj, string connection = "build")
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        if (string.IsNullOrWhiteSpace(obj)) return BadRequest("obj required");
        schema = schema.Trim();
        obj = obj.Trim();

        var cols = new List<ColumnMetaDto>();

        await using var con = new SqlConnection(ConnStr(connection));
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
    public async Task<IActionResult> GetDistinctValues(string schema, string obj, string field, int take = 500, string? search = null, string connection = "build")
    {
        if (string.IsNullOrWhiteSpace(schema)) return BadRequest("schema required");
        if (string.IsNullOrWhiteSpace(obj)) return BadRequest("obj required");
        if (string.IsNullOrWhiteSpace(field)) return BadRequest("field required");

        schema = schema.Trim();
        obj = obj.Trim();
        field = field.Trim();
        take = Math.Clamp(take <= 0 ? 500 : take, 10, 500);

        await using var con = new SqlConnection(ConnStr(connection));
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

        await using var con = new SqlConnection(ConnStr(req.ConnectionName));
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

    private const string LayoutTitleOverrideCookieName = "DashboardLayoutTitleOverride";
    private const string LayoutTitleOverridePageCookieName = "DashboardLayoutTitleOverridePage";

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

IF COL_LENGTH('dbo.DashboardLayoutVersion', 'Favorite') IS NULL
BEGIN
    ALTER TABLE dbo.DashboardLayoutVersion ADD Favorite bit NULL;
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

    private List<string> ReadAllowedTitleList()
    {
        // Preferred location:
        // Dashboard:ExternalTitleLinks:AllowedTitles
        var titles = _cfg.GetSection("Dashboard:ExternalTitleLinks:AllowedTitles")
            .GetChildren()
            .Select(x => (x.Value ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (titles.Count > 0)
        {
            return titles;
        }

        // Backward-tolerant fallback if it was accidentally placed under CustomHtml.
        return _cfg.GetSection("Dashboard:CustomHtml:ExternalTitleLinks:AllowedTitles")
            .GetChildren()
            .Select(x => (x.Value ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private bool IsTitleOverrideEnabled()
    {
        var enabledRaw = _cfg["Dashboard:ExternalTitleLinks:Enabled"];
        if (string.IsNullOrWhiteSpace(enabledRaw))
        {
            enabledRaw = _cfg["Dashboard:CustomHtml:ExternalTitleLinks:Enabled"];
        }

        return string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAllowedTitleOverride(string? title)
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!IsTitleOverrideEnabled()) return false;

        var allowedTitles = ReadAllowedTitleList();

        // If Enabled=true but no list is configured, allow any non-blank title.
        // To restrict this in production, set Dashboard:ExternalTitleLinks:AllowedTitles.
        if (allowedTitles.Count == 0)
        {
            return true;
        }

        return allowedTitles.Any(x => string.Equals(x, title, StringComparison.OrdinalIgnoreCase));
    }

    private void SetTitleOverrideCookie(string title, string page)
    {
        title = (title ?? "").Trim();
        page = string.IsNullOrWhiteSpace(page) ? "Multi" : page.Trim();

        var options = new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        Response.Cookies.Append(LayoutTitleOverrideCookieName, title, options);
        Response.Cookies.Append(LayoutTitleOverridePageCookieName, page, options);
    }

    private void ClearLayoutLaunchCookies()
    {
        // Delete both root-path cookies and common app-path cookies, so old deploys do not linger.
        Response.Cookies.Delete(LayoutTitleOverrideCookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(LayoutTitleOverridePageCookieName, new CookieOptions { Path = "/" });

        if (Request.PathBase.HasValue && !string.IsNullOrWhiteSpace(Request.PathBase.Value))
        {
            Response.Cookies.Delete(LayoutTitleOverrideCookieName, new CookieOptions { Path = Request.PathBase.Value! });
            Response.Cookies.Delete(LayoutTitleOverridePageCookieName, new CookieOptions { Path = Request.PathBase.Value! });
        }
    }

    private string? ReadCookieValue(string name)
    {
        if (!Request.Cookies.TryGetValue(name, out var value))
        {
            return null;
        }

        value = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string? ReadLayoutTitleFromReferer()
    {
        if (!Request.Headers.TryGetValue("Referer", out var refererValues))
        {
            return null;
        }

        var refererRaw = refererValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(refererRaw))
        {
            return null;
        }

        if (!Uri.TryCreate(refererRaw, UriKind.Absolute, out var referer))
        {
            return null;
        }

        var query = referer.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
        if (!parsed.TryGetValue("layoutTitle", out var values))
        {
            return null;
        }

        var title = values.FirstOrDefault();
        title = (title ?? "").Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private string? ResolveRequestedLayoutTitle(string? layoutTitle)
    {
        var title = (layoutTitle ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        title = (ReadCookieValue(LayoutTitleOverrideCookieName) ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        title = (ReadLayoutTitleFromReferer() ?? "").Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private string ResolveRequestedLayoutPage(string requestedPage)
    {
        var page = (requestedPage ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        var cookiePage = (ReadCookieValue(LayoutTitleOverridePageCookieName) ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(cookiePage))
        {
            return cookiePage;
        }

        return page;
    }

    private async Task<long?> ResolveLayoutVersionIdByTitleAsync(
        SqlConnection con,
        string title,
        string page,
        bool allowAnyUser)
    {
        title = (title ?? "").Trim();
        page = string.IsNullOrWhiteSpace(page) ? "Multi" : page.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        await using var cmd = CreateCommand(con);

        if (allowAnyUser)
        {
            cmd.CommandText = @"
SELECT TOP (1) LayoutVersionId
FROM dbo.DashboardLayoutVersion
WHERE LTRIM(RTRIM(ISNULL(Title, ''))) = @title
  AND Page = @p
ORDER BY CreatedUtc DESC, LayoutVersionId DESC;";
            cmd.Parameters.Add(new SqlParameter("@title", title));
            cmd.Parameters.Add(new SqlParameter("@p", page));
        }
        else
        {
            var user = LayoutUserKey();

            cmd.CommandText = @"
SELECT TOP (1) LayoutVersionId
FROM dbo.DashboardLayoutVersion
WHERE LTRIM(RTRIM(ISNULL(Title, ''))) = @title
  AND Page = @p
  AND UserName = @u
ORDER BY CreatedUtc DESC, LayoutVersionId DESC;";
            cmd.Parameters.Add(new SqlParameter("@title", title));
            cmd.Parameters.Add(new SqlParameter("@p", page));
            cmd.Parameters.Add(new SqlParameter("@u", user));
        }

        var value = await cmd.ExecuteScalarAsync();
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt64(value);
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
                VersionNo = rdr.GetInt32(4),
                Total = rdr.GetInt32(5)
            });
        }

        return Json(new { page, user, versions = items });
    }

    private bool IsAllowedLayoutTitle(string title)
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return false;

        var enabled = string.Equals(
            _cfg["Dashboard:ExternalTitleLinks:Enabled"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!enabled) return false;

        var allowedTitles = _cfg.GetSection("Dashboard:ExternalTitleLinks:AllowedTitles")
            .GetChildren()
            .Select(x => (x.Value ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        return allowedTitles.Count == 0 ||
               allowedTitles.Any(x => string.Equals(x, title, StringComparison.OrdinalIgnoreCase));
    }

    [HttpGet]
    public async Task<IActionResult> GetLayoutVersion(long id, string page = "Multi", string? layoutTitle = null)
    {
        page = (page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        var user = LayoutUserKey();
        var title = (layoutTitle ?? "").Trim();

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        if (!string.IsNullOrWhiteSpace(title) && IsAllowedLayoutTitle(title))
        {
            await using var titleCmd = CreateCommand(con);
            titleCmd.CommandText = @"
SELECT TOP (1) LayoutJson
FROM dbo.DashboardLayoutVersion
WHERE LTRIM(RTRIM(ISNULL(Title, ''))) = @title
  AND Page = @p
ORDER BY CreatedUtc DESC, LayoutVersionId DESC;";
            titleCmd.Parameters.Add(new SqlParameter("@title", title));
            titleCmd.Parameters.Add(new SqlParameter("@p", page));

            var titleJson = (string?)await titleCmd.ExecuteScalarAsync();

            if (!string.IsNullOrWhiteSpace(titleJson))
            {
                return Content(titleJson, "application/json");
            }

            return NotFound("title version not found");
        }

        if (id <= 0) return BadRequest("id required");

        await using var cmd = CreateCommand(con);
        cmd.CommandText = @"
SELECT LayoutJson
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id
  AND UserName = @u
  AND Page = @p;";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@u", user));
        cmd.Parameters.Add(new SqlParameter("@p", page));

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
                    VersionNo = rdr.GetInt32(4),
                    Total = rdr.GetInt32(5)
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
        ClearLayoutLaunchCookies();
        return Json(new { page, user, currentVersionId = req.CurrentVersionId });
    }

    [HttpGet]
    public async Task<IActionResult> GetCurrentLayout(string page = "Multi", string? layoutTitle = null)
    {
        page = (page ?? "Multi").Trim();
        if (string.IsNullOrWhiteSpace(page)) page = "Multi";

        var user = LayoutUserKey();
        var requestedTitle = ResolveRequestedLayoutTitle(layoutTitle);
        var requestedPage = ResolveRequestedLayoutPage(page);

        await using var con = new SqlConnection(ConnStr());
        await con.OpenAsync();
        await EnsureLayoutTablesAsync(con);

        if (!string.IsNullOrWhiteSpace(requestedTitle) && IsAllowedTitleOverride(requestedTitle))
        {
            var resolvedId = await ResolveLayoutVersionIdByTitleAsync(
                con,
                requestedTitle,
                requestedPage,
                allowAnyUser: true);

            if (resolvedId.HasValue)
            {
                return Json(new
                {
                    page = requestedPage,
                    user,
                    currentVersionId = resolvedId.Value,
                    title = requestedTitle,
                    titleOverride = true
                });
            }
        }

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
        await EnsureLayoutTablesAsync(con);

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
        await EnsureLayoutTablesAsync(con);

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
