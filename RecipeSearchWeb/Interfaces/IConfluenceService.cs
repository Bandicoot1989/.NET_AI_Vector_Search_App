using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Atlassian Confluence operations
/// </summary>
public interface IConfluenceService
{
    /// <summary>
    /// Initialize the Confluence service
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Get all pages from configured spaces
    /// </summary>
    Task<List<ConfluencePage>> GetAllPagesAsync();

    /// <summary>
    /// Get pages from a specific space
    /// </summary>
    Task<List<ConfluencePage>> GetPagesInSpaceAsync(string spaceKey);

    /// <summary>
    /// Sync pages from Confluence to local cache
    /// </summary>
    Task<int> SyncPagesAsync();

    /// <summary>
    /// Get page content by ID
    /// </summary>
    Task<string> GetPageContentAsync(string pageId);

    /// <summary>
    /// Search pages using semantic search
    /// </summary>
    Task<List<ConfluencePage>> SearchAsync(string query, int topResults = 5);

    /// <summary>
    /// Check if the service is properly configured
    /// </summary>
    bool IsConfigured { get; }
}
