namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Mutable runtime options for the scraper. Toggling <see cref="Headless"/> while the
/// browser is running causes it to relaunch with the new setting on the next use.
/// </summary>
public class ScraperOptions
{
    /// <summary>
    /// When <c>true</c> the browser runs without a window (default). Set to <c>false</c>
    /// to watch the scrape happen in a visible Chromium window.
    /// </summary>
    public bool Headless { get; set; } = true;
}