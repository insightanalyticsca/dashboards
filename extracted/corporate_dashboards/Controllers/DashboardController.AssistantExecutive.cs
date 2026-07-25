using corporate_dashboards.Models;
using corporate_dashboards.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    [HttpPost]
    public async Task<IActionResult> ExecuteExecutiveAssistant(
        [FromBody] AssistantExecutiveRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest("Missing executive assistant request.");

        var expectedSuite = request.LayoutVersionId switch
        {
            213 => "ebill",
            214 => "ar",
            215 => "disconnects",
            216 => "finalbill",
            217 => "payments",
            _ => string.Empty
        };

        var requestedSuite = NormalizeExecutiveVersionKey(request.Suite);
        if (string.IsNullOrWhiteSpace(expectedSuite) ||
            !string.Equals(expectedSuite, requestedSuite, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("The executive assistant request does not match the active dashboard version.");
        }

        try
        {
            var payload = await LoadExecutiveVersionAsync(expectedSuite, cancellationToken);
            var result = BuildExecutiveAssistantResult(payload, request);
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Json(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (DashboardAssistantQueryException ex)
        {
            _log.LogInformation(
                ex,
                "Executive assistant request could not be resolved. Version={VersionId}, Measure={Measure}",
                request.LayoutVersionId,
                request.Measure);

            return BadRequest(new ProblemDetails
            {
                Title = "Dashboard assistant could not resolve the request",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (Exception ex)
        {
            var traceId = HttpContext.TraceIdentifier;
            _log.LogError(
                ex,
                "Executive assistant query failed. Version={VersionId}, Measure={Measure}, TraceId={TraceId}",
                request.LayoutVersionId,
                request.Measure,
                traceId);

            return Problem(
                title: "Dashboard assistant data failed",
                detail: $"The current dashboard data could not answer this request. TraceId={traceId}",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static object BuildExecutiveAssistantResult(
        ExecutiveVersionPayload payload,
        AssistantExecutiveRequestDto request)
    {
        var measure = (request.Measure ?? string.Empty).Trim().ToLowerInvariant();
        var dimensions = request.Dimensions ?? new List<string>();
        var hasRequestedPeriod = !string.IsNullOrWhiteSpace(request.FromUtc) ||
                                 !string.IsNullOrWhiteSpace(request.ToUtc);
        var includePeriod = dimensions.Contains("period", StringComparer.OrdinalIgnoreCase) ||
                            !string.Equals(request.ChartType, "metric", StringComparison.OrdinalIgnoreCase) ||
                            hasRequestedPeriod;

        List<Dictionary<string, object?>> rows;
        string[] rowFields;
        string[] colFields = Array.Empty<string>();
        var source = "executive metric";
        string? warning = null;

        switch (payload.Key.ToLowerInvariant())
        {
            case "ebill":
                (rows, rowFields, source, warning) = BuildEbillAssistantRows(
                    payload,
                    request,
                    measure,
                    includePeriod);
                break;

            case "payments":
                (rows, rowFields, source, warning) = BuildPaymentsAssistantRows(
                    payload,
                    request,
                    measure,
                    includePeriod);
                break;

            case "finalbill":
                (rows, rowFields, source, warning) = BuildFinalBillAssistantRows(
                    payload,
                    request,
                    measure,
                    includePeriod);
                break;

            case "disconnects":
                (rows, rowFields, source, warning) = BuildDisconnectAssistantRows(
                    payload,
                    request,
                    measure,
                    includePeriod);
                break;

            case "ar":
                (rows, rowFields, source, warning) = BuildArAssistantRows(
                    payload,
                    request,
                    measure,
                    includePeriod);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported executive assistant payload: {payload.Key}");
        }

        if (rows.Count > 0)
        {
            var normalized = NormalizeExecutiveRows(
                payload.Key,
                measure,
                request.Aggregation,
                dimensions,
                rows);
            rows = normalized.Rows;
            rowFields = normalized.RowFields;
        }

        return new
        {
            data = rows,
            rowFields,
            colFields,
            valueFields = new[] { "Value" },
            agg = "SemanticValue",
            source,
            warning,
            payloadKey = payload.Key,
            payloadTitle = payload.Title,
            asOfLabel = payload.AsOfLabel
        };
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        BuildEbillAssistantRows(
            ExecutiveVersionPayload payload,
            AssistantExecutiveRequestDto request,
            string measure,
            bool includePeriod)
    {
        var includeCustomerType = request.Dimensions.Contains(
            "customer_type",
            StringComparer.OrdinalIgnoreCase);

        if (measure == "total_ebill_customers")
        {
            var table = payload.Tables.FirstOrDefault(item =>
                string.Equals(item.Id, "ebill-total-matrix", StringComparison.OrdinalIgnoreCase));
            if (table == null) return MetricFallback(payload, "total");

            var rows = FlattenHierarchyTable(
                table,
                request,
                includeCustomerType,
                "Total");

            return (
                rows,
                includeCustomerType
                    ? new[] { "period", "customer_type" }
                    : new[] { "period" },
                table.Title,
                "Total E-Bill Customers is a monthly snapshot. Monthly values are displayed individually and are never summed across months.");
        }

        if (measure is "new_ebill_customers" or "new_ebill_percent")
        {
            var chart = payload.Charts.FirstOrDefault(item =>
                string.Equals(item.Id, "ebill-new", StringComparison.OrdinalIgnoreCase));
            if (chart == null)
                return MetricFallback(payload, measure == "new_ebill_percent" ? "new-pct" : "new");

            var percent = measure == "new_ebill_percent";
            var selected = chart.Series.Where(series =>
                percent
                    ? series.Name.Contains("%", StringComparison.OrdinalIgnoreCase)
                    : !series.Name.Contains("%", StringComparison.OrdinalIgnoreCase));

            var rows = FlattenChart(
                chart,
                selected,
                request,
                includeCustomerType && !percent ? "customer_type" : null,
                combineSeries: !includeCustomerType || percent);

            return (
                rows,
                includeCustomerType && !percent
                    ? new[] { "period", "customer_type" }
                    : new[] { "period" },
                chart.Title,
                null);
        }

        if (measure == "total_ebill_percent")
        {
            return MetricFallback(
                payload,
                "total-pct",
                "The current executive payload contains the latest Total E-Bill % card, but not a monthly Total E-Bill % series.");
        }

        return MetricFallback(payload, "total");
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        BuildPaymentsAssistantRows(
            ExecutiveVersionPayload payload,
            AssistantExecutiveRequestDto request,
            string measure,
            bool includePeriod)
    {
        var metricKey = measure == "transactions" ? "transactions" : "payment-value";
        if (!includePeriod) return MetricFallback(payload, metricKey);

        var chart = payload.Charts.FirstOrDefault(item =>
            string.Equals(item.Id, "payments-rolling", StringComparison.OrdinalIgnoreCase));
        if (chart == null) return MetricFallback(payload, metricKey);

        var includePaymentType = request.Dimensions.Contains(
            "payment_type",
            StringComparer.OrdinalIgnoreCase);
        var transactionMeasure = measure == "transactions";
        if (transactionMeasure && includePaymentType)
        {
            throw new InvalidOperationException(
                "Transactions is available by period on this screen, but not by payment type. Use Payment Value for payment-type grouping.");
        }

        var selected = chart.Series.Where(series =>
            transactionMeasure
                ? series.Name.Equals("Transactions", StringComparison.OrdinalIgnoreCase)
                : !series.Name.Equals("Transactions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var requestedPaymentTypes = ReadExecutiveFilterValues(request, "payment_type");
        if (!transactionMeasure && requestedPaymentTypes.Count > 0)
        {
            selected = selected
                .Where(series => requestedPaymentTypes.Any(filter =>
                    PaymentTypeMatchesFilter(series.Name, filter)))
                .ToList();

            if (selected.Count == 0)
            {
                var available = chart.Series
                    .Where(series => !series.Name.Equals("Transactions", StringComparison.OrdinalIgnoreCase))
                    .Select(series => series.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                throw new DashboardAssistantQueryException(
                    $"No payment type matched {string.Join(" or ", requestedPaymentTypes)}. " +
                    $"Available payment types on this screen: {string.Join(", ", available)}.");
            }
        }

        var rows = FlattenChart(
            chart,
            selected,
            request,
            includePaymentType && !transactionMeasure ? "payment_type" : null,
            combineSeries: !includePaymentType || transactionMeasure);

        var filterSuffix = requestedPaymentTypes.Count > 0
            ? $" · {string.Join(" / ", requestedPaymentTypes)}"
            : string.Empty;

        return (
            rows,
            includePaymentType && !transactionMeasure
                ? new[] { "period", "payment_type" }
                : new[] { "period" },
            chart.Title + filterSuffix,
            null);
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        BuildFinalBillAssistantRows(
            ExecutiveVersionPayload payload,
            AssistantExecutiveRequestDto request,
            string measure,
            bool includePeriod)
    {
        var metricKey = measure switch
        {
            "accounts" => "accounts",
            "balance" => "balance",
            "post_paid" => "postpaid",
            "paid_ratio" => "ratio",
            _ => "balance"
        };

        if (!includePeriod) return MetricFallback(payload, metricKey);

        var table = payload.Tables.FirstOrDefault(item =>
            string.Equals(item.Id, "finalbill-current", StringComparison.OrdinalIgnoreCase));
        if (table == null) return MetricFallback(payload, metricKey);

        var suffix = measure switch
        {
            "accounts" => "Accts",
            "post_paid" => "Post Paid",
            "paid_ratio" => "Paid Ratio",
            _ => "Balance"
        };
        var includeCustomerType = request.Dimensions.Contains(
            "customer_type",
            StringComparer.OrdinalIgnoreCase);

        var output = new List<Dictionary<string, object?>>();
        foreach (var row in table.Rows)
        {
            if (string.Equals(ReadText(row, "__rowType"), "total", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryParseExecutiveDate(ReadText(row, "Date In"), out var date) ||
                !InRequestedRange(date, request))
                continue;

            if (includeCustomerType)
            {
                foreach (var type in new[] { "Commercial", "Residential" })
                {
                    output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["period"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["customer_type"] = type,
                        ["Value"] = ReadDecimal(row, $"{type} {suffix}")
                    });
                }
            }
            else
            {
                output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["period"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Value"] = ReadDecimal(row, $"Total {suffix}")
                });
            }
        }

        return (
            output,
            includeCustomerType
                ? new[] { "period", "customer_type" }
                : new[] { "period" },
            table.Title,
            null);
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        BuildDisconnectAssistantRows(
            ExecutiveVersionPayload payload,
            AssistantExecutiveRequestDto request,
            string measure,
            bool includePeriod)
    {
        var metricKey = measure switch
        {
            "reconnected_accounts" => "reconnect-month",
            "bankruptcy_accounts" => "bankruptcy-accounts",
            "bankruptcy_amount" => "bankruptcy-amount",
            _ => "disconnect-month"
        };

        if (!includePeriod) return MetricFallback(payload, metricKey);

        if (measure is "bankruptcy_accounts" or "bankruptcy_amount")
        {
            var table = payload.Tables.FirstOrDefault(item =>
                string.Equals(item.Id, "bankruptcy-rolling-matrix", StringComparison.OrdinalIgnoreCase));
            if (table != null)
            {
                var includeCustomerType = request.Dimensions.Contains(
                    "customer_type",
                    StringComparer.OrdinalIgnoreCase);
                var valueColumn = measure == "bankruptcy_amount" ? "Total Amount In" : "Total Accounts";
                var rows = FlattenHierarchyTable(
                    table,
                    request,
                    includeCustomerType,
                    valueColumn,
                    measure == "bankruptcy_amount" ? "Amount In" : "Accounts");
                return (
                    rows,
                    includeCustomerType
                        ? new[] { "period", "customer_type" }
                        : new[] { "period" },
                    table.Title,
                    null);
            }
        }

        return MetricFallback(
            payload,
            metricKey,
            "The current screen payload exposes this measure as a validated card. A monthly trend is not available for this card.");
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        BuildArAssistantRows(
            ExecutiveVersionPayload payload,
            AssistantExecutiveRequestDto request,
            string measure,
            bool includePeriod)
    {
        var metricKey = measure switch
        {
            "commercial_arrears" => "comm",
            "total_arrears_customers" => "customers",
            "average_bill" => "average",
            _ => "res"
        };

        if (!includePeriod) return MetricFallback(payload, metricKey);

        if (measure is "residential_arrears" or "commercial_arrears")
        {
            var chartId = measure == "commercial_arrears" ? "ar-comm" : "ar-res";
            var chart = payload.Charts.FirstOrDefault(item =>
                string.Equals(item.Id, chartId, StringComparison.OrdinalIgnoreCase));
            if (chart != null)
            {
                var includeBucket = request.Dimensions.Contains(
                    "aging_bucket",
                    StringComparer.OrdinalIgnoreCase);
                var selected = includeBucket
                    ? chart.Series.Where(series => !series.Name.Equals("Total", StringComparison.OrdinalIgnoreCase))
                    : chart.Series.Where(series => series.Name.Equals("Total", StringComparison.OrdinalIgnoreCase));
                var rows = FlattenChart(
                    chart,
                    selected,
                    request,
                    includeBucket ? "aging_bucket" : null,
                    combineSeries: !includeBucket);
                return (
                    rows,
                    includeBucket
                        ? new[] { "period", "aging_bucket" }
                        : new[] { "period" },
                    chart.Title,
                    null);
            }
        }

        var totalTable = payload.Tables.FirstOrDefault(item =>
            string.Equals(item.Id, "ar-total", StringComparison.OrdinalIgnoreCase));
        if (totalTable != null && measure is "total_arrears_customers" or "average_bill")
        {
            var column = measure == "average_bill" ? "Average Bill" : "Total Arrears Customers";
            var rows = new List<Dictionary<string, object?>>();
            foreach (var row in totalTable.Rows)
            {
                if (!TryParseExecutiveDate(ReadText(row, "Month"), out var date) ||
                    !InRequestedRange(date, request))
                    continue;
                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["period"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Value"] = ReadDecimal(row, column)
                });
            }
            return (rows, new[] { "period" }, totalTable.Title, null);
        }

        return MetricFallback(payload, metricKey);
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields, string Source, string? Warning)
        MetricFallback(
            ExecutiveVersionPayload payload,
            string metricKey,
            string? warning = null)
    {
        var metric = payload.Metrics.FirstOrDefault(item =>
            string.Equals(item.Key, metricKey, StringComparison.OrdinalIgnoreCase));
        if (metric == null)
            throw new InvalidOperationException($"Executive metric was not found: {metricKey}");

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["period"] = metric.Period,
            ["Value"] = metric.Value,
            ["mom"] = metric.Mom,
            ["yoy"] = metric.Yoy,
            ["mom_label"] = metric.MomLabel,
            ["yoy_label"] = metric.YoyLabel
        };

        return (new List<Dictionary<string, object?>> { row }, Array.Empty<string>(), metric.Label, warning);
    }

    private static List<string> ReadExecutiveFilterValues(
        AssistantExecutiveRequestDto request,
        string fieldName)
    {
        if (request.Filters == null ||
            !request.Filters.TryGetValue(fieldName, out var filter) ||
            filter?.Values == null)
        {
            return new List<string>();
        }

        return filter.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool PaymentTypeMatchesFilter(
        string seriesName,
        string requestedValue)
    {
        var series = NormalizeAssistantMatchText(seriesName);
        var requested = NormalizeAssistantMatchText(requestedValue);
        if (series.Length == 0 || requested.Length == 0) return false;
        if (series == requested || series.Contains(requested, StringComparison.Ordinal) ||
            requested.Contains(series, StringComparison.Ordinal))
        {
            return true;
        }

        var aliases = DashboardAssistantSemanticCatalog.GetDimensionValueAliases(
            "executive-customer-payments",
            "payment_type");
        if (!aliases.TryGetValue(requestedValue, out var requestedAliases))
        {
            requestedAliases = new[] { requestedValue };
        }

        return requestedAliases.Any(alias =>
        {
            var normalizedAlias = NormalizeAssistantMatchText(alias);
            return normalizedAlias.Length > 0 &&
                   (series == normalizedAlias ||
                    series.Contains(normalizedAlias, StringComparison.Ordinal) ||
                    normalizedAlias.Contains(series, StringComparison.Ordinal));
        });
    }

    private static string NormalizeAssistantMatchText(string? value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();
        return string.Join(
            " ",
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<Dictionary<string, object?>> FlattenChart(
        ExecutiveChartDto chart,
        IEnumerable<ExecutiveSeriesDto> selectedSeries,
        AssistantExecutiveRequestDto request,
        string? seriesDimension,
        bool combineSeries)
    {
        var series = selectedSeries.ToList();
        var output = new List<Dictionary<string, object?>>();

        for (var index = 0; index < chart.Categories.Count; index++)
        {
            if (!TryParseExecutiveDate(chart.Categories[index], out var period) ||
                !InRequestedRange(period, request))
                continue;

            if (combineSeries)
            {
                var value = series.Sum(item =>
                    index < item.Data.Count ? item.Data[index] ?? 0m : 0m);
                output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["period"] = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Value"] = value
                });
            }
            else
            {
                foreach (var item in series)
                {
                    output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["period"] = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        [seriesDimension ?? "series"] = item.Name,
                        ["Value"] = index < item.Data.Count ? item.Data[index] : null
                    });
                }
            }
        }

        return output;
    }

    private static List<Dictionary<string, object?>> FlattenHierarchyTable(
        ExecutiveTableDto table,
        AssistantExecutiveRequestDto request,
        bool includeCategory,
        string totalColumn,
        string? categorySuffix = null)
    {
        var output = new List<Dictionary<string, object?>>();
        var year = 0;

        var categoryColumns = table.Columns
            .Where(column =>
                !string.Equals(column, "Year / Month", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(column, totalColumn, StringComparison.OrdinalIgnoreCase) &&
                (categorySuffix == null ||
                 column.EndsWith(" " + categorySuffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var row in table.Rows)
        {
            var rowType = ReadText(row, "__rowType");
            var label = ReadText(row, "Year / Month");
            if (string.Equals(rowType, "group", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
                continue;
            }

            if (year <= 0 || !DateTime.TryParseExact(
                    label,
                    "MMM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var monthOnly))
                continue;

            var period = new DateTime(year, monthOnly.Month, 1);
            if (!InRequestedRange(period, request)) continue;

            if (includeCategory)
            {
                foreach (var column in categoryColumns)
                {
                    var category = categorySuffix != null &&
                                   column.EndsWith(" " + categorySuffix, StringComparison.OrdinalIgnoreCase)
                        ? column[..^(categorySuffix.Length + 1)]
                        : column;
                    output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["period"] = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["customer_type"] = category,
                        ["Value"] = ReadDecimal(row, column)
                    });
                }
            }
            else
            {
                output.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["period"] = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Value"] = ReadDecimal(row, totalColumn)
                });
            }
        }

        return output;
    }

    private static (List<Dictionary<string, object?>> Rows, string[] RowFields)
        NormalizeExecutiveRows(
            string payloadKey,
            string measure,
            string? aggregation,
            IReadOnlyList<string> requestedDimensions,
            IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var dimensions = requestedDimensions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dimensions.Count == 0)
        {
            return (
                new List<Dictionary<string, object?>>
                {
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Value"] = ReduceExecutiveValue(
                            payloadKey,
                            measure,
                            aggregation,
                            rows)
                    }
                },
                Array.Empty<string>());
        }

        var grouped = rows
            .GroupBy(
                row => string.Join(
                    "\u001f",
                    dimensions.Select(dimension => ReadText(row, dimension))),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var dimension in dimensions)
                {
                    output[dimension] = ReadText(first, dimension);
                }

                output["Value"] = ReduceExecutiveValue(
                    payloadKey,
                    measure,
                    aggregation,
                    group.ToList());
                return output;
            })
            .OrderBy(row =>
            {
                if (row.TryGetValue("period", out var periodValue) &&
                    TryParseExecutiveDate(Convert.ToString(periodValue, CultureInfo.InvariantCulture), out var period))
                {
                    return period;
                }

                return DateTime.MinValue;
            })
            .ThenBy(row => string.Join(
                "\u001f",
                dimensions
                    .Where(dimension => !string.Equals(dimension, "period", StringComparison.OrdinalIgnoreCase))
                    .Select(dimension => ReadText(row, dimension))),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (grouped, dimensions.ToArray());
    }

    private static decimal ReduceExecutiveValue(
        string payloadKey,
        string measure,
        string? aggregation,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var valuedRows = rows
            .Select(row => new
            {
                Value = ReadDecimal(row, "Value"),
                Period = TryParseExecutiveDate(ReadText(row, "period"), out var parsed)
                    ? parsed
                    : DateTime.MinValue
            })
            .ToList();

        if (valuedRows.Count == 0) return 0m;

        var mode = (aggregation ?? "Sum").Trim().ToLowerInvariant();
        if (mode == "average") return valuedRows.Average(item => item.Value);
        if (mode == "minimum") return valuedRows.Min(item => item.Value);
        if (mode == "maximum") return valuedRows.Max(item => item.Value);

        if (UsesLatestExecutiveValue(payloadKey, measure))
        {
            return valuedRows
                .OrderBy(item => item.Period)
                .Last()
                .Value;
        }

        return valuedRows.Sum(item => item.Value);
    }

    private static bool UsesLatestExecutiveValue(string payloadKey, string measure)
    {
        var payload = (payloadKey ?? string.Empty).Trim().ToLowerInvariant();
        var fact = (measure ?? string.Empty).Trim().ToLowerInvariant();

        return payload switch
        {
            "ebill" => fact is
                "total_ebill_customers" or
                "total_ebill_percent" or
                "new_ebill_percent",

            "ar" => true,

            "disconnects" => fact is
                "bankruptcy_accounts" or
                "bankruptcy_amount",

            "finalbill" => true,

            "payments" => false,

            _ => false
        };
    }

    private static bool InRequestedRange(DateTime period, AssistantExecutiveRequestDto request)
    {
        var from = TryParseIsoDate(request.FromUtc);
        var to = TryParseIsoDate(request.ToUtc);
        return (!from.HasValue || period >= from.Value) &&
               (!to.HasValue || period < to.Value);
    }

    private static DateTime? TryParseIsoDate(string? value)
        => DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.Date
            : null;

    private static bool TryParseExecutiveDate(string? value, out DateTime date)
    {
        date = default;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        var formats = new[]
        {
            "yyyy-MM-dd",
            "MMM yy",
            "MMM yyyy",
            "M/d/yyyy",
            "MM/dd/yyyy",
            "yyyy-MM-ddTHH:mm:ss"
        };

        return DateTime.TryParseExact(
                   text,
                   formats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out date) ||
               DateTime.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out date);
    }

    private static string ReadText(
        IReadOnlyDictionary<string, object?> row,
        string key)
    {
        var pair = row.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return pair.Key == null || pair.Value == null
            ? string.Empty
            : Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static decimal? ReadDecimal(
        IReadOnlyDictionary<string, object?> row,
        string key)
    {
        var pair = row.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (pair.Key == null || pair.Value == null || pair.Value == DBNull.Value)
            return null;

        try
        {
            return Convert.ToDecimal(pair.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return decimal.TryParse(
                Convert.ToString(pair.Value, CultureInfo.InvariantCulture),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null;
        }
    }

    private sealed class DashboardAssistantQueryException : Exception
    {
        public DashboardAssistantQueryException(string message)
            : base(message)
        {
        }
    }

}
