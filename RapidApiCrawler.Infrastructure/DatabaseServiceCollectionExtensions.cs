using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Registers the EF Core MySQL DbContext and the MySQL-backed repository.
/// The repository is a singleton that lazily applies EF migrations on first use.
/// </summary>
public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddRapidApiDatabase(this IServiceCollection services, string connectionString)
    {
        var connection = string.IsNullOrWhiteSpace(connectionString)
            ? DbDefaults.ConnectionString
            : connectionString;

        services.AddPooledDbContextFactory<AppDbContext>(options =>
        {
            options.UseMySql(connection, ServerVersion.AutoDetect(connection));
        });

        services.AddSingleton<ISearchRunRepository, MySqlSearchRunRepository>();

        return services;
    }
}