using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace corporate_dashboards.Controllers;

public sealed partial class DashboardController
{
    private const string CsrStandardRuntimeVersion = "executive-dashboard.css?v=20260724-polish-3";
    private static readonly object CsrManifestSync = new();
    private static string? CsrManifestCachedPath;
    private static DateTime CsrManifestCachedWriteUtc;
    private static JsonObject? CsrManifestCachedRoot;

    private string BuildConfiguredHtmlUrl(
        CustomHtmlRuleConfig rule,
        string basePath,
        string appBasePath)
    {
        var safeFile = Path.GetFileName((rule.HtmlFile ?? string.Empty).Trim());
        var url = BuildStaticHtmlUrl(basePath, safeFile, appBasePath);

        if (!rule.Role.StartsWith("csr-", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(rule.Key))
        {
            return url;
        }

        var separator = url.Contains('?') ? "&" : "?";
        return url + separator +
               "templateId=" + Uri.EscapeDataString(rule.Key) +
               "&v=" + Uri.EscapeDataString(CsrStandardRuntimeVersion);
    }

    [HttpGet]
    public IActionResult GetCsrDefinition(string templateId)
    {
        var requestedKey = (templateId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return BadRequest("templateId is required");
        }

        var templates = LoadCustomHtmlTemplates();
        var requested = templates.FirstOrDefault(template =>
            string.Equals(template.Key, requestedKey, StringComparison.OrdinalIgnoreCase));

        if (requested == null || !requested.Role.StartsWith("csr-", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound($"CSR template was not found: {requestedKey}");
        }

        var isPage = string.Equals(requested.Role, "csr-page", StringComparison.OrdinalIgnoreCase);
        var pageKey = isPage ? requested.Key : requested.PageKey;
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return BadRequest($"CSR visual '{requested.Key}' has no PageKey.");
        }

        var pageRule = templates.FirstOrDefault(template =>
            string.Equals(template.Key, pageKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(template.Role, "csr-page", StringComparison.OrdinalIgnoreCase));
        if (pageRule == null)
        {
            return NotFound($"CSR page template was not found: {pageKey}");
        }

        var manifestPage = ReadCsrManifestPage(pageKey);
        var appVisualRules = templates
            .Where(template =>
                string.Equals(template.Role, "csr-visual", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(template.PageKey, pageKey, StringComparison.OrdinalIgnoreCase) &&
                template.VisualConfig.Count > 0)
            .OrderBy(template => ReadVisualPositionNumber(template.VisualConfig, "z"))
            .ThenBy(template => ReadVisualPositionNumber(template.VisualConfig, "y"))
            .ThenBy(template => ReadVisualPositionNumber(template.VisualConfig, "x"))
            .ToList();

        if (!isPage)
        {
            appVisualRules = appVisualRules
                .Where(template => string.Equals(template.Key, requested.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var visuals = appVisualRules
            .Select(template => PrepareVisualConfig(template, isPage))
            .ToList();

        var aliases = manifestPage?["aliases"]?.DeepClone() ?? new JsonObject();
        var relationships = manifestPage?["relationships"]?.DeepClone() ?? new JsonArray();
        var width = ReadJsonDouble(manifestPage, "width", 1280d);
        var height = ReadJsonDouble(manifestPage, "height", 720d);
        var slug = ReadJsonString(manifestPage, "slug") ?? pageKey.Replace("csr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        var sourcePage = ReadJsonString(manifestPage, "sourcePage") ?? string.Empty;

        var orderedSources = OrderSourcesForDefinition(pageRule.Sources, visuals);
        var appBasePath = Request.PathBase.HasValue ? Request.PathBase.Value!.TrimEnd('/') : string.Empty;
        var dashboardBaseUrl = string.IsNullOrWhiteSpace(appBasePath) ? "/Dashboard" : appBasePath + "/Dashboard";

        return Json(new
        {
            key = requested.Key,
            templateId = requested.Key,
            pageKey,
            role = requested.Role,
            slug,
            title = isPage ? pageRule.Title : requested.Title,
            sourcePage,
            width,
            height,
            paletteLight = ReadCsrPalette(manifestPage, dark: false),
            paletteDark = ReadCsrPalette(manifestPage, dark: true),
            visuals,
            aliases,
            relationships,
            sources = orderedSources.Select(source => new
            {
                alias = string.IsNullOrWhiteSpace(source.Alias) ? source.Object : source.Alias,
                connectionName = source.ConnectionName,
                schema = source.Schema,
                @object = source.Object,
                objectKind = source.ObjectKind,
                top = source.Top,
                required = source.Required
            }),
            directSourceLoading = isPage,
            sourceEndpoint = dashboardBaseUrl + "/GetCustomHtmlLiveData",
            refreshSeconds = pageRule.RefreshSeconds,
            layoutMode = "appsettings-components",
            styleProfile = "legacy-compatible",
            enableVisualLayoutEdit = isPage,
            showTextBanners = _cfg.GetValue<bool>(
                "Dashboard:Csr:ShowTextBanners",
                true
            ),
            runtimeVersion = CsrStandardRuntimeVersion
        });
    }

    private JsonObject? ReadCsrManifestPage(string pageKey)
    {
        try
        {
            var webRoot = string.IsNullOrWhiteSpace(_env.WebRootPath)
                ? Path.Combine(_env.ContentRootPath, "wwwroot")
                : _env.WebRootPath;
            var path = Path.Combine(webRoot, "csr", "csr-pages.manifest.json");
            if (!System.IO.File.Exists(path)) return null;

            var writeUtc = System.IO.File.GetLastWriteTimeUtc(path);
            JsonObject? root;
            lock (CsrManifestSync)
            {
                if (CsrManifestCachedRoot == null ||
                    !string.Equals(CsrManifestCachedPath, path, StringComparison.OrdinalIgnoreCase) ||
                    CsrManifestCachedWriteUtc != writeUtc)
                {
                    CsrManifestCachedRoot = JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject;
                    CsrManifestCachedPath = path;
                    CsrManifestCachedWriteUtc = writeUtc;
                }
                root = CsrManifestCachedRoot;
            }

            var pages = root?["pages"] as JsonArray;
            if (pages == null) return null;

            var page = pages
                .OfType<JsonObject>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate["key"]?.GetValue<string>(),
                    pageKey,
                    StringComparison.OrdinalIgnoreCase));
            return page?.DeepClone() as JsonObject;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CSR page manifest could not be read for {PageKey}.", pageKey);
            return null;
        }
    }

    private static Dictionary<string, object?> PrepareVisualConfig(
        CustomHtmlRuleConfig rule,
        bool preservePosition)
    {
        var json = JsonSerializer.Serialize(rule.VisualConfig);
        var clone = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        clone["id"] = string.IsNullOrWhiteSpace(rule.VisualId) ? rule.Key : rule.VisualId;
        if (!clone.ContainsKey("type") && !string.IsNullOrWhiteSpace(rule.VisualType))
        {
            clone["type"] = rule.VisualType;
        }
        if (!clone.ContainsKey("title") && !string.IsNullOrWhiteSpace(rule.Title))
        {
            clone["title"] = rule.Title;
        }

        if (!preservePosition)
        {
            clone["position"] = new Dictionary<string, object?>
            {
                ["x"] = 0d,
                ["y"] = 0d,
                ["w"] = 100d,
                ["h"] = 100d,
                ["z"] = 0
            };
        }

        return clone;
    }

    private static List<CustomHtmlSourceConfig> OrderSourcesForDefinition(
        List<CustomHtmlSourceConfig> sources,
        List<Dictionary<string, object?>> visuals)
    {
        if (sources.Count <= 1 || visuals.Count == 0) return sources.ToList();

        var serialized = JsonSerializer.Serialize(visuals[0]);
        var primary = sources.FirstOrDefault(source =>
        {
            var alias = string.IsNullOrWhiteSpace(source.Alias) ? source.Object : source.Alias;
            return !string.IsNullOrWhiteSpace(alias) &&
                   serialized.Contains($"\"entity\":\"{alias}\"", StringComparison.OrdinalIgnoreCase);
        });

        if (primary == null) return sources.ToList();
        return new[] { primary }
            .Concat(sources.Where(source => !ReferenceEquals(source, primary)))
            .ToList();
    }

    private static double ReadVisualPositionNumber(Dictionary<string, object?> config, string key)
    {
        if (!config.TryGetValue("position", out var rawPosition) || rawPosition == null) return 0d;
        var json = JsonSerializer.Serialize(rawPosition);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(key, out var value)) return 0d;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0d;
    }


    private static string[] ReadCsrPalette(JsonObject? manifestPage, bool dark)
    {
        if (dark)
        {
            return new[]
            {
                "#A78BFA", "#22D3EE", "#34D399", "#38BDF8", "#FBBF24",
                "#60A5FA", "#A3E635", "#7DD3FC", "#2DD4BF", "#BAE6FD"
            };
        }

        var fallback = new[]
        {
            "#845EF7", "#00D4FF", "#00E6A8", "#38BDF8", "#FFD166",
            "#4C6FFF", "#B7F34A", "#22D3EE", "#2DD4BF", "#7DD3FC"
        };

        if (manifestPage?["palette"] is not JsonArray palette || palette.Count == 0)
        {
            return fallback;
        }

        var result = palette
            .Select(item => item?.GetValue<string>()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.ToUpperInvariant() switch
            {
                "#FF4D8D" => "#38BDF8",
                "#FF8A65" => "#22D3EE",
                "#E879F9" => "#7DD3FC",
                _ => value!
            })
            .ToArray();

        return result.Length > 0 ? result : fallback;
    }

    private static double ReadJsonDouble(JsonObject? node, string property, double fallback)
    {
        try { return node?[property]?.GetValue<double>() ?? fallback; }
        catch { return fallback; }
    }

    private static string? ReadJsonString(JsonObject? node, string property)
    {
        try { return node?[property]?.GetValue<string>(); }
        catch { return null; }
    }
}
