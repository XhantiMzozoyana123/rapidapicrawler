using MySqlConnector;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// MySQL-backed implementation of ISearchRunRepository.
/// Replaces the previous SQLite repository. The database (and all four tables)
/// are created automatically on first use if they do not already exist.
/// All HTML is stored as LONGTEXT so large RapidAPI pages are fully captured.
/// </summary>
public class MySqlSearchRunRepository : ISearchRunRepository
{
    private readonly string _connectionString;

    public MySqlSearchRunRepository(MySqlOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "MySQL connection string is not configured. " +
                "Set MYSQL_CONNECTION_STRING or the 'MySql:ConnectionString' configuration value.");

        _connectionString = options.ConnectionString;
        InitializeAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeAsync()
    {
        using var conn = await OpenAsync();
        // Tables are created in dependency order; all use IF NOT EXISTS so this is idempotent.
        var statements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS SearchRuns (
                Id            INT AUTO_INCREMENT PRIMARY KEY,
                Keyword       VARCHAR(255) NOT NULL,
                StartedUtc    DATETIME(6)  NOT NULL,
                CompletedUtc  DATETIME(6),
                PagesCrawled  INT          NOT NULL DEFAULT 0,
                ListingsFound INT          NOT NULL DEFAULT 0,
                Status        VARCHAR(50)
            )",

            @"CREATE TABLE IF NOT EXISTS ApiListings (
                Id          INT AUTO_INCREMENT PRIMARY KEY,
                SearchRunId INT NOT NULL,
                RelativeUrl VARCHAR(512) NOT NULL,
                Name        TEXT NOT NULL,
                Provider    VARCHAR(255) NOT NULL,
                ApiSlug     VARCHAR(255) NOT NULL,
                SearchPage  INT NOT NULL,
                CONSTRAINT FK_ApiListings_SearchRuns FOREIGN KEY (SearchRunId) REFERENCES SearchRuns(Id) ON DELETE CASCADE,
                INDEX IX_ApiListings_RunId (SearchRunId)
            )",

