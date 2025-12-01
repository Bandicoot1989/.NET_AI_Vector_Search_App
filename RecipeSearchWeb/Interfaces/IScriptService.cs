using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Script search and management operations
/// </summary>
public interface IScriptService
{
    Task InitializeAsync();
    Task<List<Script>> SearchScriptsAsync(string query, int topResults = 6);
    List<Script> GetAllScripts();
    List<Script> GetCustomScripts();
    Task AddCustomScriptAsync(Script script);
    Task UpdateCustomScriptAsync(Script script);
    Task DeleteCustomScriptAsync(int scriptKey);
    int GetNextAvailableKey();
}
