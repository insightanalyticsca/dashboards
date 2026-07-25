using corporate_dashboards.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace corporate_dashboards.Services;

public sealed class PbiHtmlVisualTemplateConstructorService
{
    private const string VisualConfigPath = "Dashboard:CustomHtml:Templates";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<CxVisualsOptions> _options;

    public PbiHtmlVisualTemplateConstructorService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptionsMonitor<CxVisualsOptions> options)
    {
        _configuration = configuration;
        _environment = environment;
        _options = options;
    }

    public async Task RebuildCxTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var visuals = (options.Visuals ?? new List<CxVisualOptions>())
            .Where(v => v.Enabled)
            .ToList();

        if (visuals.Count == 0)
        {
            throw new InvalidOperationException(
                $"No enabled CX visuals were supplied from {VisualConfigPath}.");
        }

        var duplicateKeys = visuals
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .GroupBy(v => v.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate CX visual keys were found: " +
                string.Join(", ", duplicateKeys));
        }

        foreach (var visual in visuals)
        {
            ValidateVisual(visual);
        }

        var htmlPath = ResolveContentPath(options.HtmlSourceFile);
        if (!File.Exists(htmlPath))
        {
            throw new FileNotFoundException(
                "CX visual HTML source file was not found. " +
                "Check Dashboard:CustomHtml:CxConstructor:HtmlSourceFile.",
                htmlPath);
        }

        var html = await File.ReadAllTextAsync(
            htmlPath,
            Encoding.UTF8,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException(
                $"CX visual HTML source file is empty: {htmlPath}");
        }

        var sourceModifiedUtc = File.GetLastWriteTimeUtc(htmlPath);
        var htmlHash = Sha256Hex(html);
        var chunkSize = options.ChunkSize > 0 ? options.ChunkSize : 30000;
        var chunks = Chunk(html, chunkSize).ToArray();

        if (chunks.Length == 0)
        {
            throw new InvalidOperationException(
                "The CX visual HTML source produced no chunks.");
        }

        var templateTable = SafeTwoPartName(
            options.TemplateTable,
            "dbo.PbiHtmlVisualTemplate");

        var chunkTable = SafeTwoPartName(
            options.ChunkTable,
            "dbo.PbiHtmlVisualTemplateChunk");

        var constructorConnectionName = string.IsNullOrWhiteSpace(options.ConnectionName)
            ? "build"
            : options.ConnectionName.Trim();

        var connectionString = _configuration.GetConnectionString(
            constructorConnectionName)
            ?? throw new InvalidOperationException(
                $"Missing connection string: {constructorConnectionName}");

        var commandTimeoutSeconds = Math.Max(
            30,
            _configuration.GetValue<int?>(
                "Timeouts:SqlServerCommandTimeoutSeconds") ?? 300);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transactionBase =
            await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqlTransaction)transactionBase;

        try
        {
            foreach (var visual in visuals)
            {
                var sourceSection = FindVisualConfigurationSection(visual.Key);

                var htmlFile = FirstNotBlank(
                    sourceSection?["HtmlFile"],
                    visual.HtmlFile,
                    options.DefaultHtmlFile,
                    "cx-visual.html");

                var payloadMode = FirstNotBlank(
                    sourceSection?["PayloadMode"],
                    "rawRows");

                /*
                 * ConnectionName stored in PbiHtmlVisualTemplate identifies
                 * the source connection used to query the visual's rpt view.
                 * It is independent of the constructor connection used above.
                 */
                var sourceConnectionName = FirstNotBlank(
                    sourceSection?["ConnectionName"],
                    constructorConnectionName);

                var configJson = BuildConfigJson(sourceSection, visual);

                await UpsertTemplateAsync(
                    connection,
                    transaction,
                    templateTable,
                    visual,
                    htmlFile,
                    payloadMode,
                    sourceConnectionName,
                    htmlHash,
                    chunks.Length,
                    configJson,
                    sourceModifiedUtc,
                    commandTimeoutSeconds,
                    cancellationToken);

                await ReplaceChunksAsync(
                    connection,
                    transaction,
                    chunkTable,
                    visual.Key,
                    chunks,
                    htmlHash,
                    commandTimeoutSeconds,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private IConfigurationSection? FindVisualConfigurationSection(string visualKey)
    {
        return _configuration
            .GetSection(VisualConfigPath)
            .GetChildren()
            .FirstOrDefault(section =>
                string.Equals(
                    section["Key"],
                    visualKey,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildConfigJson(
        IConfigurationSection? sourceSection,
        CxVisualOptions visual)
    {
        if (sourceSection is not null)
        {
            var node = ConfigurationSectionToJsonNode(sourceSection);
            if (node is not null)
            {
                return node.ToJsonString(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null,
                    WriteIndented = false
                });
            }
        }

        return JsonSerializer.Serialize(
            visual,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = false
            });
    }

    private static JsonNode? ConfigurationSectionToJsonNode(
        IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();

        if (children.Count == 0)
        {
            return ScalarToJsonNode(section.Value);
        }

        var isArray = children.All(child =>
            int.TryParse(child.Key, out _));

        if (isArray)
        {
            var array = new JsonArray();

            foreach (var child in children.OrderBy(child => int.Parse(child.Key)))
            {
                array.Add(ConfigurationSectionToJsonNode(child));
            }

            return array;
        }

        var obj = new JsonObject();

        foreach (var child in children)
        {
            obj[child.Key] = ConfigurationSectionToJsonNode(child);
        }

        return obj;
    }

    private static JsonNode? ScalarToJsonNode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        if (long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var decimalValue))
        {
            return JsonValue.Create(decimalValue);
        }

        return JsonValue.Create(value);
    }

    private string ResolveContentPath(string? configuredPath)
    {
        var path = FirstNotBlank(
            configuredPath,
            "Templates/cx-visual.html");

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(
            Path.Combine(_environment.ContentRootPath, path));
    }

    private static void ValidateVisual(CxVisualOptions visual)
    {
        if (string.IsNullOrWhiteSpace(visual.Key))
        {
            throw new InvalidOperationException(
                "Every CX visual must have Key.");
        }

        if (string.IsNullOrWhiteSpace(visual.Role))
        {
            throw new InvalidOperationException(
                $"CX visual {visual.Key} is missing Role.");
        }

        if (string.IsNullOrWhiteSpace(visual.Schema) ||
            string.IsNullOrWhiteSpace(visual.Object))
        {
            throw new InvalidOperationException(
                $"CX visual {visual.Key} is missing Schema/Object.");
        }
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var index = 0; index < value.Length; index += size)
        {
            yield return value.Substring(
                index,
                Math.Min(size, value.Length - index));
        }
    }

    private static string Sha256Hex(string value)
    {
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static async Task UpsertTemplateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string templateTable,
        CxVisualOptions visual,
        string htmlFile,
        string payloadMode,
        string sourceConnectionName,
        string htmlHash,
        int chunkCount,
        string configJson,
        DateTime sourceModifiedUtc,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var sql = $"""
UPDATE tgt WITH (UPDLOCK, HOLDLOCK)
SET
    HtmlFile = @HtmlFile,
    Title = @Title,
    Label = @Label,
    Role = @Role,
    PayloadMode = @PayloadMode,
    ConnectionName = @ConnectionName,
    SourceSchema = @SourceSchema,
    SourceObject = @SourceObject,
    ConfigJson = @ConfigJson,
    HtmlHash = @HtmlHash,
    ChunkCount = @ChunkCount,
    IsActive = 1,
    SourceModifiedUtc = @SourceModifiedUtc,
    GeneratedUtc = SYSUTCDATETIME()
FROM {templateTable} AS tgt
WHERE tgt.VisualKey = @VisualKey;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO {templateTable}
    (
        VisualKey,
        HtmlFile,
        Title,
        Label,
        Role,
        PayloadMode,
        ConnectionName,
        SourceSchema,
        SourceObject,
        ConfigJson,
        HtmlHash,
        ChunkCount,
        IsActive,
        SourceModifiedUtc,
        GeneratedUtc
    )
    VALUES
    (
        @VisualKey,
        @HtmlFile,
        @Title,
        @Label,
        @Role,
        @PayloadMode,
        @ConnectionName,
        @SourceSchema,
        @SourceObject,
        @ConfigJson,
        @HtmlHash,
        @ChunkCount,
        1,
        @SourceModifiedUtc,
        SYSUTCDATETIME()
    );
END;
""";

        await using var command = new SqlCommand(
            sql,
            connection,
            transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        };

        AddNVarChar(command, "@VisualKey", visual.Key, 256);
        AddNVarChar(command, "@HtmlFile", htmlFile, 1024);
        AddNullableNVarChar(command, "@Title", visual.Title, 512);
        AddNullableNVarChar(command, "@Label", visual.Label, 512);
        AddNVarChar(command, "@Role", visual.Role, 256);
        AddNVarChar(command, "@PayloadMode", payloadMode, 128);
        AddNVarChar(command, "@ConnectionName", sourceConnectionName, 256);
        AddNVarChar(command, "@SourceSchema", visual.Schema, 256);
        AddNVarChar(command, "@SourceObject", visual.Object, 512);

        command.Parameters.Add(
            new SqlParameter("@ConfigJson", SqlDbType.NVarChar, -1)
            {
                Value = configJson
            });

        command.Parameters.Add(
            new SqlParameter("@HtmlHash", SqlDbType.VarChar, 64)
            {
                Value = htmlHash
            });

        command.Parameters.Add(
            new SqlParameter("@ChunkCount", SqlDbType.Int)
            {
                Value = chunkCount
            });

        command.Parameters.Add(
            new SqlParameter("@SourceModifiedUtc", SqlDbType.DateTime2)
            {
                Value = sourceModifiedUtc
            });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceChunksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string chunkTable,
        string visualKey,
        IReadOnlyList<string> chunks,
        string htmlHash,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = new SqlCommand(
                         $"DELETE FROM {chunkTable} WHERE VisualKey = @VisualKey;",
                         connection,
                         transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        })
        {
            AddNVarChar(deleteCommand, "@VisualKey", visualKey, 256);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string valuesSql = """
(
    @VisualKey,
    @ChunkOrdinal,
    @HtmlChunk,
    @HtmlHash,
    SYSUTCDATETIME()
)
""";

        for (var index = 0; index < chunks.Count; index++)
        {
            var sql = $"""
INSERT INTO {chunkTable}
(
    VisualKey,
    ChunkOrdinal,
    HtmlChunk,
    HtmlHash,
    GeneratedUtc
)
VALUES
{valuesSql};
""";

            await using var insertCommand = new SqlCommand(
                sql,
                connection,
                transaction)
            {
                CommandTimeout = commandTimeoutSeconds
            };

            AddNVarChar(insertCommand, "@VisualKey", visualKey, 256);

            insertCommand.Parameters.Add(
                new SqlParameter("@ChunkOrdinal", SqlDbType.Int)
                {
                    Value = index + 1
                });

            insertCommand.Parameters.Add(
                new SqlParameter("@HtmlChunk", SqlDbType.NVarChar, -1)
                {
                    Value = chunks[index]
                });

            insertCommand.Parameters.Add(
                new SqlParameter("@HtmlHash", SqlDbType.VarChar, 64)
                {
                    Value = htmlHash
                });

            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddNVarChar(
        SqlCommand command,
        string name,
        string value,
        int size)
    {
        command.Parameters.Add(
            new SqlParameter(name, SqlDbType.NVarChar, size)
            {
                Value = value.Trim()
            });
    }

    private static void AddNullableNVarChar(
        SqlCommand command,
        string name,
        string? value,
        int size)
    {
        command.Parameters.Add(
            new SqlParameter(name, SqlDbType.NVarChar, size)
            {
                Value = string.IsNullOrWhiteSpace(value)
                    ? DBNull.Value
                    : value.Trim()
            });
    }

    private static string FirstNotBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string SafeTwoPartName(
        string? configuredName,
        string fallback)
    {
        var raw = FirstNotBlank(configuredName, fallback);
        var parts = raw.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                "Expected a two-part SQL object name like dbo.TableName, " +
                $"got: {raw}");
        }

        return $"{SafeSqlIdentifier(parts[0])}.{SafeSqlIdentifier(parts[1])}";
    }

    private static string SafeSqlIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "SQL identifier cannot be empty.");
        }

        foreach (var character in value)
        {
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                throw new InvalidOperationException(
                    $"Unsafe SQL identifier: {value}");
            }
        }

        return $"[{value}]";
    }
}