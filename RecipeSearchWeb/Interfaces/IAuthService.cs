using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for user authentication using Azure Easy Auth
/// </summary>
public interface IAuthService
{
    User? GetCurrentUser();
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
    string GetLogoutUrl();
    string GetLoginUrl();
}
