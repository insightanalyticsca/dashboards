namespace corporate_dashboards.Models;

public sealed class CxVisualsOptions
{
    public string ConnectionName { get; set; } = "ItsDashboard";
    public string TemplateTable { get; set; } = "dbo.PbiHtmlVisualTemplate";
    public string ChunkTable { get; set; } = "dbo.PbiHtmlVisualTemplateChunk";

    // Physical source file used by the constructor app when rebuilding template chunks.
    // Relative paths are resolved from ContentRootPath. Absolute paths are used as-is.
    public string HtmlSourceFile { get; set; } = "Templates/cx-visual.html";

    // Metadata path stored in PbiHtmlVisualTemplate.HtmlFile. This is not a cache-busting URL.
    public string DefaultHtmlFile { get; set; } = "/custom-html/cx-visual.html";

    public int ChunkSize { get; set; } = 30000;
    public List<CxVisualOptions> Visuals { get; set; } = new();
}

public sealed class CxVisualOptions
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string HtmlFile { get; set; } = "";
    public string Schema { get; set; } = "rpt";
    public string Object { get; set; } = "";
    public string Role { get; set; } = "";
    public string Title { get; set; } = "";
    public Dictionary<string, string[]> FieldAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CxKpiShapeOptions? Kpi { get; set; }
    public CxChartShapeOptions? Chart { get; set; }
    public CxPieShapeOptions? Pie { get; set; }
    public CxTableShapeOptions? Table { get; set; }
}

public sealed class CxKpiShapeOptions
{
    public string TitleSource { get; set; } = "config";
    public string ValueAlias { get; set; } = "value";
    public string ValueTextAlias { get; set; } = "valueText";
    public string ValueFormatAlias { get; set; } = "valueType";
    public bool CleanTitleMonthSuffix { get; set; } = true;
    public List<CxKpiPillOptions> Pills { get; set; } = new();
    public List<string> Narratives { get; set; } = new();
    public List<string> Tooltip { get; set; } = new();
}

public sealed class CxKpiPillOptions
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string Alias { get; set; } = "";
    public string TextAlias { get; set; } = "";
    public string Class { get; set; } = "";
    public string Format { get; set; } = "";
    public string ToneMode { get; set; } = "";
}

public sealed class CxChartShapeOptions
{
    public string Type { get; set; } = "stacked-bar-line";
    public string XAxisAlias { get; set; } = "period";
    public string XAxisSortAlias { get; set; } = "periodSort";
    public string SeriesAlias { get; set; } = "category";
    public string SeriesSortAlias { get; set; } = "categorySort";
    public string ValueAlias { get; set; } = "value";
    public string LineAlias { get; set; } = "";
    public string LineLabel { get; set; } = "";
    public string TargetAlias { get; set; } = "target";
    public string TargetLabel { get; set; } = "Target";
    public string TargetMode { get; set; } = "max-per-period";
    public string LegendPosition { get; set; } = "top-right";
    public string LegendSize { get; set; } = "compact";
    public int XAxisRotate { get; set; } = -90;
    public int MaxPeriods { get; set; } = 0;
    public string ValueFormat { get; set; } = "raw";
    public string ValuePrefix { get; set; } = "";
    public string ValueSuffix { get; set; } = "";
    public CxGridOptions Grid { get; set; } = new();
}

public sealed class CxGridOptions
{
    public int Top { get; set; } = 12;
    public int Right { get; set; } = 54;
    public int Bottom { get; set; } = 42;
    public int Left { get; set; } = 50;
}

public sealed class CxPieShapeOptions
{
    public string NameAlias { get; set; } = "category";
    public string ValueAlias { get; set; } = "value";
    public string SortAlias { get; set; } = "categorySort";
    public string TotalLabel { get; set; } = "TOTAL";
    public string ValueFormat { get; set; } = "raw";
}

public sealed class CxTableShapeOptions
{
    public List<CxTableColumnOptions> Columns { get; set; } = new();
}

public sealed class CxTableColumnOptions
{
    public string Header { get; set; } = "";
    public string HeaderAlias { get; set; } = "";
    public string FallbackHeader { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Type { get; set; } = "";
    public string Format { get; set; } = "";
    public string StatusAlias { get; set; } = "";
}
