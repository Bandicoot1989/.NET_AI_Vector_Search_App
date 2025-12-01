using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Script persistence to Azure Blob Storage
/// </summary>
public interface IScriptStorageService
{
    Task InitializeAsync();
    Task SaveScriptsAsync(List<Script> scripts);
    Task<List<Script>> LoadScriptsAsync();
}
