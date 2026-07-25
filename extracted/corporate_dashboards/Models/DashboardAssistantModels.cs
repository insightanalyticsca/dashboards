using System.Text.Json.Serialization;

namespace corporate_dashboards.Models;

public sealed class DashboardAssistantOptions
{
    public bool Enabled { get; set; } = true;
    public bool NarrativeEnabled { get; set; } = true;
    public bool RequireLayoutVersionContext { get; set; } = true;
    public bool DetectOutOfScopeQuestions { get; set; } = true;
    public int MaxRows { get; set; } = 5000;
    public int SuggestionLimit { get; set; } = 12;
    public double MinimumDatasetConfidence { get; set; } = 0.62;
    public double MinimumMeasureConfidence { get; set; } = 0.58;
    public double OutOfScopeConfidence { get; set; } = 0.72;
}

public sealed class AssistantSectorDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Accent { get; set; } = "";
}

public sealed class AssistantDatasetDto
{
    public string Key { get; set; } = "";
    public string Sector { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string TemplateKey { get; set; } = "";
    public List<string> TemplateKeys { get; set; } = new();
    public string SourceAlias { get; set; } = "";
    public string ConnectionName { get; set; } = "build";
    public string Schema { get; set; } = "dbo";
    public string Object { get; set; } = "";
    public string ObjectKind { get; set; } = "table";
    public string PayloadMode { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string[]> FieldAliases { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DeclaredMeasureFields { get; set; } = new();
    public List<string> DeclaredDimensionFields { get; set; } = new();
}


public sealed class AssistantColumnDto
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Category { get; set; } = "dimension";
    public bool Nullable { get; set; }
    public List<string> Aliases { get; set; } = new();
    public string DefaultAggregation { get; set; } = "Sum";
    public string ValueFormat { get; set; } = "number";
    public bool IsDefault { get; set; }
    public bool IsSnapshot { get; set; }
    public int SemanticPriority { get; set; }
    public List<string> AllowedDimensions { get; set; } = new();
}


public sealed class AssistantVersionContextDto
{
    public bool Resolved { get; set; }
    public long LayoutVersionId { get; set; }
    public string LayoutTitle { get; set; } = "";
    public string ContextLabel { get; set; } = "";
    public string ContextDetail { get; set; } = "";
    public string Sector { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> TemplateKeys { get; set; } = new();
    public List<string> DatasetKeys { get; set; } = new();
    public List<AssistantDatasetDto> Datasets { get; set; } = new();
}

public sealed class AssistantBootstrapResponse
{
    public bool Enabled { get; set; }
    public string BuildId { get; set; } = "";
    public AssistantVersionContextDto? Context { get; set; }
    public List<string> Facts { get; set; } = new();
    public List<string> Dimensions { get; set; } = new();
    public List<string> Examples { get; set; } = new();
}

public sealed class AssistantSuggestionDto
{
    public string Kind { get; set; } = "phrase";
    public string Label { get; set; } = "";
    public string InsertText { get; set; } = "";
    public string? Value { get; set; }
    public string? Detail { get; set; }
}

public sealed class AssistantQueryRequest
{
    public long? LayoutVersionId { get; set; }
    public string? LayoutVersionTitle { get; set; }
    public string Question { get; set; } = "";
    public string? DatasetKey { get; set; }
    public string? Measure { get; set; }
    public List<string> Dimensions { get; set; } = new();
    public string? PeriodMode { get; set; }
    public string? ChartType { get; set; }
    public string? CurrentLayoutTitle { get; set; }
    public List<string> CurrentTemplateKeys { get; set; } = new();
}

public sealed class AssistantClarificationDto
{
    public string Kind { get; set; } = "dataset";
    public string Prompt { get; set; } = "";
    public List<AssistantChoiceDto> Choices { get; set; } = new();
}

public sealed class AssistantChoiceDto
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Detail { get; set; }
    public double Confidence { get; set; }
}

public sealed class AssistantFilterSpecDto
{
    public string Mode { get; set; } = "in";
    public List<string?> Values { get; set; } = new();
    public string? FromUtc { get; set; }
    public string? ToUtc { get; set; }
}

public sealed class AssistantAggregateRequestDto
{
    public string ConnectionName { get; set; } = "build";
    public string Schema { get; set; } = "dbo";
    public string Obj { get; set; } = "";
    public List<string> Rows { get; set; } = new();
    public List<string> Cols { get; set; } = new();
    public List<string> Values { get; set; } = new();
    public string Agg { get; set; } = "Sum";
    public Dictionary<string, string> DateGroups { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AssistantFilterSpecDto> Filters { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public int MaxCells { get; set; } = 5000;
}

public sealed class AssistantVisualPlanDto
{
    public string Type { get; set; } = "bar";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ValueFormat { get; set; } = "number";
    public string? DateField { get; set; }
    public string? MeasureField { get; set; }
    public List<string> DimensionFields { get; set; } = new();
    public List<string> Comparisons { get; set; } = new();
    public string? ReportingPeriodLabel { get; set; }
}

public sealed class AssistantSemanticPlanDto
{
    public long LayoutVersionId { get; set; }
    public string LayoutTitle { get; set; } = "";
    public string DatasetKey { get; set; } = "";
    public string DatasetTitle { get; set; } = "";
    public string Aggregation { get; set; } = "Sum";
    public string? Measure { get; set; }
    public List<string> Dimensions { get; set; } = new();
    public Dictionary<string, AssistantFilterSpecDto> Filters { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public string? DateField { get; set; }
    public string PeriodMode { get; set; } = "all";
    public string PeriodLabel { get; set; } = "All available data";
    public List<string> Comparisons { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<string> MatchedTerms { get; set; } = new();
}

public sealed class AssistantExecutiveRequestDto
{
    public long LayoutVersionId { get; set; }
    public string Suite { get; set; } = "";
    public string Measure { get; set; } = "";
    public string Aggregation { get; set; } = "Sum";
    public List<string> Dimensions { get; set; } = new();
    public Dictionary<string, AssistantFilterSpecDto> Filters { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public string PeriodMode { get; set; } = "last-completed-month";
    public string? FromUtc { get; set; }
    public string? ToUtc { get; set; }
    public string ChartType { get; set; } = "metric";
    public string Question { get; set; } = "";
}

public sealed class AssistantPlanResponse
{
    public bool Ready { get; set; }
    public bool OutOfScope { get; set; }
    public string Sector { get; set; } = "";
    public double Confidence { get; set; }
    public string Message { get; set; } = "";
    public string ExecutionMode { get; set; } = "aggregate";
    public AssistantVersionContextDto? Context { get; set; }
    public AssistantDatasetDto? Dataset { get; set; }
    public AssistantClarificationDto? Clarification { get; set; }
    public AssistantAggregateRequestDto? AggregateRequest { get; set; }
    public AssistantExecutiveRequestDto? ExecutiveRequest { get; set; }
    public AssistantVisualPlanDto? Visual { get; set; }
    public AssistantSemanticPlanDto? Plan { get; set; }
}

public sealed class AssistantNarrativeRequest
{
    public AssistantSemanticPlanDto? Plan { get; set; }
    public AssistantVisualPlanDto? Visual { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
}

public sealed class AssistantNarrativeResponse
{
    public string Narrative { get; set; } = "";
    public bool UsedLlm { get; set; }
    public List<string> Facts { get; set; } = new();
}
