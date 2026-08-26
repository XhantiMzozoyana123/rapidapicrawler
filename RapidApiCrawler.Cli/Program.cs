using Microsoft.Extensions.DependencyInjection;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;
using RapidApiCrawler.Infrastructure;

// Usage:
//   RapidApiCrawler.Cli "instagram scraper" [--analyze] [--api-key KEY] [--conn CONN_STR]
//   RapidApiCrawler.Cli --import-sqlite <sqlite-db-path> [--conn CONN_STR]
//   RapidApiCrawler.Cli --stats [--conn CONN_STR]
//   RapidApiCrawler.Cli --export [--export-dir <path>] [--conn CONN_STR]
//
// Before first run, install Playwright Chromium once:
//   cd RapidApiCrawler.Cli && dotnet playwright install chromium

var keyword = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "instagram scraper";
var analyze = args.Contains("--analyze");
var llamaModelPath = ParseArg(args, "--model") ?? Environment.GetEnvironmentVariable("LLAMA_MODEL_PATH") ?? "";
var gpuLayers = int.TryParse(ParseArg(args, "--gpu-layers"), out var g) ? g : 999;
var contextSize = int.TryParse(ParseArg(args, "--context"), out var ctx) ? ctx : 4096;
var max = int.TryParse(ParseArg(args, "--max"), out var m) ? m : 5;
var headless = !args.Contains("--headed");
var connectionString = ParseArg(args, "--conn") ?? Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? "";

var services = new ServiceCollection();
services.AddSingleton(new MySqlOptions(connectionString));
services.AddSingleton(new LlamaOptions { ModelPath = llamaModelPath, GpuLayerCount = gpuLayers, ContextSize = contextSize });
services.AddSingleton(new ScraperOptions { Headless = headless });
services.AddSingleton<ILlmAnalyzer, LlamaSharpLlmClient>();
services.AddSingleton<IRapidApiClient, PlaywrightRapidApiClient>();
services.AddSingleton<ISearchRunRepository>(sp => new MySqlSearchRunRepository(sp.GetRequiredService<MySqlOptions>()));
services.AddSingleton<ICsvExporter, CsvExporter>();
services.AddSingleton<CrawlOrchestrator>();
await using var provider = services.BuildServiceProvider();

var orchestrator = provider.GetRequiredService<CrawlOrchestrator>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

orchestrator.Progress += (_, e) => Console.WriteLine("[crawl] " + e.Message);

Console.WriteLine($"Keyword : {keyword}");
Console.WriteLine($"Analyze : {analyze}");
Console.WriteLine($"Headless: {(headless ? "yes" : "no")}");
Console.WriteLine($"LLM     : {(llamaModelPath.Length > 0 ? llamaModelPath : "NOT SET (skipping AI)")}");
Console.WriteLine($"GPU     : {(gpuLayers > 0 ? $"on ({gpuLayers} layers)" : "off (CPU only)")}");
Console.WriteLine($"Database: MySQL{(string.IsNullOrEmpty(connectionString) ? " (NOT SET)" : "")}");
Console.WriteLine();

