using Microsoft.EntityFrameworkCore;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Shared database defaults used by the Web, MAUI, and CLI entry points as a
/// last-resort fallback connection string.
/// </summary>
public static class DbDefaults
{
    /// <summary>
    /// Default MySQL connection string (code-first approach). Override at runtime
    /// via MYSQL_CONNECTION_STRING, the "ConnectionStrings:DefaultConnection"
    /// config section, or the MySql:ConnectionString config section.
    /// </summary>
    public const string ConnectionString =
        "Server=mysql-db;Port=3306;Database=rapidapicrawlerdb;User=zola;Password=YOUR_PASSWORD;";

    /// <summary>MySQL server version used when the live server version is not auto-detected.</summary>
    public static MySqlServerVersion DefaultMySqlVersion { get; } = new(new Version(8, 0, 36));
}