using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using corporate_dashboards.Models;
using Microsoft.Data.SqlClient;

namespace corporate_dashboards.Services;

public interface IDashboardAssistantCatalogService
{
    IReadOnlyList<AssistantSectorDto> GetSectors();
    IReadOnlyList<AssistantDatasetDto> GetAllDatasets();
    IReadOnlyList<AssistantDatasetDto> GetDatasets(string sector);
    IReadOnlyList<AssistantDatasetDto> GetDatasetsForTemplates(IEnumerable<string> templateKeys);
    AssistantDatasetDto? FindDataset(string sector, string datasetKey);
    AssistantDatasetDto? FindDataset(IEnumerable<AssistantDatasetDto> scope, string datasetKey);
    Task<IReadOnlyList<AssistantColumnDto>> GetColumnsAsync(
        AssistantDatasetDto dataset,
        CancellationToken cancellationToken);
}

public sealed class DashboardAssistantCatalogService : IDashboardAssistantCatalogService
{
    private static readonly Regex WordBoundaryRegex = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonWordRegex = new("[^a-zA-Z0-9]+", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly ILogger<DashboardAssistantCatalogService> _logger;
    private readonly Lazy<IReadOnlyList<AssistantDatasetDto>> _datasets;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<AssistantColumnDto>>>> _columnCache
        = new(StringComparer.OrdinalIgnoreCase);

    public DashboardAssistantCatalogService(
        IConfiguration configuration,
        ILogger<DashboardAssistantCatalogService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _datasets = new Lazy<IReadOnlyList<AssistantDatasetDto>>(
            BuildDatasets,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<AssistantSectorDto> GetSectors()
        => new List<AssistantSectorDto>
        {
            new()
            {
                Key = "cx",
                Label = "CX / Executive",
                Description = "Customer experience, executive KPIs, payments, E-Bill and service activity",
                Icon = "fa-users-viewfinder",
                Accent = "indigo"
            },
            new()
            {
                Key = "csr",
                Label = "CSR",
                Description = "A/R, collections, aging, customer service and operational reporting",
                Icon = "fa-headset",
                Accent = "teal"
            },
            new()
            {
                Key = "its",
                Label = "ITS",
                Description = "Tickets, SLA, cyber security, KB4, uptime and infrastructure health",
                Icon = "fa-server",
                Accent = "violet"
            }
        };

    public IReadOnlyList<AssistantDatasetDto> GetAllDatasets()
        => _datasets.Value
            .OrderBy(dataset => dataset.Sector, StringComparer.OrdinalIgnoreCase)
            .ThenBy(dataset => dataset.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<AssistantDatasetDto> GetDatasets(string sector)
    {
        var normalized = NormalizeSector(sector);
        return _datasets.Value
            .Where(dataset => string.Equals(dataset.Sector, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(dataset => dataset.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<AssistantDatasetDto> GetDatasetsForTemplates(IEnumerable<string> templateKeys)
    {
        var wanted = (templateKeys ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0) return Array.Empty<AssistantDatasetDto>();

        return _datasets.Value
            .Where(dataset => dataset.TemplateKeys.Any(wanted.Contains))
            .OrderBy(dataset => dataset.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AssistantDatasetDto? FindDataset(string sector, string datasetKey)
        => FindDataset(GetDatasets(sector), datasetKey);

    public AssistantDatasetDto? FindDataset(
        IEnumerable<AssistantDatasetDto> scope,
        string datasetKey)
    {
        var normalizedKey = (datasetKey ?? "").Trim();
        if (normalizedKey.Length == 0) return null;

        return (scope ?? Array.Empty<AssistantDatasetDto>()).FirstOrDefault(dataset =>
            string.Equals(dataset.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<AssistantColumnDto>> GetColumnsAsync(
        AssistantDatasetDto dataset,
        CancellationToken cancellationToken)
    {
        if (string.Equals(dataset.ObjectKind, "executiveSuite", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(
                DashboardAssistantSemanticCatalog.GetFields(dataset.TemplateKey));
        }

        var cacheKey = string.Join("|",
            dataset.ConnectionName,
            dataset.Schema,
            dataset.Object);

        var loader = _columnCache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<IReadOnlyList<AssistantColumnDto>>>(
                () => LoadColumnsAsync(dataset, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return loader.Value.WaitAsync(cancellationToken);
    }

    private IReadOnlyList<AssistantDatasetDto> BuildDatasets()
    {
        var templates = _configuration
            .GetSection("Dashboard:CustomHtml:Templates")
            .GetChildren()
            .SelectMany(ReadTemplateDatasets)
            .ToList();

        var merged = templates
            .GroupBy(
                DatasetMergeKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeDatasetGroup(group.ToList()))
            .Where(dataset => !string.IsNullOrWhiteSpace(dataset.Object))
            .ToList();

        _logger.LogInformation(
            "Dashboard assistant semantic catalog loaded {DatasetCount} datasets: CX={CxCount}, CSR={CsrCount}, ITS={ItsCount}.",
            merged.Count,
            merged.Count(x => x.Sector == "cx"),
            merged.Count(x => x.Sector == "csr"),
            merged.Count(x => x.Sector == "its"));

        return merged;
    }

    private IEnumerable<AssistantDatasetDto> ReadTemplateDatasets(IConfigurationSection section)
    {
        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled) yield break;

        var key = (section["Key"] ?? section["Id"] ?? "").Trim();
        if (key.Length == 0) yield break;

        var role = (section["Role"] ?? "").Trim();
        var sector = ResolveSector(key, role);
        if (sector.Length == 0) yield break;

        var title = FirstNonEmpty(
            section["Title"],
            section["Label"],
            Humanize(key));

        var defaultConnection = (section["ConnectionName"] ?? "").Trim();
        var defaultSchema = (section["Schema"] ?? "").Trim();
        var defaultObject = (section["Object"] ?? section["Obj"] ?? "").Trim();
        var defaultObjectKind = (section["ObjectKind"] ?? "").Trim();
        var payloadMode = (section["PayloadMode"] ?? "").Trim();

        var fieldAliases = ReadFieldAliases(section);
        var declaredMeasures = ReadConfiguredFieldNames(
            section,
            "ValueFields",
            "Measures");
        var declaredDimensions = ReadConfiguredFieldNames(
            section,
            "RowFields",
            "ColFields",
            "Dimensions");
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AssistantDatasetDto Create(
            string sourceAlias,
            string connectionName,
            string schema,
            string objectName,
            string objectKind,
            bool primary)
        {
            if (schema.Length == 0) schema = "dbo";
            if (connectionName.Length == 0)
            {
                connectionName = sector == "csr" ? "csr_pbip_source" : "build";
            }

            var sourceTitle = primary || string.IsNullOrWhiteSpace(sourceAlias)
                ? title
                : $"{title} · {Humanize(sourceAlias)}";

            var datasetKey = primary || string.IsNullOrWhiteSpace(sourceAlias)
                ? key
                : $"{key}::{sourceAlias}";

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                key,
                datasetKey,
                title,
                sourceTitle,
                objectName,
                sourceAlias,
                role,
                Humanize(key),
                Humanize(objectName),
                Humanize(sourceAlias)
            };

            foreach (var alias in fieldAliases.Keys)
            {
                aliases.Add(alias);
                aliases.Add(Humanize(alias));
            }

            return new AssistantDatasetDto
            {
                Key = datasetKey,
                Sector = sector,
                Title = sourceTitle,
                Description = BuildDescription(sector, sourceTitle, objectName),
                TemplateKey = key,
                TemplateKeys = new List<string> { key },
                SourceAlias = sourceAlias,
                ConnectionName = connectionName,
                Schema = schema,
                Object = objectName,
                ObjectKind = objectKind.Length == 0 ? "tableOrView" : objectKind,
                PayloadMode = payloadMode,
                Role = role,
                Aliases = aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList(),
                FieldAliases = fieldAliases.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                DeclaredMeasureFields = declaredMeasures.ToList(),
                DeclaredDimensionFields = declaredDimensions.ToList()
            };
        }

        if (string.Equals(payloadMode, "executiveSuite", StringComparison.OrdinalIgnoreCase))
        {
            // Executive versions are calculated by DashboardController.ExecutiveVersions.
            // Do not expose their parameterized table-valued functions to the generic
            // Aggregate endpoint. The assistant queries the same normalized payload that
            // renders the current executive screen.
            yield return Create(
                "",
                "build",
                "dbo",
                key,
                "executiveSuite",
                true);
            yield break;
        }

        if (defaultObject.Length > 0)
        {
            var effectiveConnection = defaultConnection.Length > 0
                ? defaultConnection
                : sector == "csr" ? "csr_pbip_source" : "build";
            var effectiveSchema = defaultSchema.Length > 0 ? defaultSchema : "dbo";
            var identity = string.Join("|", effectiveConnection, effectiveSchema, defaultObject);
            emitted.Add(identity);
            yield return Create(
                "",
                effectiveConnection,
                effectiveSchema,
                defaultObject,
                defaultObjectKind,
                true);
        }

        foreach (var source in section.GetSection("Sources").GetChildren())
        {
            var sourceAlias = FirstNonEmpty(source["Alias"], source["Key"], source["Object"]);
            var objectName = (source["Object"] ?? source["Obj"] ?? "").Trim();
            if (objectName.Length == 0) continue;

            var connectionName = FirstNonEmpty(source["ConnectionName"], defaultConnection);
            var schema = FirstNonEmpty(source["Schema"], defaultSchema, "dbo");
            var objectKind = FirstNonEmpty(source["ObjectKind"], defaultObjectKind);
            var identity = string.Join("|", connectionName, schema, objectName);
            if (!emitted.Add(identity)) continue;

            yield return Create(
                sourceAlias,
                connectionName,
                schema,
                objectName,
                objectKind,
                defaultObject.Length == 0 && emitted.Count == 1);
        }
    }

    private static HashSet<string> ReadConfiguredFieldNames(
        IConfigurationSection section,
        params string[] sectionNames)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sectionName in sectionNames)
        {
            var configured = section.GetSection(sectionName);
            foreach (var child in configured.GetChildren())
            {
                var value = (child.Value ?? string.Empty).Trim();
                if (value.Length > 0) output.Add(value);
            }

            if (!string.IsNullOrWhiteSpace(configured.Value))
            {
                foreach (var value in configured.Value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    output.Add(value);
                }
            }
        }

        return output;
    }

    private static Dictionary<string, string[]> ReadFieldAliases(IConfigurationSection section)
    {
        var fieldAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var aliasSection in section.GetSection("FieldAliases").GetChildren())
        {
            var values = aliasSection.GetChildren()
                .Select(value => value.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (values.Length == 0 && !string.IsNullOrWhiteSpace(aliasSection.Value))
            {
                values = new[] { aliasSection.Value.Trim() };
            }

            if (values.Length > 0)
            {
                fieldAliases[aliasSection.Key] = values;
            }
        }

        return fieldAliases;
    }

    private static string DatasetMergeKey(AssistantDatasetDto dataset)
    {
        // An executive semantic payload may use the same physical SQL object as
        // legacy CSR pages. It must remain a separate dataset because its facts,
        // dimensions and execution path come from the executive controller payload,
        // not from raw SQL-column discovery.
        if (string.Equals(dataset.ObjectKind, "executiveSuite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dataset.PayloadMode, "executiveSuite", StringComparison.OrdinalIgnoreCase) ||
            dataset.Key.StartsWith("executive-", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join("|", "executive", dataset.Key);
        }

        return string.Join("|",
            "source",
            dataset.Sector,
            dataset.ConnectionName,
            dataset.Schema,
            dataset.Object);
    }

    private static AssistantDatasetDto MergeDatasetGroup(List<AssistantDatasetDto> datasets)
    {
        var preferred = datasets
            .OrderByDescending(DatasetPreferenceScore)
            .ThenBy(dataset => dataset.Title.Length)
            .First();

        var aliases = datasets
            .SelectMany(dataset => dataset.Aliases)
            .Append(preferred.Title)
            .Append(preferred.Object)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fieldAliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataset in datasets)
        {
            foreach (var pair in dataset.FieldAliases)
            {
                if (!fieldAliases.TryGetValue(pair.Key, out var values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fieldAliases[pair.Key] = values;
                }

                foreach (var value in pair.Value)
                {
                    values.Add(value);
                }
            }
        }

        return new AssistantDatasetDto
        {
            Key = preferred.Key,
            Sector = preferred.Sector,
            Title = preferred.Title,
            Description = preferred.Description,
            TemplateKey = preferred.TemplateKey,
            TemplateKeys = datasets
                .SelectMany(dataset => dataset.TemplateKeys.Count > 0
                    ? (IEnumerable<string>)dataset.TemplateKeys
                    : new[] { dataset.TemplateKey })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SourceAlias = preferred.SourceAlias,
            ConnectionName = preferred.ConnectionName,
            Schema = preferred.Schema,
            Object = preferred.Object,
            ObjectKind = preferred.ObjectKind,
            PayloadMode = preferred.PayloadMode,
            Role = preferred.Role,
            Aliases = aliases,
            FieldAliases = fieldAliases.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            DeclaredMeasureFields = datasets
                .SelectMany(dataset => dataset.DeclaredMeasureFields)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DeclaredDimensionFields = datasets
                .SelectMany(dataset => dataset.DeclaredDimensionFields)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static IReadOnlyList<AssistantColumnDto> BuildExecutiveColumns(string templateKey)
        => DashboardAssistantSemanticCatalog.GetFields(templateKey);

    private async Task<IReadOnlyList<AssistantColumnDto>> LoadColumnsAsync(
        AssistantDatasetDto dataset,
        CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString(dataset.ConnectionName)
            ?? _configuration.GetConnectionString("build")
            ?? throw new InvalidOperationException(
                $"Assistant dataset '{dataset.Title}' uses unknown connection '{dataset.ConnectionName}'.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = new List<AssistantColumnDto>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 60;
            command.CommandText = @"
SELECT
    c.name AS column_name,
    LOWER(t.name) AS data_type,
    c.is_nullable,
    o.type
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.columns c ON c.object_id = o.object_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE s.name = @schema
  AND o.name = @object
  AND o.type IN ('U','V')
ORDER BY c.column_id;";
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128)
            {
                Value = dataset.Schema
            });
            command.Parameters.Add(new SqlParameter("@object", SqlDbType.NVarChar, 128)
            {
                Value = dataset.Object
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var dataType = reader.GetString(1);
                var aliases = BuildColumnAliases(name, dataset.FieldAliases);

                columns.Add(new AssistantColumnDto
                {
                    Name = name,
                    Label = Humanize(name),
                    DataType = dataType,
                    Nullable = reader.GetBoolean(2),
                    Category = Categorize(dataType, name, dataset),
                    Aliases = aliases,
                    DefaultAggregation = dataset.DeclaredMeasureFields.Contains(
                        name,
                        StringComparer.OrdinalIgnoreCase)
                        ? "Sum"
                        : "Sum",
                    ValueFormat = InferValueFormat(name)
                });
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Assistant source {dataset.ConnectionName}:{dataset.Schema}.{dataset.Object} is not a directly queryable table or view. Parameterized functions must be exposed through a validated dashboard payload or wrapper view.");
        }

        return columns;
    }

    private static List<string> BuildColumnAliases(
        string columnName,
        Dictionary<string, string[]> configuredAliases)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            columnName,
            Humanize(columnName)
        };

        foreach (var pair in configuredAliases)
        {
            var keyMatches = string.Equals(pair.Key, columnName, StringComparison.OrdinalIgnoreCase);
            var valueMatches = pair.Value.Any(value =>
                string.Equals(value, columnName, StringComparison.OrdinalIgnoreCase));

            if (!keyMatches && !valueMatches) continue;

            aliases.Add(pair.Key);
            aliases.Add(Humanize(pair.Key));
            foreach (var value in pair.Value)
            {
                aliases.Add(value);
                aliases.Add(Humanize(value));
            }
        }

        return aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList();
    }

    private static int DatasetPreferenceScore(AssistantDatasetDto dataset)
    {
        var key = dataset.Key.ToLowerInvariant();
        var score = 0;
        if (key.StartsWith("executive-")) score += 80;
        if (key.StartsWith("cx_")) score += 70;
        if (key.StartsWith("csr_")) score += 70;
        if (key.StartsWith("its-")) score += 70;
        if (key.StartsWith("csr-v")) score += 10;
        if (!dataset.Title.StartsWith(dataset.Key, StringComparison.OrdinalIgnoreCase)) score += 25;
        if (!string.IsNullOrWhiteSpace(dataset.Role)) score += 5;
        return score;
    }

    private static string ResolveSector(string key, string role)
    {
        var normalizedKey = key.ToLowerInvariant();
        var normalizedRole = role.ToLowerInvariant();

        // The template key is authoritative. Executive templates deliberately use
        // role=csr-page for rendering, but they are not raw CSR semantic datasets.
        if (normalizedKey.StartsWith("executive-"))
            return "cx";

        if (normalizedKey.StartsWith("its-") ||
            normalizedKey.StartsWith("its_") ||
            normalizedRole.StartsWith("its-"))
            return "its";

        if (normalizedKey.StartsWith("csr-") ||
            normalizedKey.StartsWith("csr_") ||
            normalizedRole.StartsWith("csr-"))
            return "csr";

        if (normalizedKey.StartsWith("cx_") ||
            normalizedKey.StartsWith("cx-"))
            return "cx";

        return "";
    }

    public static string NormalizeSector(string? sector)
    {
        var normalized = (sector ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "x" => "cx",
            "exec" => "cx",
            "executive" => "cx",
            "cx/executive" => "cx",
            "sr" => "csr",
            "service" => "csr",
            "it" => "its",
            _ => normalized
        };
    }

    public static string Humanize(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        raw = WordBoundaryRegex.Replace(raw, "$1 $2");
        raw = NonWordRegex.Replace(raw, " ");
        return string.Join(" ", raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length <= 3 && word.All(char.IsUpper)
                ? word
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Categorize(
        string dataType,
        string columnName,
        AssistantDatasetDto dataset)
    {
        if (dataset.DeclaredMeasureFields.Contains(
                columnName,
                StringComparer.OrdinalIgnoreCase))
        {
            return "measure";
        }

        var type = dataType.ToLowerInvariant();
        if (type.Contains("date") || type.Contains("time")) return "date";

        if (dataset.DeclaredDimensionFields.Contains(
                columnName,
                StringComparer.OrdinalIgnoreCase))
        {
            return "dimension";
        }

        // Numeric keys, years, months, sequence numbers and flags are dimensions
        // unless the template explicitly declares them as values.
        var normalizedName = columnName.ToLowerInvariant();
        if (normalizedName is "year" or "month" or "day" or "quarter" ||
            normalizedName.EndsWith("_id", StringComparison.Ordinal) ||
            normalizedName.EndsWith("id", StringComparison.Ordinal) ||
            normalizedName.Contains("code", StringComparison.Ordinal) ||
            normalizedName.Contains("sequence", StringComparison.Ordinal) ||
            normalizedName.StartsWith("is_", StringComparison.Ordinal) ||
            normalizedName.StartsWith("has_", StringComparison.Ordinal))
        {
            return "dimension";
        }

        if (type is "tinyint" or "smallint" or "int" or "bigint" or
            "decimal" or "numeric" or "money" or "smallmoney" or
            "float" or "real")
        {
            return "measure";
        }

        return "dimension";
    }

    private static string InferValueFormat(string columnName)
    {
        var name = columnName.ToLowerInvariant();
        if (name.Contains("amount", StringComparison.Ordinal) ||
            name.Contains("balance", StringComparison.Ordinal) ||
            name.Contains("payment", StringComparison.Ordinal) ||
            name.Contains("paid", StringComparison.Ordinal) ||
            name.Contains("cost", StringComparison.Ordinal) ||
            name.Contains("revenue", StringComparison.Ordinal))
        {
            return "currency";
        }

        if (name.Contains("percent", StringComparison.Ordinal) ||
            name.Contains("percentage", StringComparison.Ordinal) ||
            name.Contains("pct", StringComparison.Ordinal) ||
            name.Contains("rate", StringComparison.Ordinal) ||
            name.Contains("ratio", StringComparison.Ordinal))
        {
            return "percent";
        }

        return "number";
    }

    private static string BuildDescription(string sector, string title, string objectName)
        => $"{title} · {sector.ToUpperInvariant()} semantic source · {objectName}";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
