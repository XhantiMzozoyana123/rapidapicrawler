using Hangfire.AspNetCore;
using Hangfire.Dashboard;

namespace RapidApiCrawler.Web.Services;

/// <summary>
/// Protects the Hangfire Dashboard. If <c>Hangfire:DashboardSecret</c> is configured,
/// the request must include a matching <c>X-Hangfire-Secret</c> header. When no secret is
/// configured the dashboard is open (matching the rest of the app's unauth'd surface).
/// </summary>
public class HangfireAuthorizationFilter(IConfiguration configuration) : IDashboardAuthorizationFilter
{
    private readonly IConfiguration _configuration = configuration;

    public bool Authorize(DashboardContext context)
    {
        if (context is not AspNetCoreDashboardContext aspNet)
            return true;

        var secret = _configuration["Hangfire:DashboardSecret"] ?? string.Empty;
        if (string.IsNullOrEmpty(secret))
            return true;

        var provided = aspNet.HttpContext.Request.Headers["X-Hangfire-Secret"].ToString();
        return string.Equals(provided, secret, StringComparison.Ordinal);
    }
}