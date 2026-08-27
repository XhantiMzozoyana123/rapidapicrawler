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

/// <summary>
/// One structured customer-voice signal extracted (by the LLM) from an API's captured
/// discussions/reviews. Rows are the intelligence layer: complaint clusters, feature
/// requests and demand signals are aggregated from these rows deterministically in C#.
/// </summary>
public class CustomerFeedback
{
    public int Id { get; set; }
    public int SearchRunId { get; set; }
    public int ListingId { get; set; }
    /// <summary>positive | negative | neutral | question | request</summary>
    public string Sentiment { get; set; } = string.Empty;
    /// <summary>performance | pricing | documentation | reliability | integration | developer-experience | feature-gap | other</summary>
    public string Topic { get; set; } = string.Empty;
    /// <summary>Short pain-point phrase, empty if none.</summary>
    public string PainPoint { get; set; } = string.Empty;
    /// <summary>Short feature-request phrase, empty if none.</summary>
    public string FeatureRequest { get; set; } = string.Empty;
    /// <summary>0.0 - 1.0 how severe/impactful the signal is.</summary>
    public double Severity { get; set; }
    /// <summary>Short verbatim quote from the captured discussion backing this row.</summary>
    public string Quote { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A query result for a database table (column names plus rows of raw cell values).</summary>
public record TableResult(string[] Columns, List<object?[]> Rows);