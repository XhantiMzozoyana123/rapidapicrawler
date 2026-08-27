using System.Text;
using System.Text.RegularExpressions;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Application;

/// <summary>One computed keyword statistic.</summary>
public sealed record KeywordStat(
    string Term,
    int CoverageListings,
    int TotalListings,
    double CoveragePercent,
    int DemandMentions);

/// <summary>
/// Keyword Intelligence engine (deterministic, no LLM): unigram + bigram frequency
/// extraction from competitor listing overviews, cross-referenced with structured
/// customer-signal mentions. The LLM only ever interprets these computed facts.
/// </summary>
public static class KeywordAnalyzer
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","for","with","from","that","this","these","those","is",
        "are","was","were","be","been","being","to","of","in","on","at","by","as","it","its",
        "you","your","our","we","they","their","can","will","not","no","yes","if","then","than",
        "more","most","other","some","any","all","use","using","used","get","also","into","up",
        "out","about","over","under","between","within","without","through","during","before",
        "after","above","below","which","who","whom","what","when","where","why","how","api",
        "apis","data","service","services","simple","easy","powerful","best","new","one","two",
        "per","via","such","like","just","only","very","much","many","support","supports",
        "provide","provides","allow","allows","based","access","includes","including"
    };

    /// <summary>Computes keyword statistics from overviews + customer feedback.</summary>
    public static List<KeywordStat> ComputeStats(
        List<ApiListing> listings,
        IReadOnlyDictionary<int, string> overviewTextByListing,
        List<CustomerFeedback> feedbackRows)
    {
        var withOverviews = listings.Where(l => overviewTextByListing.ContainsKey(l.Id)).ToList();
        if (withOverviews.Count == 0) return new List<KeywordStat>();

        static IEnumerable<string> Terms(string text)
        {
            var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z0-9+#-]{2,}")
                .Select(m => m.Value)
                .Where(w => !Stopwords.Contains(w))
                .ToList();
            for (var i = 0; i < words.Count; i++)
            {
                yield return words[i];
                if (i + 1 < words.Count)
                    yield return $"{words[i]} {words[i + 1]}"; // bigram — the SEO sweet spot
            }
        }

        // term -> set of listing IDs whose overview contains it
        var coverage = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in withOverviews)
        {
            foreach (var term in Terms(overviewTextByListing[l.Id]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!coverage.TryGetValue(term, out var set))
                    coverage[term] = set = new HashSet<int>();
                set.Add(l.Id);
            }
        }

        // Demand signal: how many extracted customer feedback rows reference the term.
        var feedbackCorpus = string.Join(" ", feedbackRows.Select(f =>
            $"{f.PainPoint} {f.FeatureRequest} {f.Quote}")).ToLowerInvariant();

        return coverage
            .Where(kv => kv.Key.Contains(' ') || kv.Key.Length >= 4) // skip tiny/noisy unigrams
            .Select(kv => new KeywordStat(
                kv.Key,
                kv.Value.Count,
                withOverviews.Count,
                Math.Round(100.0 * kv.Value.Count / withOverviews.Count, 1),
                feedbackCorpus.Split(kv.Key.ToLowerInvariant()).Length - 1))
            .Where(s => s.CoverageListings >= 2) // term must appear in 2+ overviews to matter
            .OrderByDescending(s => s.CoverageListings).ThenByDescending(s => s.DemandMentions)
            .Take(25)
            .ToList();
    }

    /// <summary>Renders the stats as the verified-facts block injected into LLM prompts.</summary>
    public static string BuildFacts(List<KeywordStat> stats)
    {
        if (stats.Count == 0)
            return "VERIFIED KEYWORD DATA: no keyword appeared in 2+ listing overviews.";

        var sb = new StringBuilder();
        sb.AppendLine($"VERIFIED KEYWORD DATA (computed programmatically from {stats[0].TotalListings} captured listing overviews — cite exactly; do not invent keywords):");
        foreach (var s in stats)
            sb.AppendLine($"  - \"{s.Term}\": appears in {s.CoverageListings}/{s.TotalListings} overviews " +
                          $"({s.CoveragePercent}%), {s.DemandMentions} customer-signal mention(s)");
        sb.AppendLine("Interpretation guide: high coverage = crowded/expected term; " +
                      "low coverage + high demand mentions = differentiation opportunity.");
        return sb.ToString();
    }
}
