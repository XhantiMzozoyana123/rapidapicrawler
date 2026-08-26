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

            // Read MySQL connection string from env var (or appsettings).
            var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? string.Empty;
            builder.Services.AddSingleton(new MySqlOptions(connectionString));

            // Local LLM via LLamaSharp — point LLAMA_MODEL_PATH at a .gguf model file.
            var llamaModelPath = Environment.GetEnvironmentVariable("LLAMA_MODEL_PATH") ?? string.Empty;
            builder.Services.AddSingleton(new LlamaOptions { ModelPath = llamaModelPath });
            builder.Services.AddSingleton(new ScraperOptions { Headless = true });
            builder.Services.AddSingleton<ILlmAnalyzer, LlamaSharpLlmClient>();
            builder.Services.AddSingleton<IRapidApiClient, PlaywrightRapidApiClient>();
            builder.Services.AddSingleton<ISearchRunRepository>(sp => new MySqlSearchRunRepository(sp.GetRequiredService<MySqlOptions>()));
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
