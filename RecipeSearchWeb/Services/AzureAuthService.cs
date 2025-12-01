using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Security.Claims;
using System.Text.Json;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Authentication service using Azure App Service Easy Auth
/// Reads user identity from Azure-provided headers
/// </summary>
public class AzureAuthService : IAuthService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    // List of admin email addresses - can be configured in appsettings.json
    private readonly HashSet<string> _adminEmails;

    public AzureAuthService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        
        // Load admin emails from configuration
        var adminEmailsConfig = configuration.GetSection("Authorization:AdminEmails").Get<string[]>() ?? Array.Empty<string>();
        _adminEmails = new HashSet<string>(adminEmailsConfig, StringComparer.OrdinalIgnoreCase);
        
        // Default admin if none configured
        if (_adminEmails.Count == 0)
        {
            _adminEmails.Add("osmany.fajardo@antolin.com");
        }
    }

    /// <summary>
    /// Get the current authenticated user from Azure Easy Auth headers
    /// </summary>
    public User? GetCurrentUser()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;

        // Try to get user info from Easy Auth headers
        var principalName = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
        var principalId = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        
        // Also check ClaimsPrincipal (works in some configurations)
        if (string.IsNullOrEmpty(principalName) && context.User?.Identity?.IsAuthenticated == true)
        {
            principalName = context.User.Identity.Name 
                ?? context.User.FindFirst(ClaimTypes.Email)?.Value
                ?? context.User.FindFirst("preferred_username")?.Value
                ?? context.User.FindFirst("email")?.Value;
            principalId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        if (string.IsNullOrEmpty(principalName))
        {
            return null;
        }

        // Determine role based on admin list
        var isAdmin = _adminEmails.Contains(principalName);

        return new User
        {
            Id = principalId?.GetHashCode() ?? principalName.GetHashCode(),
            Username = principalName,
            FullName = GetDisplayName(context) ?? principalName,
            Role = isAdmin ? UserRole.Admin : UserRole.Tecnico,
            LastLogin = DateTime.Now
        };
    }

    private string? GetDisplayName(HttpContext context)
    {
        // Try to get display name from headers or claims
        var displayName = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
        
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            displayName = context.User.FindFirst(ClaimTypes.GivenName)?.Value
                ?? context.User.FindFirst("name")?.Value
                ?? displayName;
        }

        return displayName;
    }

    /// <summary>
    /// Check if user is authenticated
    /// </summary>
    public bool IsAuthenticated => GetCurrentUser() != null;

    /// <summary>
    /// Check if current user is admin
    /// </summary>
    public bool IsAdmin => GetCurrentUser()?.IsAdmin ?? false;

    /// <summary>
    /// Get logout URL for Azure Easy Auth
    /// </summary>
    public string GetLogoutUrl()
    {
        return "/.auth/logout?post_logout_redirect_uri=/";
    }

    /// <summary>
    /// Get login URL for Azure Easy Auth
    /// </summary>
    public string GetLoginUrl()
    {
        return "/.auth/login/aad";
    }
}
