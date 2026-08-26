namespace RapidApiCrawler.Domain;

public class SearchRun
{
    public int Id { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public int PagesCrawled { get; set; }
    public int ListingsFound { get; set; }
    public string? Status { get; set; } = "Running";
}

public class ApiListing
{
    public int Id { get; set; }
    public int SearchRunId { get; set; }
    /// <summary>e.g. /Glavier/api/youtube138/playground</summary>
    public string RelativeUrl { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ApiSlug { get; set; } = string.Empty;
    public int SearchPage { get; set; }
}

public class CrawledPage
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    /// <summary>Discussions list page snapshot</summary>
    public string PageType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
}

public class AnalysisReport
{
    public int Id { get; set; }
    public int SearchRunId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string ReportText { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A query result for a database table (column names plus rows of raw cell values).</summary>
public record TableResult(string[] Columns, List<object?[]> Rows);