using System.Globalization;
using System.Text;
using System.Text.Json;
using corporate_dashboards.Models;
using corporate_dashboards.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Controllers;

[Route("DashboardAssistant")]
public sealed class DashboardAssistantController : Controller
{
    private readonly IDashboardAssistantContextService _context;
    private readonly IDashboardAssistantPlanner _planner;
    private readonly IDashboardAssistantCatalogService _catalog;
    private readonly IOllamaClient _ollama;
    private readonly DashboardAssistantOptions _options;
    private readonly ILogger<DashboardAssistantController> _logger;

    public DashboardAssistantController(
        IDashboardAssistantContextService context,
        IDashboardAssistantPlanner planner,
        IDashboardAssistantCatalogService catalog,
        IOllamaClient ollama,
        IOptions<DashboardAssistantOptions> options,
        ILogger<DashboardAssistantController> logger)
    {
        _context = context;
        _planner = planner;
        _catalog = catalog;
        _ollama = ollama;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("Bootstrap")]
    public async Task<IActionResult> Bootstrap(
        long? layoutVersionId,
        [FromQuery] List<string>? currentTemplateKeys,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return NotFound();

        var context = await _context.ResolveAsync(
            layoutVersionId,
            currentTemplateKeys,
            cancellationToken);

        var examples = context.Resolved
            ? await _planner.GetExamplesAsync(context, cancellationToken)
            : Array.Empty<string>();

        var facts = new List<string>();
        var dimensions = new List<string>();
        if (context.Resolved)
        {
            foreach (var dataset in context.Datasets)
            {
                try
                {
                    var fields = await _catalog.GetColumnsAsync(dataset, cancellationToken);
                    facts.AddRange(fields
                        .Where(field => field.Category == "measure")
                        .OrderByDescending(field => field.IsDefault)
                        .ThenByDescending(field => field.SemanticPriority)
                        .Select(field => field.Label));
                    dimensions.AddRange(fields
                        .Where(field => field.Category is "dimension" or "date")
                        .OrderBy(field => field.Category == "date" ? 0 : 1)
                        .ThenByDescending(field => field.SemanticPriority)
                        .Select(field => field.Label));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Dashboard assistant bootstrap could not load semantic fields for {DatasetKey}.",
                        dataset.Key);
                }
            }
        }

