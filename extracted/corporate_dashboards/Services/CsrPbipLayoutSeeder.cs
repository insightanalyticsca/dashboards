using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

namespace corporate_dashboards.Services;

public sealed class CsrPbipLayoutSeeder
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CsrPbipLayoutSeeder> _logger;

    public CsrPbipLayoutSeeder(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<CsrPbipLayoutSeeder> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var section = _configuration.GetSection("Dashboard:CsrPbipImport");
        if (!section.GetValue<bool>("Enabled")) return;

        var connectionName = section["ConnectionName"]?.Trim() ?? "build";
        var connectionString = _configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException($"Missing connection string: {connectionName}");
        var sharedUserName = section["SharedUserName"]?.Trim() ?? "__csr_pbip__";
        var pageName = section["Page"]?.Trim() ?? "Multi";
        var startVersionId = section.GetValue<int?>("StartVersionId") ?? 192;
        var tileHeight = Math.Max(4, section.GetValue<int?>("TileHeight") ?? 9);
        var autoRefreshSeconds = Math.Max(0, section.GetValue<int?>("AutoRefreshSeconds") ?? 300);
        var overwriteExistingLayouts = section.GetValue<bool?>("OverwriteExistingLayoutsOnStartup") ?? false;
        var manifestSetting = section["ManifestPath"]?.Trim() ?? "wwwroot/csr/csr-pages.manifest.json";
        var manifestPath = Path.IsPathRooted(manifestSetting)
            ? manifestSetting
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, manifestSetting));

        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("CSR PBIP page manifest was not found.", manifestPath);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        if (!doc.RootElement.TryGetProperty("pages", out var pagesElement) || pagesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CSR PBIP manifest has no pages array.");

        var templateMap = _configuration.GetSection("Dashboard:CustomHtml:Templates")
            .GetChildren()
            .Select(x => new
            {
                Key = (x["Key"] ?? "").Trim(),
                ConnectionName = (x["ConnectionName"] ?? "build").Trim(),
                Schema = (x["Schema"] ?? "").Trim(),
                Object = (x["Object"] ?? "").Trim(),
                RefreshSeconds = int.TryParse(x["RefreshSeconds"], out var refresh) ? refresh : autoRefreshSeconds
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        var records = new List<SeedRecord>();
        var ordinal = 0;
        foreach (var pageElement in pagesElement.EnumerateArray())
        {
            var key = pageElement.GetProperty("key").GetString()?.Trim() ?? "";
            var title = pageElement.GetProperty("title").GetString()?.Trim() ?? key;
            if (!templateMap.TryGetValue(key, out var template))
            {
                _logger.LogWarning("Skipping PBIP page because its custom HTML template is not configured: {Key}", key);
                continue;
            }

            var layout = new
            {
                v = 1,
                grid = new[] { new { id = "1", x = 0, y = 0, w = 12, h = tileHeight, minW = 1, minH = 1 } },
                tiles = new Dictionary<string, object>
                {
                    ["1"] = new
                    {
                        dataset = new { connection = template.ConnectionName, schema = template.Schema, obj = template.Object },
                        pivot = new
                        {
                            rows = Array.Empty<string>(),
                            cols = Array.Empty<string>(),
                            vals = Array.Empty<string>(),
                            filters = new Dictionary<string, object>(),
                            dateGroups = new Dictionary<string, string>()
                        },
                        ui = new
                        {
                            agg = "Sum",
                            chartType = "customHtml",
                            maxCells = "200000",
                            auto = true,
                            autoRefreshSeconds = template.RefreshSeconds > 0 ? template.RefreshSeconds : autoRefreshSeconds,
                            sideHidden = true,
                            sideCollapsed = false,
                            focus = false,
                            customHtml = "",
                            customHtmlTemplate = key,
                            slicerSelection = "",
                            manualTitle = title,
                            presentMode = true
                        }
                    }
                }
            };

            records.Add(new SeedRecord(
                startVersionId + ordinal,
                sharedUserName,
                pageName,
                title,
                JsonSerializer.Serialize(layout),
                ordinal));
            ordinal++;
        }

        if (records.Count == 0)
            throw new InvalidOperationException("No CSR PBIP layout records could be constructed.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await EnsureTablesAsync(connection, transaction, cancellationToken);
        var conflicts = new List<long>();
        var inserted = 0;
        var updated = 0;
        var preserved = 0;

        try
        {
            await ExecuteAsync(connection, transaction, "SET IDENTITY_INSERT dbo.DashboardLayoutVersion ON;", cancellationToken);

            foreach (var record in records)
            {
                await using var lookup = new SqlCommand(@"
SELECT UserName, Page
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId = @id;", connection, transaction);
                lookup.Parameters.Add("@id", SqlDbType.BigInt).Value = record.VersionId;
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                string? existingUser = null;
                string? existingPage = null;
                if (await reader.ReadAsync(cancellationToken))
                {
                    existingUser = reader.GetString(0);
                    existingPage = reader.GetString(1);
                }
                await reader.CloseAsync();

                if (existingUser == null)
                {
                    await using var insert = new SqlCommand(@"
INSERT dbo.DashboardLayoutVersion
(
    LayoutVersionId, UserName, Page, Title, LayoutJson, CreatedUtc, Favorite
)
VALUES
(
    @id, @user, @page, @title, @json,
    DATEADD(SECOND, @ordinal, SYSUTCDATETIME()), NULL
);", connection, transaction);
                    AddParams(insert, record);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    inserted++;
                }
                else if (string.Equals(existingUser, sharedUserName, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(existingPage, pageName, StringComparison.OrdinalIgnoreCase))
                {
                    if (overwriteExistingLayouts)
                    {
                        await using var update = new SqlCommand(@"
UPDATE dbo.DashboardLayoutVersion
SET Title = @title,
    LayoutJson = @json
WHERE LayoutVersionId = @id
  AND UserName = @user
  AND Page = @page;", connection, transaction);
                        AddParams(update, record);
                        await update.ExecuteNonQueryAsync(cancellationToken);
                        updated++;
                    }
                    else
                    {
                        // Existing shared rows may contain user-arranged geometry. Keep the
                        // exact LayoutJson so application restart does not restore defaults.
                        preserved++;
                    }
                }
                else
                {
                    conflicts.Add(record.VersionId);
                }
            }

            await ExecuteAsync(connection, transaction, "SET IDENTITY_INSERT dbo.DashboardLayoutVersion OFF;", cancellationToken);

            if (!conflicts.Contains(records[0].VersionId))
            {
                await using var state = new SqlCommand(@"
MERGE dbo.DashboardLayoutState AS target
USING (SELECT @user AS UserName, @page AS Page) AS source
ON target.UserName = source.UserName AND target.Page = source.Page
WHEN MATCHED THEN
    UPDATE SET CurrentVersionId = @versionId, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (UserName, Page, CurrentVersionId)
    VALUES (@user, @page, @versionId);", connection, transaction);
                state.Parameters.Add("@user", SqlDbType.NVarChar, 256).Value = sharedUserName;
                state.Parameters.Add("@page", SqlDbType.NVarChar, 128).Value = pageName;
                state.Parameters.Add("@versionId", SqlDbType.BigInt).Value = records[0].VersionId;
                await state.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await ExecuteAsync(connection, transaction, "SET IDENTITY_INSERT dbo.DashboardLayoutVersion OFF;", cancellationToken); } catch { }
            try { await transaction.RollbackAsync(cancellationToken); } catch { }
            throw;
        }

        if (conflicts.Count > 0)
            _logger.LogWarning("PBIP layout IDs were already owned by other records and were not overwritten: {Ids}", string.Join(", ", conflicts));

        _logger.LogInformation(
            "CSR PBIP layout seed complete. Inserted={Inserted}; Updated={Updated}; Preserved={Preserved}; Conflicts={ConflictCount}; Range={Start}-{End}",
            inserted, updated, preserved, conflicts.Count, records.Min(x => x.VersionId), records.Max(x => x.VersionId));
    }

    private static void AddParams(SqlCommand command, SeedRecord record)
    {
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = record.VersionId;
        command.Parameters.Add("@user", SqlDbType.NVarChar, 256).Value = record.UserName;
        command.Parameters.Add("@page", SqlDbType.NVarChar, 128).Value = record.Page;
        command.Parameters.Add("@title", SqlDbType.NVarChar, 256).Value = record.Title;
        command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = record.LayoutJson;
        command.Parameters.Add("@ordinal", SqlDbType.Int).Value = record.Ordinal;
    }

    private static async Task EnsureTablesAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, @"
IF OBJECT_ID('dbo.DashboardLayoutVersion','U') IS NULL
BEGIN
    CREATE TABLE dbo.DashboardLayoutVersion
    (
        LayoutVersionId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashboardLayoutVersion PRIMARY KEY,
        UserName nvarchar(256) NOT NULL,
        Page nvarchar(128) NOT NULL,
        Title nvarchar(256) NULL,
        LayoutJson nvarchar(max) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_DashboardLayoutVersion_CreatedUtc DEFAULT SYSUTCDATETIME(),
        Favorite bit NULL
    );
    CREATE INDEX IX_DashboardLayoutVersion_User_Page_Created
        ON dbo.DashboardLayoutVersion(UserName, Page, CreatedUtc DESC, LayoutVersionId DESC);
END;
IF COL_LENGTH('dbo.DashboardLayoutVersion','Favorite') IS NULL
    ALTER TABLE dbo.DashboardLayoutVersion ADD Favorite bit NULL;
IF OBJECT_ID('dbo.DashboardLayoutState','U') IS NULL
BEGIN
    CREATE TABLE dbo.DashboardLayoutState
    (
        UserName nvarchar(256) NOT NULL,
        Page nvarchar(128) NOT NULL,
        CurrentVersionId bigint NULL,
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_DashboardLayoutState_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DashboardLayoutState PRIMARY KEY(UserName, Page)
    );
END;", cancellationToken);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SeedRecord(
        long VersionId,
        string UserName,
        string Page,
        string Title,
        string LayoutJson,
        int Ordinal);
}
