using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Implements the RapidAPI scraping flow using a real browser (Playwright) + HtmlAgilityPack:
/// 1) open search URL, 2) extract all '/api/' listing links with regex into a hashmap,
/// clicking "Next Page" until exhausted, 3) for each link: extract the API overview
/// section from /{provider}/api/{slug}, then extract only the comments container from
/// /{provider}/api/{slug}/discussions per pagination page (clicking "Next Page"),
/// 4) close tab.
/// </summary>
public partial class PlaywrightRapidApiClient : IRapidApiClient, IAsyncDisposable
{
    private readonly ScraperOptions _options;
    private bool _launchedHeadless;

    private const string BaseUrl = "https://rapidapi.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxDiscussionListPages = 50;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightRapidApiClient(ScraperOptions options)
    {
        _options = options;
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        await _browserLock.WaitAsync(ct);
        try
        {
            // If the headless preference changed while a browser was running, relaunch it.
            if (_browser is { IsConnected: true } && _launchedHeadless != _options.Headless)
            {
                try { await _browser.CloseAsync(); } catch { /* ignore */ }
                _browser = null;
            }

            if (_browser is { IsConnected: true }) return _browser;
            _playwright ??= await Playwright.CreateAsync();
            _launchedHeadless = _options.Headless;
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });
            return _browser;
        }
        finally { _browserLock.Release(); }
    }

    public async IAsyncEnumerable<ApiListing> PopularListingsAsync(
        int runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        });
        try
        {
            // Crawl the "Popular APIs" collection page (follows "Next Page" if present).
            await foreach (var listing in CollectListingsFromCollectionAsync(
                context, $"{BaseUrl}/collection/popular-apis", 1, ct))
            {
                yield return listing;
            }
        }
        finally
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens <paramref name="startUrl"/>, extracts all '/api/' listing links via HtmlAgilityPack +
    /// regex into a hashmap (deduped), clicking the "Next Page" button across pages.
    /// </summary>
    private static async IAsyncEnumerable<ApiListing> CollectListingsFromCollectionAsync(
        IBrowserContext context, string startUrl, int pageNumber,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var page = await context.NewPageAsync();
        try
        {
            // Hashmap of unique listing links (href -> parsed listing) — dedupes repeats.
            var linkMap = new Dictionary<string, ApiListing>(StringComparer.OrdinalIgnoreCase);
            var currentUrl = startUrl;

            do
            {
                ct.ThrowIfCancellationRequested();

                var rawHtml = "";
                for (var attempt = 0; attempt < 3 && string.IsNullOrEmpty(rawHtml) && !ct.IsCancellationRequested; attempt++)
                {
                    await page.GotoAsync(currentUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    try
                    {
                        await page.WaitForSelectorAsync("a[href*='/api/']",
                            new PageWaitForSelectorOptions { Timeout = 20000 });
                        rawHtml = await page.ContentAsync();
                    }
                    catch (TimeoutException)
                    {
                        // results not ready yet — reload and retry
                    }
                }
                if (string.IsNullOrEmpty(rawHtml))
                    break;

                // Extract links via HtmlAgilityPack + a regex filter for '/api/' hrefs,
                // adding each unique one into the hashmap.
                foreach (var listing in ExtractListingLinks(rawHtml, pageNumber))
                    linkMap.TryAdd(listing.RelativeUrl, listing);

                // Click the button containing the "Next Page" keyword.
                var nextButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next Page" });
                if (await nextButton.CountAsync() == 0 || !await nextButton.First.IsEnabledAsync())
                    break;

                await nextButton.First.ClickAsync();
                await page.WaitForTimeoutAsync(2500); // allow the SPA to render the next page

                // On collection/search pages pagination is reflected in the URL query string.
                pageNumber++;
                currentUrl = pageNumber > 1 && !currentUrl.Contains("page=")
                    ? $"{currentUrl}{(currentUrl.Contains('?') ? "&" : "?")}page={pageNumber}"
                    : System.Text.RegularExpressions.Regex.Replace(currentUrl, @"([?&])page=\d+", $"$1page={pageNumber}");
            } while (pageNumber <= 50); // hard safety cap

            foreach (var listing in linkMap.Values)
            {
                ct.ThrowIfCancellationRequested();
                yield return listing;
            }
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ApiListing> SearchListingsAsync(
        string keyword, int runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        });
        try
        {
            var page = await context.NewPageAsync();
            int pageNumber = 1;

            // ---- PHASE 1: collect every listing link first, across all search pages. ----
            // Hashmap of unique listing links (href -> parsed listing) — dedupes repeats.
            var linkMap = new Dictionary<string, ApiListing>(StringComparer.OrdinalIgnoreCase);

            do
            {
                ct.ThrowIfCancellationRequested();
                var url = $"{BaseUrl}/search?term={Uri.EscapeDataString(keyword)}&sortBy=ByRelevance";
                if (pageNumber > 1)
                    url += $"&page={pageNumber}";

                var rawHtml = "";
                for (var attempt = 0; attempt < 3 && string.IsNullOrEmpty(rawHtml) && !ct.IsCancellationRequested; attempt++)
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    try
                    {
                        await page.WaitForSelectorAsync("a[href*='/api/']",
                            new PageWaitForSelectorOptions { Timeout = 20000 });
                        rawHtml = await page.ContentAsync();
                    }
                    catch (TimeoutException)
                    {
                        // results not ready yet — reload and retry
                    }
                }
                if (string.IsNullOrEmpty(rawHtml))
                    yield break;

                // Extract links via HtmlAgilityPack + a regex filter for '/api/' hrefs,
                // adding each unique one into the hashmap.
                foreach (var listing in ExtractListingLinks(rawHtml, pageNumber))
                    linkMap.TryAdd(listing.RelativeUrl, listing);

                // Click the button containing the "Next Page" keyword.
                var nextButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next Page" });
                if (await nextButton.CountAsync() == 0 || !await nextButton.First.IsEnabledAsync())
                    break;

                await nextButton.First.ClickAsync();
                await page.WaitForTimeoutAsync(2500); // allow the SPA to render the next page
                pageNumber++;
            } while (pageNumber <= 50); // hard safety cap

            // ---- PHASE 2: yield every collected listing from the hashmap. ----
            foreach (var listing in linkMap.Values)
            {
                ct.ThrowIfCancellationRequested();
                yield return listing;
            }
        }
        finally
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the full search-page HTML with HtmlAgilityPack, runs a regex over every
    /// anchor href looking for the '/api/' keyword, and converts hits into ApiListings.
    /// Example link shape: /letscrape-6bRBa3QguO5/api/real-time-amazon-data/playground
    /// </summary>
    private static IEnumerable<ApiListing> ExtractListingLinks(string html, int pageNumber)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
        {
            var href = anchor.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href) || !href.Contains("/api/"))
                continue;
            if (!seen.Add(href))
                continue;

            var listing = ParseListing(href, pageNumber, anchor.InnerText);
            if (listing != null)
                yield return listing;
        }
    }
    
    public async Task<List<CrawledPage>> CaptureListingAsync(ApiListing listing, CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent });
        var pages = new List<CrawledPage>();
        try
        {
            var tab = await context.NewPageAsync();

            // ---- Step 1: API home -> extract ONLY the API overview <section> HTML. ----
            // baseUrl https://rapidapi.com/{provider}/api + /{api-name}/
            var apiHomeUrl = $"{BaseUrl}/{listing.Provider}/api/{listing.ApiSlug}";
            await tab.GotoAsync(apiHomeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await tab.WaitForTimeoutAsync(3000); // allow the SPA to hydrate

            const string overviewSelector = "section.flex.flex-col.gap-4.pt-5.transition-all";
            try
            {
                await tab.WaitForSelectorAsync(overviewSelector,
                    new PageWaitForSelectorOptions { Timeout = 15000 });
                var overviewHtml = await tab.Locator(overviewSelector).First
                    .EvaluateAsync<string>("el => el.outerHTML");
                if (!string.IsNullOrWhiteSpace(overviewHtml))
                    pages.Add(new CrawledPage { PageType = PageTypes.ApiOverview, Url = tab.Url, Html = overviewHtml });
            }
            catch (TimeoutException)
            {
                // Overview section not found — non-fatal, continue to discussions.
            }

            // ---- Step 2: discussions -> navigate directly and extract ONLY the ----
            // comments inside <div class="mb-6 w-full items-center justify-between">.
            var discussionsUrl = $"{apiHomeUrl}/discussions";
            await tab.GotoAsync(discussionsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await tab.WaitForTimeoutAsync(3000); // allow the SPA to hydrate

            const string commentsSelector = "div.mb-6.w-full.items-center.justify-between";

            string? previousFingerprint = null;
            for (var listPage = 1; listPage <= MaxDiscussionListPages && !ct.IsCancellationRequested; listPage++)
            {
                ct.ThrowIfCancellationRequested();

                string? commentsHtml = null;
                try
                {
                    commentsHtml = await tab.Locator(commentsSelector).First
                        .EvaluateAsync<string>("el => el.outerHTML");
                }
                catch (Exception)
                {
                    // Comments container not present on this page — nothing more to grab.
                }

                if (string.IsNullOrWhiteSpace(commentsHtml))
                    break;

                var fingerprint = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(commentsHtml)));
                if (previousFingerprint == fingerprint)
                    break; // no new content rendered after the last click — done
                pages.Add(new CrawledPage { PageType = PageTypes.Discussions, Url = tab.Url, Html = commentsHtml });
                previousFingerprint = fingerprint;

                // Click the button containing the keyword "Next Page".
                var nextButton = tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next Page" });
                if (await nextButton.CountAsync() == 0 || !await nextButton.First.IsEnabledAsync())
                    break; // no more pages

                await nextButton.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                await tab.WaitForTimeoutAsync(2500); // allow the SPA to render the next page
            }

            // Close the tab.
            await tab.CloseAsync();
        }
        finally
        {
            await context.CloseAsync();
        }
        return pages;
    }

    private static ApiListing? ParseListing(string href, int pageNumber, string? name = null)
    {
        var match = ListingRegex().Match(href);
        if (!match.Success) return null;
        return new ApiListing
        {
            RelativeUrl = href,
            Name = string.IsNullOrWhiteSpace(name)
                ? match.Groups["slug"].Value
                : StripHtmlEntities(name!),
            Provider = match.Groups["provider"].Value,
            ApiSlug = match.Groups["slug"].Value,
            SearchPage = pageNumber
        };
    }

    private static string StripHtmlEntities(string s)
        => s.Replace("<em>", "").Replace("</em>", "").Replace("&amp;", "&").Trim();

    [GeneratedRegex("^/(?<provider>[^/]+)/(?:hub/)?api/(?<slug>[^/?]+)(?:/playground)?")]
    private static partial Regex ListingRegex();

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) { try { await _browser.CloseAsync(); } catch { } }
        _playwright?.Dispose();
        _browserLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class PageTypes
{
    public const string ApiOverview = "ApiOverview";
    public const string Discussions = "Discussions";
}