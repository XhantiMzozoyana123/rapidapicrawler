using Microsoft.EntityFrameworkCore;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Entity Framework Core (first-code approach) DbContext for the MySQL database.
/// Each DbSet maps to the corresponding Domain entity; table names match the
/// original schema (SearchRuns, ApiListings, CrawledPages, AnalysisReports).
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<SearchRun> SearchRuns => Set<SearchRun>();
    public DbSet<ApiListing> ApiListings => Set<ApiListing>();
    public DbSet<CrawledPage> CrawledPages => Set<CrawledPage>();
    public DbSet<AnalysisReport> AnalysisReports => Set<AnalysisReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Map to the existing table names.
        modelBuilder.Entity<SearchRun>().ToTable("SearchRuns");
        modelBuilder.Entity<ApiListing>().ToTable("ApiListings");
        modelBuilder.Entity<CrawledPage>().ToTable("CrawledPages");
        modelBuilder.Entity<AnalysisReport>().ToTable("AnalysisReports");

        // Configure column types for MySQL.
        modelBuilder.Entity<SearchRun>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd(); // AUTO_INCREMENT
            entity.Property(e => e.Keyword).HasMaxLength(255).IsRequired();
            entity.Property(e => e.StartedUtc).HasColumnType("DATETIME(6)");
            entity.Property(e => e.CompletedUtc).HasColumnType("DATETIME(6)");
            entity.Property(e => e.Status).HasMaxLength(50);
        });

        modelBuilder.Entity<ApiListing>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RelativeUrl).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Name).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(255).IsRequired();
            entity.Property(e => e.ApiSlug).HasMaxLength(255).IsRequired();
        });

        modelBuilder.Entity<CrawledPage>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PageType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Url).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Html).HasColumnType("LONGTEXT").IsRequired();
            entity.Property(e => e.CapturedUtc).HasColumnType("DATETIME(6)");
        });

        modelBuilder.Entity<AnalysisReport>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Model).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ReportText).HasColumnType("LONGTEXT").IsRequired();
            entity.Property(e => e.CreatedUtc).HasColumnType("DATETIME(6)");
        });

        base.OnModelCreating(modelBuilder);
    }
}