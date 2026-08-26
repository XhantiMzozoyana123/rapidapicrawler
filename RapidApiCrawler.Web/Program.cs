using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;
using RapidApiCrawler.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

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

// MySQL repository (connection string from env var or config)
builder.Services.AddSingleton(new MySqlOptions(
    Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ??
    builder.Configuration["MySql:ConnectionString"] ?? string.Empty));
builder.Services.AddSingleton<ISearchRunRepository>(sp =>
    new MySqlSearchRunRepository(sp.GetRequiredService<MySqlOptions>()));

builder.Services.AddSingleton<ICsvExporter, CsvExporter>();
builder.Services.AddSingleton<CrawlOrchestrator>();
builder.Services.AddSingleton<CrawlSessionStore>();

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

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
