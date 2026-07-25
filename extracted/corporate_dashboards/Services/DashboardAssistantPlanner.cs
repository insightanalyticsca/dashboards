using System.Globalization;
using System.Text.RegularExpressions;
using corporate_dashboards.Models;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public interface IDashboardAssistantPlanner
{
    Task<AssistantPlanResponse> PlanAsync(
        AssistantQueryRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantSuggestionDto>> SuggestAsync(
        long? layoutVersionId,
        IReadOnlyCollection<string>? currentTemplateKeys,
        string? datasetKey,
        string? prefix,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetExamplesAsync(
        AssistantVersionContextDto context,
        CancellationToken cancellationToken);
}

public sealed class DashboardAssistantPlanner : IDashboardAssistantPlanner
{
    private const string MonthNamePattern =
        @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private const string DateExpressionPattern =
        @"(?:" +
        @"20\d{2}-\d{1,2}-\d{1,2}" +
        @"|\d{1,2}/\d{1,2}/20\d{2}" +
        @"|" + MonthNamePattern + @"\s+\d{1,2}(?:st|nd|rd|th)?(?:,\s*|\s+)20\d{2}" +
        @"|" + MonthNamePattern + @"\s+20\d{2}" +
        @"|q[1-4]\s+20\d{2}" +
        @"|(?:first|second|third|fourth)\s+quarter(?:\s+of)?\s+20\d{2}" +
        @"|20\d{2}" +
        @"|last\s+completed\s+month|last\s+month|previous\s+month|prior\s+month" +
        @"|this\s+month|current\s+month" +
        @"|last\s+week|previous\s+week|prior\s+week" +
        @"|this\s+week|current\s+week" +
        @"|last\s+quarter|previous\s+quarter|prior\s+quarter" +
        @"|this\s+quarter|current\s+quarter" +
        @"|last\s+year|previous\s+year|prior\s+year" +
        @"|this\s+year|current\s+year" +
        @"|today|yesterday" +
        @")";

    private static readonly Regex SpaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex RollingPeriodRegex = new(
        @"\b(?:last|previous|prior|past|over\s+(?:the\s+)?last|within\s+(?:the\s+)?last)\s+" +
        @"(?<n>\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+" +
        @"(?<unit>days?|weeks?|months?|quarters?|years?|periods?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BetweenDateRegex = new(
        $@"\bbetween\s+(?<start>{DateExpressionPattern})\s+(?:and|to)\s+(?<end>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromToDateRegex = new(
        $@"\bfrom\s+(?<start>{DateExpressionPattern})\s+(?:to|through|thru|until|till)\s+(?<end>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MonthRangeShorthandRegex = new(
        $@"\b(?:between|from)\s+(?<startMonth>{MonthNamePattern})\s+(?:and|to|through|thru|until|till)\s+(?<endMonth>{MonthNamePattern})\s+(?<year>20\d{{2}})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SinceDateRegex = new(
        $@"\b(?:since|from|starting(?:\s+in|\s+from)?|beginning(?:\s+in|\s+from)?)\s+(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DuringDateRegex = new(
        $@"\b(?:during|within|in|for)\s+(?:the\s+)?(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BeforeDateRegex = new(
        $@"\b(?:before|prior\s+to|earlier\s+than)\s+(?:the\s+)?(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AfterDateRegex = new(
        $@"\b(?:after|later\s+than|following)\s+(?:the\s+)?(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThroughDateRegex = new(
        $@"\b(?:through|thru|until|till|up\s+to|ending(?:\s+in|\s+on)?|as\s+of)\s+(?:the\s+)?(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StandaloneDateExpressionRegex = new(
        $@"\b(?<value>{DateExpressionPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TemporalOperatorRegex = new(
        @"\b(?:since|starting|beginning|during|within|before|prior\s+to|earlier\s+than|after|later\s+than|between|from|through|thru|until|till|up\s+to|ending|as\s+of)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GroupingClauseRegex = new(
        @"\b(?:grouped\s+by|broken\s+down\s+by|split\s+by|by|per)\s+(?<group>.*?)(?=\s+(?:for|since|from|during|within|before|after|between|through|until|over|as|with|compared|versus|vs|by)\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> DomainAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ebill"] = new[] { "e bill", "ebill", "electronic bill", "paperless bill", "adoption" },
            ["payments"] = new[] { "payment", "payments", "collections", "cash", "payment type", "transaction" },
            ["aging"] = new[] { "aging", "arrears", "overdue", "debt", "balance", "credit risk" },
            ["disconnects"] = new[] { "disconnect", "reconnect", "bankrupt", "bankruptcy" },
            ["calls"] = new[] { "call", "calls", "abandon", "response time", "contact centre", "contact center" },
            ["tickets"] = new[] { "ticket", "tickets", "request", "requests", "service desk", "sla" },
            ["security"] = new[] { "security", "cyber", "ocsf", "risk", "phish", "kb4", "training" },
            ["uptime"] = new[] { "uptime", "availability", "outage", "monitor" },
            ["email"] = new[] { "email", "emails", "queue", "mail" },
            ["moves"] = new[] { "move", "moves", "monthly move" }
        };

    private readonly IDashboardAssistantCatalogService _catalog;
    private readonly IDashboardAssistantContextService _context;
    private readonly DashboardAssistantOptions _options;
    private readonly ILogger<DashboardAssistantPlanner> _logger;

    public DashboardAssistantPlanner(
        IDashboardAssistantCatalogService catalog,
        IDashboardAssistantContextService context,
        IOptions<DashboardAssistantOptions> options,
        ILogger<DashboardAssistantPlanner> logger)
    {
        _catalog = catalog;
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AssistantPlanResponse> PlanAsync(
        AssistantQueryRequest request,
        CancellationToken cancellationToken)
    {
        var question = NormalizeText(request.Question);
        var context = await _context.ResolveAsync(
            request.LayoutVersionId,
            request.CurrentTemplateKeys,
            cancellationToken);

        if (!context.Resolved)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Context = context,
                Message = context.Message
            };
        }

        if (IsCustomerPaymentsScreen(request, context))
        {
            return PlanCustomerPaymentsScreen(request, context, question);
        }

        var sector = context.Sector;
        var datasets = context.Datasets;

        if (question.Length == 0)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Message = "Ask a question about the current dashboard version or use one of its generated examples."
            };
        }

        if (datasets.Count == 0)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Message = $"Version {context.LayoutVersionId} has no approved assistant datasets."
            };
        }

        var isFixedExecutiveContext = datasets.Count == 1 &&
            string.Equals(datasets[0].ObjectKind, "executiveSuite", StringComparison.OrdinalIgnoreCase) &&
            context.LayoutVersionId is >= 213 and <= 217;

        var datasetScores = isFixedExecutiveContext
            ? new List<Scored<AssistantDatasetDto>>
            {
                new(datasets[0], 1d, new List<string> { "active version semantic contract" })
            }
            : ScoreDatasets(
                datasets,
                question,
                context.LayoutTitle,
                context.TemplateKeys);

        if (_options.DetectOutOfScopeQuestions && !isFixedExecutiveContext)
        {
            var globalScores = ScoreDatasets(
                _catalog.GetAllDatasets(),
                question,
                context.LayoutTitle,
                context.TemplateKeys);

            var globalBest = globalScores.FirstOrDefault();
            var scopedBestScore = datasetScores.FirstOrDefault()?.Score ?? 0;
            var globalIsOutside = globalBest != null &&
                datasets.All(dataset => !string.Equals(
                    dataset.Key,
                    globalBest.Value.Key,
                    StringComparison.OrdinalIgnoreCase));
            var globalSharesScopedDomain = globalBest != null &&
                datasets.Any(dataset => SharesSemanticDomain(dataset, globalBest.Value));

            if (globalIsOutside &&
                !globalSharesScopedDomain &&
                globalBest!.Score >= _options.OutOfScopeConfidence &&
                globalBest.Score - scopedBestScore >= 0.20)
            {
                return new AssistantPlanResponse
                {
                    Ready = false,
                    OutOfScope = true,
                    Sector = sector,
                    Context = context,
                    Message = $"{globalBest.Value.Title} is not available in {context.ContextLabel}. Open the dashboard version that contains that subject, then ask again."
                };
            }
        }

        AssistantDatasetDto? dataset;
        double datasetConfidence;

        if (isFixedExecutiveContext)
        {
            dataset = datasets[0];
            datasetConfidence = 1d;
        }
        else if (!string.IsNullOrWhiteSpace(request.DatasetKey))
        {
            dataset = _catalog.FindDataset(datasets, request.DatasetKey!);
            if (dataset == null)
            {
                return new AssistantPlanResponse
                {
                    Ready = false,
                    OutOfScope = true,
                    Sector = sector,
                    Context = context,
                    Message = "The selected data source is not part of the current dashboard version."
                };
            }

            datasetConfidence = 1;
            datasetScores = new List<Scored<AssistantDatasetDto>>
            {
                new(dataset, 1, new List<string>())
            };
        }
        else if (datasets.Count == 1)
        {
            dataset = datasets[0];
            datasetConfidence = 1;
            datasetScores = new List<Scored<AssistantDatasetDto>>
            {
                new(dataset, 1, new List<string> { context.ContextLabel })
            };
        }
        else
        {
            dataset = datasetScores.FirstOrDefault()?.Value;
            datasetConfidence = datasetScores.FirstOrDefault()?.Score ?? 0;
        }

        if (dataset == null)
        {
            return Clarify(
                sector,
                "dataset",
                "Which data source on this screen should answer the question?",
                datasets.Take(6).Select(item => new AssistantChoiceDto
                {
                    Label = item.Title,
                    Value = item.Key,
                    Detail = item.Description,
                    Confidence = 0
                }),
                context: context);
        }

        var secondDatasetScore = datasetScores.Skip(1).FirstOrDefault()?.Score ?? 0;
        var closeDatasetTie = secondDatasetScore > 0 && datasetConfidence - secondDatasetScore < 0.12;
        if (!isFixedExecutiveContext &&
            string.IsNullOrWhiteSpace(request.DatasetKey) &&
            datasets.Count > 1 &&
            (datasetConfidence < _options.MinimumDatasetConfidence || closeDatasetTie))
        {
            return Clarify(
                sector,
                "dataset",
                "More than one source on this screen fits the wording. Pick one so the result remains deterministic.",
                datasetScores.Take(5).Select(item => new AssistantChoiceDto
                {
                    Label = item.Value.Title,
                    Value = item.Value.Key,
                    Detail = item.Value.Description,
                    Confidence = Math.Round(item.Score, 3)
                }),
                context: context);
        }

        IReadOnlyList<AssistantColumnDto> columns;
        try
        {
            columns = await _catalog.GetColumnsAsync(dataset, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dashboard assistant metadata load failed for {Connection}:{Schema}.{Object}.",
                dataset.ConnectionName,
                dataset.Schema,
                dataset.Object);

            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Dataset = dataset,
                Message = ex.Message
            };
        }

        var isExecutiveSuite = string.Equals(
            dataset.ObjectKind,
            "executiveSuite",
            StringComparison.OrdinalIgnoreCase);

        var aggregation = ResolveAggregation(question);
        if (isExecutiveSuite && aggregation.Equals("Count", StringComparison.OrdinalIgnoreCase))
        {
            // In an executive semantic model, phrases such as "how many total
            // E-Bill customers" refer to a named business measure, not a count
            // of raw source rows.
            aggregation = "Sum";
        }

        var numericColumns = columns.Where(column => column.Category == "measure").ToList();
        var dateColumns = columns.Where(column => column.Category == "date").ToList();
        var dimensionColumns = columns.Where(column => column.Category == "dimension").ToList();

        AssistantColumnDto? measure = null;
        var measureConfidence = 1d;
        List<Scored<AssistantColumnDto>> measureScores = new();

        if (!aggregation.Equals("Count", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.Measure))
            {
                var requestedMeasure = NormalizeText(request.Measure);
                measure = numericColumns.FirstOrDefault(column =>
                    string.Equals(column.Name, request.Measure, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeText(column.Label), requestedMeasure, StringComparison.Ordinal) ||
                    column.Aliases.Any(alias =>
                        string.Equals(NormalizeText(alias), requestedMeasure, StringComparison.Ordinal)));
            }
            else
            {
                measure = ResolveVersionBoundMeasure(
                    context.LayoutVersionId,
                    question,
                    numericColumns);

                if (measure != null)
                {
                    measureConfidence = 1d;
                    measureScores = new List<Scored<AssistantColumnDto>>
                    {
                        new(measure, 1d, new List<string> { "version semantic contract" })
                    };
                }
                else
                {
                    measureScores = ScoreColumns(numericColumns, question, true, dataset);
                    measure = measureScores.FirstOrDefault()?.Value;
                    measureConfidence = measureScores.FirstOrDefault()?.Score ?? 0;
                }
            }

            if (isExecutiveSuite &&
                string.IsNullOrWhiteSpace(request.Measure) &&
                measureConfidence < 0.30)
            {
                var defaultMeasure = numericColumns
                    .OrderByDescending(column => column.IsDefault)
                    .ThenByDescending(column => column.SemanticPriority)
                    .FirstOrDefault();

                if (defaultMeasure != null && defaultMeasure.IsDefault)
                {
                    measure = defaultMeasure;
                    measureConfidence = 0.78;
                }
            }

            if (measure == null && numericColumns.Count == 1)
            {
                measure = numericColumns[0];
                measureConfidence = 0.7;
            }

            if (measure == null)
            {
                if (numericColumns.Count == 0)
                {
                    aggregation = "Count";
                }
                else
                {
                    return Clarify(
                        sector,
                        "measure",
                        $"Which measure from {dataset.Title} should be calculated?",
                        numericColumns.Take(8).Select(column => new AssistantChoiceDto
                        {
                            Label = column.Label,
                            Value = column.Name,
                            Detail = column.DataType,
                            Confidence = 0
                        }),
                        dataset,
                        context);
                }
            }
            else
            {
                var secondMeasureScore = measureScores.Skip(1).FirstOrDefault()?.Score ?? 0;
                var closeMeasureTie = secondMeasureScore > 0 &&
                    measureConfidence < 0.90 &&
                    measureConfidence - secondMeasureScore < 0.11;
                if (string.IsNullOrWhiteSpace(request.Measure) &&
                    numericColumns.Count > 1 &&
                    (measureConfidence < _options.MinimumMeasureConfidence || closeMeasureTie))
                {
                    return Clarify(
                        sector,
                        "measure",
                        $"I need the exact measure before querying {dataset.Title}.",
                        measureScores.Take(6).Select(item => new AssistantChoiceDto
                        {
                            Label = item.Value.Label,
                            Value = item.Value.Name,
                            Detail = item.Value.DataType,
                            Confidence = Math.Round(item.Score, 3)
                        }),
                        dataset,
                        context);
                }
            }
        }

        var dimensions = ResolveDimensions(
            question,
            request.Dimensions,
            dimensionColumns,
            dateColumns);
        var semanticFilters = ResolveSemanticFilters(dataset, question);

        if (isExecutiveSuite && measure != null && measure.AllowedDimensions.Count > 0)
        {
            var unsupportedDimensions = dimensions
                .Where(column => !measure.AllowedDimensions.Contains(
                    column.Name,
                    StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unsupportedDimensions.Count > 0)
            {
                var supportedLabels = columns
                    .Where(column => column.Category is "dimension" or "date")
                    .Where(column => measure.AllowedDimensions.Contains(
                        column.Name,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(column => column.Label)
                    .ToList();

                return new AssistantPlanResponse
                {
                    Ready = false,
                    Sector = sector,
                    Context = context,
                    Dataset = dataset,
                    Message = $"{measure.Label} cannot be grouped by {string.Join(" and ", unsupportedDimensions.Select(column => column.Label))} on this screen. Supported grouping: {string.Join(", ", supportedLabels)}."
                };
            }
        }

        var dateField = ResolveDateField(question, dateColumns);
        var period = ResolvePeriod(question, request.PeriodMode, dateField != null);
        var comparisons = ResolveComparisons(question);

        if (period.Mode == "unresolved-temporal")
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Dataset = dataset,
                Message = "I recognized a time-range instruction, but not a complete boundary. Include the full month and year, date, quarter, or year—for example, ‘since March 2026’ or ‘between March 2026 and June 2026’."
            };
        }

        if (comparisons.Count > 0 && dateField == null)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Dataset = dataset,
                Message = $"{dataset.Title} has no date field that can support {string.Join(" and ", comparisons)} comparison."
            };
        }

        if (dateField != null && comparisons.Count > 0)
        {
            dimensions.RemoveAll(column =>
                string.Equals(column.Name, dateField.Name, StringComparison.OrdinalIgnoreCase));
            dimensions.Insert(0, dateField);
        }

        var visualResolution = ResolveVisualType(
            question,
            request.ChartType,
            dimensions,
            dateField,
            measure,
            aggregation);

        if (visualResolution.Ambiguous)
        {
            return Clarify(
                sector,
                "visual",
                "You named more than one visual. Pick the exact output so the layout is deterministic.",
                visualResolution.Candidates.Select(type => new AssistantChoiceDto
                {
                    Label = VisualLabel(type),
                    Value = type,
                    Detail = VisualDescription(type),
                    Confidence = 1
                }),
                dataset,
                context);
        }

        var chartType = visualResolution.Type;

        if (dateField != null &&
            chartType is "line" or "area" or "combo" &&
            dimensions.All(column =>
                !string.Equals(column.Name, dateField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            dimensions.Insert(0, dateField);
        }

        if (chartType == "metric" && dimensions.Count > 0)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Dataset = dataset,
                Message = $"A metric card can show only one aggregate point and cannot preserve grouping by {string.Join(" and ", dimensions.Select(column => column.Label))}. Use a bar chart, line chart, matrix, or table."
            };
        }

        if (chartType == "combo" && dateField == null)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = sector,
                Context = context,
                Dataset = dataset,
                Message = "A bar-and-line chart requires a date field for the shared horizontal axis."
            };
        }

        if (isExecutiveSuite &&
            string.Equals(measure?.Name, "total_ebill_customers", StringComparison.OrdinalIgnoreCase) &&
            period.From.HasValue &&
            period.ToExclusive.HasValue &&
            period.ToExclusive.Value > period.From.Value.AddMonths(1) &&
            string.IsNullOrWhiteSpace(request.ChartType) &&
            period.Mode is "since" or "explicit-range" or "after" or "before" or "through" or "last-n-months" or "last-n-years" or "last-n-quarters")
        {
            return Clarify(
                sector,
                "visual",
                "Total E-Bill Customers is a monthly snapshot, so adding monthly snapshots would be misleading. Show the latest snapshot in the range or the monthly trend?",
                new[]
                {
                    new AssistantChoiceDto
                    {
                        Label = "Latest snapshot",
                        Value = "metric",
                        Detail = "One value: the latest completed month in the requested range.",
                        Confidence = 1
                    },
                    new AssistantChoiceDto
                    {
                        Label = "Monthly trend",
                        Value = "line",
                        Detail = "One snapshot per completed month; values are not summed together.",
                        Confidence = 1
                    }
                },
                dataset,
                context);
        }

        AssistantAggregateRequestDto? requestDto = null;
        AssistantExecutiveRequestDto? executiveRequest = null;
        var executionMode = "aggregate";

        if (isExecutiveSuite)
        {
            executionMode = "executiveSuite";
            executiveRequest = new AssistantExecutiveRequestDto
            {
                LayoutVersionId = context.LayoutVersionId,
                Suite = context.LayoutVersionId.ToString(CultureInfo.InvariantCulture),
                Measure = measure?.Name ?? string.Empty,
                Aggregation = aggregation,
                Dimensions = dimensions.Select(column => column.Name).ToList(),
                Filters = semanticFilters,
                PeriodMode = period.Mode,
                FromUtc = period.From?.ToString("O", CultureInfo.InvariantCulture),
                ToUtc = period.ToExclusive?.ToString("O", CultureInfo.InvariantCulture),
                ChartType = chartType,
                Question = request.Question
            };
        }
        else
        {
            requestDto = BuildAggregateRequest(
            dataset,
            aggregation,
            measure,
            dimensions,
            dateField,
            period,
                comparisons,
                chartType);
        }

        var valueFormat = ResolveValueFormat(measure, aggregation);
        var title = BuildTitle(
            dataset,
            aggregation,
            measure,
            dimensions,
            semanticFilters,
            period,
            comparisons);

        var matchedTerms = datasetScores.FirstOrDefault()?.Matches ?? new List<string>();
        if (measureScores.Count > 0)
        {
            matchedTerms.AddRange(measureScores[0].Matches);
        }

        var assumptions = new List<string>();
        assumptions.AddRange(period.Assumptions);
        foreach (var filter in semanticFilters)
        {
            assumptions.Add($"Filtered {filter.Key} to {string.Join(", ", filter.Value.Values.Where(value => !string.IsNullOrWhiteSpace(value)))}.");
        }
        if (dimensions.Count == 0)
            assumptions.Add("No grouping was requested; the result is a single aggregate.");
        if (aggregation == "Count")
            assumptions.Add("Count means the number of source rows after filters.");
        assumptions.Add($"Visual request resolved to {VisualLabel(chartType)}.");

        var confidence = Math.Clamp(
            (datasetConfidence * 0.55) +
            (measureConfidence * 0.30) +
            (period.Confidence * 0.15),
            0,
            1);

        return new AssistantPlanResponse
        {
            Ready = true,
            Sector = sector,
            Context = context,
            Confidence = Math.Round(confidence, 3),
            Message = isExecutiveSuite
                ? $"Plan validated against the normalized payload that renders {context.ContextLabel}."
                : $"Plan validated against {context.ContextLabel} and SQL metadata.",
            ExecutionMode = executionMode,
            Dataset = dataset,
            AggregateRequest = requestDto,
            ExecutiveRequest = executiveRequest,
            Visual = new AssistantVisualPlanDto
            {
                Type = chartType,
                Title = title,
                Subtitle = $"{dataset.Title} · {period.Label}",
                ValueFormat = valueFormat,
                DateField = isExecutiveSuite && dateField != null ? "period" : dateField?.Name,
                MeasureField = isExecutiveSuite
                    ? "Value"
                    : aggregation == "Count" ? "Count" : measure?.Name,
                DimensionFields = dimensions.Select(column => column.Name).ToList(),
                Comparisons = comparisons,
                ReportingPeriodLabel = period.Label
            },
            Plan = new AssistantSemanticPlanDto
            {
                LayoutVersionId = context.LayoutVersionId,
                LayoutTitle = context.LayoutTitle,
                DatasetKey = dataset.Key,
                DatasetTitle = dataset.Title,
                Aggregation = aggregation,
                Measure = aggregation == "Count" ? null : measure?.Name,
                Dimensions = dimensions.Select(column => column.Name).ToList(),
                Filters = semanticFilters,
                DateField = dateField?.Name,
                PeriodMode = period.Mode,
                PeriodLabel = period.Label,
                Comparisons = comparisons,
                Assumptions = assumptions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedTerms = matchedTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            }
        };
    }

    private static bool IsCustomerPaymentsScreen(
        AssistantQueryRequest request,
        AssistantVersionContextDto context)
    {
        if (request.LayoutVersionId == 217 || context.LayoutVersionId == 217)
        {
            return true;
        }

        if ((request.CurrentTemplateKeys ?? new List<string>()).Any(value =>
            string.Equals(value, "executive-customer-payments", StringComparison.OrdinalIgnoreCase)) ||
            context.TemplateKeys.Any(value =>
                string.Equals(value, "executive-customer-payments", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        static bool IsExactCustomerPaymentsTitle(string? value)
            => NormalizeText(value) == "customer payments";

        return IsExactCustomerPaymentsTitle(request.LayoutVersionTitle) ||
               IsExactCustomerPaymentsTitle(request.CurrentLayoutTitle) ||
               IsExactCustomerPaymentsTitle(context.LayoutTitle);
    }

    private static AssistantPlanResponse PlanCustomerPaymentsScreen(
        AssistantQueryRequest request,
        AssistantVersionContextDto incomingContext,
        string question)
    {
        const string templateKey = "executive-customer-payments";
        var dataset = new AssistantDatasetDto
        {
            Key = templateKey,
            Sector = "cx",
            Title = "Customer Payments",
            Description = "Customer Payments · fixed executive semantic contract",
            TemplateKey = templateKey,
            TemplateKeys = new List<string> { templateKey },
            SourceAlias = templateKey,
            ConnectionName = "build",
            Schema = "dbo",
            Object = templateKey,
            ObjectKind = "executiveSuite",
            PayloadMode = "executiveSuite",
            Role = "csr-page",
            Aliases = new List<string>
            {
                templateKey,
                "Customer Payments",
                "payments",
                "collections",
                "transactions",
                "credit card"
            }
        };

        var context = new AssistantVersionContextDto
        {
            Resolved = true,
            LayoutVersionId = 217,
            LayoutTitle = "Customer Payments",
            ContextLabel = "Version 217 · Customer Payments",
            ContextDetail = "Current screen only · fixed semantic contract executive-customer-payments",
            Sector = "cx",
            Message = "Semantic scope locked to executive-customer-payments; raw SQL metadata is disabled.",
            TemplateKeys = new List<string> { templateKey },
            DatasetKeys = new List<string> { templateKey },
            Datasets = new List<AssistantDatasetDto> { dataset }
        };

        var fields = DashboardAssistantSemanticCatalog.GetFields(templateKey);
        var measures = fields.Where(field => field.Category == "measure").ToList();
        var dates = fields.Where(field => field.Category == "date").ToList();
        var dimensionsAvailable = fields.Where(field => field.Category == "dimension").ToList();
        var dateField = dates.FirstOrDefault();

        var transactionLanguage = ContainsAny(
            question,
            "transaction",
            "transactions",
            "transaction count",
            "number of transactions",
            "count of transactions",
            "payment count",
            "number of payments",
            "count of payments",
            "payment volume");

        var measureName = transactionLanguage ? "transactions" : "payment_value";
        var measure = measures.First(field =>
            string.Equals(field.Name, measureName, StringComparison.OrdinalIgnoreCase));

        var dimensions = ResolveDimensions(
            question,
            request.Dimensions,
            dimensionsAvailable,
            dates);
        var filters = ResolveSemanticFilters(dataset, question);
        var period = ResolvePeriod(question, request.PeriodMode, dateField != null);

        if (period.Mode == "unresolved-temporal")
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = "cx",
                Context = context,
                Dataset = dataset,
                Message = "I recognized a time-range instruction, but not a complete boundary. Include the full month and year, date, quarter, or year."
            };
        }

        var unsupported = dimensions
            .Where(dimension => !measure.AllowedDimensions.Contains(
                dimension.Name,
                StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unsupported.Count > 0)
        {
            return new AssistantPlanResponse
            {
                Ready = false,
                Sector = "cx",
                Context = context,
                Dataset = dataset,
                Message = $"{measure.Label} cannot be grouped by {string.Join(" and ", unsupported.Select(value => value.Label))} on this screen."
            };
        }

        var visualResolution = ResolveVisualType(
            question,
            request.ChartType,
            dimensions,
            dateField,
            measure,
            "Sum");
        if (visualResolution.Ambiguous)
        {
            return Clarify(
                "cx",
                "visual",
                "You named more than one visual. Pick the exact output.",
                visualResolution.Candidates.Select(type => new AssistantChoiceDto
                {
                    Label = VisualLabel(type),
                    Value = type,
                    Detail = VisualDescription(type),
                    Confidence = 1
                }),
                dataset,
                context);
        }

        // No grouping means one aggregate point. Never draw a meaningless graph.
        var chartType = dimensions.Count == 0 ? "metric" : visualResolution.Type;
        var executiveRequest = new AssistantExecutiveRequestDto
        {
            LayoutVersionId = 217,
            Suite = "217",
            Measure = measure.Name,
            Aggregation = "Sum",
            Dimensions = dimensions.Select(value => value.Name).ToList(),
            Filters = filters,
            PeriodMode = period.Mode,
            FromUtc = period.From?.ToString("O", CultureInfo.InvariantCulture),
            ToUtc = period.ToExclusive?.ToString("O", CultureInfo.InvariantCulture),
            ChartType = chartType,
            Question = request.Question
        };

        var title = BuildTitle(
            dataset,
            "Sum",
            measure,
            dimensions,
            filters,
            period,
            Array.Empty<string>());
        var assumptions = period.Assumptions.ToList();
        assumptions.Add("Version 217 uses the fixed Customer Payments semantic contract.");
        foreach (var filter in filters)
        {
            assumptions.Add($"Filtered {filter.Key} to {string.Join(", ", filter.Value.Values.Where(value => !string.IsNullOrWhiteSpace(value)))}.");
        }
        if (dimensions.Count == 0)
        {
            assumptions.Add("No grouping was requested; the result is one aggregate value and no graph is rendered.");
        }

        return new AssistantPlanResponse
        {
            Ready = true,
            Sector = "cx",
            Confidence = 1,
            Message = "Plan validated against Version 217 Customer Payments facts and dimensions. Raw Customer Payments Daily columns were not consulted.",
            ExecutionMode = "executiveSuite",
            Context = context,
            Dataset = dataset,
            ExecutiveRequest = executiveRequest,
            Visual = new AssistantVisualPlanDto
            {
                Type = chartType,
                Title = title,
                Subtitle = $"Customer Payments · {period.Label}",
                ValueFormat = measure.ValueFormat,
                DateField = dateField?.Name,
                MeasureField = measure.Name,
                DimensionFields = dimensions.Select(value => value.Name).ToList(),
                ReportingPeriodLabel = period.Label
            },
            Plan = new AssistantSemanticPlanDto
            {
                LayoutVersionId = 217,
                LayoutTitle = "Customer Payments",
                DatasetKey = templateKey,
                DatasetTitle = "Customer Payments",
                Aggregation = "Sum",
                Measure = measure.Name,
                Dimensions = dimensions.Select(value => value.Name).ToList(),
                Filters = filters,
                DateField = dateField?.Name,
                PeriodMode = period.Mode,
                PeriodLabel = period.Label,
                Assumptions = assumptions,
                MatchedTerms = new List<string>
                {
                    "fixed Version 217 contract",
                    measure.Label
                }
            }
        };
    }

    public async Task<IReadOnlyList<AssistantSuggestionDto>> SuggestAsync(
        long? layoutVersionId,
        IReadOnlyCollection<string>? currentTemplateKeys,
        string? datasetKey,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var context = await _context.ResolveAsync(
            layoutVersionId,
            currentTemplateKeys,
            cancellationToken);

        if (!context.Resolved) return Array.Empty<AssistantSuggestionDto>();

        var query = NormalizeText(prefix);
        var suggestions = new List<AssistantSuggestionDto>();

        var phraseSuggestions = new[]
        {
            ("period", "last completed month", "last completed month"),
            ("period", "since March 2026", "since a month"),
            ("period", "during March 2026", "during a month"),
            ("period", "before March 2026", "before a month"),
            ("period", "after March 2026", "after a month"),
            ("period", "between March 2026 and June 2026", "between two periods"),
            ("period", "from March 2026 through June 2026", "from / through"),
            ("period", "year to date through last month", "YTD through last completed month"),
            ("comparison", "month over month", "MoM"),
            ("comparison", "year over year", "YoY"),
            ("visual", "as a line chart", "line chart"),
            ("visual", "as a bar chart", "bar chart"),
            ("visual", "as a bar chart with a total line", "bar + line"),
            ("visual", "as a matrix", "matrix"),
            ("visual", "as a stacked bar chart", "stacked bar"),
            ("visual", "as a table", "table"),
            ("visual", "as a metric card", "metric card")
        };

        foreach (var item in phraseSuggestions)
        {
            if (query.Length == 0 || NormalizeText(item.Item2).Contains(query, StringComparison.Ordinal))
            {
                suggestions.Add(new AssistantSuggestionDto
                {
                    Kind = item.Item1,
                    Label = item.Item3,
                    InsertText = item.Item2
                });
            }
        }

        var datasets = context.Datasets;
        if (datasets.Count > 1)
        {
            foreach (var dataset in datasets)
            {
                if (query.Length > 0 && !DatasetSearchText(dataset).Contains(query, StringComparison.Ordinal))
                    continue;

                suggestions.Add(new AssistantSuggestionDto
                {
                    Kind = "dataset",
                    Label = dataset.Title,
                    InsertText = dataset.Title,
                    Value = dataset.Key,
                    Detail = dataset.Object
                });
            }
        }

        var selectedDataset = !string.IsNullOrWhiteSpace(datasetKey)
            ? _catalog.FindDataset(datasets, datasetKey!)
            : datasets.Count == 1 ? datasets[0] : null;

        if (selectedDataset != null)
        {
            try
            {
                var columns = await _catalog.GetColumnsAsync(selectedDataset, cancellationToken);
                foreach (var column in columns
                    .OrderBy(column => column.Category == "measure" ? 0 : column.Category == "date" ? 1 : 2)
                    .ThenByDescending(column => column.SemanticPriority)
                    .ThenBy(column => column.Label, StringComparer.OrdinalIgnoreCase))
                {
                    var searchable = NormalizeText(string.Join(" ", column.Aliases));
                    if (query.Length > 0 &&
                        !searchable.Contains(query, StringComparison.Ordinal) &&
                        !column.Aliases.Any(alias => ContainsWholePhrase(query, NormalizeText(alias))))
                    {
                        continue;
                    }

                    suggestions.Add(new AssistantSuggestionDto
                    {
                        Kind = column.Category,
                        Label = column.Label,
                        InsertText = column.Category is "dimension" or "date"
                            ? $"by {column.Label}"
                            : column.Label,
                        Value = column.Name,
                        Detail = column.Category == "measure"
                            ? $"Fact · {column.ValueFormat} · {column.DefaultAggregation}"
                            : column.Category == "date"
                                ? "Time dimension"
                                : "Dimension"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Assistant suggestions could not load version-scoped metadata.");
            }
        }

        return suggestions
            .GroupBy(item => $"{item.Kind}|{item.Label}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => SuggestionKindPriority(item.Kind))
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_options.SuggestionLimit, 4, 30))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetExamplesAsync(
        AssistantVersionContextDto context,
        CancellationToken cancellationToken)
    {
        var examples = new List<string>();

        foreach (var dataset in context.Datasets.Take(3))
        {
            try
            {
                var columns = await _catalog.GetColumnsAsync(dataset, cancellationToken);
                var measures = columns
                    .Where(column => column.Category == "measure")
                    .OrderByDescending(column => column.IsDefault)
                    .ThenByDescending(column => column.SemanticPriority)
                    .Take(3)
                    .ToList();
                var date = columns.FirstOrDefault(column => column.Category == "date");
                var dimensions = columns
                    .Where(column => column.Category == "dimension")
                    .OrderByDescending(column => column.SemanticPriority)
                    .ToList();

                foreach (var measure in measures)
                {
                    examples.Add($"Show {measure.Label} for the last completed month as a metric card");

                    if (date != null &&
                        (measure.AllowedDimensions.Count == 0 ||
                         measure.AllowedDimensions.Contains(date.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        examples.Add($"Show {measure.Label} by {date.Label} for the last 12 completed months as a line chart");
                    }

                    var compatibleDimension = dimensions.FirstOrDefault(dimension =>
                        measure.AllowedDimensions.Count == 0 ||
                        measure.AllowedDimensions.Contains(dimension.Name, StringComparer.OrdinalIgnoreCase));
                    if (compatibleDimension != null)
                    {
                        examples.Add($"Show {measure.Label} by {compatibleDimension.Label} for the last completed month as a bar chart");
                    }

                    if (examples.Count >= 7) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Assistant examples could not load metadata for {DatasetKey}.",
                    dataset.Key);
            }

            if (examples.Count >= 7) break;
        }

        if (examples.Count == 0)
        {
            examples.Add("Show the default fact for the last completed month as a metric card");
            examples.Add("Show the default fact for the last 12 completed months as a line chart");
        }

        return examples
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private AssistantAggregateRequestDto BuildAggregateRequest(
        AssistantDatasetDto dataset,
        string aggregation,
        AssistantColumnDto? measure,
        List<AssistantColumnDto> dimensions,
        AssistantColumnDto? dateField,
        PeriodResolution period,
        List<string> comparisons,
        string chartType)
    {
        var distinctDimensions = dimensions
            .DistinctBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<string>();
        var cols = new List<string>();

        if (chartType == "matrix")
        {
            var nonDates = distinctDimensions
                .Where(column => column.Category != "date")
                .Select(column => column.Name)
                .ToList();
            var dates = distinctDimensions
                .Where(column => column.Category == "date")
                .Select(column => column.Name)
                .ToList();

            rows.AddRange(nonDates);
            cols.AddRange(dates);

            if (rows.Count == 0 && cols.Count > 0)
            {
                rows.Add(cols[0]);
                cols.RemoveAt(0);
            }
            if (cols.Count == 0 && rows.Count > 1)
            {
                cols.Add(rows[^1]);
                rows.RemoveAt(rows.Count - 1);
            }
        }
        else if (chartType == "combo")
        {
            var date = distinctDimensions.FirstOrDefault(column => column.Category == "date");
            if (date != null) rows.Add(date.Name);
            cols.AddRange(distinctDimensions
                .Where(column => date == null || !string.Equals(column.Name, date.Name, StringComparison.OrdinalIgnoreCase))
                .Select(column => column.Name));
        }
        else
        {
            rows.AddRange(distinctDimensions.Select(column => column.Name));
        }

        var dateGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dateField != null &&
            (rows.Contains(dateField.Name, StringComparer.OrdinalIgnoreCase) ||
             cols.Contains(dateField.Name, StringComparer.OrdinalIgnoreCase)))
        {
            dateGroups[dateField.Name] = period.Grouping;
        }

        var filters = new Dictionary<string, AssistantFilterSpecDto>(StringComparer.OrdinalIgnoreCase);
        if (dateField != null && period.From.HasValue && period.ToExclusive.HasValue)
        {
            filters[dateField.Name] = new AssistantFilterSpecDto
            {
                Mode = "range",
                FromUtc = period.From.Value.ToString("O", CultureInfo.InvariantCulture),
                ToUtc = period.ToExclusive.Value.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        return new AssistantAggregateRequestDto
        {
            ConnectionName = dataset.ConnectionName,
            Schema = dataset.Schema,
            Obj = dataset.Object,
            Rows = rows,
            Cols = cols,
            Values = aggregation == "Count" || measure == null
                ? new List<string>()
                : new List<string> { measure.Name },
            Agg = aggregation,
            DateGroups = dateGroups,
            Filters = filters,
            MaxCells = Math.Clamp(_options.MaxRows, 100, 50000)
        };
    }

    private static Dictionary<string, AssistantFilterSpecDto> ResolveSemanticFilters(
        AssistantDatasetDto dataset,
        string question)
    {
        var filters = new Dictionary<string, AssistantFilterSpecDto>(StringComparer.OrdinalIgnoreCase);
        var templateKey = dataset.TemplateKeys.FirstOrDefault(value =>
            value.StartsWith("executive-", StringComparison.OrdinalIgnoreCase))
            ?? dataset.TemplateKey;

        foreach (var dimensionName in new[] { "payment_type" })
        {
            var valueAliases = DashboardAssistantSemanticCatalog.GetDimensionValueAliases(
                templateKey,
                dimensionName);
            if (valueAliases.Count == 0) continue;

            var matchedValues = valueAliases
                .Where(pair => pair.Value.Any(alias =>
                    ContainsWholePhrase(question, NormalizeText(alias))))
                .Select(pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchedValues.Count > 0)
            {
                filters[dimensionName] = new AssistantFilterSpecDto
                {
                    Mode = "in",
                    Values = matchedValues.Cast<string?>().ToList()
                };
            }
        }

        return filters;
    }

    private static bool SharesSemanticDomain(
        AssistantDatasetDto left,
        AssistantDatasetDto right)
    {
        var leftDomains = InferSemanticDomains(left);
        var rightDomains = InferSemanticDomains(right);
        return leftDomains.Overlaps(rightDomains);
    }

    private static HashSet<string> InferSemanticDomains(AssistantDatasetDto dataset)
    {
        var templateText = NormalizeText(string.Join(
            " ",
            dataset.TemplateKeys.Append(dataset.TemplateKey)));
        var explicitDomain = templateText switch
        {
            var value when value.Contains("customer payments", StringComparison.Ordinal) => "payments",
            var value when value.Contains("final bill", StringComparison.Ordinal) => "finalbill",
            var value when value.Contains("ebill", StringComparison.Ordinal) => "ebill",
            var value when value.Contains("ar portfolio", StringComparison.Ordinal) => "aging",
            var value when value.Contains("disconnect", StringComparison.Ordinal) => "disconnects",
            var value when value.Contains("call", StringComparison.Ordinal) ||
                           value.Contains("genesys", StringComparison.Ordinal) => "calls",
            var value when value.Contains("ticket", StringComparison.Ordinal) ||
                           value.Contains("service desk", StringComparison.Ordinal) => "tickets",
            var value when value.Contains("security", StringComparison.Ordinal) ||
                           value.Contains("ocsf", StringComparison.Ordinal) => "security",
            _ => string.Empty
        };

        if (explicitDomain.Length > 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                explicitDomain
            };
        }

        var searchText = DatasetSearchText(dataset);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in DomainAliases)
        {
            if (domain.Value.Any(alias =>
                ContainsWholePhrase(searchText, NormalizeText(alias))))
            {
                domains.Add(domain.Key);
            }
        }

        return domains;
    }

    private static List<Scored<AssistantDatasetDto>> ScoreDatasets(
        IReadOnlyList<AssistantDatasetDto> datasets,
        string question,
        string? currentLayoutTitle,
        IReadOnlyList<string> currentTemplateKeys)
    {
        var questionTokens = Tokenize(question);
        var currentContext = NormalizeText(string.Join(" ",
            new[] { currentLayoutTitle ?? "" }.Concat(currentTemplateKeys ?? Array.Empty<string>())));

        var scores = new List<Scored<AssistantDatasetDto>>();
        foreach (var dataset in datasets)
        {
            var matches = new List<string>();
            var score = 0d;
            var aliases = dataset.Aliases
                .Concat(ExpandDomainAliases(dataset))
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var alias in aliases)
            {
                var normalizedAlias = NormalizeText(alias);
                if (normalizedAlias.Length < 2) continue;

                if (question.Contains(normalizedAlias, StringComparison.Ordinal))
                {
                    score += normalizedAlias.Contains(' ') ? 0.55 : 0.30;
                    matches.Add(alias);
                }

                var aliasTokens = Tokenize(normalizedAlias);
                var overlap = aliasTokens.Intersect(questionTokens).Count();
                if (overlap > 0)
                {
                    score += Math.Min(0.34, overlap * 0.10);
                }
            }

            if (currentContext.Length > 0)
            {
                var contextHit = aliases.Any(alias =>
                {
                    var normalizedAlias = NormalizeText(alias);
                    return normalizedAlias.Length > 2 &&
                           currentContext.Contains(normalizedAlias, StringComparison.Ordinal);
                });
                if (contextHit) score += 0.18;
            }

            score = Math.Min(1, score);
            scores.Add(new Scored<AssistantDatasetDto>(dataset, score, matches));
        }

        return scores
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Value.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AssistantColumnDto? ResolveVersionBoundMeasure(
        long layoutVersionId,
        string question,
        IReadOnlyList<AssistantColumnDto> measures)
    {
        if (layoutVersionId != 217 || measures.Count == 0) return null;

        var normalized = NormalizeText(question);
        var tokens = Tokenize(normalized);

        var asksForTransactions =
            tokens.Contains("transaction") ||
            tokens.Contains("transactions") ||
            ContainsAny(normalized,
                "transaction count",
                "number of transactions",
                "count of transactions",
                "payment count",
                "number of payments",
                "count of payments",
                "payment volume");

        if (asksForTransactions)
        {
            return measures.FirstOrDefault(column =>
                string.Equals(column.Name, "transactions", StringComparison.OrdinalIgnoreCase));
        }

        var asksForPaymentValue =
            tokens.Contains("amount") ||
            tokens.Contains("amounts") ||
            tokens.Contains("value") ||
            tokens.Contains("paid") ||
            tokens.Contains("collection") ||
            tokens.Contains("collections") ||
            ContainsAny(normalized,
                "how much",
                "payment value",
                "payment amount",
                "amount paid",
                "was paid",
                "were paid",
                "amount collected",
                "money collected",
                "credit card",
                "debit card",
                "online banking",
                "pre authorized payment",
                "preauthorized payment",
                "electronic funds transfer",
                "credit cards",
                "debit cards",
                "card payment",
                "cash",
                "cheque",
                "check");

        if (asksForPaymentValue)
        {
            return measures.FirstOrDefault(column =>
                string.Equals(column.Name, "payment_value", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static List<Scored<AssistantColumnDto>> ScoreColumns(
        IReadOnlyList<AssistantColumnDto> columns,
        string question,
        bool measureMode,
        AssistantDatasetDto dataset)
    {
        var normalizedQuestion = NormalizeText(question);
        var questionTokens = Tokenize(normalizedQuestion);
        var list = new List<Scored<AssistantColumnDto>>();

        foreach (var column in columns)
        {
            var score = 0d;
            var matches = new List<string>();

            foreach (var alias in column.Aliases)
            {
                var normalizedAlias = NormalizeText(alias);
                if (normalizedAlias.Length < 2) continue;

                var aliasTokens = Tokenize(normalizedAlias);
                double aliasScore;

                if (string.Equals(normalizedQuestion, normalizedAlias, StringComparison.Ordinal))
                {
                    aliasScore = 1.0;
                }
                else if (ContainsWholePhrase(normalizedQuestion, normalizedAlias))
                {
                    aliasScore = aliasTokens.Count <= 1 ? 0.86 : 0.94;
                }
                else if (aliasTokens.Count > 0)
                {
                    var overlap = aliasTokens.Intersect(questionTokens).Count();
                    var coverage = (double)overlap / aliasTokens.Count;
                    aliasScore = coverage >= 1
                        ? (aliasTokens.Count == 1 ? 0.72 : 0.82)
                        : coverage * 0.52;
                }
                else
                {
                    aliasScore = 0;
                }

                if (aliasScore > score)
                {
                    score = aliasScore;
                }

                if (aliasScore >= 0.72)
                {
                    matches.Add(alias);
                }
            }

            if (measureMode)
            {
                var name = NormalizeText(column.Name);

                if (questionTokens.Contains("transaction") ||
                    questionTokens.Contains("transactions"))
                {
                    if (ContainsAny(name, "transaction", "count")) score = Math.Max(score, 0.98);
                }

                if (ContainsAny(normalizedQuestion, "how many", "number of", "count of") &&
                    ContainsAny(name, "transaction", "count", "accounts", "customers", "volume"))
                {
                    score = Math.Max(score, 0.96);
                }

                if (questionTokens.Contains("amount") || questionTokens.Contains("amounts"))
                {
                    if (column.ValueFormat == "currency") score = Math.Max(score, 0.91);
                }

                if (questionTokens.Contains("value") || questionTokens.Contains("values"))
                {
                    if (ContainsAny(name, "value", "amount", "balance")) score = Math.Max(score, 0.90);
                }

                if ((questionTokens.Contains("collection") || questionTokens.Contains("collections")) &&
                    ContainsAny(name, "amount", "value", "payment", "paid", "balance"))
                {
                    score = Math.Max(score, 0.90);
                }

                if (questionTokens.Contains("balance") && name.Contains("balance"))
                    score = Math.Max(score, 0.94);
                if (questionTokens.Contains("rate") && ContainsAny(name, "rate", "percent", "pct", "ratio"))
                    score = Math.Max(score, 0.92);
                if (questionTokens.Contains("percent") && ContainsAny(name, "percent", "pct", "rate", "ratio"))
                    score = Math.Max(score, 0.92);

                // Priority is only a deterministic tie-breaker. It must never turn
                // an unrelated field into a match.
                if (score > 0)
                {
                    score = Math.Min(1, score + Math.Min(0.025, column.SemanticPriority / 10000d));
                }
            }

            list.Add(new Scored<AssistantColumnDto>(column, Math.Min(1, score), matches));
        }

        return list
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Value.SemanticPriority)
            .ThenBy(item => item.Value.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<AssistantColumnDto> ResolveDimensions(
        string question,
        IReadOnlyList<string> requestedDimensions,
        IReadOnlyList<AssistantColumnDto> dimensions,
        IReadOnlyList<AssistantColumnDto> dates)
    {
        var available = dimensions.Concat(dates).ToList();
        var resolved = new List<AssistantColumnDto>();

        foreach (var requested in requestedDimensions ?? Array.Empty<string>())
        {
            var requestedText = NormalizeText(requested);
            var column = available.FirstOrDefault(item =>
                string.Equals(item.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeText(item.Label), requestedText, StringComparison.Ordinal) ||
                item.Aliases.Any(alias =>
                    string.Equals(NormalizeText(alias), requestedText, StringComparison.Ordinal)));
            if (column != null) resolved.Add(column);
        }

        var groupingClauses = GroupingClauseRegex
            .Matches(question)
            .Select(match => NormalizeText(match.Groups["group"].Value))
            .Where(value => value.Length > 0)
            .ToList();

        foreach (var groupingText in groupingClauses)
        {
            foreach (var column in available)
            {
                var score = column.Aliases.Max(alias => PhraseScore(groupingText, alias));
                if (score >= 0.58 && resolved.All(item =>
                        !string.Equals(item.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    resolved.Add(column);
                }
            }
        }

        // Time-filter words such as "last month" do not imply a time-series
        // grouping. Add the date dimension only when the user explicitly asks
        // for a trend or repeated monthly points.
        if (groupingClauses.Count == 0 &&
            ContainsAny(question, "trend", "over time", "month by month",
                "each month", "monthly trend", "time series"))
        {
            var date = dates
                .OrderByDescending(column => DatePriority(column.Name))
                .FirstOrDefault();
            if (date != null && resolved.All(item =>
                    !string.Equals(item.Name, date.Name, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(date);
            }
        }

        return resolved.Take(4).ToList();
    }

    private static AssistantColumnDto? ResolveDateField(
        string question,
        IReadOnlyList<AssistantColumnDto> dateColumns)
    {
        if (dateColumns.Count == 0) return null;

        var explicitMatches = dateColumns
            .Select(column => new
            {
                Column = column,
                Score = column.Aliases.Max(alias => PhraseScore(question, alias))
            })
            .OrderByDescending(item => item.Score)
            .ToList();

        if (explicitMatches[0].Score >= 0.58) return explicitMatches[0].Column;

        return dateColumns
            .OrderByDescending(column => DatePriority(column.Name))
            .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static PeriodResolution ResolvePeriod(
        string question,
        string? requestedMode,
        bool hasDateField)
    {
        if (!hasDateField)
        {
            return new PeriodResolution(
                "all",
                "All available data",
                null,
                null,
                "Date",
                false,
                1,
                new List<string>());
        }

        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var currentMonthStart = new DateTime(today.Year, today.Month, 1);
        var lastCompletedMonth = currentMonthStart.AddMonths(-1);
        var currentQuarterStart = StartOfQuarter(today);
        var mode = NormalizeText(requestedMode);
        var assumptions = new List<string>();

        PeriodResolution ClosedRange(
            string modeName,
            DateExpressionResolution first,
            DateExpressionResolution last,
            string label)
        {
            if (last.EndExclusive <= first.Start)
            {
                assumptions.Add("The requested end precedes the requested start.");
            }

            return new PeriodResolution(
                modeName,
                label,
                first.Start,
                last.EndExclusive,
                MoreDetailedGrouping(first.Grouping, last.Grouping),
                true,
                1,
                assumptions);
        }

        var shorthandRange = MonthRangeShorthandRegex.Match(question);
        if (shorthandRange.Success &&
            TryMonthNumber(shorthandRange.Groups["startMonth"].Value, out var shortStartMonth) &&
            TryMonthNumber(shorthandRange.Groups["endMonth"].Value, out var shortEndMonth) &&
            int.TryParse(shorthandRange.Groups["year"].Value, out var shortYear))
        {
            var startYear = shortStartMonth <= shortEndMonth ? shortYear : shortYear - 1;
            var first = new DateExpressionResolution(
                new DateTime(startYear, shortStartMonth, 1),
                new DateTime(startYear, shortStartMonth, 1).AddMonths(1),
                new DateTime(startYear, shortStartMonth, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture),
                "Month",
                "month");
            var lastStart = new DateTime(shortYear, shortEndMonth, 1);
            var last = new DateExpressionResolution(
                lastStart,
                lastStart.AddMonths(1),
                lastStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                "Month",
                "month");

            return ClosedRange(
                "explicit-range",
                first,
                last,
                $"{first.Label} through {last.Label}");
        }

        foreach (var rangeMatch in new[] { BetweenDateRegex.Match(question), FromToDateRegex.Match(question) })
        {
            if (rangeMatch.Success &&
                TryParseDateExpression(rangeMatch.Groups["start"].Value, today, out var first) &&
                TryParseDateExpression(rangeMatch.Groups["end"].Value, today, out var last))
            {
                return ClosedRange(
                    "explicit-range",
                    first,
                    last,
                    $"{first.Label} through {last.Label}");
            }
        }

        var beforeMatch = BeforeDateRegex.Match(question);
        if (beforeMatch.Success &&
            TryParseDateExpression(beforeMatch.Groups["value"].Value, today, out var before))
        {
            return new PeriodResolution(
                "before",
                $"Before {before.Label}",
                null,
                before.Start,
                before.Grouping,
                true,
                1,
                assumptions);
        }

        var afterMatch = AfterDateRegex.Match(question);
        if (afterMatch.Success &&
            TryParseDateExpression(afterMatch.Groups["value"].Value, today, out var after))
        {
            var rangeEnd = RangeEndFor(after.Precision, currentMonthStart, tomorrow);
            assumptions.Add(after.Precision == "day"
                ? "The range includes completed calendar days through today."
                : "The range ends before the current partial calendar month.");
            return new PeriodResolution(
                "after",
                $"After {after.Label}",
                after.EndExclusive,
                rangeEnd,
                after.Grouping,
                true,
                1,
                assumptions);
        }

        var sinceMatch = SinceDateRegex.Match(question);
        if (sinceMatch.Success &&
            TryParseDateExpression(sinceMatch.Groups["value"].Value, today, out var since))
        {
            var rangeEnd = RangeEndFor(since.Precision, currentMonthStart, tomorrow);
            assumptions.Add(since.Precision == "day"
                ? "Since is inclusive and runs through today."
                : "Since is inclusive and ends at the last completed calendar month; current-month partial data is excluded.");
            return new PeriodResolution(
                "since",
                $"{since.Label} through {RangeEndLabel(since.Precision, today, lastCompletedMonth)}",
                since.Start,
                rangeEnd,
                since.Grouping,
                true,
                1,
                assumptions);
        }

        var throughMatch = ThroughDateRegex.Match(question);
        if (throughMatch.Success &&
            TryParseDateExpression(throughMatch.Groups["value"].Value, today, out var through))
        {
            assumptions.Add("Through, until, up to, ending, and as of are treated as inclusive of the named day, month, quarter, or year.");
            return new PeriodResolution(
                "through",
                $"Through {through.Label}",
                null,
                through.EndExclusive,
                through.Grouping,
                true,
                1,
                assumptions);
        }

        var duringMatch = DuringDateRegex.Match(question);
        if (duringMatch.Success &&
            TryParseDateExpression(duringMatch.Groups["value"].Value, today, out var during))
        {
            return new PeriodResolution(
                "during",
                during.Label,
                during.Start,
                during.EndExclusive,
                during.Grouping,
                true,
                1,
                assumptions);
        }

        var rollingMatch = RollingPeriodRegex.Match(question);
        if (rollingMatch.Success &&
            TrySmallNumber(rollingMatch.Groups["n"].Value, out var rollingCount))
        {
            rollingCount = Math.Clamp(rollingCount, 1, 120);
            var unit = NormalizeText(rollingMatch.Groups["unit"].Value);

            if (unit.StartsWith("day", StringComparison.Ordinal))
            {
                return new PeriodResolution(
                    "last-n-days",
                    $"Last {rollingCount} days through {today:MMM d, yyyy}",
                    tomorrow.AddDays(-rollingCount),
                    tomorrow,
                    "Date",
                    true,
                    1,
                    assumptions);
            }

            if (unit.StartsWith("week", StringComparison.Ordinal))
            {
                return new PeriodResolution(
                    "last-n-weeks",
                    $"Last {rollingCount} weeks through {today:MMM d, yyyy}",
                    tomorrow.AddDays(-(rollingCount * 7)),
                    tomorrow,
                    "Date",
                    true,
                    1,
                    assumptions);
            }

            if (unit.StartsWith("quarter", StringComparison.Ordinal))
            {
                return new PeriodResolution(
                    "last-n-quarters",
                    $"Last {rollingCount} completed quarters",
                    currentQuarterStart.AddMonths(-(rollingCount * 3)),
                    currentQuarterStart,
                    "Quarter",
                    true,
                    1,
                    assumptions);
            }

            if (unit.StartsWith("year", StringComparison.Ordinal))
            {
                var months = rollingCount * 12;
                assumptions.Add($"Last {rollingCount} years means {months} completed rolling months ending with {lastCompletedMonth:MMM yyyy}.");
                return new PeriodResolution(
                    "last-n-years",
                    $"Last {months} completed months through {lastCompletedMonth:MMM yyyy}",
                    currentMonthStart.AddMonths(-months),
                    currentMonthStart,
                    "Month",
                    true,
                    1,
                    assumptions);
            }

            return new PeriodResolution(
                "last-n-months",
                $"Last {rollingCount} completed months through {lastCompletedMonth:MMM yyyy}",
                currentMonthStart.AddMonths(-rollingCount),
                currentMonthStart,
                "Month",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "today"))
        {
            return new PeriodResolution(
                "today",
                today.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                today,
                tomorrow,
                "Date",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "yesterday"))
        {
            var yesterday = today.AddDays(-1);
            return new PeriodResolution(
                "yesterday",
                yesterday.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                yesterday,
                today,
                "Date",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "ytd", "year to date", "current year"))
        {
            assumptions.Add("YTD is closed through the last completed calendar month.");
            return new PeriodResolution(
                "ytd-lm",
                $"YTD through {lastCompletedMonth:MMM yyyy}",
                new DateTime(lastCompletedMonth.Year, 1, 1),
                currentMonthStart,
                "Month",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "mom", "month over month", "month-over-month"))
        {
            return new PeriodResolution(
                "mom",
                $"{lastCompletedMonth:MMM yyyy} MoM",
                currentMonthStart.AddMonths(-2),
                currentMonthStart,
                "Month",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "yoy", "year over year", "year-over-year"))
        {
            return new PeriodResolution(
                "yoy",
                $"{lastCompletedMonth:MMM yyyy} YoY",
                lastCompletedMonth.AddYears(-1),
                currentMonthStart,
                "Month",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "this month", "current month", "mtd") || mode == "current-month")
        {
            return new PeriodResolution(
                "current-month",
                currentMonthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture) + " MTD",
                currentMonthStart,
                currentMonthStart.AddMonths(1),
                "Month",
                true,
                1,
                assumptions);
        }

        if (ContainsAny(question, "last month", "last completed month", "lm") || mode == "last-completed-month")
        {
            return new PeriodResolution(
                "last-completed-month",
                lastCompletedMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                lastCompletedMonth,
                currentMonthStart,
                "Month",
                true,
                1,
                assumptions);
        }

        var standaloneMatch = StandaloneDateExpressionRegex.Match(question);
        if (standaloneMatch.Success &&
            TryParseDateExpression(standaloneMatch.Groups["value"].Value, today, out var explicitPeriod))
        {
            return new PeriodResolution(
                "explicit-period",
                explicitPeriod.Label,
                explicitPeriod.Start,
                explicitPeriod.EndExclusive,
                explicitPeriod.Grouping,
                true,
                1,
                assumptions);
        }

        if (TemporalOperatorRegex.IsMatch(question))
        {
            return new PeriodResolution(
                "unresolved-temporal",
                "Unresolved time range",
                null,
                null,
                "Date",
                false,
                0,
                assumptions);
        }

        assumptions.Add("No period was stated; the assistant used the last completed calendar month to avoid partial-month distortion.");
        return new PeriodResolution(
            "last-completed-month",
            lastCompletedMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            lastCompletedMonth,
            currentMonthStart,
            "Month",
            true,
            0.88,
            assumptions);
    }

    private static DateTime StartOfQuarter(DateTime value)
        => new(value.Year, ((value.Month - 1) / 3 * 3) + 1, 1);

    private static DateTime StartOfWeek(DateTime value)
    {
        var offset = ((int)value.DayOfWeek + 6) % 7;
        return value.Date.AddDays(-offset);
    }

    private static DateTime RangeEndFor(
        string precision,
        DateTime currentMonthStart,
        DateTime tomorrow)
        => string.Equals(precision, "day", StringComparison.OrdinalIgnoreCase)
            ? tomorrow
            : currentMonthStart;

    private static string RangeEndLabel(
        string precision,
        DateTime today,
        DateTime lastCompletedMonth)
        => string.Equals(precision, "day", StringComparison.OrdinalIgnoreCase)
            ? today.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : lastCompletedMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    private static string MoreDetailedGrouping(string left, string right)
    {
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = 0,
            ["Month"] = 1,
            ["Quarter"] = 2,
            ["Year"] = 3
        };

        return priority.GetValueOrDefault(left, 1) <= priority.GetValueOrDefault(right, 1)
            ? left
            : right;
    }

    private static bool TryParseDateExpression(
        string? value,
        DateTime today,
        out DateExpressionResolution resolution)
    {
        resolution = default!;
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return false;

        var text = Regex.Replace(
                raw,
                @"(?<=\d)(?:st|nd|rd|th)\b",
                string.Empty,
                RegexOptions.IgnoreCase)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        var normalized = NormalizeText(text);
        var currentMonthStart = new DateTime(today.Year, today.Month, 1);
        var currentQuarterStart = StartOfQuarter(today);

        if (normalized is "today")
        {
            resolution = new DateExpressionResolution(
                today,
                today.AddDays(1),
                today.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                "Date",
                "day");
            return true;
        }

        if (normalized is "yesterday")
        {
            var yesterday = today.AddDays(-1);
            resolution = new DateExpressionResolution(
                yesterday,
                today,
                yesterday.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                "Date",
                "day");
            return true;
        }

        if (ContainsAny(normalized, "last completed month", "last month", "previous month", "prior month"))
        {
            var start = currentMonthStart.AddMonths(-1);
            resolution = new DateExpressionResolution(
                start,
                currentMonthStart,
                start.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                "Month",
                "month");
            return true;
        }

        if (ContainsAny(normalized, "this month", "current month"))
        {
            resolution = new DateExpressionResolution(
                currentMonthStart,
                currentMonthStart.AddMonths(1),
                currentMonthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                "Month",
                "month");
            return true;
        }

        if (ContainsAny(normalized, "last week", "previous week", "prior week"))
        {
            var thisWeek = StartOfWeek(today);
            var start = thisWeek.AddDays(-7);
            resolution = new DateExpressionResolution(
                start,
                thisWeek,
                $"{start:MMM d}–{thisWeek.AddDays(-1):MMM d, yyyy}",
                "Date",
                "day");
            return true;
        }

        if (ContainsAny(normalized, "this week", "current week"))
        {
            var start = StartOfWeek(today);
            resolution = new DateExpressionResolution(
                start,
                start.AddDays(7),
                $"Week of {start:MMM d, yyyy}",
                "Date",
                "day");
            return true;
        }

        if (ContainsAny(normalized, "last quarter", "previous quarter", "prior quarter"))
        {
            var start = currentQuarterStart.AddMonths(-3);
            resolution = new DateExpressionResolution(
                start,
                currentQuarterStart,
                $"Q{((start.Month - 1) / 3) + 1} {start.Year}",
                "Quarter",
                "quarter");
            return true;
        }

        if (ContainsAny(normalized, "this quarter", "current quarter"))
        {
            resolution = new DateExpressionResolution(
                currentQuarterStart,
                currentQuarterStart.AddMonths(3),
                $"Q{((currentQuarterStart.Month - 1) / 3) + 1} {currentQuarterStart.Year}",
                "Quarter",
                "quarter");
            return true;
        }

        if (ContainsAny(normalized, "last year", "previous year", "prior year"))
        {
            var start = new DateTime(today.Year - 1, 1, 1);
            resolution = new DateExpressionResolution(
                start,
                start.AddYears(1),
                start.Year.ToString(CultureInfo.InvariantCulture),
                "Month",
                "year");
            return true;
        }

        if (ContainsAny(normalized, "this year", "current year"))
        {
            var start = new DateTime(today.Year, 1, 1);
            resolution = new DateExpressionResolution(
                start,
                start.AddYears(1),
                start.Year.ToString(CultureInfo.InvariantCulture),
                "Month",
                "year");
            return true;
        }

        var quarterMatch = Regex.Match(
            normalized,
            @"^(?:q(?<q>[1-4])|(?<word>first|second|third|fourth)\s+quarter(?:\s+of)?)\s+(?<year>20\d{2})$",
            RegexOptions.IgnoreCase);
        if (quarterMatch.Success && int.TryParse(quarterMatch.Groups["year"].Value, out var quarterYear))
        {
            var quarter = quarterMatch.Groups["q"].Success
                ? int.Parse(quarterMatch.Groups["q"].Value, CultureInfo.InvariantCulture)
                : quarterMatch.Groups["word"].Value.ToLowerInvariant() switch
                {
                    "first" => 1,
                    "second" => 2,
                    "third" => 3,
                    "fourth" => 4,
                    _ => 0
                };
            if (quarter is >= 1 and <= 4)
            {
                var start = new DateTime(quarterYear, ((quarter - 1) * 3) + 1, 1);
                resolution = new DateExpressionResolution(
                    start,
                    start.AddMonths(3),
                    $"Q{quarter} {quarterYear}",
                    "Quarter",
                    "quarter");
                return true;
            }
        }

        var exactFormats = new[]
        {
            "yyyy-M-d",
            "yyyy-MM-dd",
            "M/d/yyyy",
            "MM/dd/yyyy",
            "MMM d yyyy",
            "MMMM d yyyy"
        };
        if (DateTime.TryParseExact(
                text,
                exactFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exactDate))
        {
            exactDate = exactDate.Date;
            resolution = new DateExpressionResolution(
                exactDate,
                exactDate.AddDays(1),
                exactDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                "Date",
                "day");
            return true;
        }

        if (DateTime.TryParseExact(
                text,
                new[] { "MMM yyyy", "MMMM yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var monthDate))
        {
            var start = new DateTime(monthDate.Year, monthDate.Month, 1);
            resolution = new DateExpressionResolution(
                start,
                start.AddMonths(1),
                start.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                "Month",
                "month");
            return true;
        }

        if (Regex.IsMatch(normalized, @"^20\d{2}$") &&
            int.TryParse(normalized, out var year))
        {
            var start = new DateTime(year, 1, 1);
            resolution = new DateExpressionResolution(
                start,
                start.AddYears(1),
                year.ToString(CultureInfo.InvariantCulture),
                "Month",
                "year");
            return true;
        }

        return false;
    }

    private static List<string> ResolveComparisons(string question)
    {
        var comparisons = new List<string>();
        if (ContainsAny(question, "mom", "month over month", "month-over-month")) comparisons.Add("mom");
        if (ContainsAny(question, "yoy", "year over year", "year-over-year")) comparisons.Add("yoy");
        return comparisons;
    }

    private static string ResolveAggregation(string question)
    {
        if (ContainsAny(question, "how many", "count", "number of", "volume of")) return "Count";
        if (ContainsAny(question, "average", "avg", "mean")) return "Average";
        if (ContainsAny(question, "minimum", "lowest", "smallest")) return "Minimum";
        if (ContainsAny(question, "maximum", "highest", "largest")) return "Maximum";
        return "Sum";
    }

    private static VisualResolution ResolveVisualType(
        string question,
        string? requested,
        IReadOnlyList<AssistantColumnDto> dimensions,
        AssistantColumnDto? dateField,
        AssistantColumnDto? measure,
        string aggregation)
    {
        var explicitText = NormalizeText(requested);
        var source = explicitText.Length > 0 ? explicitText : question;
        var candidates = new List<string>();

        void Add(string type, params string[] phrases)
        {
            if (phrases.Any(phrase => source.Contains(NormalizeText(phrase), StringComparison.Ordinal)) &&
                !candidates.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(type);
            }
        }

        Add("combo", "bar chart with line", "bar chart with a line",
            "bar chart with total line", "bar chart with a total line",
            "bar with line", "bar with a line", "bar with total line",
            "bars with line", "bar and line", "bars and line",
            "combo chart", "combination chart");
        Add("matrix", "matrix", "pivot table", "pivot matrix");
        Add("stacked100", "100 percent stacked", "100% stacked");
        Add("stackedBar", "stacked bar", "stacked columns");
        Add("hbar", "horizontal bar");
        Add("heatmap", "heat map", "heatmap");
        Add("scatter", "scatter plot", "scatter chart");
        Add("donut", "donut", "doughnut");
        Add("pie", "pie chart", "pie");
        Add("area", "area chart", "area graph");
        Add("line", "line chart", "line graph", "trend line");
        Add("bar", "bar chart", "column chart", "columns");
        Add("table", "data table", "as a table", "table view");
        Add("metric", "metric card", "kpi card", "scorecard", "single number", "as a card");

        // Specific phrases own their component words.
        if (candidates.Contains("combo"))
        {
            candidates.RemoveAll(type => type is "bar" or "line");
        }
        if (candidates.Contains("matrix"))
        {
            candidates.RemoveAll(type => type == "table");
        }
        if (candidates.Contains("stacked100"))
        {
            candidates.RemoveAll(type => type is "stackedBar" or "bar");
        }
        if (candidates.Contains("stackedBar"))
        {
            candidates.RemoveAll(type => type == "bar");
        }
        if (candidates.Contains("hbar"))
        {
            candidates.RemoveAll(type => type == "bar");
        }

        if (candidates.Count > 1)
        {
            return new VisualResolution(candidates[0], true, candidates);
        }

        if (candidates.Count == 1)
        {
            return new VisualResolution(candidates[0], false, candidates);
        }

        var inferred = dimensions.Count == 0
            ? "metric"
            : dateField != null && dimensions.Any(column =>
                string.Equals(column.Name, dateField.Name, StringComparison.OrdinalIgnoreCase))
                ? "line"
                : dimensions.Count > 1 ? "stackedBar" : "bar";

        return new VisualResolution(inferred, false, new List<string> { inferred });
    }

    private static int SuggestionKindPriority(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "measure" => 0,
            "date" => 1,
            "dimension" => 2,
            "period" => 3,
            "comparison" => 4,
            "visual" => 5,
            "dataset" => 6,
            _ => 7
        };

    private static string VisualLabel(string type)
        => type switch
        {
            "combo" => "Bar chart with total line",
            "matrix" => "Matrix",
            "stacked100" => "100% stacked bar",
            "stackedBar" => "Stacked bar",
            "hbar" => "Horizontal bar",
            "heatmap" => "Heat map",
            "scatter" => "Scatter plot",
            "donut" => "Donut chart",
            "pie" => "Pie chart",
            "area" => "Area chart",
            "line" => "Line chart",
            "bar" => "Bar chart",
            "table" => "Table",
            "metric" => "Metric card",
            _ => DashboardAssistantCatalogService.Humanize(type)
        };

    private static string VisualDescription(string type)
        => type switch
        {
            "combo" => "Grouped bars by series with an aggregate total line on the same date axis.",
            "matrix" => "Pivoted rows and columns with the requested aggregate in each cell.",
            "metric" => "One aggregate value for the requested period and filters.",
            "table" => "Flat grouped result rows without chart transformation.",
            _ => $"Render the validated aggregate as {VisualLabel(type).ToLowerInvariant()}."
        };

    private static string ResolveValueFormat(AssistantColumnDto? measure, string aggregation)
    {
        if (aggregation == "Count" || measure == null) return "number";
        if (!string.IsNullOrWhiteSpace(measure.ValueFormat)) return measure.ValueFormat;

        var name = NormalizeText(measure.Name);
        if (ContainsAny(name, "amount", "balance", "payment", "paid", "cost", "revenue", "dollar"))
            return "currency";
        if (ContainsAny(name, "percent", "percentage", "pct", "rate", "ratio"))
            return "percent";
        return "number";
    }

    private static string BuildTitle(
        AssistantDatasetDto dataset,
        string aggregation,
        AssistantColumnDto? measure,
        IReadOnlyList<AssistantColumnDto> dimensions,
        IReadOnlyDictionary<string, AssistantFilterSpecDto> filters,
        PeriodResolution period,
        IReadOnlyList<string> comparisons)
    {
        var measureLabel = aggregation == "Count"
            ? "Count"
            : measure?.Label ?? aggregation;
        var grouping = dimensions.Count > 0
            ? " by " + string.Join(" and ", dimensions.Select(item => item.Label))
            : "";
        var filterLabel = filters.Count > 0
            ? " · " + string.Join(
                ", ",
                filters.Select(filter => string.Join(
                    " / ",
                    filter.Value.Values.Where(value => !string.IsNullOrWhiteSpace(value)))))
            : "";
        var comparisonLabel = comparisons.Count > 0
            ? " · " + string.Join(" / ", comparisons.Select(item => item.ToUpperInvariant()))
            : "";
        return $"{measureLabel}{grouping}{filterLabel} · {period.Label}{comparisonLabel}";
    }

    private static AssistantPlanResponse Clarify(
        string sector,
        string kind,
        string prompt,
        IEnumerable<AssistantChoiceDto> choices,
        AssistantDatasetDto? dataset = null,
        AssistantVersionContextDto? context = null)
        => new()
        {
            Ready = false,
            Sector = sector,
            Context = context,
            Dataset = dataset,
            Message = prompt,
            Clarification = new AssistantClarificationDto
            {
                Kind = kind,
                Prompt = prompt,
                Choices = choices.ToList()
            }
        };

    private static IEnumerable<string> ExpandDomainAliases(AssistantDatasetDto dataset)
    {
        var text = NormalizeText(dataset.Title + " " + dataset.Object + " " + dataset.Key);
        foreach (var group in DomainAliases)
        {
            if (group.Value.Any(alias => text.Contains(NormalizeText(alias), StringComparison.Ordinal)) ||
                text.Contains(group.Key, StringComparison.Ordinal))
            {
                foreach (var alias in group.Value) yield return alias;
            }
        }
    }

    private static string DatasetSearchText(AssistantDatasetDto dataset)
        => NormalizeText(string.Join(" ", dataset.Aliases));

    private static int DatePriority(string name)
    {
        var normalized = NormalizeText(name);
        if (normalized.Contains("period date")) return 100;
        if (normalized.Contains("snapshot date")) return 95;
        if (normalized == "date") return 90;
        if (normalized.Contains("report date")) return 85;
        if (normalized.Contains("month")) return 80;
        if (normalized.Contains("date in")) return 75;
        if (normalized.Contains("created")) return 50;
        return 30;
    }

    private static bool ContainsWholePhrase(string normalizedText, string normalizedPhrase)
    {
        if (normalizedText.Length == 0 || normalizedPhrase.Length == 0) return false;
        return (" " + normalizedText + " ").Contains(
            " " + normalizedPhrase + " ",
            StringComparison.Ordinal);
    }

    private static double PhraseScore(string haystack, string phrase)
    {
        var normalizedHaystack = NormalizeText(haystack);
        var normalizedPhrase = NormalizeText(phrase);
        if (normalizedPhrase.Length == 0) return 0;
        if (normalizedHaystack.Contains(normalizedPhrase, StringComparison.Ordinal))
            return normalizedPhrase.Contains(' ') ? 0.9 : 0.7;

        var phraseTokens = Tokenize(normalizedPhrase);
        if (phraseTokens.Count == 0) return 0;
        var overlap = phraseTokens.Intersect(Tokenize(normalizedHaystack)).Count();
        return (double)overlap / phraseTokens.Count * 0.65;
    }

    private static HashSet<string> Tokenize(string text)
        => NormalizeText(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeText(string? value)
    {
        var raw = (value ?? "").Trim().ToLowerInvariant();
        raw = raw.Replace("e-bill", "ebill")
                 .Replace("month-over-month", "mom")
                 .Replace("year-over-year", "yoy")
                 .Replace("month over month", "mom")
                 .Replace("year over year", "yoy");
        raw = Regex.Replace(raw, "[^a-z0-9%]+", " ");
        return SpaceRegex.Replace(raw, " ").Trim();
    }

    private static bool ContainsAny(string text, params string[] phrases)
    {
        var normalized = NormalizeText(text);
        return phrases.Any(phrase =>
            normalized.Contains(NormalizeText(phrase), StringComparison.Ordinal));
    }

    private static bool TrySmallNumber(string text, out int value)
    {
        value = 0;
        if (int.TryParse(text, out value)) return true;
        value = NormalizeText(text) switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            _ => 0
        };
        return value > 0;
    }

    private static bool TryMonthNumber(string text, out int month)
    {
        month = 0;
        return DateTime.TryParseExact(
            text,
            new[] { "MMM", "MMMM" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed) &&
            (month = parsed.Month) > 0;
    }

    private sealed record Scored<T>(T Value, double Score, List<string> Matches);

    private sealed record VisualResolution(
        string Type,
        bool Ambiguous,
        List<string> Candidates);

    private sealed record DateExpressionResolution(
        DateTime Start,
        DateTime EndExclusive,
        string Label,
        string Grouping,
        string Precision);

    private sealed record PeriodResolution(
        string Mode,
        string Label,
        DateTime? From,
        DateTime? ToExclusive,
        string Grouping,
        bool RequiresMonthlySeries,
        double Confidence,
        List<string> Assumptions);
}
