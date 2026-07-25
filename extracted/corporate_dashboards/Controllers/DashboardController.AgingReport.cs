using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Text;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    private const string AgingReportPageKey = "csr_aging-report-hourly-updates";
    private const string AgingReportAgingSourceAlias = "agingcube_net";
    private const string AgingReportTransactionsSourceAlias = "aging_trans_details";

    private const string AgingReportPivotVisualId = "7274d6a502a30ce75356";
    private const string AgingReportCategoryVisualId = "4f0c9a7b2e6d8c153a91";
    private const string AgingReportRiskTableVisualId = "7344146e0bb3717702ac";
    private const string AgingReportTextVisualId = "cd156f2138207d8412a1";
    private const string AgingReportClosedCardVisualId = "428b4912601772ed9c27";
    private const string AgingReportBucketVisualId = "26d33e960ba87850b085";
    private const string AgingReportMonthlyVisualId = "bb05fc35857712b42752";

    private static readonly HashSet<string> AgingReportVisualIds = new(StringComparer.OrdinalIgnoreCase)
    {
        AgingReportPivotVisualId,
        AgingReportCategoryVisualId,
        AgingReportRiskTableVisualId,
        AgingReportTextVisualId,
        AgingReportClosedCardVisualId,
        AgingReportBucketVisualId,
        AgingReportMonthlyVisualId
    };

    private sealed class AgingReportBatchPayload
    {
        public Dictionary<string, List<Dictionary<string, object?>>> VisualDataSets { get; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AgingReportCacheEntry
    {
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public required Lazy<Task<AgingReportBatchPayload>> Loader { get; init; }
    }

    private static readonly ConcurrentDictionary<string, AgingReportCacheEntry> AgingReportCache
        = new(StringComparer.Ordinal);

    private static bool IsAgingReportPageKey(string? key) =>
        string.Equals(key, AgingReportPageKey, StringComparison.OrdinalIgnoreCase);

    private static bool IsAgingReportVisualRule(CustomHtmlRuleConfig rule) =>
        IsAgingReportPageKey(rule.PageKey) &&
        AgingReportVisualIds.Contains(rule.VisualId ?? string.Empty);

    private async Task<IActionResult> GetCsrAgingReportPageDataAsync(
        CustomHtmlLiveDataRequest req,
        CustomHtmlRuleConfig? configuredRule)
    {
        var pageRule = ResolveCustomHtmlRuleByKey(AgingReportPageKey)
            ?? throw new InvalidOperationException($"CSR template was not found: {AgingReportPageKey}");

        var currentYear = DateTime.Today.Year;
        var batch = await GetAgingReportBatchCachedAsync(pageRule, currentYear);
        var agingSource = RequireAgingReportSource(pageRule, AgingReportAgingSourceAlias);
        var transactionsSource = RequireAgingReportSource(pageRule, AgingReportTransactionsSourceAlias);
        var sourceServer = (_cfg["Dashboard:CsrPbipImport:SourceServer"] ?? "app100.camhydro.com").Trim();
        var sourceDatabase = (_cfg["Dashboard:CsrPbipImport:SourceDatabase"] ?? "corporate_dashboards").Trim();

        return Json(new
        {
            found = true,
            mode = "csrPage",
            templateId = AgingReportPageKey,
            pageKey = AgingReportPageKey,
            currentYear,
            visualDataSets = batch.VisualDataSets,
            serverFilteredVisualData = true,
            pageInfoByVisual = new Dictionary<string, object>(),
            queryContextByVisual = new Dictionary<string, object>(),
            sources = new[]
            {
                BuildAgingReportSourceMetadata(agingSource, sourceServer, sourceDatabase,
                    batch.VisualDataSets.Where(pair => !string.Equals(pair.Key, AgingReportMonthlyVisualId, StringComparison.OrdinalIgnoreCase))
                        .Sum(pair => pair.Value.Count)),
                BuildAgingReportSourceMetadata(transactionsSource, sourceServer, sourceDatabase,
                    batch.VisualDataSets.TryGetValue(AgingReportMonthlyVisualId, out var monthlyRows) ? monthlyRows.Count : 0)
            },
            debug = new
            {
                rawRowsMaterializedInBrowser = false,
                sourceExecutions = 2,
                sourceExecutionsRunConcurrently = true,
                currentYear,
                visualResultSets = batch.VisualDataSets.Count
            }
        });
    }

    private async Task<IActionResult> GetCsrAgingReportVisualDataAsync(
        CustomHtmlLiveDataRequest req,
        CustomHtmlRuleConfig rule)
    {
        var visualId = string.IsNullOrWhiteSpace(rule.VisualId)
            ? rule.Key[(rule.Key.LastIndexOf('-') + 1)..]
            : rule.VisualId.Trim();

        if (!AgingReportVisualIds.Contains(visualId))
            return BadRequest($"Aging-report visual is not supported by csrVisual: {visualId}");

        List<Dictionary<string, object?>> data;
        if (string.Equals(visualId, AgingReportTextVisualId, StringComparison.OrdinalIgnoreCase))
        {
            data = new List<Dictionary<string, object?>>();
        }
        else
        {
            var pageRule = ResolveCustomHtmlRuleByKey(AgingReportPageKey)
                ?? throw new InvalidOperationException($"CSR template was not found: {AgingReportPageKey}");
            var batch = await GetAgingReportBatchCachedAsync(pageRule, DateTime.Today.Year);
            if (!batch.VisualDataSets.TryGetValue(visualId, out var visualData))
                return BadRequest($"Aging-report visual result was not generated: {visualId}");
            data = visualData;
        }

        var sourceAlias = string.Equals(visualId, AgingReportMonthlyVisualId, StringComparison.OrdinalIgnoreCase)
            ? AgingReportTransactionsSourceAlias
            : AgingReportAgingSourceAlias;
        var source = RequireAgingReportSource(
            ResolveCustomHtmlRuleByKey(AgingReportPageKey)
                ?? throw new InvalidOperationException($"CSR template was not found: {AgingReportPageKey}"),
            sourceAlias);
        var connectionName = AgingReportConnectionName(source);
        var sourceServer = (_cfg["Dashboard:CsrPbipImport:SourceServer"] ?? "app100.camhydro.com").Trim();
        var sourceDatabase = (_cfg["Dashboard:CsrPbipImport:SourceDatabase"] ?? "corporate_dashboards").Trim();
        var currentYear = DateTime.Today.Year;

        return Json(new
        {
            found = true,
            mode = "csrVisual",
            templateId = rule.Key,
            pageKey = AgingReportPageKey,
            visualId,
            role = rule.Role,
            title = rule.Title,
            currentYear,
            connectionName,
            schema = source.Schema,
            obj = source.Object,
            rowFields = rule.RowFields,
            colFields = rule.ColFields,
            valueFields = rule.ValueFields,
            data,
            serverFilteredVisualData = true,
            pageInfo = (object?)null,
            queryContext = (object?)null,
            sources = new[]
            {
                new
                {
                    alias = sourceAlias,
                    semanticEntity = sourceAlias,
                    connectionName,
                    sourceServer,
                    sourceDatabase,
                    schema = source.Schema,
                    @object = source.Object,
                    objectType = source.ObjectKind,
                    returnedRows = data.Count,
                    truncated = false,
                    error = (string?)null
                }
            },
            debug = new
            {
                queryKind = AgingReportVisualKind(visualId),
                returnedRows = data.Count,
                rawRowsMaterialized = false,
                currentYear,
                sharedCachedBatch = !string.Equals(visualId, AgingReportTextVisualId, StringComparison.OrdinalIgnoreCase)
            }
        });
    }

    private static string AgingReportVisualKind(string visualId) => visualId switch
    {
        AgingReportPivotVisualId => "matrix",
        AgingReportCategoryVisualId or AgingReportBucketVisualId or AgingReportMonthlyVisualId => "chart",
        AgingReportRiskTableVisualId => "table",
        AgingReportClosedCardVisualId => "card",
        AgingReportTextVisualId => "text",
        _ => "unknown"
    };

    private static CustomHtmlSourceConfig RequireAgingReportSource(CustomHtmlRuleConfig rule, string alias) =>
        rule.Sources.FirstOrDefault(source =>
            string.Equals(CsrSourceAlias(source), alias, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"CSR template '{rule.Key}' has no {alias} source.");

    private string AgingReportConnectionName(CustomHtmlSourceConfig source)
    {
        var configured = (_cfg["Dashboard:CsrPbipImport:SourceConnectionName"] ?? "csr_pbip_source").Trim();
        return string.IsNullOrWhiteSpace(source.ConnectionName) ? configured : source.ConnectionName.Trim();
    }

    private object BuildAgingReportSourceMetadata(
        CustomHtmlSourceConfig source,
        string sourceServer,
        string sourceDatabase,
        int returnedRows)
    {
        var alias = CsrSourceAlias(source);
        return new
        {
            alias,
            semanticEntity = alias,
            connectionName = AgingReportConnectionName(source),
            sourceServer,
            sourceDatabase,
            schema = source.Schema,
            @object = source.Object,
            objectType = source.ObjectKind,
            returnedRows,
            truncated = false,
            error = (string?)null
        };
    }

    private async Task<AgingReportBatchPayload> GetAgingReportBatchCachedAsync(
        CustomHtmlRuleConfig pageRule,
        int currentYear)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in AgingReportCache.Where(pair => pair.Value.ExpiresAtUtc <= now).Take(16).ToList())
        {
            AgingReportCache.TryRemove(stale.Key, out _);
        }

        var cacheKey = BuildAgingReportCacheKey(pageRule, currentYear);
        var entry = AgingReportCache.GetOrAdd(cacheKey, _ => new AgingReportCacheEntry
        {
            ExpiresAtUtc = now.AddMinutes(5),
            Loader = new Lazy<Task<AgingReportBatchPayload>>(
                () => LoadAgingReportBatchAsync(pageRule, currentYear, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication)
        });

        try
        {
            return await entry.Loader.Value;
        }
        catch
        {
            AgingReportCache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private static string BuildAgingReportCacheKey(CustomHtmlRuleConfig pageRule, int currentYear)
    {
        var builder = new StringBuilder("aging-report|").Append(currentYear).Append('|');
        foreach (var source in pageRule.Sources.OrderBy(CsrSourceAlias, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(CsrSourceAlias(source)).Append('|')
                .Append(source.ConnectionName).Append('|')
                .Append(source.Schema).Append('|')
                .Append(source.Object).Append('|')
                .Append(source.ObjectKind).Append(';');
        }
        return builder.ToString();
    }

    private async Task<AgingReportBatchPayload> LoadAgingReportBatchAsync(
        CustomHtmlRuleConfig pageRule,
        int currentYear,
        CancellationToken cancellationToken)
    {
        var agingSource = RequireAgingReportSource(pageRule, AgingReportAgingSourceAlias);
        var transactionsSource = RequireAgingReportSource(pageRule, AgingReportTransactionsSourceAlias);

        var agingTask = LoadAgingReportAgingVisualsAsync(agingSource, cancellationToken);
        var monthlyTask = LoadAgingReportMonthlyVisualAsync(transactionsSource, currentYear, cancellationToken);
        await Task.WhenAll(agingTask, monthlyTask);

        var payload = new AgingReportBatchPayload();
        foreach (var pair in await agingTask)
        {
            payload.VisualDataSets[pair.Key] = pair.Value;
        }
        payload.VisualDataSets[AgingReportMonthlyVisualId] = await monthlyTask;
        payload.VisualDataSets[AgingReportTextVisualId] = new List<Dictionary<string, object?>>();
        return payload;
    }

    private async Task<Dictionary<string, List<Dictionary<string, object?>>>> LoadAgingReportAgingVisualsAsync(
        CustomHtmlSourceConfig source,
        CancellationToken cancellationToken)
    {
        var connectionName = AgingReportConnectionName(source);
        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var sourceSql = await ResolveAgingReportSourceSqlAsync(connection, source, cancellationToken);

        var sql = $"""
            SET NOCOUNT ON;

            SELECT
                CONVERT(nvarchar(300), a.[Category]) AS [Category],
                CONVERT(nvarchar(150), a.[AgingBucket]) AS [AgingBucket],
                TRY_CONVERT(decimal(38, 4), a.[Amount]) AS [Amount],
                CONVERT(nvarchar(200), a.[CategoryGroup]) AS [CategoryGroup],
                CONVERT(nvarchar(150), a.[ACCOUNT]) AS [Account],
                CONVERT(nvarchar(400), a.[Name]) AS [Name],
                TRY_CONVERT(decimal(38, 4), a.[DepositBalance]) AS [DepositBalance],
                CONVERT(nvarchar(150), a.[Status]) AS [Status]
            INTO #csr_aging
            FROM {sourceSql} AS a;

            SELECT
                COALESCE(NULLIF(LTRIM(RTRIM([Category])), N''), N'(blank)') AS [Category],
                COALESCE(NULLIF(LTRIM(RTRIM([AgingBucket])), N''), N'(blank)') AS [AgingBucket],
                COALESCE(SUM([Amount]), 0) AS [Amount]
            FROM #csr_aging
            GROUP BY
                COALESCE(NULLIF(LTRIM(RTRIM([Category])), N''), N'(blank)'),
                COALESCE(NULLIF(LTRIM(RTRIM([AgingBucket])), N''), N'(blank)')
            ORDER BY [Category], [AgingBucket];

            SELECT
                COALESCE(NULLIF(LTRIM(RTRIM([CategoryGroup])), N''), N'(blank)') AS [CategoryGroup],
                N'Balance Overdue' AS [Series],
                COALESCE(SUM([Amount]), 0) AS [Amount]
            FROM #csr_aging
            GROUP BY COALESCE(NULLIF(LTRIM(RTRIM([CategoryGroup])), N''), N'(blank)')
            ORDER BY [Amount] DESC;

            SELECT TOP (80)
                [Account],
                MAX([Name]) AS [Name],
                COALESCE(SUM([Amount]), 0) AS [Amount],
                COALESCE(MAX([DepositBalance]), 0) AS [DepositBalance]
            FROM #csr_aging
            WHERE [DepositBalance] > 0
              AND NULLIF(LTRIM(RTRIM([Account])), N'') IS NOT NULL
            GROUP BY [Account]
            ORDER BY [Amount] DESC, [Account];

            SELECT
                CONVERT(decimal(38, 0), COUNT_BIG(DISTINCT NULLIF(LTRIM(RTRIM([Account])), N''))) AS [ClosedFinalAccounts],
                N'Final Billed' AS [Status]
            FROM #csr_aging
            WHERE LTRIM(RTRIM([Status])) = N'Final Billed';

            SELECT
                COALESCE(NULLIF(LTRIM(RTRIM([AgingBucket])), N''), N'(blank)') AS [AgingBucket],
                COALESCE(NULLIF(LTRIM(RTRIM([CategoryGroup])), N''), N'(blank)') AS [CategoryGroup],
                COALESCE(SUM([Amount]), 0) AS [Amount]
            FROM #csr_aging
            GROUP BY
                COALESCE(NULLIF(LTRIM(RTRIM([AgingBucket])), N''), N'(blank)'),
                COALESCE(NULLIF(LTRIM(RTRIM([CategoryGroup])), N''), N'(blank)')
            ORDER BY [AgingBucket], [CategoryGroup];
            """;

        var resultSets = await ReadCsrResultSetsAsync(
            connection,
            sql,
            Array.Empty<SqlParameter>(),
            cancellationToken);

        if (resultSets.Count < 5)
            throw new InvalidOperationException($"Aging report returned {resultSets.Count} result sets; expected 5.");

        return new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            [AgingReportPivotVisualId] = resultSets[0],
            [AgingReportCategoryVisualId] = resultSets[1],
            [AgingReportRiskTableVisualId] = resultSets[2],
            [AgingReportClosedCardVisualId] = resultSets[3],
            [AgingReportBucketVisualId] = resultSets[4]
        };
    }

    private async Task<List<Dictionary<string, object?>>> LoadAgingReportMonthlyVisualAsync(
        CustomHtmlSourceConfig source,
        int currentYear,
        CancellationToken cancellationToken)
    {
        var connectionName = AgingReportConnectionName(source);
        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var sourceSql = await ResolveAgingReportSourceSqlAsync(connection, source, cancellationToken);

        var sql = $"""
            SET NOCOUNT ON;

            WITH normalized AS
            (
                SELECT
                    COALESCE(TRY_CONVERT(int, t.[year]), YEAR(TRY_CONVERT(date, t.[trans_date]))) AS [year],
                    COALESCE(TRY_CONVERT(int, t.[month]), MONTH(TRY_CONVERT(date, t.[trans_date]))) AS [month],
                    CASE
                        WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(50), t.[month_name]))), N'') IS NOT NULL
                            THEN LTRIM(RTRIM(CONVERT(nvarchar(50), t.[month_name])))
                        WHEN COALESCE(TRY_CONVERT(int, t.[month]), MONTH(TRY_CONVERT(date, t.[trans_date]))) BETWEEN 1 AND 12
                            THEN DATENAME(month, DATEFROMPARTS(
                                @csr_current_year,
                                COALESCE(TRY_CONVERT(int, t.[month]), MONTH(TRY_CONVERT(date, t.[trans_date]))),
                                1))
                        ELSE N'(blank)'
                    END AS [month_name],
                    CASE
                        WHEN t.[trans_type] IS NULL THEN N'CR'
                        ELSE LTRIM(RTRIM(CONVERT(nvarchar(200), t.[trans_type])))
                    END AS [trans_type],
                    TRY_CONVERT(decimal(38, 4), t.[trans_amt]) AS [trans_amt]
                FROM {sourceSql} AS t
                WHERE COALESCE(TRY_CONVERT(int, t.[year]), YEAR(TRY_CONVERT(date, t.[trans_date]))) = @csr_current_year
                  AND (t.[trans_type] IS NULL OR CONVERT(nvarchar(200), t.[trans_type]) <> N' ')
            )
            SELECT
                [year],
                [month],
                [month_name],
                COALESCE(NULLIF([trans_type], N''), N'(blank)') AS [trans_type],
                COALESCE(SUM([trans_amt]), 0) AS [trans_amt]
            FROM normalized
            WHERE [month] BETWEEN 1 AND 12
            GROUP BY [year], [month], [month_name], COALESCE(NULLIF([trans_type], N''), N'(blank)')
            ORDER BY [year], [month], [trans_type];
            """;

        return await ReadCsrRowsAsync(
            connection,
            sql,
            new[] { new SqlParameter("@csr_current_year", SqlDbType.Int) { Value = currentYear } },
            cancellationToken);
    }

    private async Task<string> ResolveAgingReportSourceSqlAsync(
        SqlConnection connection,
        CustomHtmlSourceConfig source,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(source.Schema) ? "dbo" : source.Schema.Trim();
        var obj = (source.Object ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(obj))
            throw new InvalidOperationException("CSR source Object is required.");

        var configuredKind = (source.ObjectKind ?? "auto").Trim().ToLowerInvariant();
        if (configuredKind == "function")
            return $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}()";
        if (configuredKind is "table" or "view" or "synonym")
            return $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}";

        try
        {
            var (_, objectType) = await ResolveObjectAsync(connection, schema, obj);
            if (string.Equals(objectType, "function", StringComparison.OrdinalIgnoreCase))
                return $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}()";
            if (!string.IsNullOrWhiteSpace(objectType))
                return $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}";
        }
        catch (SqlException)
        {
            // SELECT permission can exist without VIEW DEFINITION. Direct probes below are authoritative.
        }

        var candidates = new[]
        {
            $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}",
            $"{QuoteSqlIdentifier(schema)}.{QuoteSqlIdentifier(obj)}()"
        };
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                await using var probe = connection.CreateCommand();
                probe.CommandTimeout = SqlCommandTimeoutSeconds();
                probe.CommandText = $"SELECT TOP (0) 1 AS [probe] FROM {candidate} AS s;";
                await probe.ExecuteNonQueryAsync(cancellationToken);
                return candidate;
            }
            catch (SqlException ex)
            {
                errors.Add(ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"Unable to resolve CSR source {schema}.{obj} as a table/view/synonym or parameterless TVF. " +
            string.Join(" | ", errors));
    }
}