        return Json(new AssistantBootstrapResponse
        {
            Enabled = _options.Enabled,
            BuildId = "assistant-v8-direct-v217-contract",
            Context = context,
            Facts = facts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Dimensions = dimensions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Examples = examples.ToList()
        });
    }

    [HttpGet("Suggestions")]
    public async Task<IActionResult> Suggestions(
        long? layoutVersionId,
        [FromQuery] List<string>? currentTemplateKeys,
        string? datasetKey,
        string? prefix,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return NotFound();
        var items = await _planner.SuggestAsync(
            layoutVersionId,
            currentTemplateKeys,
            datasetKey,
            prefix,
            cancellationToken);
        return Json(items);
    }

    [HttpPost("Plan")]
    public async Task<IActionResult> Plan(
        [FromBody] AssistantQueryRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return NotFound();
        if (request == null) return BadRequest("Missing assistant request.");

        var response = await _planner.PlanAsync(request, cancellationToken);
        Response.Headers["X-Dashboard-Assistant-Build"] = "assistant-v8-direct-v217-contract";
        return Json(response);
    }

    [HttpPost("Narrate")]
    public async Task<IActionResult> Narrate(
        [FromBody] AssistantNarrativeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return NotFound();
        if (request?.Plan == null || request.Visual == null)
            return BadRequest("A validated assistant plan is required.");

        var facts = BuildFacts(request);
        var fallback = BuildDeterministicNarrative(request, facts);

        if (!_options.NarrativeEnabled || facts.Count == 0)
        {
            return Json(new AssistantNarrativeResponse
            {
                Narrative = fallback,
                UsedLlm = false,
                Facts = facts
            });
        }

        try
        {
            var prompt = BuildNarrativePrompt(request, facts);
            var narrative = (await _ollama.ChatAsync(prompt, null, cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(narrative)) narrative = fallback;

            return Json(new AssistantNarrativeResponse
            {
                Narrative = narrative,
                UsedLlm = !string.Equals(narrative, fallback, StringComparison.Ordinal),
                Facts = facts
            });
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Dashboard assistant narrative model was unavailable; deterministic narrative returned.");

            return Json(new AssistantNarrativeResponse
            {
                Narrative = fallback,
                UsedLlm = false,
                Facts = facts
            });
        }
    }

    private static List<string> BuildFacts(AssistantNarrativeRequest request)
    {
        var facts = new List<string>
        {
            $"Dashboard version: {request.Plan!.LayoutVersionId} · {request.Plan.LayoutTitle}",
            $"Dataset: {request.Plan.DatasetTitle}",
            $"Period: {request.Plan.PeriodLabel}",
            $"Visual: {request.Visual!.Type}",
            $"Returned rows: {request.Rows.Count.ToString("N0", CultureInfo.InvariantCulture)}"
        };

        var measure = ResolveNarrativeMeasureField(request);
        if (string.IsNullOrWhiteSpace(measure)) return facts;

        var values = request.Rows
            .Select(row => TryGetNumber(row, measure!, out var value) ? value : (decimal?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (values.Count == 0) return facts;

        if (values.Count == 1)
        {
            facts.Add($"Returned value: {values[0].ToString("N2", CultureInfo.InvariantCulture)}");
        }
        else
        {
            facts.Add($"Minimum grouped value: {values.Min().ToString("N2", CultureInfo.InvariantCulture)}");
            facts.Add($"Maximum grouped value: {values.Max().ToString("N2", CultureInfo.InvariantCulture)}");
        }

        var top = request.Rows
            .Select(row => new
            {
                Row = row,
                HasValue = TryGetNumber(row, measure!, out var value),
                Value = value
            })
            .Where(item => item.HasValue)
            .OrderByDescending(item => item.Value)
            .FirstOrDefault();

        if (top != null)
        {
            var labels = request.Visual.DimensionFields
                .Select(field => TryGetText(top.Row, field))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (labels.Count > 0)
            {
                facts.Add($"Highest group: {string.Join(" / ", labels)} at {top.Value.ToString("N2", CultureInfo.InvariantCulture)}");
            }
        }

        return facts;
    }

    private static string BuildDeterministicNarrative(
        AssistantNarrativeRequest request,
        IReadOnlyList<string> facts)
    {
        var measureField = ResolveNarrativeMeasureField(request);
        if (request.Rows.Count == 1 &&
            !string.IsNullOrWhiteSpace(measureField) &&
            TryGetNumber(request.Rows[0], measureField!, out var singleValue))
        {
            var measureLabel = DashboardAssistantCatalogService.Humanize(
                request.Plan!.Measure ?? measureField!);
            var formattedValue = FormatNarrativeValue(
                singleValue,
                request.Visual.ValueFormat);
            var qualifiers = request.Visual.DimensionFields
                .Select(field => TryGetText(request.Rows[0], field))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Concat(request.Plan.Filters.Values.SelectMany(filter =>
                    filter.Values.Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var qualifierText = qualifiers.Count > 0
                ? $" for {string.Join(" / ", qualifiers)}"
                : string.Empty;

            return $"{measureLabel}{qualifierText} was {formattedValue} for {request.Plan.PeriodLabel}.";
        }

        var result = new StringBuilder();
        result.Append(request.Plan!.DatasetTitle)
            .Append(" returned ")
            .Append(request.Rows.Count.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" grouped result")
            .Append(request.Rows.Count == 1 ? "" : "s")
            .Append(" for ")
            .Append(request.Plan.PeriodLabel)
            .Append('.');

        var top = facts.FirstOrDefault(item => item.StartsWith("Highest group:", StringComparison.Ordinal));
        if (top != null)
        {
            result.Append(' ').Append(top.Replace("Highest group:", "The highest group was", StringComparison.Ordinal)).Append('.');
        }

        return result.ToString();
    }

    private static string? ResolveNarrativeMeasureField(AssistantNarrativeRequest request)
    {
        var preferred = request.Visual?.MeasureField;
        if (!string.IsNullOrWhiteSpace(preferred) &&
            request.Rows.Any(row => row.ContainsKey(preferred)))
        {
            return preferred;
        }

        if (request.Rows.Any(row => row.ContainsKey("Value")))
        {
            return "Value";
        }

        return preferred;
    }

    private static string FormatNarrativeValue(decimal value, string? format)
        => (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "currency" => value.ToString("$#,##0.00", CultureInfo.InvariantCulture),
            "percent" or "percent2" => value.ToString("0.00", CultureInfo.InvariantCulture) + "%",
            "number" => value.ToString("#,##0.##", CultureInfo.InvariantCulture),
            _ => value.ToString("#,##0.##", CultureInfo.InvariantCulture)
        };

    private static string BuildNarrativePrompt(
        AssistantNarrativeRequest request,
        IReadOnlyList<string> facts)
    {
        return $$"""
You are wording a dashboard narrative from already-computed facts.
Write at most three concise sentences for a business dashboard.
Use only the facts below. Do not calculate, estimate, infer causation, introduce new numbers, or claim significance.
Do not mention SQL, language models, prompts, or implementation details.
If the facts do not support a conclusion, only describe what is present.
When exactly one value is returned, state that value directly for the requested period; do not describe it as a chart or a grouped result.

Validated semantic plan:
- Dashboard version: {{request.Plan!.LayoutVersionId}} · {{request.Plan.LayoutTitle}}
- Dataset: {{request.Plan.DatasetTitle}}
- Aggregation: {{request.Plan.Aggregation}}
- Measure: {{request.Plan.Measure ?? "row count"}}
- Dimensions: {{string.Join(", ", request.Plan.Dimensions)}}
- Filters: {{string.Join("; ", request.Plan.Filters.Select(filter => filter.Key + "=" + string.Join("/", filter.Value.Values)))}}
- Period: {{request.Plan.PeriodLabel}}
- Visual: {{request.Visual!.Type}}

Computed facts:
{{string.Join(Environment.NewLine, facts.Select(item => "- " + item))}}
""";
    }

    private static bool TryGetNumber(
        IReadOnlyDictionary<string, object?> row,
        string field,
        out decimal value)
    {
        value = 0;
        var pair = row.FirstOrDefault(item =>
            string.Equals(item.Key, field, StringComparison.OrdinalIgnoreCase));
        if (pair.Key == null || pair.Value == null) return false;

        if (pair.Value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetDecimal(out value)) return true;
            if (json.ValueKind == JsonValueKind.String &&
                decimal.TryParse(json.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
            return false;
        }

        try
        {
            value = Convert.ToDecimal(pair.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetText(
        IReadOnlyDictionary<string, object?> row,
        string field)
    {
        var pair = row.FirstOrDefault(item =>
            string.Equals(item.Key, field, StringComparison.OrdinalIgnoreCase));
        if (pair.Key == null || pair.Value == null) return null;
        if (pair.Value is JsonElement json) return json.ToString();
        return Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
    }
}
