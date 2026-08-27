using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// MySQL-backed implementation of ISearchRunRepository using Entity Framework Core
/// (first-code approach). The database schema is owned and created by EF Core
/// migrations: <c>Migrate()</c> is applied lazily the first time the repository is
/// used. All large HTML columns are LONGTEXT so RapidAPI pages are captured in full.
/// </summary>
public class MySqlSearchRunRepository : ISearchRunRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    private static bool _migrated;
    private static readonly object MigrationLock = new();

    public MySqlSearchRunRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
        EnsureMigrated();
    }

    /// <summary>Applies pending EF Core migrations (idempotent) the first time the repository is used.</summary>
    private void EnsureMigrated()
    {
        if (_migrated)
            return;

        lock (MigrationLock)
        {
            if (_migrated)
                return;

            // Database.Migrate() is idempotent; it creates the DB if missing and
            // applies only the pending migrations.
            using var ctx = _factory.CreateDbContext();
            ctx.Database.Migrate();
            _migrated = true;
        }
    }

    // ---- Write operations (EF Core) ----

    public async Task<int> CreateRunAsync(SearchRun run)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.SearchRuns.Add(run);
        await ctx.SaveChangesAsync();
        return run.Id;
    }

    public async Task UpdateRunAsync(SearchRun run)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.SearchRuns.Update(run);
        await ctx.SaveChangesAsync();
    }

    public async Task AddListingAsync(ApiListing listing)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.ApiListings.Add(listing);
        await ctx.SaveChangesAsync();
    }

    public async Task AddPageAsync(CrawledPage page)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.CrawledPages.Add(page);
        await ctx.SaveChangesAsync();
    }

    public async Task AddReportAsync(AnalysisReport report)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.AnalysisReports.Add(report);
        await ctx.SaveChangesAsync();
    }

    // ---- Read operations (EF Core) ----

    public async Task<List<ApiListing>> GetListingsAsync(int runId)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.ApiListings
            .Where(l => l.SearchRunId == runId)
            .OrderBy(l => l.SearchPage)
            .ThenBy(l => l.Id)
            .ToListAsync();
    }

    public async Task<List<SearchRun>> GetRunsAsync()
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.SearchRuns
            .OrderByDescending(r => r.Id)
            .ToListAsync();
    }

    public async Task<string?> GetLatestReportAsync(int runId)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.AnalysisReports
            .Where(r => r.SearchRunId == runId)
            .OrderByDescending(r => r.Id)
            .Select(r => r.ReportText)
            .FirstOrDefaultAsync();
    }

    public async Task<int> CountPagesAsync(int runId)
    {
        await using var ctx = _factory.CreateDbContext();
        return await (from page in ctx.CrawledPages
                      join listing in ctx.ApiListings on page.ListingId equals listing.Id
                      where listing.SearchRunId == runId
                      select page).CountAsync();
    }

    public async Task<List<CrawledPage>> GetPagesForRunAsync(int runId)
    {
        await using var ctx = _factory.CreateDbContext();
        return await (from page in ctx.CrawledPages
                      join listing in ctx.ApiListings on page.ListingId equals listing.Id
                      where listing.SearchRunId == runId
                      orderby page.ListingId, page.Id
                      select page).ToListAsync();
    }

    public async Task ReplaceCustomerFeedbackAsync(int runId, List<CustomerFeedback> items)
    {
        await using var ctx = _factory.CreateDbContext();
        var existing = ctx.CustomerFeedback.Where(f => f.SearchRunId == runId);
        ctx.CustomerFeedback.RemoveRange(existing);
        foreach (var item in items)
        {
            item.SearchRunId = runId;
            ctx.CustomerFeedback.Add(item);
        }
        await ctx.SaveChangesAsync();
    }

    public async Task<List<CustomerFeedback>> GetCustomerFeedbackAsync(int runId)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.CustomerFeedback
            .Where(f => f.SearchRunId == runId)
            .OrderBy(f => f.Id)
            .ToListAsync();
    }

    // ---- Raw table browser (MetaData table inspection) ----

    private string GetConnectionString()
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.Database.GetConnectionString() ?? DbDefaults.ConnectionString;
    }

    private static MySqlConnection OpenConnection(string connectionString)
    {
        var conn = new MySqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    public async Task<List<string>> GetTableNamesAsync()
    {
        var names = new List<string>();
        using var conn = OpenConnection(GetConnectionString());
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
        using var conn = OpenConnection(GetConnectionString());
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

    public async Task<int> ClearTableAsync(string tableName)
    {
        // Guard: only allow tables that actually exist (prevents SQL injection on table name).
        var valid = await GetTableNamesAsync();
        var safe = valid.FirstOrDefault(t => string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase));
        if (safe is null)
            return 0;

        var sql = $"DELETE FROM `{safe}`"; // table name verified-safe above
        using var conn = OpenConnection(GetConnectionString());
        using var cmd = new MySqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> ClearAllDataAsync()
    {
        var valid = await GetTableNamesAsync();
        // Skip EF migration-history; delete children before parents to respect FKs.
        var order = new[] { "CrawledPages", "AnalysisReports", "ApiListings", "SearchRuns" };
        var targets = order
            .Where(t => valid.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Concat(valid.Where(t =>
                !order.Contains(t, StringComparer.OrdinalIgnoreCase) &&
                !t.Equals("__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var total = 0;
        using var conn = OpenConnection(GetConnectionString());
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var table in targets)
            {
                using var cmd = new MySqlCommand($"DELETE FROM `{table}`", conn, (MySqlTransaction)tx);
                total += await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        return total;
    }

    public void Dispose() { /* contexts are created per operation and disposed by the factory */ }
}