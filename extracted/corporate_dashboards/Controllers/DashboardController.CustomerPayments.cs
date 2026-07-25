using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Text;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    private const string CustomerPaymentsDailyPageKey = "csr_customer-payments-daily";
    private const string CustomerPaymentsMonthlyPageKey = "csr_customer-payments-monthly";
    private const string CustomerPaymentsSourceAlias = "ns_daily_cash_by_cycle_view";

    private sealed class CustomerPaymentsPageDefinition
    {
        public required string PageKey { get; init; }
        public required int VersionId { get; init; }
        public required bool Daily { get; init; }
        public required string TableVisualId { get; init; }
        public required string ChartVisualId { get; init; }
        public required string FirstBillSlicerVisualId { get; init; }
        public required string EBillSlicerVisualId { get; init; }
        public required string PaymentSlicerVisualId { get; init; }
        public required string TypesSlicerVisualId { get; init; }

        public IEnumerable<string> VisualIds
        {
            get
            {
                yield return TableVisualId;
                yield return ChartVisualId;
                yield return FirstBillSlicerVisualId;
                yield return EBillSlicerVisualId;
                yield return PaymentSlicerVisualId;
                yield return TypesSlicerVisualId;
            }
        }
    }

    private static readonly CustomerPaymentsPageDefinition CustomerPaymentsDailyDefinition = new()
    {
        PageKey = CustomerPaymentsDailyPageKey,
        VersionId = 200,
        Daily = true,
        TableVisualId = "aec9755f719d88b2b6c2",
        ChartVisualId = "27117bab2ee60e11a046",
        FirstBillSlicerVisualId = "bd525ac30581c4a492c8",
        EBillSlicerVisualId = "b67e2cb0b74048adc790",
        PaymentSlicerVisualId = "d085a6f0b4189a19b6e9",
        TypesSlicerVisualId = "343320b2f925876e9e8d"
    };

    private static readonly CustomerPaymentsPageDefinition CustomerPaymentsMonthlyDefinition = new()
    {
        PageKey = CustomerPaymentsMonthlyPageKey,
        VersionId = 201,
        Daily = false,
        TableVisualId = "b2c24d6121ae0793bdaa",
        ChartVisualId = "833754fa857486c84c60",
        FirstBillSlicerVisualId = "9563b424103651c8d80c",
        EBillSlicerVisualId = "76909a735ed5bbc90e26",
        PaymentSlicerVisualId = "6670b7529e0e3a1ca0ad",
        TypesSlicerVisualId = "4f05a45fd4115b1cebed"
    };

    private sealed class CustomerPaymentsTablePage
    {
        public int Skip { get; init; }
        public int PageSize { get; init; }
        public int ReturnedRows { get; init; }
        public bool HasMore { get; init; }
        public int? NextOffset { get; init; }
    }

    private sealed class CustomerPaymentsBatchPayload
    {
        public Dictionary<string, List<Dictionary<string, object?>>> VisualDataSets { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CustomerPaymentsTablePage> PageInfoByVisual { get; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CustomerPaymentsCacheEntry
    {
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public required Lazy<Task<CustomerPaymentsBatchPayload>> Loader { get; init; }
    }

    private static readonly ConcurrentDictionary<string, CustomerPaymentsCacheEntry> CustomerPaymentsCache
        = new(StringComparer.Ordinal);

    private static bool IsServerAggregatedCsrPageKey(string? key) =>
        string.Equals(key, MonthlyEbnotesPageKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, CustomerPaymentsDailyPageKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, CustomerPaymentsMonthlyPageKey, StringComparison.OrdinalIgnoreCase) ||
        IsAgingReportPageKey(key);

    private static bool IsCustomerPaymentsPageKey(string? key) =>
        string.Equals(key, CustomerPaymentsDailyPageKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, CustomerPaymentsMonthlyPageKey, StringComparison.OrdinalIgnoreCase);

    private static CustomerPaymentsPageDefinition? ResolveCustomerPaymentsDefinition(string? pageKey) =>
        string.Equals(pageKey, CustomerPaymentsDailyPageKey, StringComparison.OrdinalIgnoreCase)
            ? CustomerPaymentsDailyDefinition
            : string.Equals(pageKey, CustomerPaymentsMonthlyPageKey, StringComparison.OrdinalIgnoreCase)
                ? CustomerPaymentsMonthlyDefinition
                : null;

    private static bool IsCustomerPaymentsVisualRule(CustomHtmlRuleConfig rule) =>
        ResolveCustomerPaymentsDefinition(rule.PageKey) is { } definition &&
        definition.VisualIds.Contains(rule.VisualId, StringComparer.OrdinalIgnoreCase);

    private async Task<IActionResult> GetCsrServerPageDataAsync(
        CustomHtmlLiveDataRequest req,
        CustomHtmlRuleConfig? configuredRule)
    {
        var requestedKey = configuredRule?.Key;
        if (!IsCustomerPaymentsPageKey(requestedKey)) requestedKey = req.TemplateId;

        if (IsCustomerPaymentsPageKey(requestedKey))
        {
            return await GetCsrCustomerPaymentsPageDataAsync(req, configuredRule);
        }

        if (IsAgingReportPageKey(requestedKey))
        {
            return await GetCsrAgingReportPageDataAsync(req, configuredRule);
        }

        return await GetCsrMonthlyEbnotesPageDataAsync(req, configuredRule);
    }

    private async Task<IActionResult> GetCsrCustomerPaymentsPageDataAsync(
        CustomHtmlLiveDataRequest req,
        CustomHtmlRuleConfig? configuredRule)
    {
        var pageKey = configuredRule?.Key;
        if (!IsCustomerPaymentsPageKey(pageKey)) pageKey = req.TemplateId;
        var definition = ResolveCustomerPaymentsDefinition(pageKey)
            ?? throw new InvalidOperationException($"Unsupported customer-payments CSR page: {pageKey}");

        var pageRule = ResolveCustomHtmlRuleByKey(definition.PageKey)
            ?? throw new InvalidOperationException($"CSR template was not found: {definition.PageKey}");
        var source = RequireCustomerPaymentsSource(pageRule);
        var connectionName = CustomerPaymentsConnectionName(source);
        var requestFilters = req.Filters ?? new Dictionary<string, FilterSpec>();
        var batch = await GetCustomerPaymentsBatchCachedAsync(pageRule, definition, requestFilters);
        var sourceServer = (_cfg["Dashboard:CsrPbipImport:SourceServer"] ?? "app100.camhydro.com").Trim();
        var sourceDatabase = (_cfg["Dashboard:CsrPbipImport:SourceDatabase"] ?? "corporate_dashboards").Trim();

        var queryContextByVisual = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [definition.TableVisualId] = BuildCustomerPaymentsTableQueryContext(
                definition,
                connectionName,
                source,
                requestFilters,
                100)
        };

        return Json(new
        {
            found = true,
            mode = "csrPage",
            templateId = definition.PageKey,
            pageKey = definition.PageKey,
            visualDataSets = batch.VisualDataSets,
            serverFilteredVisualData = true,
            pageInfoByVisual = batch.PageInfoByVisual,
            queryContextByVisual,
            sources = new[]
            {
                new
                {
                    alias = CustomerPaymentsSourceAlias,
                    semanticEntity = CustomerPaymentsSourceAlias,
                    connectionName,
                    sourceServer,
                    sourceDatabase,
                    schema = source.Schema,
                    @object = source.Object,
                    objectType = source.ObjectKind,
                    returnedRows = batch.VisualDataSets.Values.Sum(rows => rows.Count),
                    truncated = false,
                    error = (string?)null
                }
            },
            debug = new
            {
                rawRowsMaterializedInBrowser = false,
                sourceObjectExecutions = 1,
                visualResultSets = batch.VisualDataSets.Count,
                requestFilterCount = requestFilters.Count,
                grain = definition.Daily ? "day" : "month"
            }
        });
    }

    private async Task<IActionResult> GetCsrCustomerPaymentsVisualDataAsync(
        CustomHtmlLiveDataRequest req,
        CustomHtmlRuleConfig rule)
    {
        var definition = ResolveCustomerPaymentsDefinition(rule.PageKey)
            ?? throw new InvalidOperationException($"Unsupported customer-payments CSR page: {rule.PageKey}");
        var source = RequireCustomerPaymentsSource(rule);
        var connectionName = CustomerPaymentsConnectionName(source);
        var requestFilters = req.Filters ?? new Dictionary<string, FilterSpec>();
        var visualId = string.IsNullOrWhiteSpace(rule.VisualId)
            ? rule.Key[(rule.Key.LastIndexOf('-') + 1)..]
            : rule.VisualId.Trim();

        List<Dictionary<string, object?>> data;
        CustomerPaymentsTablePage? pageInfo = null;
        object? queryContext = null;

        if (string.Equals(visualId, definition.TableVisualId, StringComparison.OrdinalIgnoreCase) && req.Skip > 0)
        {
            (data, pageInfo) = await LoadCustomerPaymentsTablePageAsync(
                rule,
                definition,
                requestFilters,
                req.Skip,
                req.Take <= 0 ? 100 : req.Take,
                HttpContext?.RequestAborted ?? CancellationToken.None);
        }
        else
        {
            var batch = await GetCustomerPaymentsBatchCachedAsync(rule, definition, requestFilters);
            if (!batch.VisualDataSets.TryGetValue(visualId, out var visualData))
                return BadRequest($"Customer-payments visual is not supported by csrVisual: {visualId}");

            data = visualData;
            batch.PageInfoByVisual.TryGetValue(visualId, out pageInfo);
        }

        if (string.Equals(visualId, definition.TableVisualId, StringComparison.OrdinalIgnoreCase))
        {
            queryContext = BuildCustomerPaymentsTableQueryContext(
                definition,
                connectionName,
                source,
                requestFilters,
                req.Take <= 0 ? 100 : Math.Clamp(req.Take, 25, 500));
        }

        var sourceServer = (_cfg["Dashboard:CsrPbipImport:SourceServer"] ?? "app100.camhydro.com").Trim();
        var sourceDatabase = (_cfg["Dashboard:CsrPbipImport:SourceDatabase"] ?? "corporate_dashboards").Trim();

        return Json(new
        {
            found = true,
            mode = "csrVisual",
            templateId = rule.Key,
            pageKey = rule.PageKey,
            visualId,
            role = rule.Role,
            title = rule.Title,
            connectionName,
            schema = source.Schema,
            obj = source.Object,
            rowFields = rule.RowFields,
            colFields = rule.ColFields,
            valueFields = rule.ValueFields,
            data,
            serverFilteredVisualData = true,
            pageInfo,
            queryContext,
            sources = new[]
            {
                new
                {
                    alias = CustomerPaymentsSourceAlias,
                    semanticEntity = CustomerPaymentsSourceAlias,
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
                queryKind = CustomerPaymentsVisualKind(definition, visualId),
                returnedRows = data.Count,
                rawRowsMaterialized = false,
                requestFilterCount = requestFilters.Count,
                sharedInitialBatch = req.Skip <= 0,
                grain = definition.Daily ? "day" : "month"
            }
        });
    }

    private static string CustomerPaymentsVisualKind(CustomerPaymentsPageDefinition definition, string visualId)
    {
        if (string.Equals(visualId, definition.TableVisualId, StringComparison.OrdinalIgnoreCase)) return "table";
        if (string.Equals(visualId, definition.ChartVisualId, StringComparison.OrdinalIgnoreCase)) return "chart";
        if (new[]
            {
                definition.FirstBillSlicerVisualId,
                definition.EBillSlicerVisualId,
                definition.PaymentSlicerVisualId,
                definition.TypesSlicerVisualId
            }.Contains(visualId, StringComparer.OrdinalIgnoreCase)) return "slicer";
        return "unknown";
    }

    private static CustomHtmlSourceConfig RequireCustomerPaymentsSource(CustomHtmlRuleConfig rule) =>
        rule.Sources.FirstOrDefault(source =>
            string.Equals(CsrSourceAlias(source), CustomerPaymentsSourceAlias, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"CSR template '{rule.Key}' has no {CustomerPaymentsSourceAlias} source.");

    private string CustomerPaymentsConnectionName(CustomHtmlSourceConfig source)
    {
        var configured = (_cfg["Dashboard:CsrPbipImport:SourceConnectionName"] ?? "csr_pbip_source").Trim();
        return string.IsNullOrWhiteSpace(source.ConnectionName) ? configured : source.ConnectionName.Trim();
    }

    private static object BuildCustomerPaymentsTableQueryContext(
        CustomerPaymentsPageDefinition definition,
        string connectionName,
        CustomHtmlSourceConfig source,
        IReadOnlyDictionary<string, FilterSpec> filters,
        int take) => new
        {
            endpoint = "../Dashboard/GetCustomHtmlLiveData",
            templateId = $"csr-v{definition.VersionId}-{definition.TableVisualId}",
            payloadMode = "csrVisual",
            connectionName,
            schema = source.Schema,
            obj = source.Object,
            filters,
            take
        };

    private async Task<CustomerPaymentsBatchPayload> GetCustomerPaymentsBatchCachedAsync(
        CustomHtmlRuleConfig sourceRule,
        CustomerPaymentsPageDefinition definition,
        IReadOnlyDictionary<string, FilterSpec> filters)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in CustomerPaymentsCache.Where(pair => pair.Value.ExpiresAtUtc <= now).Take(16).ToList())
        {
            CustomerPaymentsCache.TryRemove(stale.Key, out _);
        }

        var cacheKey = BuildCustomerPaymentsCacheKey(sourceRule, definition, filters);
        var entry = CustomerPaymentsCache.GetOrAdd(cacheKey, _ => new CustomerPaymentsCacheEntry
        {
            ExpiresAtUtc = now.AddMinutes(5),
            Loader = new Lazy<Task<CustomerPaymentsBatchPayload>>(
                () => LoadCustomerPaymentsBatchAsync(sourceRule, definition, filters, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication)
        });

        try
        {
            return await entry.Loader.Value;
        }
        catch
        {
            CustomerPaymentsCache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private static string BuildCustomerPaymentsCacheKey(
        CustomHtmlRuleConfig sourceRule,
        CustomerPaymentsPageDefinition definition,
        IReadOnlyDictionary<string, FilterSpec> filters)
    {
        var source = RequireCustomerPaymentsSource(sourceRule);
        var builder = new StringBuilder("customer-payments|")
            .Append(definition.PageKey).Append('|')
            .Append(source.ConnectionName).Append('|')
            .Append(source.Schema).Append('|')
            .Append(source.Object).Append('|')
            .Append(source.ObjectKind).Append(';');

        foreach (var pair in filters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var filter = pair.Value;
            builder.Append(pair.Key.Trim().ToLowerInvariant()).Append('|')
                .Append((filter?.Mode ?? "in").Trim().ToLowerInvariant()).Append('|')
                .Append(filter?.FromUtc ?? "").Append('|')
                .Append(filter?.ToUtc ?? "").Append('|');

            foreach (var value in (filter?.Values ?? new List<string?>())
                         .Where(value => value != null)
                         .Select(value => value!)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(value).Append(',');
            }
            builder.Append(';');
        }
        return builder.ToString();
    }

    private async Task<CustomerPaymentsBatchPayload> LoadCustomerPaymentsBatchAsync(
        CustomHtmlRuleConfig sourceRule,
        CustomerPaymentsPageDefinition definition,
        IReadOnlyDictionary<string, FilterSpec> requestFilters,
        CancellationToken cancellationToken)
    {
        var source = RequireCustomerPaymentsSource(sourceRule);
        var sourceSql = CsrSourceSql(source);
        var connectionName = CustomerPaymentsConnectionName(source);
        var allParameters = new List<SqlParameter>();

        string FilterFor(string visualId, string ignoredField, string prefix)
        {
            var parameters = new List<SqlParameter>();
            var clause = BuildCustomerPaymentsWhereClause(
                ReadCsrPbipVisualFilters($"csr-v{definition.VersionId}-{visualId}"),
                requestFilters,
                ignoredField,
                parameters,
                prefix);
            allParameters.AddRange(parameters);
            return clause;
        }

        var firstBillWhere = FilterFor(definition.FirstBillSlicerVisualId, "IsFirstBill", "@csr_cp_fb_");
        var eBillWhere = FilterFor(definition.EBillSlicerVisualId, "IsEBill", "@csr_cp_eb_");
        var paymentWhere = FilterFor(definition.PaymentSlicerVisualId, "IsPayment", "@csr_cp_pay_");
        var typesWhere = FilterFor(definition.TypesSlicerVisualId, "Types", "@csr_cp_ty_");
        var chartWhere = FilterFor(definition.ChartVisualId, "", "@csr_cp_ch_");
        var tableWhere = FilterFor(definition.TableVisualId, "", "@csr_cp_tb_");

        const int tableTake = 100;
        allParameters.Add(new SqlParameter("@csr_cp_table_fetch", SqlDbType.Int) { Value = tableTake + 1 });

        var chartSql = definition.Daily
            ? $"""
                SELECT
                    n.[Year],
                    n.[MonthName] AS [Month],
                    CONVERT(date, n.[TRANS_DATE]) AS [Date],
                    n.[Types],
                    SUM(n.[Amount]) AS [Amount],
                    COUNT_BIG(n.[SEQUENCE_]) AS [Transactions]
                FROM #csr_cash AS n
                WHERE {chartWhere}
                GROUP BY n.[Year], n.[MonthNumber], n.[MonthName], CONVERT(date, n.[TRANS_DATE]), n.[Types]
                ORDER BY CONVERT(date, n.[TRANS_DATE]), n.[Types];
                """
            : $"""
                SELECT
                    n.[Year],
                    n.[MonthName] AS [Month],
                    n.[Types],
                    SUM(n.[Amount]) AS [Amount],
                    COUNT_BIG(n.[SEQUENCE_]) AS [Transactions]
                FROM #csr_cash AS n
                WHERE {chartWhere}
                GROUP BY n.[Year], n.[MonthNumber], n.[MonthName], n.[Types]
                ORDER BY n.[Year], n.[MonthNumber], n.[Types];
                """;

        var sql = $"""
            SET NOCOUNT ON;

            SELECT
                CONVERT(nvarchar(100), c.[ACCOUNT_NO]) AS [ACCOUNT_NO],
                CONVERT(nvarchar(50), c.[OCCUPANT_CODE]) AS [OCCUPANT_CODE],
                CONVERT(nvarchar(400), c.[NAME]) AS [NAME],
                CONVERT(nvarchar(100), c.[CYCLE]) AS [CYCLE],
                TRY_CONVERT(datetime2, c.[TRANS_DATE]) AS [TRANS_DATE],
                COALESCE(CONVERT(nvarchar(400), c.[DESCRIPTION]), N'') AS [Types],
                CONVERT(nvarchar(100), c.[IsFirstBill]) AS [IsFirstBill],
                CONVERT(nvarchar(100), c.[IsEBill]) AS [IsEBill],
                CONVERT(nvarchar(100), c.[IsPayment]) AS [IsPayment],
                TRY_CONVERT(decimal(38, 6), c.[TRANS_AMT]) AS [Amount],
                CONVERT(nvarchar(200), c.[SEQUENCE_]) AS [SEQUENCE_],
                DATEPART(year, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [Year],
                DATEPART(month, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [MonthNumber],
                DATENAME(month, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [MonthName]
            INTO #csr_cash
            FROM {sourceSql} AS c;

            CREATE CLUSTERED INDEX [IX_csr_cash_date] ON #csr_cash ([TRANS_DATE]);
            CREATE INDEX [IX_csr_cash_filters] ON #csr_cash ([IsFirstBill], [IsEBill], [IsPayment]);
            CREATE INDEX [IX_csr_cash_types] ON #csr_cash ([Types]);

            SELECT DISTINCT n.[IsFirstBill]
            FROM #csr_cash AS n
            WHERE {firstBillWhere} AND NULLIF(LTRIM(RTRIM(n.[IsFirstBill])), N'') IS NOT NULL
            ORDER BY n.[IsFirstBill];

            SELECT DISTINCT n.[IsEBill]
            FROM #csr_cash AS n
            WHERE {eBillWhere} AND NULLIF(LTRIM(RTRIM(n.[IsEBill])), N'') IS NOT NULL
            ORDER BY n.[IsEBill];

            SELECT DISTINCT n.[IsPayment]
            FROM #csr_cash AS n
            WHERE {paymentWhere} AND NULLIF(LTRIM(RTRIM(n.[IsPayment])), N'') IS NOT NULL
            ORDER BY n.[IsPayment];

            SELECT DISTINCT n.[Types]
            FROM #csr_cash AS n
            WHERE {typesWhere} AND NULLIF(LTRIM(RTRIM(n.[Types])), N'') IS NOT NULL
            ORDER BY n.[Types] DESC;

            {chartSql}

            SELECT
                n.[ACCOUNT_NO], n.[OCCUPANT_CODE], n.[NAME], n.[CYCLE], n.[TRANS_DATE],
                n.[Types], n.[IsFirstBill], n.[IsEBill], n.[Amount], n.[IsPayment],
                n.[Year], n.[MonthName] AS [Month], n.[SEQUENCE_]
            FROM #csr_cash AS n
            WHERE {tableWhere}
            ORDER BY n.[TRANS_DATE] DESC, n.[SEQUENCE_] DESC, n.[ACCOUNT_NO], n.[OCCUPANT_CODE]
            OFFSET 0 ROWS FETCH NEXT @csr_cp_table_fetch ROWS ONLY;
            """;

        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var resultSets = await ReadCsrResultSetsAsync(connection, sql, allParameters, cancellationToken);
        if (resultSets.Count != 6)
            throw new InvalidOperationException($"Customer-payments batch returned {resultSets.Count} result sets; expected 6.");

        var tableRows = resultSets[5];
        var hasMore = tableRows.Count > tableTake;
        var payload = new CustomerPaymentsBatchPayload();
        payload.VisualDataSets[definition.FirstBillSlicerVisualId] = resultSets[0];
        payload.VisualDataSets[definition.EBillSlicerVisualId] = resultSets[1];
        payload.VisualDataSets[definition.PaymentSlicerVisualId] = resultSets[2];
        payload.VisualDataSets[definition.TypesSlicerVisualId] = resultSets[3];
        payload.VisualDataSets[definition.ChartVisualId] = resultSets[4];
        payload.VisualDataSets[definition.TableVisualId] = tableRows.Take(tableTake).ToList();
        payload.PageInfoByVisual[definition.TableVisualId] = new CustomerPaymentsTablePage
        {
            Skip = 0,
            PageSize = tableTake,
            ReturnedRows = Math.Min(tableRows.Count, tableTake),
            HasMore = hasMore,
            NextOffset = hasMore ? tableTake : null
        };
        return payload;
    }

    private async Task<(List<Dictionary<string, object?>> Data, CustomerPaymentsTablePage PageInfo)>
        LoadCustomerPaymentsTablePageAsync(
            CustomHtmlRuleConfig rule,
            CustomerPaymentsPageDefinition definition,
            IReadOnlyDictionary<string, FilterSpec> requestFilters,
            int requestedSkip,
            int requestedTake,
            CancellationToken cancellationToken)
    {
        var source = RequireCustomerPaymentsSource(rule);
        var connectionName = CustomerPaymentsConnectionName(source);
        var sourceSql = CsrSourceSql(source);
        var parameters = new List<SqlParameter>();
        var whereClause = BuildCustomerPaymentsWhereClause(
            ReadCsrPbipVisualFilters(rule.Key),
            requestFilters,
            "",
            parameters,
            "@csr_cp_page_");

        var skip = Math.Max(0, requestedSkip);
        var take = Math.Clamp(requestedTake <= 0 ? 100 : requestedTake, 25, 500);
        parameters.Add(new SqlParameter("@csr_cp_skip", SqlDbType.Int) { Value = skip });
        parameters.Add(new SqlParameter("@csr_cp_fetch", SqlDbType.Int) { Value = take + 1 });

        await using var connection = new SqlConnection(ConnStr(connectionName));
        await connection.OpenAsync(cancellationToken);
        var fetched = await ReadCsrRowsAsync(connection, $"""
            WITH normalized AS
            (
                SELECT
                    CONVERT(nvarchar(100), c.[ACCOUNT_NO]) AS [ACCOUNT_NO],
                    CONVERT(nvarchar(50), c.[OCCUPANT_CODE]) AS [OCCUPANT_CODE],
                    CONVERT(nvarchar(400), c.[NAME]) AS [NAME],
                    CONVERT(nvarchar(100), c.[CYCLE]) AS [CYCLE],
                    TRY_CONVERT(datetime2, c.[TRANS_DATE]) AS [TRANS_DATE],
                    COALESCE(CONVERT(nvarchar(400), c.[DESCRIPTION]), N'') AS [Types],
                    CONVERT(nvarchar(100), c.[IsFirstBill]) AS [IsFirstBill],
                    CONVERT(nvarchar(100), c.[IsEBill]) AS [IsEBill],
                    CONVERT(nvarchar(100), c.[IsPayment]) AS [IsPayment],
                    TRY_CONVERT(decimal(38, 6), c.[TRANS_AMT]) AS [Amount],
                    CONVERT(nvarchar(200), c.[SEQUENCE_]) AS [SEQUENCE_],
                    DATEPART(year, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [Year],
                    DATEPART(month, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [MonthNumber],
                    DATENAME(month, TRY_CONVERT(datetime2, c.[TRANS_DATE])) AS [MonthName]
                FROM {sourceSql} AS c
            )
            SELECT
                n.[ACCOUNT_NO], n.[OCCUPANT_CODE], n.[NAME], n.[CYCLE], n.[TRANS_DATE],
                n.[Types], n.[IsFirstBill], n.[IsEBill], n.[Amount], n.[IsPayment],
                n.[Year], n.[MonthName] AS [Month], n.[SEQUENCE_]
            FROM normalized AS n
            WHERE {whereClause}
            ORDER BY n.[TRANS_DATE] DESC, n.[SEQUENCE_] DESC, n.[ACCOUNT_NO], n.[OCCUPANT_CODE]
            OFFSET @csr_cp_skip ROWS FETCH NEXT @csr_cp_fetch ROWS ONLY;
            """, parameters, cancellationToken);

        var hasMore = fetched.Count > take;
        var data = fetched.Take(take).ToList();
        return (data, new CustomerPaymentsTablePage
        {
            Skip = skip,
            PageSize = take,
            ReturnedRows = data.Count,
            HasMore = hasMore,
            NextOffset = hasMore ? skip + data.Count : null
        });
    }

    private static string BuildCustomerPaymentsWhereClause(
        IReadOnlyCollection<CsrPbipVisualFilter> pbiFilters,
        IReadOnlyDictionary<string, FilterSpec> requestFilters,
        string ignoredRequestField,
        ICollection<SqlParameter> parameters,
        string parameterPrefix)
    {
        var clauses = new List<string> { "1 = 1" };
        var parameterIndex = 0;

        foreach (var filter in pbiFilters)
        {
            AppendCustomerPaymentsPbipFilterClause(
                clauses,
                parameters,
                ref parameterIndex,
                filter,
                parameterPrefix);
        }

        foreach (var pair in requestFilters)
        {
            if (!string.IsNullOrWhiteSpace(ignoredRequestField) &&
                string.Equals(NormalizeCustomerPaymentsField(pair.Key), ignoredRequestField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendCustomerPaymentsRequestFilterClause(
                clauses,
                parameters,
                ref parameterIndex,
                pair.Key,
                pair.Value,
                parameterPrefix);
        }

        return string.Join(" AND ", clauses);
    }

    private static void AppendCustomerPaymentsPbipFilterClause(
        ICollection<string> clauses,
        ICollection<SqlParameter> parameters,
        ref int parameterIndex,
        CsrPbipVisualFilter filter,
        string parameterPrefix)
    {
        var field = NormalizeCustomerPaymentsField(filter.Field);
        if (field == null) return;

        var op = (filter.Op ?? "eq").Trim().ToLowerInvariant();
        var values = filter.Values.Count > 0
            ? filter.Values
            : string.IsNullOrWhiteSpace(filter.Value)
                ? new List<string>()
                : new List<string> { filter.Value! };
        var column = CustomerPaymentsColumn(field);

        if (op == "notnull") { clauses.Add($"{column} IS NOT NULL"); return; }
        if (op == "null") { clauses.Add($"{column} IS NULL"); return; }
        if (values.Count == 0) return;

        if (op is "in" or "notin")
        {
            var names = new List<string>();
            foreach (var value in values)
            {
                var name = $"{parameterPrefix}{parameterIndex++}";
                names.Add(name);
                parameters.Add(new SqlParameter(name, CustomerPaymentsFilterValue(field, value) ?? DBNull.Value));
            }
            clauses.Add($"{column} {(op == "notin" ? "NOT IN" : "IN")} ({string.Join(", ", names)})");
            return;
        }

        var parameterName = $"{parameterPrefix}{parameterIndex++}";
        parameters.Add(new SqlParameter(parameterName, CustomerPaymentsFilterValue(field, values[0]) ?? DBNull.Value));
        var sqlOperator = op switch
        {
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            "neq" => "<>",
            _ => "="
        };
        clauses.Add($"{column} {sqlOperator} {parameterName}");
    }

    private static void AppendCustomerPaymentsRequestFilterClause(
        ICollection<string> clauses,
        ICollection<SqlParameter> parameters,
        ref int parameterIndex,
        string requestedField,
        FilterSpec? filter,
        string parameterPrefix)
    {
        var field = NormalizeCustomerPaymentsField(requestedField);
        if (field == null || filter == null) return;
        var column = CustomerPaymentsColumn(field);
        var mode = (filter.Mode ?? "in").Trim().ToLowerInvariant();

        if (mode == "isnull") { clauses.Add($"{column} IS NULL"); return; }
        if (mode == "notnull") { clauses.Add($"{column} IS NOT NULL"); return; }

        if (mode == "range")
        {
            if (!string.IsNullOrWhiteSpace(filter.FromUtc))
            {
                var name = $"{parameterPrefix}{parameterIndex++}";
                parameters.Add(new SqlParameter(name, CustomerPaymentsFilterValue(field, filter.FromUtc!) ?? DBNull.Value));
                clauses.Add($"{column} >= {name}");
            }
            if (!string.IsNullOrWhiteSpace(filter.ToUtc))
            {
                var name = $"{parameterPrefix}{parameterIndex++}";
                parameters.Add(new SqlParameter(name, CustomerPaymentsFilterValue(field, filter.ToUtc!) ?? DBNull.Value));
                clauses.Add($"{column} <= {name}");
            }
            return;
        }

        var values = filter.Values.Where(value => value != null).Select(value => value!).ToList();
        if (values.Count == 0) return;
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = $"{parameterPrefix}{parameterIndex++}";
            names.Add(name);
            parameters.Add(new SqlParameter(name, CustomerPaymentsFilterValue(field, value) ?? DBNull.Value));
        }
        clauses.Add($"{column} {(mode == "notin" ? "NOT IN" : "IN")} ({string.Join(", ", names)})");
    }

    private static string? NormalizeCustomerPaymentsField(string? field)
    {
        var value = (field ?? "").Trim();
        if (value.Equals("IsFirstBill", StringComparison.OrdinalIgnoreCase)) return "IsFirstBill";
        if (value.Equals("IsEBill", StringComparison.OrdinalIgnoreCase)) return "IsEBill";
        if (value.Equals("IsPayment", StringComparison.OrdinalIgnoreCase)) return "IsPayment";
        if (value.Equals("Types", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase)) return "Types";
        if (value.Equals("Year", StringComparison.OrdinalIgnoreCase)) return "Year";
        if (value.Equals("Month", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("MonthNumber", StringComparison.OrdinalIgnoreCase)) return "MonthNumber";
        if (value.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("TRANS_DATE", StringComparison.OrdinalIgnoreCase)) return "TRANS_DATE";
        if (value.Equals("CYCLE", StringComparison.OrdinalIgnoreCase)) return "CYCLE";
        return null;
    }

    private static string CustomerPaymentsColumn(string field) => field switch
    {
        "MonthNumber" => "n.[MonthNumber]",
        "TRANS_DATE" => "n.[TRANS_DATE]",
        _ => $"n.[{field}]"
    };

    private static object? CustomerPaymentsFilterValue(string field, string value)
    {
        if (field.Equals("Year", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("MonthNumber", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(value, out var number)) return number;
            var month = DateTime.TryParse("1 " + value, out var monthDate) ? monthDate.Month : 0;
            return month > 0 ? month : value;
        }

        if (field.Equals("TRANS_DATE", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out var date))
            return date;

        return value;
    }
}
