namespace corporate_dashboards.Models;

public sealed class CxDashboardUploadOptions
{
    public bool Enabled { get; set; } = true;
    public string ConnectionName { get; set; } = "build";
    public string ApplyProcedure { get; set; } = "dbo.usp_cx_excel_apply_visual";
    public List<CxDashboardUploadVisualOptions> Visuals { get; set; } = new();
}

public sealed class CxDashboardUploadVisualOptions
{
    public string Key { get; set; } = string.Empty;
    public string ApplyKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Schema { get; set; } = "rpt";
    public string Object { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Headers { get; set; } = new();
    public List<string> RequiredHeaders { get; set; } = new();
    public List<string> SheetAliases { get; set; } = new();

    public string EffectiveApplyKey => string.IsNullOrWhiteSpace(ApplyKey) ? Key : ApplyKey;
}

public sealed class CxDashboardUploadAccessOptions
{
    public bool AllowAnonymous { get; set; } = true;
    public List<string> Users { get; set; } = new();
    public List<string> Groups { get; set; } = new();
}

public sealed class CxDashboardUploadPageVm
{
    public bool Success { get; set; }
    public long? UploadBatchId { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public int PublishedVisuals { get; set; }
    public int PublishedRows { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<CxDashboardUploadResultVm> Results { get; set; } = new();
    public List<CxDashboardUploadVisualVm> Visuals { get; set; } = new();
}

public sealed class CxDashboardUploadResultVm
{
    public string VisualKey { get; set; } = string.Empty;
    public string PipelineKey { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class CxDashboardUploadVisualVm
{
    public string Key { get; set; } = string.Empty;
    public string ApplyKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public List<string> Headers { get; set; } = new();
    public List<string> RequiredHeaders { get; set; } = new();
}
