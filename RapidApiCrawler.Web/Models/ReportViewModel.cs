namespace RapidApiCrawler.Web.Models;

/// <summary>The AI gap-analysis report for a run.</summary>
public record ReportViewModel(int? RunId, string? Report);

/// <summary>View model for the standalone Keyword Strategy page.</summary>
public record KeywordStrategyViewModel(int? RunId, string? Strategy);

/// <summary>One recommended opportunity + its generated/absent SEO listing doc.</summary>
public record SeoListingEntry(int Number, string Name, string? Doc, bool HasDoc);

/// <summary>View model for the standalone SEO Listing Documentation page.</summary>
public record SeoListingViewModel(int? RunId, List<SeoListingEntry> Entries, bool HasReport, string? ReportError);
