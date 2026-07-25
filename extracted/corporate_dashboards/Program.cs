using corporate_dashboards.Data;
using corporate_dashboards.Models;
using corporate_dashboards.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

/*
 * AppDbContext remains the local SQLite application database.
 * The SQL Server "build" connection is consumed directly by
 * PbiHtmlVisualTemplateConstructorService through SqlConnection.
 */
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("AppDb")
        ?? "Data Source=App_Data/app.db"));

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection("Storage"));

builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("Ollama"));

builder.Services.Configure<RagOptions>(
    builder.Configuration.GetSection("Rag"));

builder.Services.Configure<CxDashboardUploadOptions>(
    builder.Configuration.GetSection("CxDashboardUpload"));

builder.Services.Configure<CxDashboardUploadAccessOptions>(
    builder.Configuration.GetSection("CxDashboardUploadAccess"));

builder.Services.Configure<DashboardAssistantOptions>(
    builder.Configuration.GetSection("DashboardAssistant"));

builder.Services.AddSingleton<IDashboardAssistantCatalogService, DashboardAssistantCatalogService>();
builder.Services.AddSingleton<IDashboardAssistantContextService, DashboardAssistantContextService>();
builder.Services.AddSingleton<IDashboardAssistantPlanner, DashboardAssistantPlanner>();
builder.Services.AddScoped<ICxDashboardUploadAccessService, CxDashboardUploadAccessService>();

builder.Services.AddSingleton<IDocumentQueue, DocumentQueue>();
builder.Services.AddScoped<ITextExtractor, TextExtractor>();
builder.Services.AddScoped<IChunker, Chunker>();
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>();
builder.Services.AddScoped<IRetrievalService, RetrievalService>();
builder.Services.AddHostedService<DocumentProcessorHostedService>();

/*
 * Use the existing visual definitions from:
 * Dashboard:CustomHtml:Templates
 */
var cxVisuals = builder.Configuration
    .GetSection("Dashboard:CustomHtml:Templates")
    .Get<List<CxVisualOptions>>()
    ?? new List<CxVisualOptions>();

cxVisuals = cxVisuals
    .Where(v =>
        v.Enabled
        && !string.IsNullOrWhiteSpace(v.Key)
        && v.Key.StartsWith("cx_", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            Path.GetFileName(v.HtmlFile ?? string.Empty),
            "cx-visual.html",
            StringComparison.OrdinalIgnoreCase))
    .ToList();

builder.Services.Configure<CxVisualsOptions>(options =>
{
    options.ConnectionName =
        builder.Configuration[
            "Dashboard:CustomHtml:CxConstructor:ConnectionName"]
        ?? "build";

    options.TemplateTable =
        builder.Configuration[
            "Dashboard:CustomHtml:CxConstructor:TemplateTable"]
        ?? "dbo.PbiHtmlVisualTemplate";

    options.ChunkTable =
        builder.Configuration[
            "Dashboard:CustomHtml:CxConstructor:ChunkTable"]
        ?? "dbo.PbiHtmlVisualTemplateChunk";

    options.HtmlSourceFile =
        builder.Configuration[
            "Dashboard:CustomHtml:CxConstructor:HtmlSourceFile"]
        ?? "Templates/cx-visual.html";

    options.DefaultHtmlFile =
        builder.Configuration[
            "Dashboard:CustomHtml:CxConstructor:DefaultHtmlFile"]
        ?? "cx-visual.html";

    options.ChunkSize =
        builder.Configuration.GetValue<int?>(
            "Dashboard:CustomHtml:CxConstructor:ChunkSize")
        ?? 30000;

    options.Visuals = cxVisuals;
});

builder.Services.AddScoped<PbiHtmlVisualTemplateConstructorService>();
builder.Services.AddScoped<CsrPbipLayoutSeeder>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");

    var environment =
        scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

    var configuration =
        scope.ServiceProvider.GetRequiredService<IConfiguration>();

    try
    {
        Directory.CreateDirectory(
            Path.Combine(environment.ContentRootPath, "App_Data"));

        var uploadsRoot =
            configuration["Storage:UploadsRoot"]
            ?? "App_Data/uploads";

        Directory.CreateDirectory(
            Path.Combine(environment.ContentRootPath, uploadsRoot));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        /*
         * Do not terminate the IIS process solely because the local
         * document database could not initialize. The error is logged.
         */
        logger.LogError(ex, "Local application database initialization failed.");
    }

    var rebuildOnStartup = configuration.GetValue<bool>(
        "Dashboard:CustomHtml:CxConstructor:RebuildOnStartup");

    if (rebuildOnStartup)
    {
        try
        {
            var constructor = scope.ServiceProvider
                .GetRequiredService<PbiHtmlVisualTemplateConstructorService>();

            await constructor.RebuildCxTemplatesAsync();

            logger.LogInformation(
                "CX visual templates rebuilt successfully during application startup.");
        }
        catch (Exception ex)
        {
            /*
             * Critical fix:
             * a missing HTML source file, SQL permission issue, schema mismatch,
             * or invalid visual definition is logged but no longer produces
             * IIS HTTP 500.30 and stops the whole application.
             */
            logger.LogError(
                ex,
                "CX template rebuild failed during startup. The web application will continue to run.");
        }
    }
    else
    {
        logger.LogInformation(
            "CX template startup rebuild is disabled. Set " +
            "Dashboard:CustomHtml:CxConstructor:RebuildOnStartup=true " +
            "only after the constructor has been verified.");
    }


    var seedCsrPbipLayouts = configuration.GetValue<bool>(
        "Dashboard:CsrPbipImport:SeedOnStartup");

    if (seedCsrPbipLayouts)
    {
        try
        {
            var seeder = scope.ServiceProvider.GetRequiredService<CsrPbipLayoutSeeder>();
            await seeder.SeedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "CSR PBIP layout seeding failed during startup. The web application will continue to run.");
        }
    }

}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Disable BrowserLink in development — it spawns a SignalR connection per iframe,
// which exhausts browser resources when 8+ CSR visual iframes load per page.
// (No explicit middleware call needed — ASPNETCORE_BROWSER_LINK=false in launchSettings
//  tells VS not to inject the BrowserLink script.)

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();