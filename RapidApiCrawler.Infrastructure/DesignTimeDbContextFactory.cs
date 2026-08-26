using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add</c> / <c>dotnet ef database update</c>.
/// Picks the connection string from: MYSQL_CONNECTION_STRING env var, otherwise the default.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ??
            DbDefaults.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, DbDefaults.DefaultMySqlVersion)
            .Options;

        return new AppDbContext(options);
    }
}