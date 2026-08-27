namespace RapidApiCrawler.Web.Models;

/// <summary>The AI gap-analysis report for a run.</summary>
public record ReportViewModel(int? RunId, string? Report);

/// <summary>View model for the standalone Keyword Strategy page.</summary>
public record KeywordStrategyViewModel(int? RunId, string? Strategy);
