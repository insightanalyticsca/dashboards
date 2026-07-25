using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    private sealed record CxAnsweredSummary(
        decimal? CurrentAnswered,
        decimal? YtdAnswered,
        int? CurrentPeriod,
        string Source);

    private sealed record CxAbandonedCounts(
        decimal? CurrentAbandoned,
        decimal? YtdAbandoned,
        decimal? CurrentAnsweredForRate,
        decimal? YtdAnsweredForRate,
        string CurrentSource,
        string YtdSource,
        bool ExactThirtySecondRule);

    private sealed record LocatedDecimal(decimal Value, string Source);

    private sealed record GenesysSettings(
        string Environment,
        string ClientId,
        string ClientSecret,
        IReadOnlyList<string> QueueIds,
        string MediaType,
        string Direction,
        string TimeZoneId,
        decimal ThresholdSeconds,
        int CacheMinutes);

    private sealed record GenesysCounts(
        decimal CurrentAnswered,
        decimal CurrentAbandoned,
        decimal YtdAnswered,
        decimal YtdAbandoned,
        DateTimeOffset LoadedUtc);

    private sealed record GenesysToken(string AccessToken, DateTimeOffset ExpiresUtc);

    private sealed record GenesysCacheEntry(GenesysCounts Counts, DateTimeOffset ExpiresUtc);

    private static readonly HttpClient CxGenesysHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(4)
    };

    private static readonly SemaphoreSlim CxGenesysGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, GenesysToken> CxGenesysTokens = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, GenesysCacheEntry> CxGenesysCache = new(StringComparer.Ordinal);

    private static readonly string[] CurrentAbandonedCountAliases =
    {
        "current_month_abandoned_within_30_sec",
        "current_month_abandoned_within_30_seconds",
        "current_abandoned_within_30_sec",
        "current_abandoned_within_30_seconds",
        "abandoned_within_30_sec",
        "abandoned_within_30_seconds",
        "calls_abandoned_within_30_sec",
        "calls_abandoned_within_30_seconds",
        "abandoned_under_30_sec",
        "abandoned_under_30_seconds",
        "abandoned_30_sec",
        "abandoned_30_seconds",
        "short_abandoned_calls",
        "short_abandons",
        "current_month_abandoned_calls",
        "current_abandoned_calls",
        "abandoned_calls",
        "calls_abandoned",
        "abandoned_count"
    };

    private static readonly string[] YtdAbandonedCountAliases =
    {
        "ytd_abandoned_within_30_sec",
        "ytd_abandoned_within_30_seconds",
        "ytd_calls_abandoned_within_30_sec",
        "ytd_calls_abandoned_within_30_seconds",
        "ytd_abandoned_under_30_sec",
        "ytd_abandoned_under_30_seconds",
        "ytd_abandoned_30_sec",
        "ytd_abandoned_30_seconds",
        "ytd_short_abandoned_calls",
        "ytd_short_abandons",
        "ytd_abandoned_calls",
        "ytd_calls_abandoned",
        "ytd_abandoned_count"
    };


    private static readonly string[] CurrentAnsweredAliases =
    {
        "current_month_calls_answered",
        "current_calls_answered",
        "calls_answered",
        "answered",
        "handled",
        "calls_handled"
    };

    private static readonly string[] YtdAnsweredAliases =
    {
        "ytd_calls_answered",
        "ytd_answered",
        "ytd_handled",
        "ytd_calls_handled"
    };

    private static readonly string[] GenericCountValueAliases =
    {
        "abandoned_calls",
        "calls_abandoned",
        "abandoned_count",
        "bucket_count",
        "metric_count",
        "call_count",
        "count",
        "value"
    };

    private async Task<IActionResult> GetCxCallVolumeAnsweredDataAsync(
        CustomHtmlLiveDataRequest req,
        CancellationToken ct)
    {
        await using var con = new SqlConnection(ConnStr(req.ConnectionName));
        await con.OpenAsync(ct);

        const string sql = """
            ;WITH answered_by_period AS
            (
                SELECT
                    period_label,
                    TRY_CONVERT(int, period_sort) AS period_sort,
                    MAX(TRY_CONVERT(decimal(19, 4), handled)) AS calls_answered
                FROM rpt.vw_cx_response_time_bucket_chart_latest
                WHERE handled IS NOT NULL
                GROUP BY period_label, TRY_CONVERT(int, period_sort)
            ), sequenced AS
            (
                SELECT
                    period_label,
                    period_sort,
                    calls_answered,
                    LAG(calls_answered) OVER (ORDER BY period_sort) AS previous_answered
                FROM answered_by_period
                WHERE period_sort IS NOT NULL
            )
            SELECT
                CAST('Call Volume' AS nvarchar(100)) AS title,
                calls_answered AS value,
                calls_answered AS calls_answered,
                CAST(NULL AS nvarchar(100)) AS value_text,
                CAST('integer' AS nvarchar(20)) AS value_type,
                period_label,
                period_sort,
                period_sort AS sort_order,
                CASE
                    WHEN previous_answered IS NULL OR previous_answered = 0 THEN NULL
                    ELSE ((calls_answered - previous_answered) / previous_answered) * 100.0
                END AS delta_pct,
                CAST(NULL AS decimal(19, 4)) AS target_value,
                CAST(NULL AS nvarchar(30)) AS status
            FROM sequenced
            ORDER BY period_sort;
            """;

        var rows = await ReadCxRowsAsync(con, sql, ct);

        return Json(new
        {
            found = true,
            mode = "rawRows",
            connectionName = string.IsNullOrWhiteSpace(req.ConnectionName) ? "build" : req.ConnectionName,
            schema = req.Schema,
            obj = req.Obj,
            objectType = "derived",
            agg = "RawRows",
            rowFields = Array.Empty<string>(),
            colFields = Array.Empty<string>(),
            valueFields = Array.Empty<string>(),
            data = rows,
            debug = new
            {
                source = "rpt.vw_cx_response_time_bucket_chart_latest",
                measure = "MAX(handled) by period = Calls Answered",
                returnedRows = rows.Count
            }
        });
    }

    private async Task<IActionResult> GetCxCallHandlingCorrectedDataAsync(
        CustomHtmlLiveDataRequest req,
        CancellationToken ct)
    {
        await using var con = new SqlConnection(ConnStr(req.ConnectionName));
        await con.OpenAsync(ct);

        var rows = await ReadCxRowsAsync(
            con,
            "SELECT * FROM rpt.vw_cx_call_handling_table_latest ORDER BY TRY_CONVERT(int, row_sort), row_label;",
            ct);

        var answered = await LoadCxAnsweredSummaryAsync(con, ct);
        var abandoned = await LoadExactAbandonedCountsAsync(con, rows, answered, ct);
        var abandonedRateRecalculated = ApplyExactAbandonedRate(rows, answered, abandoned);

        return Json(new
        {
            found = true,
            mode = "rawRows",
            connectionName = string.IsNullOrWhiteSpace(req.ConnectionName) ? "build" : req.ConnectionName,
            schema = req.Schema,
            obj = req.Obj,
            objectType = "derived",
            agg = "RawRows",
            rowFields = Array.Empty<string>(),
            colFields = Array.Empty<string>(),
            valueFields = Array.Empty<string>(),
            data = rows,
            debug = new
            {
                source = "rpt.vw_cx_call_handling_table_latest",
                abandonedFormula = "Calls abandoned within 30 seconds / Calls Answered * 100",
                offeredMinusAnsweredFallback = false,
                staleStoredPercentageAllowed = true,
                answeredSource = answered.Source,
                answered.CurrentPeriod,
                answered.CurrentAnswered,
                answered.YtdAnswered,
                abandoned.CurrentAbandoned,
                abandoned.YtdAbandoned,
                abandoned.CurrentAnsweredForRate,
                abandoned.YtdAnsweredForRate,
                abandoned.CurrentSource,
                abandoned.YtdSource,
                abandoned.ExactThirtySecondRule,
                abandonedRateRecalculated,
                returnedRows = rows.Count
            }
        });
    }

    private static async Task<List<Dictionary<string, object?>>> ReadCxRowsAsync(
        SqlConnection con,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 300;

        var rows = new List<Dictionary<string, object?>>(64);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct)
                    ? null
                    : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }

    private async Task<CxAnsweredSummary> LoadCxAnsweredSummaryAsync(
        SqlConnection con,
        CancellationToken ct)
    {
        const string historicalSql = """
            ;WITH answered_by_period AS
            (
                SELECT
                    TRY_CONVERT(int, period_sort) AS period_sort,
                    MAX(TRY_CONVERT(decimal(19, 4), handled)) AS answered
                FROM its_dashboard_dev.cx.original_composition_series
                WHERE visual_key = 'response_time_bucket'
                  AND ISNULL(is_sample_data, 0) = 0
                  AND handled IS NOT NULL
                GROUP BY TRY_CONVERT(int, period_sort)
            ), latest_period AS
            (
                SELECT MAX(period_sort) AS period_sort
                FROM answered_by_period
                WHERE period_sort IS NOT NULL
            )
            SELECT
                lp.period_sort AS current_period,
                MAX(CASE WHEN a.period_sort = lp.period_sort THEN a.answered END) AS current_answered,
                SUM(CASE WHEN a.period_sort / 100 = lp.period_sort / 100 THEN a.answered END) AS ytd_answered
            FROM latest_period lp
            INNER JOIN answered_by_period a
                ON a.period_sort / 100 = lp.period_sort / 100
            GROUP BY lp.period_sort;
            """;

        try
        {
            var historical = await ReadCxRowsAsync(con, historicalSql, ct);
            var row = historical.FirstOrDefault();
            if (row != null)
            {
                var summary = new CxAnsweredSummary(
                    ToNullableDecimal(row, "current_answered"),
                    ToNullableDecimal(row, "ytd_answered"),
                    ToNullableInt(row, "current_period"),
                    "its_dashboard_dev.cx.original_composition_series.handled");

                if (summary.CurrentAnswered.HasValue)
                {
                    return summary;
                }
            }
        }
        catch (SqlException ex)
        {
            _log.LogWarning(
                ex,
                "Could not read CX response-time history; falling back to rpt.vw_cx_response_time_bucket_chart_latest.");
        }

        const string fallbackSql = """
            ;WITH answered_by_period AS
            (
                SELECT
                    TRY_CONVERT(int, period_sort) AS period_sort,
                    MAX(TRY_CONVERT(decimal(19, 4), handled)) AS answered
                FROM rpt.vw_cx_response_time_bucket_chart_latest
                WHERE handled IS NOT NULL
                GROUP BY TRY_CONVERT(int, period_sort)
            ), latest_period AS
            (
                SELECT MAX(period_sort) AS period_sort
                FROM answered_by_period
                WHERE period_sort IS NOT NULL
            )
            SELECT
                lp.period_sort AS current_period,
                MAX(CASE WHEN a.period_sort = lp.period_sort THEN a.answered END) AS current_answered,
                SUM(CASE WHEN a.period_sort / 100 = lp.period_sort / 100 THEN a.answered END) AS ytd_answered
            FROM latest_period lp
            INNER JOIN answered_by_period a
                ON a.period_sort / 100 = lp.period_sort / 100
            GROUP BY lp.period_sort;
            """;

        var fallback = await ReadCxRowsAsync(con, fallbackSql, ct);
        var fallbackRow = fallback.FirstOrDefault();

        return fallbackRow == null
            ? new CxAnsweredSummary(null, null, null, "unavailable")
            : new CxAnsweredSummary(
                ToNullableDecimal(fallbackRow, "current_answered"),
                ToNullableDecimal(fallbackRow, "ytd_answered"),
                ToNullableInt(fallbackRow, "current_period"),
                "rpt.vw_cx_response_time_bucket_chart_latest.handled");
    }

    private async Task<CxAbandonedCounts> LoadExactAbandonedCountsAsync(
        SqlConnection con,
        IReadOnlyList<Dictionary<string, object?>> callHandlingRows,
        CxAnsweredSummary answered,
        CancellationToken ct)
    {
        // 1. Accept only explicitly named count columns from the call-handling view.
        // Never interpret current_month_value/ytd_value as counts because those are percentages.
        var direct = ExtractExplicitAbandonedCounts(
            callHandlingRows,
            "rpt.vw_cx_call_handling_table_latest",
            answered.CurrentPeriod);
        if (direct.CurrentAbandoned.HasValue)
        {
            return direct;
        }

        // 2. Inspect the other existing CX call views. Some deployments expose the
        // exact 30-second abandoned count beside offered/handled without exposing it
        // in the call-handling table view.
        var viewQueries = new[]
        {
            new
            {
                Source = "rpt.vw_cx_call_volume_card_latest",
                Sql = "SELECT * FROM rpt.vw_cx_call_volume_card_latest;",
                AllowLabelledCount = false
            },
            new
            {
                Source = "rpt.vw_cx_response_time_bucket_chart_latest",
                Sql = "SELECT * FROM rpt.vw_cx_response_time_bucket_chart_latest;",
                AllowLabelledCount = true
            }
        };

        foreach (var candidate in viewQueries)
        {
            try
            {
                var candidateRows = await ReadCxRowsAsync(con, candidate.Sql, ct);
                var counts = ExtractExactCountsFromRows(
                    candidateRows,
                    candidate.Source,
                    answered.CurrentPeriod,
                    candidate.AllowLabelledCount);
                if (counts.CurrentAbandoned.HasValue)
                {
                    return counts;
                }
            }
            catch (SqlException ex)
            {
                _log.LogDebug(ex, "CX abandoned-count source {Source} was unavailable.", candidate.Source);
            }
        }

        // 3. Inspect the raw monthly CX series. This supports a dedicated series/bucket
        // such as "Abandoned within 30 sec" and sums one count per month for YTD.
        var rawQueries = new[]
        {
            new
            {
                Source = "its_dashboard_dev.cx.original_composition_series",
                Sql = """
                    SELECT TOP (10000) *
                    FROM its_dashboard_dev.cx.original_composition_series
                    WHERE ISNULL(is_sample_data, 0) = 0
                      AND visual_key IN ('response_time_bucket', 'call_volume', 'call_handling')
                    ORDER BY TRY_CONVERT(int, period_sort) DESC;
                    """
            },
            new
            {
                Source = "cx.original_composition_series",
                Sql = """
                    SELECT TOP (10000) *
                    FROM cx.original_composition_series
                    WHERE ISNULL(is_sample_data, 0) = 0
                      AND visual_key IN ('response_time_bucket', 'call_volume', 'call_handling')
                    ORDER BY TRY_CONVERT(int, period_sort) DESC;
                    """
            }
        };

        foreach (var candidate in rawQueries)
        {
            try
            {
                var candidateRows = await ReadCxRowsAsync(con, candidate.Sql, ct);
                var counts = ExtractExactCountsFromRows(
                    candidateRows,
                    candidate.Source,
                    answered.CurrentPeriod,
                    allowLabelledThirtySecondCount: true);
                if (counts.CurrentAbandoned.HasValue)
                {
                    return counts;
                }
            }
            catch (SqlException ex)
            {
                _log.LogDebug(ex, "CX raw abandoned-count source {Source} was unavailable.", candidate.Source);
            }
        }

        // 4. Final exact source: Genesys conversation detail metrics. This is used only
        // when credentials and queue IDs are configured. Results are cached, so normal
        // dashboard refreshes do not repeatedly scan the Genesys API.
        var genesys = await TryLoadGenesysCountsAsync(answered.CurrentPeriod, ct);
        if (genesys != null)
        {
            return new CxAbandonedCounts(
                genesys.CurrentAbandoned,
                genesys.YtdAbandoned,
                genesys.CurrentAnswered,
                genesys.YtdAnswered,
                "Genesys Cloud tAbandon <= configured threshold",
                "Genesys Cloud tAbandon <= configured threshold",
                true);
        }

        return new CxAbandonedCounts(
            null,
            null,
            null,
            null,
            "No explicit <=30-second abandoned count was found",
            "No explicit <=30-second abandoned count was found",
            false);
    }

    private static CxAbandonedCounts ExtractExplicitAbandonedCounts(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string source,
        int? currentPeriod)
    {
        var currentRows = FilterCurrentPeriod(rows, currentPeriod).ToList();
        if (currentRows.Count == 0)
        {
            currentRows = rows.ToList();
        }

        var current = FindFirstExplicitValue(currentRows, CurrentAbandonedCountAliases);
        var ytd = FindFirstExplicitValue(rows, YtdAbandonedCountAliases);
        var currentAnswered = FindFirstExplicitValue(currentRows, CurrentAnsweredAliases);
        var ytdAnswered = FindFirstExplicitValue(rows, YtdAnsweredAliases);

        return new CxAbandonedCounts(
            current?.Value,
            ytd?.Value,
            currentAnswered?.Value,
            ytdAnswered?.Value,
            current == null ? $"{source}: not found" : $"{source}.{current.Source}",
            ytd == null ? $"{source}: not found" : $"{source}.{ytd.Source}",
            current != null);
    }

    private static CxAbandonedCounts ExtractExactCountsFromRows(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string source,
        int? currentPeriod,
        bool allowLabelledThirtySecondCount)
    {
        var explicitCounts = ExtractExplicitAbandonedCounts(rows, source, currentPeriod);
        if (explicitCounts.CurrentAbandoned.HasValue)
        {
            return explicitCounts;
        }

        if (!allowLabelledThirtySecondCount)
        {
            return explicitCounts;
        }

        var monthly = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            var label = GetMetricLabel(row);
            if (!IsThirtySecondAbandonedCountLabel(label))
            {
                continue;
            }

            var count = FindFirstValue(row, GenericCountValueAliases);
            if (!count.HasValue || count.Value < 0m)
            {
                continue;
            }

            var period = GetPeriodSort(row);
            if (!period.HasValue)
            {
                continue;
            }

            if (!monthly.TryGetValue(period.Value, out var existing) || count.Value > existing)
            {
                // MAX per month avoids multiplying the same count across repeated bucket rows.
                monthly[period.Value] = count.Value;
            }
        }

        if (monthly.Count == 0)
        {
            return explicitCounts;
        }

        var resolvedCurrentPeriod = currentPeriod.HasValue && monthly.ContainsKey(currentPeriod.Value)
            ? currentPeriod.Value
            : monthly.Keys.Max();
        var current = monthly[resolvedCurrentPeriod];
        var currentYear = resolvedCurrentPeriod / 100;
        var ytd = monthly
            .Where(pair => pair.Key / 100 == currentYear && pair.Key <= resolvedCurrentPeriod)
            .Sum(pair => pair.Value);

        return new CxAbandonedCounts(
            current,
            ytd,
            null,
            null,
            $"{source}: labelled <=30-second count series",
            $"{source}: summed labelled <=30-second count series",
            true);
    }

    private async Task<GenesysCounts?> TryLoadGenesysCountsAsync(
        int? currentPeriod,
        CancellationToken ct)
    {
        var settings = ReadGenesysSettings();
        if (settings == null)
        {
            return null;
        }

        var period = currentPeriod ?? PreviousCompletedMonthPeriod();
        var cacheKey = string.Join(
            "|",
            settings.Environment,
            string.Join(",", settings.QueueIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            settings.MediaType,
            settings.Direction,
            settings.TimeZoneId,
            settings.ThresholdSeconds.ToString(CultureInfo.InvariantCulture),
            period.ToString(CultureInfo.InvariantCulture));

        if (CxGenesysCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            return cached.Counts;
        }

        await CxGenesysGate.WaitAsync(ct);
        try
        {
            if (CxGenesysCache.TryGetValue(cacheKey, out cached)
                && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                return cached.Counts;
            }

            var counts = await QueryGenesysCountsAsync(settings, period, ct);
            CxGenesysCache[cacheKey] = new GenesysCacheEntry(
                counts,
                DateTimeOffset.UtcNow.AddMinutes(settings.CacheMinutes));
            return counts;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Genesys abandoned-call calculation failed; stale SQL percentage will not be used.");
            return null;
        }
        finally
        {
            CxGenesysGate.Release();
        }
    }

    private GenesysSettings? ReadGenesysSettings()
    {
        string Read(string configKey, string environmentKey)
        {
            var configured = (_cfg[configKey] ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : (Environment.GetEnvironmentVariable(environmentKey) ?? string.Empty).Trim();
        }

        var environment = Read("Dashboard:CxGenesys:Environment", "GENESYS_CLOUD_ENVIRONMENT");
        var clientId = Read("Dashboard:CxGenesys:ClientId", "GENESYS_CLIENT_ID");
        var clientSecret = Read("Dashboard:CxGenesys:ClientSecret", "GENESYS_CLIENT_SECRET");
        var queueText = Read("Dashboard:CxGenesys:QueueIds", "GENESYS_QUEUE_IDS");

        if (string.IsNullOrWhiteSpace(environment)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(queueText))
        {
            return null;
        }

        var queues = queueText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (queues.Length == 0)
        {
            return null;
        }

        var thresholdText = Read("Dashboard:CxGenesys:AbandonThresholdSeconds", "CX_ABANDON_MAX_SECONDS");
        var threshold = decimal.TryParse(
            thresholdText,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsedThreshold)
            ? parsedThreshold
            : 30m;

        var cacheText = Read("Dashboard:CxGenesys:CacheMinutes", "CX_GENESYS_CACHE_MINUTES");
        var cacheMinutes = int.TryParse(cacheText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCache)
            ? Math.Clamp(parsedCache, 5, 1440)
            : 60;

        var mediaType = Read("Dashboard:CxGenesys:MediaType", "GENESYS_MEDIA_TYPE");
        var direction = Read("Dashboard:CxGenesys:Direction", "GENESYS_DIRECTION");
        var timeZone = Read("Dashboard:CxGenesys:TimeZone", "CX_LOCAL_TIME_ZONE");

        return new GenesysSettings(
            environment,
            clientId,
            clientSecret,
            queues,
            string.IsNullOrWhiteSpace(mediaType) ? "voice" : mediaType,
            string.IsNullOrWhiteSpace(direction) ? "inbound" : direction,
            string.IsNullOrWhiteSpace(timeZone) ? "Eastern Standard Time" : timeZone,
            threshold < 0m ? 30m : threshold,
            cacheMinutes);
    }

    private async Task<GenesysCounts> QueryGenesysCountsAsync(
        GenesysSettings settings,
        int period,
        CancellationToken ct)
    {
        var year = period / 100;
        var month = period % 100;
        if (year < 2000 || month is < 1 or > 12)
        {
            throw new InvalidOperationException($"Invalid CX period_sort '{period}'.");
        }

        var zone = ResolveTimeZone(settings.TimeZoneId);
        var ytdStartLocal = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var currentStartLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = currentStartLocal.AddMonths(1);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(ytdStartLocal, zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, zone);
        var interval = $"{startUtc:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}/{endUtc:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}";

        var token = await GetGenesysTokenAsync(settings, ct);
        var apiBase = $"https://api.{settings.Environment.Trim().TrimEnd('/')}";
        var pageNumber = 1;
        const int pageSize = 100;
        var thresholdMilliseconds = settings.ThresholdSeconds * 1000m;
        decimal currentAnswered = 0m;
        decimal currentAbandoned = 0m;
        decimal ytdAnswered = 0m;
        decimal ytdAbandoned = 0m;

        while (true)
        {
            var predicates = new List<object>();
            predicates.Add(new
            {
                type = "or",
                predicates = settings.QueueIds.Select(queueId => new
                {
                    type = "dimension",
                    dimension = "queueId",
                    @operator = "matches",
                    value = queueId
                }).ToArray()
            });
            predicates.Add(new
            {
                type = "or",
                predicates = new[]
                {
                    new
                    {
                        type = "dimension",
                        dimension = "mediaType",
                        @operator = "matches",
                        value = settings.MediaType
                    }
                }
            });
            predicates.Add(new
            {
                type = "or",
                predicates = new[]
                {
                    new
                    {
                        type = "dimension",
                        dimension = "direction",
                        @operator = "matches",
                        value = settings.Direction
                    }
                }
            });

            var body = new
            {
                interval,
                order = "asc",
                orderBy = "conversationStart",
                paging = new { pageSize, pageNumber },
                segmentFilters = predicates
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{apiBase}/api/v2/analytics/conversations/details/query");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            using var response = await CxGenesysHttp.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Genesys conversation query failed HTTP {(int)response.StatusCode}: {responseText}");
            }

            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("conversations", out var conversations)
                || conversations.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var returned = 0;
            foreach (var conversation in conversations.EnumerateArray())
            {
                returned++;
                if (!TryGetConversationStart(conversation, zone, out var startedLocal))
                {
                    continue;
                }

                var answeredFlag = false;
                var abandonedFlag = false;

                if (conversation.TryGetProperty("participants", out var participants)
                    && participants.ValueKind == JsonValueKind.Array)
                {
                    foreach (var participant in participants.EnumerateArray())
                    {
                        if (!participant.TryGetProperty("sessions", out var sessions)
                            || sessions.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var session in sessions.EnumerateArray())
                        {
                            if (!SessionMatchesQueue(session, settings.QueueIds))
                            {
                                continue;
                            }

                            if (!session.TryGetProperty("metrics", out var metrics)
                                || metrics.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var metric in metrics.EnumerateArray())
                            {
                                var metricName = GetJsonString(metric, "name") ?? GetJsonString(metric, "metric");
                                if (!TryGetJsonDecimal(metric, "value", out var metricValue))
                                {
                                    continue;
                                }

                                if (string.Equals(metricName, "tAnswered", StringComparison.OrdinalIgnoreCase))
                                {
                                    answeredFlag = true;
                                }
                                else if (string.Equals(metricName, "tAbandon", StringComparison.OrdinalIgnoreCase)
                                         && metricValue >= 0m
                                         && metricValue <= thresholdMilliseconds)
                                {
                                    abandonedFlag = true;
                                }
                            }
                        }
                    }
                }

                if (answeredFlag)
                {
                    ytdAnswered++;
                }
                if (abandonedFlag)
                {
                    ytdAbandoned++;
                }

                if (startedLocal.Year == year && startedLocal.Month == month)
                {
                    if (answeredFlag)
                    {
                        currentAnswered++;
                    }
                    if (abandonedFlag)
                    {
                        currentAbandoned++;
                    }
                }
            }

            if (returned < pageSize)
            {
                break;
            }

            pageNumber++;
            if (pageNumber > 10000)
            {
                throw new InvalidOperationException("Genesys pagination exceeded the safety limit.");
            }
        }

        return new GenesysCounts(
            currentAnswered,
            currentAbandoned,
            ytdAnswered,
            ytdAbandoned,
            DateTimeOffset.UtcNow);
    }

    private async Task<string> GetGenesysTokenAsync(GenesysSettings settings, CancellationToken ct)
    {
        var tokenKey = $"{settings.Environment}|{settings.ClientId}";
        if (CxGenesysTokens.TryGetValue(tokenKey, out var cached)
            && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.AccessToken;
        }

        var loginBase = $"https://login.{settings.Environment.Trim().TrimEnd('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{loginBase}/oauth/token");
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });

        using var response = await CxGenesysHttp.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Genesys OAuth failed HTTP {(int)response.StatusCode}: {responseText}");
        }

        using var document = JsonDocument.Parse(responseText);
        var accessToken = GetJsonString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Genesys OAuth response did not include access_token.");
        }

        var expiresSeconds = TryGetJsonDecimal(document.RootElement, "expires_in", out var expires)
            ? Math.Max(60m, expires)
            : 3600m;
        CxGenesysTokens[tokenKey] = new GenesysToken(
            accessToken,
            DateTimeOffset.UtcNow.AddSeconds((double)expiresSeconds));
        return accessToken;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            var alternate = timeZoneId.Equals("America/Toronto", StringComparison.OrdinalIgnoreCase)
                ? "Eastern Standard Time"
                : timeZoneId.Equals("Eastern Standard Time", StringComparison.OrdinalIgnoreCase)
                    ? "America/Toronto"
                    : string.Empty;
            if (!string.IsNullOrWhiteSpace(alternate))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(alternate);
            }
            throw;
        }
    }

    private static bool TryGetConversationStart(
        JsonElement conversation,
        TimeZoneInfo zone,
        out DateTime startedLocal)
    {
        startedLocal = default;
        var text = GetJsonString(conversation, "conversationStart");
        if (string.IsNullOrWhiteSpace(text)
            || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var started))
        {
            return false;
        }

        startedLocal = TimeZoneInfo.ConvertTime(started, zone).DateTime;
        return true;
    }

    private static bool SessionMatchesQueue(JsonElement session, IReadOnlyList<string> queueIds)
    {
        if (queueIds.Count == 0)
        {
            return true;
        }

        if (!session.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var segment in segments.EnumerateArray())
        {
            var queueId = GetJsonString(segment, "queueId");
            if (!string.IsNullOrWhiteSpace(queueId)
                && queueIds.Contains(queueId, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static bool TryGetJsonDecimal(
        JsonElement element,
        string propertyName,
        out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value))
        {
            return true;
        }

        return decimal.TryParse(
            property.ToString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool ApplyExactAbandonedRate(
        List<Dictionary<string, object?>> rows,
        CxAnsweredSummary answered,
        CxAbandonedCounts abandoned)
    {
        if (rows.Count == 0)
        {
            return false;
        }

        var abandonedRateRow = rows.FirstOrDefault(IsAbandonedRateRow)
            ?? rows.FirstOrDefault(row =>
                GetMetricLabel(row).Contains("abandon", StringComparison.OrdinalIgnoreCase));
        if (abandonedRateRow == null)
        {
            return false;
        }

        SetExistingOrAdd(abandonedRateRow, "row_label", "Abandoned %");
        SetIfPresent(abandonedRateRow, "kpi", "Abandoned %");
        SetIfPresent(abandonedRateRow, "metric", "Abandoned %");

        // Preserve the percentages already returned by the SQL view. The exact-count
        // calculation is an override only when both its numerator and denominator are
        // available. It must never replace valid SQL percentages with null.
        var storedCurrentRate = ToNullableDecimal(abandonedRateRow, "current_month_value")
            ?? ToNullableDecimal(abandonedRateRow, "current_value")
            ?? ToNullableDecimal(abandonedRateRow, "value");
        var storedYtdRate = ToNullableDecimal(abandonedRateRow, "ytd_value")
            ?? ToNullableDecimal(abandonedRateRow, "ytd");

        var currentAnsweredForRate = abandoned.CurrentAnsweredForRate ?? answered.CurrentAnswered;
        var ytdAnsweredForRate = abandoned.YtdAnsweredForRate ?? answered.YtdAnswered;

        var recalculatedCurrentRate = abandoned.ExactThirtySecondRule
            ? Percent(abandoned.CurrentAbandoned, currentAnsweredForRate)
            : null;
        var recalculatedYtdRate = abandoned.ExactThirtySecondRule
            ? Percent(abandoned.YtdAbandoned, ytdAnsweredForRate)
            : null;

        var currentRate = recalculatedCurrentRate ?? storedCurrentRate;
        var ytdRate = recalculatedYtdRate ?? storedYtdRate;

        SetExistingOrAdd(abandonedRateRow, "current_month_value", currentRate);
        SetIfPresent(abandonedRateRow, "current_value", currentRate);
        SetIfPresent(abandonedRateRow, "value", currentRate);
        SetExistingOrAdd(abandonedRateRow, "ytd_value", ytdRate);
        SetIfPresent(abandonedRateRow, "ytd", ytdRate);

        var target = ToNullableDecimal(abandonedRateRow, "target_value")
            ?? ToNullableDecimal(abandonedRateRow, "target")
            ?? 10m;

        var currentStatus = currentRate.HasValue
            ? currentRate.Value <= target ? "good" : "bad"
            : "unknown";
        var ytdStatus = ytdRate.HasValue
            ? ytdRate.Value <= target ? "good" : "bad"
            : "unknown";

        SetExistingOrAdd(abandonedRateRow, "status_current", currentStatus);
        SetExistingOrAdd(abandonedRateRow, "status", currentStatus);
        SetExistingOrAdd(abandonedRateRow, "status_ytd", ytdStatus);
        SetExistingOrAdd(abandonedRateRow, "calls_answered", currentAnsweredForRate);
        SetExistingOrAdd(abandonedRateRow, "abandoned_calls", abandoned.CurrentAbandoned);
        SetExistingOrAdd(abandonedRateRow, "ytd_calls_answered", ytdAnsweredForRate);
        SetExistingOrAdd(abandonedRateRow, "ytd_abandoned_calls", abandoned.YtdAbandoned);
        SetExistingOrAdd(abandonedRateRow, "abandoned_count_source", abandoned.CurrentSource);
        SetExistingOrAdd(abandonedRateRow, "ytd_abandoned_count_source", abandoned.YtdSource);
        SetExistingOrAdd(
            abandonedRateRow,
            "abandoned_formula",
            "calls abandoned within 30 seconds / calls answered * 100");
        SetExistingOrAdd(
            abandonedRateRow,
            "abandoned_formula_verified",
            recalculatedCurrentRate.HasValue);
        SetExistingOrAdd(
            abandonedRateRow,
            "abandoned_percentage_source",
            recalculatedCurrentRate.HasValue
                ? abandoned.CurrentSource
                : "rpt.vw_cx_call_handling_table_latest");
        SetExistingOrAdd(
            abandonedRateRow,
            "sql_percentage_fallback_used",
            !recalculatedCurrentRate.HasValue && storedCurrentRate.HasValue);

        return recalculatedCurrentRate.HasValue;
    }

    private static IEnumerable<Dictionary<string, object?>> FilterCurrentPeriod(
        IEnumerable<Dictionary<string, object?>> rows,
        int? currentPeriod)
    {
        if (!currentPeriod.HasValue)
        {
            return rows;
        }

        return rows.Where(row => GetPeriodSort(row) == currentPeriod.Value);
    }

    private static LocatedDecimal? FindFirstExplicitValue(
        IEnumerable<Dictionary<string, object?>> rows,
        IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
        {
            foreach (var row in rows)
            {
                var value = ToNullableDecimal(row, alias);
                if (value.HasValue)
                {
                    return new LocatedDecimal(value.Value, alias);
                }
            }
        }

        return null;
    }

    private static decimal? FindFirstValue(
        Dictionary<string, object?> row,
        IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
        {
            var value = ToNullableDecimal(row, alias);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsThirtySecondAlias(string alias)
    {
        var normalized = NormalizeToken(alias);
        return normalized.Contains("30sec", StringComparison.Ordinal)
            || normalized.Contains("30second", StringComparison.Ordinal)
            || normalized.Contains("shortabandon", StringComparison.Ordinal);
    }

    private static bool IsThirtySecondAbandonedCountLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var normalized = NormalizeToken(label);
        if (!normalized.Contains("abandon", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.Contains("percent", StringComparison.Ordinal)
            || normalized.Contains("percentage", StringComparison.Ordinal)
            || normalized.Contains("rate", StringComparison.Ordinal)
            || label.Contains('%'))
        {
            return false;
        }

        return normalized.Contains("30sec", StringComparison.Ordinal)
            || normalized.Contains("30second", StringComparison.Ordinal)
            || normalized.Contains("under30", StringComparison.Ordinal)
            || normalized.Contains("within30", StringComparison.Ordinal)
            || normalized.Contains("lessthan30", StringComparison.Ordinal)
            || normalized.Contains("shortabandon", StringComparison.Ordinal);
    }

    private static string NormalizeToken(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool IsAbandonedRateRow(Dictionary<string, object?> row)
    {
        var label = GetMetricLabel(row);
        return label.Contains("abandon", StringComparison.OrdinalIgnoreCase)
            && (label.Contains("%", StringComparison.OrdinalIgnoreCase)
                || label.Contains("percent", StringComparison.OrdinalIgnoreCase)
                || label.Contains("rate", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMetricLabel(Dictionary<string, object?> row)
    {
        return GetString(row, "row_label")
            ?? GetString(row, "kpi")
            ?? GetString(row, "metric")
            ?? GetString(row, "bucket")
            ?? GetString(row, "category")
            ?? GetString(row, "series_name")
            ?? GetString(row, "label")
            ?? GetString(row, "title")
            ?? string.Empty;
    }

    private static int? GetPeriodSort(Dictionary<string, object?> row)
    {
        foreach (var key in new[] { "period_sort", "month_sort", "sort_order", "yyyymm" })
        {
            var direct = ToNullableInt(row, key);
            if (direct.HasValue && direct.Value >= 190001 && direct.Value <= 299912)
            {
                return direct.Value;
            }
        }

        foreach (var key in new[] { "snapshot_date", "period_label", "month_label" })
        {
            if (!row.TryGetValue(key, out var raw) || raw == null || raw == DBNull.Value)
            {
                continue;
            }

            if (raw is DateTime date)
            {
                return date.Year * 100 + date.Month;
            }
            if (raw is DateTimeOffset offset)
            {
                return offset.Year * 100 + offset.Month;
            }
            if (DateTime.TryParse(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            {
                return parsed.Year * 100 + parsed.Month;
            }
        }

        return null;
    }

    private static int PreviousCompletedMonthPeriod()
    {
        var previous = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        return previous.Year * 100 + previous.Month;
    }

    private static decimal? Percent(decimal? numerator, decimal? denominator)
    {
        if (!numerator.HasValue || !denominator.HasValue || denominator.Value <= 0m)
        {
            return null;
        }

        return numerator.Value / denominator.Value * 100m;
    }

    private static string? GetString(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) && value != null && value != DBNull.Value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static decimal? ToNullableDecimal(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value)
        {
            return null;
        }

        if (value is decimal decimalValue)
        {
            return decimalValue;
        }

        if (value is IConvertible)
        {
            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        return decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static int? ToNullableInt(Dictionary<string, object?> row, string key)
    {
        var value = ToNullableDecimal(row, key);
        if (!value.HasValue)
        {
            return null;
        }

        var truncated = decimal.Truncate(value.Value);
        if (truncated < int.MinValue || truncated > int.MaxValue)
        {
            return null;
        }

        return decimal.ToInt32(truncated);
    }

    private static void SetIfPresent(
        Dictionary<string, object?> row,
        string key,
        object? value)
    {
        if (row.ContainsKey(key))
        {
            row[key] = value;
        }
    }

    private static void SetExistingOrAdd(
        Dictionary<string, object?> row,
        string key,
        object? value)
    {
        row[key] = value;
    }
}