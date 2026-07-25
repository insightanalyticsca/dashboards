using System.Data;
using System.Text.Json;
using corporate_dashboards.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public interface IDashboardAssistantContextService
{
    Task<AssistantVersionContextDto> ResolveAsync(
        long? layoutVersionId,
        IReadOnlyCollection<string>? clientTemplateKeys,
        CancellationToken cancellationToken);
}

public sealed class DashboardAssistantContextService : IDashboardAssistantContextService
{
    private static readonly string[] TemplatePropertyNames =
    {
        "customHtmlTemplate",
        "templateId",
        "templateKey"
    };

    // These executive screens have a fixed one-version/one-semantic-contract
    // relationship. The version ID is authoritative even when an older saved
    // LayoutJson row contains a legacy CSR template or stale tile metadata.
    // This prevents Version 217 from falling back to the raw
    // ns_daily_cash_by_cycle_view schema and exposing fields such as Account No,
    // CYCLE, Is EBill, and Occupant Code as candidate measures.
    private static readonly IReadOnlyDictionary<long, IReadOnlyList<string>>
        CanonicalVersionTemplates = new Dictionary<long, IReadOnlyList<string>>
        {
            [213] = new[] { "executive-ebill-performance" },
            [214] = new[] { "executive-ar-portfolio" },
            [215] = new[] { "executive-disconnects-bankruptcies" },
            [216] = new[] { "executive-final-bill-recovery" },
            [217] = new[] { "executive-customer-payments" }
        };

    private readonly IConfiguration _configuration;
    private readonly IDashboardAssistantCatalogService _catalog;
    private readonly DashboardAssistantOptions _options;
    private readonly ILogger<DashboardAssistantContextService> _logger;

    public DashboardAssistantContextService(
        IConfiguration configuration,
        IDashboardAssistantCatalogService catalog,
        IOptions<DashboardAssistantOptions> options,
        ILogger<DashboardAssistantContextService> logger)
    {
        _configuration = configuration;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AssistantVersionContextDto> ResolveAsync(
        long? layoutVersionId,
        IReadOnlyCollection<string>? clientTemplateKeys,
        CancellationToken cancellationToken)
    {
        var clientKeys = NormalizeTemplateKeys(clientTemplateKeys);

        if (!(layoutVersionId > 0))
        {
            if (_options.RequireLayoutVersionContext)
            {
                return Unresolved(
                    0,
                    "",
                    "No SQL layout version is selected. Open a saved dashboard version before using the assistant.");
            }

            return BuildContext(0, "Current dashboard", clientKeys, "Client layout context");
        }

        var connectionString = _configuration.GetConnectionString("build")
            ?? throw new InvalidOperationException("Dashboard assistant requires the build connection string.");

        string title;
        string layoutJson;

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 60;
            command.CommandText = @"
SELECT TOP (1)
    ISNULL(Title, '') AS Title,
    LayoutJson
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id
  AND Page = N'Multi';";
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt)
            {
                Value = layoutVersionId.Value
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Unresolved(
                    layoutVersionId.Value,
                    "",
                    $"Dashboard version {layoutVersionId.Value} was not found in dbo.DashboardLayoutVersion.");
            }

            title = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
            layoutJson = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        var serverKeys = ExtractTemplateKeys(layoutJson);
        var canonicalTemplate = ResolveCanonicalExecutiveTemplate(
            layoutVersionId.Value,
            title,
            serverKeys,
            clientKeys);
        var hasCanonicalContract = !string.IsNullOrWhiteSpace(canonicalTemplate);
        IReadOnlyList<string>? canonicalKeys = hasCanonicalContract
            ? new[] { canonicalTemplate! }
            : null;

        var effectiveKeys = hasCanonicalContract
            ? canonicalKeys!.ToList()
            : serverKeys.Count > 0
                ? serverKeys
                : clientKeys;

        if (hasCanonicalContract)
        {
            var unexpectedServerKeys = serverKeys
                .Except(canonicalKeys!, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unexpectedServerKeys.Count > 0 ||
                canonicalKeys!.Except(serverKeys, StringComparer.OrdinalIgnoreCase).Any())
            {
                _logger.LogWarning(
                    "Assistant replaced stale LayoutJson template scope for executive version {VersionId}. " +
                    "Saved templates={SavedTemplates}; canonical templates={CanonicalTemplates}.",
                    layoutVersionId.Value,
                    string.Join(", ", serverKeys),
                    string.Join(", ", canonicalKeys));
            }
        }
        else if (serverKeys.Count > 0 && clientKeys.Count > 0)
        {
            var clientOnly = clientKeys.Except(serverKeys, StringComparer.OrdinalIgnoreCase).ToList();
            if (clientOnly.Count > 0)
            {
                _logger.LogDebug(
                    "Assistant ignored {Count} client template keys not present in SQL LayoutJson for version {VersionId}.",
                    clientOnly.Count,
                    layoutVersionId.Value);
            }
        }

        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Version {layoutVersionId.Value}"
            : title;

        if (hasCanonicalContract)
        {
            return BuildCanonicalExecutiveContext(
                layoutVersionId.Value,
                resolvedTitle,
                canonicalKeys!.Single());
        }

        return BuildContext(
            layoutVersionId.Value,
            resolvedTitle,
            effectiveKeys,
            serverKeys.Count > 0
                ? "SQL LayoutJson"
                : "loaded dashboard tiles");
    }

