using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Implements the RapidAPI scraping flow using a real browser (Playwright):
/// 1) open search URL, 2) collect listing links, 3) open each in a new tab,
/// 4) capture playground HTML, 5) click breadcrumb (API home) + capture,
/// 6) click "Discussions" tab + capture, 7) click Next Page until gone, 8) close tab.
/// </summary>
public partial class PlaywrightRapidApiClient : IRapidApiClient, IAsyncDisposable
{
    private sealed record SearchItem(string? Href, string? Text);

    private readonly ScraperOptions _options;
    private bool _launchedHeadless;

    private const string BaseUrl = "https://rapidapi.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
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

            do
            {
                ct.ThrowIfCancellationRequested();
                var url = $"{BaseUrl}/search?term={Uri.EscapeDataString(keyword)}&sortBy=ByRelevance";
                if (pageNumber > 1)
                    url += $"&page={pageNumber}";

                // The site can be slow or rate-limit headless requests; retry loading until results appear.
                var hrefs = Array.Empty<SearchItem>();
                for (var attempt = 0; attempt < 3 && hrefs.Length == 0 && !ct.IsCancellationRequested; attempt++)
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    try
                    {
                        await page.WaitForSelectorAsync("a.text-inherit[href*='/api/']",
                            new PageWaitForSelectorOptions { Timeout = 20000 });
                    }
                    catch (TimeoutException)
                    {
                        continue; // results not ready yet — reload and retry
                    }
                    await page.WaitForFunctionAsync(
                        "sel => document.querySelectorAll(sel).length > 0",
                        new object[] { "a.text-inherit[href*='/api/']" },
                        new PageWaitForFunctionOptions { Timeout = 20000 });

                    var json = await page.EvalOnSelectorAllAsync<string>(
                        "a.text-inherit.hover\\:no-underline[href*='/api/']",
                        "els => JSON.stringify(els.map(e => ({ href: e.getAttribute('href'), text: (e.textContent || '').trim() })))");
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<SearchItem[]>(
                        json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    hrefs = parsed ?? Array.Empty<SearchItem>();
                }

                var seen = new HashSet<string>();
                foreach (var item in hrefs)
                {
                    if (string.IsNullOrEmpty(item.Href) || !seen.Add(item.Href)) continue;
                    var listing = ParseListing(item.Href!, pageNumber, item.Text ?? "");
                    if (listing != null) yield return listing;
                }

                // Steps 6 & 7: click "Next Page" until the button no longer exists.
                var nextButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next Page" });
                if (await nextButton.CountAsync() == 0 || !await nextButton.First.IsEnabledAsync())
                    yield break;

                await nextButton.First.ClickAsync();
                await page.WaitForTimeoutAsync(2500); // allow the SPA to render the next page
                pageNumber++;
            } while (pageNumber <= 50); // hard safety cap
        }
        finally
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
    }    public async Task<List<CrawledPage>> CaptureListingAsync(ApiListing listing, CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent });
        var pages = new List<CrawledPage>();
        try
        {
            var tab = await context.NewPageAsync(); // Step 2: dedicated "tab" per listing

            // Step 3: playground page full HTML.
            var playgroundUrl = BaseUrl + listing.RelativeUrl;
            await tab.GotoAsync(playgroundUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await tab.WaitForTimeoutAsync(3000); // allow the SPA to hydrate before capturing
            pages.Add(new CrawledPage { PageType = PageTypes.Playground, Url = playgroundUrl, Html = await tab.ContentAsync() });

            // Step 4: breadcrumb link -> API home page, capture full HTML.
            var apiHomeHref = ListingHomeRegex().Replace(listing.RelativeUrl, "$1");
            var breadcrumb = tab.Locator("a.text-breadcrumb-text");
            if (await breadcrumb.CountAsync() > 0)
            {
                try
                {
                    await breadcrumb.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                    await tab.WaitForTimeoutAsync(2500);
                    pages.Add(new CrawledPage { PageType = PageTypes.ApiHome, Url = tab.Url, Html = await tab.ContentAsync() });
                }
                catch (Exception)
                {
                    await tab.GotoAsync(BaseUrl + apiHomeHref, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    await tab.WaitForTimeoutAsync(2000);
                    pages.Add(new CrawledPage { PageType = PageTypes.ApiHome, Url = BaseUrl + apiHomeHref, Html = await tab.ContentAsync() });
                }
            }
            else
            {
                await tab.GotoAsync(BaseUrl + apiHomeHref, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                await tab.WaitForTimeoutAsync(2000);
                pages.Add(new CrawledPage { PageType = PageTypes.ApiHome, Url = BaseUrl + apiHomeHref, Html = await tab.ContentAsync() });
            }

            // Step 5: Discussions tab button -> capture full HTML.
            try
            {
                var discussionsTab = tab.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussions" });
                if (await discussionsTab.CountAsync() == 0)
                    discussionsTab = tab.Locator("button[role='tab']:has-text('Discussions')");
                if (await discussionsTab.CountAsync() > 0)
                {
                    await discussionsTab.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                    await tab.WaitForTimeoutAsync(2500); // allow panel to load
                    pages.Add(new CrawledPage { PageType = PageTypes.Discussions, Url = tab.Url, Html = await tab.ContentAsync() });
                }
            }
            catch (Exception)
            {
                // Discussions may not exist for every API — non-fatal.
            }

            // Step 8: close the tab.
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

    [GeneratedRegex("^(.*?)/playground$")]
    private static partial Regex ListingHomeRegex();

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
    public const string Playground = "Playground";
    public const string ApiHome = "ApiHome";
    public const string Discussions = "Discussions";
}