try
{
    if (args.Contains("--import-sqlite"))
    {
        var sqlitePath = ParseArg(args, "--import-sqlite") ?? ParseArg(args, "--sqlite");
        if (string.IsNullOrEmpty(sqlitePath))
        {
            Console.WriteLine("Usage: --import-sqlite <path-to-sqlite-db>");
            return;
        }
        await ImportFromSqlite(sqlitePath, connectionString);
        return;
    }

    if (args.Contains("--stats"))
    {
        var repo = provider.GetRequiredService<ISearchRunRepository>();
        var tables = await repo.GetTableNamesAsync();
        Console.WriteLine("Tables: " + string.Join(", ", tables));
        var runs = await repo.GetRunsAsync();
        foreach (var r in runs)
        {
            var listings = await repo.GetListingsAsync(r.Id);
            var pages = await repo.CountPagesAsync(r.Id);
            Console.WriteLine("Run " + r.Id + " [" + r.Keyword + "] | " + listings.Count + " listings | " + pages + " pages");
        }
        return;
    }

    if (args.Contains("--export"))
    {
        var dir = ParseArg(args, "--export-dir") ?? Path.Combine(AppContext.BaseDirectory, "exports");
        var files = await provider.GetRequiredService<ICsvExporter>().ExportAllToCsvAsync(dir);
        foreach (var f in files)
            Console.WriteLine("Exported: " + f);
        return;
    }

    var run = await orchestrator.RunAsync(keyword, analyze && llamaModelPath.Length > 0, cts.Token, max);
    Console.WriteLine();
    Console.WriteLine("RUN COMPLETE: " + run.ListingsFound + " APIs | " + run.PagesCrawled + " pages");

    var report = await provider.GetRequiredService<ISearchRunRepository>().GetLatestReportAsync(run.Id);
    if (!string.IsNullOrWhiteSpace(report))
    {
        Console.WriteLine();
        Console.WriteLine("========== AI GAP ANALYSIS REPORT ==========");
        Console.WriteLine(report);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}

/// <summary>Imports data from a SQLite db (old version) into MySQL, remapping foreign keys.</summary>
static async Task ImportFromSqlite(string sqlitePath, string mysqlConnStr)
{
    using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqlitePath}");
    await sqlite.OpenAsync();
    using var mysql = new MySqlConnector.MySqlConnection(mysqlConnStr);
    await mysql.OpenAsync();

    // Migrate SearchRuns (preserve original IDs so FK relationships stay consistent)
    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
        "SELECT Id, Keyword, StartedUtc, CompletedUtc, PagesCrawled, ListingsFound, Status FROM SearchRuns", sqlite))
    using (var read = cmd.ExecuteReader())
    {
        while (read.Read())
        {
            using var ins = new MySqlConnector.MySqlCommand(
                "INSERT INTO SearchRuns (Id, Keyword, StartedUtc, CompletedUtc, PagesCrawled, ListingsFound, Status) " +
                "VALUES (@id, @k, @s, @c, @p, @l, @st)", mysql);
            ins.Parameters.AddWithValue("@id", read.GetInt32(0));
            ins.Parameters.AddWithValue("@k", read.GetString(1));
            ins.Parameters.AddWithValue("@s", DateTime.Parse(read.GetString(2)));
            ins.Parameters.AddWithValue("@c", read.IsDBNull(3) ? DBNull.Value : (object)DateTime.Parse(read.GetString(3)));
            ins.Parameters.AddWithValue("@p", read.GetInt32(4));
            ins.Parameters.AddWithValue("@l", read.GetInt32(5));
            ins.Parameters.AddWithValue("@st", read.IsDBNull(6) ? DBNull.Value : (object)read.GetString(6));
            await ins.ExecuteNonQueryAsync();
        }
    }

    // Migrate ApiListings
    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
        "SELECT Id, SearchRunId, RelativeUrl, Name, Provider, ApiSlug, SearchPage FROM ApiListings", sqlite))
    using (var read = cmd.ExecuteReader())
    {
        while (read.Read())
        {
            using var ins = new MySqlConnector.MySqlCommand(
                "INSERT INTO ApiListings (Id, SearchRunId, RelativeUrl, Name, Provider, ApiSlug, SearchPage) " +
                "VALUES (@id, @r, @rel, @n, @pv, @slug, @pg)", mysql);
            ins.Parameters.AddWithValue("@id", read.GetInt32(0));
            ins.Parameters.AddWithValue("@r", read.GetInt32(1));
            ins.Parameters.AddWithValue("@rel", read.GetString(2));
            ins.Parameters.AddWithValue("@n", read.GetString(3));
            ins.Parameters.AddWithValue("@pv", read.GetString(4));
            ins.Parameters.AddWithValue("@slug", read.GetString(5));
            ins.Parameters.AddWithValue("@pg", read.GetInt32(6));
            await ins.ExecuteNonQueryAsync();
        }
    }

    // Migrate CrawledPages
    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
        "SELECT Id, ListingId, PageType, Url, Html, CapturedUtc FROM CrawledPages", sqlite))
    using (var read = cmd.ExecuteReader())
    {
        while (read.Read())
        {
            using var ins = new MySqlConnector.MySqlCommand(
                "INSERT INTO CrawledPages (Id, ListingId, PageType, Url, Html, CapturedUtc) " +
                "VALUES (@id, @l, @pt, @u, @h, @c)", mysql);
            ins.Parameters.AddWithValue("@id", read.GetInt32(0));
            ins.Parameters.AddWithValue("@l", read.GetInt32(1));
            ins.Parameters.AddWithValue("@pt", read.GetString(2));
            ins.Parameters.AddWithValue("@u", read.GetString(3));
            ins.Parameters.AddWithValue("@h", read.GetString(4));
            ins.Parameters.AddWithValue("@c", DateTime.Parse(read.GetString(5)));
            await ins.ExecuteNonQueryAsync();
        }
    }

    // Migrate AnalysisReports
    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
        "SELECT Id, SearchRunId, Model, ReportText, CreatedUtc FROM AnalysisReports", sqlite))
    using (var read = cmd.ExecuteReader())
    {
        while (read.Read())
        {
            using var ins = new MySqlConnector.MySqlCommand(
                "INSERT INTO AnalysisReports (Id, SearchRunId, Model, ReportText, CreatedUtc) " +
                "VALUES (@id, @r, @m, @rt, @c)", mysql);
            ins.Parameters.AddWithValue("@id", read.GetInt32(0));
            ins.Parameters.AddWithValue("@r", read.GetInt32(1));
            ins.Parameters.AddWithValue("@m", read.GetString(2));
            ins.Parameters.AddWithValue("@rt", read.GetString(3));
            ins.Parameters.AddWithValue("@c", DateTime.Parse(read.GetString(4)));
            await ins.ExecuteNonQueryAsync();
        }
    }

    Console.WriteLine("Import from SQLite complete.");
}

static string? ParseArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}