    private static string? ResolveCanonicalExecutiveTemplate(
        long versionId,
        string? layoutTitle,
        IReadOnlyCollection<string> serverKeys,
        IReadOnlyCollection<string> clientKeys)
    {
        if (CanonicalVersionTemplates.TryGetValue(versionId, out var byId))
        {
            return byId.Single();
        }

        var allKeys = serverKeys
            .Concat(clientKeys)
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var canonical in CanonicalVersionTemplates.Values.SelectMany(value => value))
        {
            if (allKeys.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        var normalizedTitle = NormalizeContractText(layoutTitle);
        return normalizedTitle switch
        {
            "e bill performance" => "executive-ebill-performance",
            "ebill performance" => "executive-ebill-performance",
            "ar portfolio" => "executive-ar-portfolio",
            "disconnects reconnects and bankruptcies" => "executive-disconnects-bankruptcies",
            "disconnects and bankruptcies" => "executive-disconnects-bankruptcies",
            "final bill collections recovery electric" => "executive-final-bill-recovery",
            "final bill recovery" => "executive-final-bill-recovery",
            "customer payments" => "executive-customer-payments",
            _ => null
        };
    }

    private static string NormalizeContractText(string? value)
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

    private static AssistantVersionContextDto BuildCanonicalExecutiveContext(
        long versionId,
        string layoutTitle,
        string templateKey)
    {
        var datasetTitle = templateKey switch
        {
            "executive-ebill-performance" => "E-Bill Performance",
            "executive-ar-portfolio" => "A/R Portfolio",
            "executive-disconnects-bankruptcies" => "Disconnects and Bankruptcies",
            "executive-final-bill-recovery" => "Final Bill Recovery",
            "executive-customer-payments" => "Customer Payments",
            _ => layoutTitle
        };

        var aliases = templateKey switch
        {
            "executive-ebill-performance" => new[] { "e bill", "ebill", "paperless billing", "adoption" },
            "executive-ar-portfolio" => new[] { "ar", "a r", "arrears", "aging", "overdue balances" },
            "executive-disconnects-bankruptcies" => new[] { "disconnects", "reconnects", "bankruptcies" },
            "executive-final-bill-recovery" => new[] { "final bill", "recovery", "collections" },
            "executive-customer-payments" => new[] { "customer payments", "payments", "collections", "transactions", "credit card" },
            _ => Array.Empty<string>()
        };

        var dataset = new AssistantDatasetDto
        {
            Key = templateKey,
            Sector = "cx",
            Title = datasetTitle,
            Description = $"{datasetTitle} · fixed executive semantic contract",
            TemplateKey = templateKey,
            TemplateKeys = new List<string> { templateKey },
            SourceAlias = templateKey,
            ConnectionName = "build",
            Schema = "dbo",
            Object = templateKey,
            ObjectKind = "executiveSuite",
            PayloadMode = "executiveSuite",
            Role = "csr-page",
            Aliases = new[] { templateKey, datasetTitle }
                .Concat(aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        return new AssistantVersionContextDto
        {
            Resolved = true,
            LayoutVersionId = versionId,
            LayoutTitle = layoutTitle,
            ContextLabel = $"Version {versionId} · {layoutTitle}",
            ContextDetail = $"Current screen only · fixed semantic contract {templateKey}",
            Sector = "cx",
            Message = $"Semantic scope locked to {templateKey}; raw SQL metadata is disabled for this version.",
            TemplateKeys = new List<string> { templateKey },
            DatasetKeys = new List<string> { templateKey },
            Datasets = new List<AssistantDatasetDto> { dataset }
        };
    }

    private AssistantVersionContextDto BuildContext(
        long versionId,
        string title,
        IReadOnlyCollection<string> templateKeys,
        string source)
    {
        var datasets = _catalog.GetDatasetsForTemplates(templateKeys).ToList();
        if (datasets.Count == 0)
        {
            return Unresolved(
                versionId,
                title,
                templateKeys.Count == 0
                    ? $"Version {versionId} contains no configured dashboard templates."
                    : $"Version {versionId} contains templates that are not available in the assistant semantic catalog.",
                templateKeys);
        }

        var sectors = datasets
            .Select(dataset => dataset.Sector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sector = sectors.Count == 1 ? sectors[0] : "mixed";
        var datasetCount = datasets.Count;
        var templateCount = templateKeys.Count;
        var contextTitle = string.IsNullOrWhiteSpace(title) ? $"Version {versionId}" : title;

        return new AssistantVersionContextDto
        {
            Resolved = true,
            LayoutVersionId = versionId,
            LayoutTitle = contextTitle,
            ContextLabel = versionId > 0
                ? $"Version {versionId} · {contextTitle}"
                : contextTitle,
            ContextDetail = $"Current screen only · {datasetCount} approved data source{(datasetCount == 1 ? "" : "s")} · {templateCount} template{(templateCount == 1 ? "" : "s")}",
            Sector = sector,
            Message = $"Semantic scope resolved from {source}.",
            TemplateKeys = templateKeys
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DatasetKeys = datasets.Select(dataset => dataset.Key).ToList(),
            Datasets = datasets
        };
    }

    private static AssistantVersionContextDto Unresolved(
        long versionId,
        string title,
        string message,
        IReadOnlyCollection<string>? templateKeys = null)
        => new()
        {
            Resolved = false,
            LayoutVersionId = versionId,
            LayoutTitle = title,
            ContextLabel = versionId > 0
                ? $"Version {versionId}{(string.IsNullOrWhiteSpace(title) ? "" : " · " + title)}"
                : "No dashboard version",
            ContextDetail = "Assistant unavailable until the version context is resolved",
            Message = message,
            TemplateKeys = NormalizeTemplateKeys(templateKeys)
        };

    private static List<string> ExtractTemplateKeys(string layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson)) return new List<string>();

        try
        {
            using var document = JsonDocument.Parse(layoutJson);
            var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(document.RootElement, output);
            return output.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static void Walk(JsonElement element, HashSet<string> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (TemplatePropertyNames.Any(name =>
                        string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = (property.Value.GetString() ?? "").Trim();
                    if (IsDashboardTemplateKey(value)) output.Add(value);
                }

                Walk(property.Value, output);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Walk(item, output);
        }
    }

    private static bool IsDashboardTemplateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.StartsWith("csr-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("csr_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("cx-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("cx_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("its-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("its_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("executive-", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeTemplateKeys(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim())
            .Where(IsDashboardTemplateKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
