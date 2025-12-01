using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Scoped service to persist user state across interactive components
/// </summary>
public class UserStateService
{
    private User? _currentUser;
    private bool _isInitialized = false;

    public User? CurrentUser => _currentUser;
    public bool IsInitialized => _isInitialized;

    public void SetUser(User? user)
    {
        _currentUser = user;
        _isInitialized = true;
    }

    public bool IsAuthenticated => _currentUser != null;
    public bool IsAdmin => _currentUser?.IsAdmin ?? false;
}
