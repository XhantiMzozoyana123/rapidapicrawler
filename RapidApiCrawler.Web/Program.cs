using Hangfire;
using Hangfire.Dashboard;
using Hangfire.MemoryStorage;
using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;
using RapidApiCrawler.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Persist DataProtection keys so antiforgery tokens survive container restarts
// (otherwise every restart invalidates all open pages' form tokens: "The key ...
// was not found in the key ring" on POST).
builder.Services.AddDataProtection(options => options.ApplicationName = "RapidApiCrawler")
    .PersistKeysToFileSystem(new DirectoryInfo("/app/data/keys"));

// ---- RapidApiCrawler services (shared clean-architecture layers) ----
var llamaModelPath = Environment.GetEnvironmentVariable("LLAMA_MODEL_PATH") ?? builder.Configuration["Llama:ModelPath"] ?? "";
builder.Services.AddSingleton(new LlamaOptions
{
    ModelPath = llamaModelPath,
    ContextSize = builder.Configuration.GetValue("Llama:ContextSize", 4096),
    MaxTokens = builder.Configuration.GetValue("Llama:MaxTokens", 1200),
    GpuLayerCount = builder.Configuration.GetValue("Llama:GpuLayerCount", 999),
    FlashAttention = builder.Configuration.GetValue("Llama:FlashAttention", true),
});
builder.Services.AddSingleton<ScraperOptions>();
builder.Services.AddSingleton<ILlmAnalyzer, LlamaSharpLlmClient>();
builder.Services.AddSingleton<IRapidApiClient, PlaywrightRapidApiClient>();

// MySQL repository (EF Core code-first; connection string from env var or config)
var mySqlConnection =
    Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ??
    builder.Configuration.GetConnectionString("DefaultConnection") ??
    builder.Configuration["MySql:ConnectionString"] ??
    string.Empty;
builder.Services.AddRapidApiDatabase(mySqlConnection);

builder.Services.AddSingleton<ICsvExporter, CsvExporter>();
builder.Services.AddSingleton<CrawlOrchestrator>();

// ---- Hangfire: background jobs + recurring cron scheduler ----
// The recurring job definition is registered below on every startup, so the schedule
// is always re-created even though the storage is in-memory.
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMemoryStorage());
builder.Services.AddHangfireServer(options =>
    options.WorkerCount = builder.Configuration.GetValue("Hangfire:Workers", 2));

builder.Services.AddSingleton<CrawlJobCoordinator>();
builder.Services.AddTransient<CrawlJobService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

// Hangfire Dashboard (route, e.g. /hangfire) guarded by an optional shared secret.
app.UseHangfireDashboard(
    builder.Configuration["Hangfire:DashboardRoute"] ?? "/hangfire",
    new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter(builder.Configuration) },
        DashboardTitle = "RapidAPI Crawler Jobs",
    });

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// ---- Register the recurring cron job that runs the crawler on a schedule ----
var cronJobId = "rapidapi-crawl";
var cronKeyword = builder.Configuration["Hangfire:DefaultKeyword"] ?? "instagram scraper";
var cronExpression = builder.Configuration["Hangfire:Cron"] ?? "0 */4 * * *";
var cronAnalyze = builder.Configuration.GetValue("Hangfire:RunAnalysis", false);
var maxListings = builder.Configuration.GetValue("Hangfire:MaxListings", 200);

RecurringJob.AddOrUpdate<CrawlJobService>(
    cronJobId,
    job => job.RunCrawlAsync(cronJobId, cronKeyword!, cronAnalyze, maxListings, true),
    cronExpression,
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

app.Logger.LogInformation("Hangfire cron job '{JobId}' scheduled: cron='{Cron}', keyword='{Keyword}'. " +
                          "Dashboard at {Route}", cronJobId, cronExpression, cronKeyword,
                          builder.Configuration["Hangfire:DashboardRoute"] ?? "/hangfire");

app.Run();
