using Microsoft.Extensions.Logging;
using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;

namespace RapidApiCrawler
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Read MySQL connection string from env var (or default).
            var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? string.Empty;
            builder.Services.AddRapidApiDatabase(connectionString);

            // Remote LLM via Ollama HTTP API — point OLLAMA_URL at the VPS host.
            builder.Services.AddSingleton(new ScraperOptions { Headless = true });
            builder.Services.AddSingleton<ILlmAnalyzer, OllamaLlmClient>();
            builder.Services.AddSingleton<IRapidApiClient, PlaywrightRapidApiClient>();
            builder.Services.AddSingleton<ICsvExporter, CsvExporter>();
            builder.Services.AddSingleton<CrawlOrchestrator>();
            builder.Services.AddSingleton<MainPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