            @"CREATE TABLE IF NOT EXISTS CrawledPages (
                Id        INT AUTO_INCREMENT PRIMARY KEY,
                ListingId INT NOT NULL,
                PageType  VARCHAR(50) NOT NULL,
                Url       TEXT NOT NULL,
                Html      LONGTEXT NOT NULL,
                CapturedUtc DATETIME(6) NOT NULL,
                CONSTRAINT FK_CrawledPages_ApiListings FOREIGN KEY (ListingId) REFERENCES ApiListings(Id) ON DELETE CASCADE,
                INDEX IX_CrawledPages_ListingId (ListingId)
            )",

            @"CREATE TABLE IF NOT EXISTS AnalysisReports (
                Id          INT AUTO_INCREMENT PRIMARY KEY,
                SearchRunId INT NOT NULL,
                Model       VARCHAR(100) NOT NULL,
                ReportText  LONGTEXT NOT NULL,
                CreatedUtc  DATETIME(6) NOT NULL,
                CONSTRAINT FK_AnalysisReports_SearchRuns FOREIGN KEY (SearchRunId) REFERENCES SearchRuns(Id) ON DELETE CASCADE
            )",
        };

        foreach (var sql in statements)
        {
            using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<int> CreateRunAsync(SearchRun run)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO SearchRuns (Keyword, StartedUtc, CompletedUtc, PagesCrawled, ListingsFound, Status)
            VALUES (@k, @s, @c, @p, @l, @st);
            SELECT LAST_INSERT_ID();";
        using var cmd = new MySqlCommand(sql, conn);
        BindRun(cmd, run);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateRunAsync(SearchRun run)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            UPDATE SearchRuns SET
                StartedUtc    = @s,
                CompletedUtc  = @c,
                PagesCrawled  = @p,
                ListingsFound = @l,
                Status        = @st
            WHERE Id = @id";
        using var cmd = new MySqlCommand(sql, conn);
        BindRun(cmd, run);
        cmd.Parameters.AddWithValue("@id", run.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddListingAsync(ApiListing listing)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO ApiListings (SearchRunId, RelativeUrl, Name, Provider, ApiSlug, SearchPage)
            VALUES (@r, @rel, @n, @pv, @slug, @pg);
            SELECT LAST_INSERT_ID();";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@r", listing.SearchRunId);
        cmd.Parameters.AddWithValue("@rel", listing.RelativeUrl);
        cmd.Parameters.AddWithValue("@n", listing.Name);
        cmd.Parameters.AddWithValue("@pv", listing.Provider);
        cmd.Parameters.AddWithValue("@slug", listing.ApiSlug);
        cmd.Parameters.AddWithValue("@pg", listing.SearchPage);
        listing.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task AddPageAsync(CrawledPage page)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO CrawledPages (ListingId, PageType, Url, Html, CapturedUtc)
            VALUES (@l, @pt, @u, @h, @c);
            SELECT LAST_INSERT_ID();";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@l", page.ListingId);
        cmd.Parameters.AddWithValue("@pt", page.PageType);
        cmd.Parameters.AddWithValue("@u", page.Url);
        cmd.Parameters.AddWithValue("@h", page.Html);
        cmd.Parameters.AddWithValue("@c", page.CapturedUtc);
        page.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task AddReportAsync(AnalysisReport report)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            INSERT INTO AnalysisReports (SearchRunId, Model, ReportText, CreatedUtc)
            VALUES (@r, @m, @rt, @c);
            SELECT LAST_INSERT_ID();";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@r", report.SearchRunId);
        cmd.Parameters.AddWithValue("@m", report.Model);
        cmd.Parameters.AddWithValue("@rt", report.ReportText);
        cmd.Parameters.AddWithValue("@c", report.CreatedUtc);
        report.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }


    public async Task<List<ApiListing>> GetListingsAsync(int runId)
    {
        var results = new List<ApiListing>();
        using var conn = await OpenAsync();
        const string sql = @"
            SELECT Id, SearchRunId, RelativeUrl, Name, Provider, ApiSlug, SearchPage
            FROM ApiListings WHERE SearchRunId = @r ORDER BY Id";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@r", runId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new ApiListing
            {
                Id = reader.GetInt32(0),
                SearchRunId = reader.GetInt32(1),
                RelativeUrl = reader.GetString(2),
                Name = reader.GetString(3),
                Provider = reader.GetString(4),
                ApiSlug = reader.GetString(5),
                SearchPage = reader.GetInt32(6)
            });
        return results;
    }

    public async Task<List<SearchRun>> GetRunsAsync()
    {
        var results = new List<SearchRun>();
        using var conn = await OpenAsync();
        const string sql = @"
            SELECT Id, Keyword, StartedUtc, CompletedUtc, PagesCrawled, ListingsFound, Status
            FROM SearchRuns ORDER BY Id DESC";
        using var cmd = new MySqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new SearchRun
            {
                Id = reader.GetInt32(0),
                Keyword = reader.GetString(1),
                StartedUtc = reader.GetDateTime(2),
                CompletedUtc = reader.IsDBNull(3) ? null : (DateTime?)reader.GetDateTime(3),
                PagesCrawled = reader.GetInt32(4),
                ListingsFound = reader.GetInt32(5),
                Status = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        return results;
    }

    public async Task<string?> GetLatestReportAsync(int runId)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            SELECT ReportText FROM AnalysisReports
            WHERE SearchRunId = @r ORDER BY Id DESC LIMIT 1";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@r", runId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result as string;
    }

    public async Task<int> CountPagesAsync(int runId)
    {
        using var conn = await OpenAsync();
        const string sql = @"
            SELECT COUNT(*) FROM CrawledPages p
            JOIN ApiListings l ON p.ListingId = l.Id
            WHERE l.SearchRunId = @r";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@r", runId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<List<string>> GetTableNamesAsync()
    {
        var names = new List<string>();
        using var conn = await OpenAsync();
        const string sql = @"
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME NOT LIKE 'sqlite_%'
              AND TABLE_NAME NOT LIKE '__EFMigrationsHistory'
            ORDER BY TABLE_NAME";
        using var cmd = new MySqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString("TABLE_NAME"));
        return names;
    }


    public async Task<TableResult> QueryTableAsync(string tableName, int? limit = 200)
    {
        // Guard: only allow tables that actually exist (prevents SQL injection on table name).
        var valid = await GetTableNamesAsync();
        var safe = valid.FirstOrDefault(t => string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase));
        if (safe is null)
            return new TableResult(Array.Empty<string>(), new List<object?[]>());

        // Quote with backticks — table name is verified-safe above.
        var sql = limit.HasValue
            ? $"SELECT * FROM `{safe}` LIMIT {limit.Value}"
            : $"SELECT * FROM `{safe}`";

        var columns = new List<string>();
        var rows = new List<object?[]>();
        using var conn = await OpenAsync();
        using var cmd = new MySqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        return new TableResult(columns.ToArray(), rows);
    }

    private static void BindRun(MySqlCommand cmd, SearchRun run)
    {
        cmd.Parameters.AddWithValue("@k", run.Keyword);
        cmd.Parameters.AddWithValue("@s", run.StartedUtc);
        cmd.Parameters.AddWithValue("@c", (object?)run.CompletedUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p", run.PagesCrawled);
        cmd.Parameters.AddWithValue("@l", run.ListingsFound);
        cmd.Parameters.AddWithValue("@st", (object?)run.Status ?? DBNull.Value);
    }

    public void Dispose() { /* nothing to dispose — connections are per-operation */ }
}

/// <summary>
/// Options for the MySQL repository. Set via MYSQL_CONNECTION_STRING env var
/// or MySql:ConnectionString in configuration. The connection string should
/// include the database name, e.g. "Server=localhost;Database=RapidApiCrawler;..."
/// </summary>
public record MySqlOptions(string ConnectionString)
{
    public string ConnectionString { get; init; } = ConnectionString;
}
