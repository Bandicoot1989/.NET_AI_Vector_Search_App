using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for SharePoint document library operations
/// </summary>
public interface ISharePointService
{
    /// <summary>
    /// Initialize the SharePoint service (authenticate, verify access)
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Get all documents from the SharePoint library
    /// </summary>
    Task<List<SharePointDocument>> GetAllDocumentsAsync();

    /// <summary>
    /// Get documents from a specific folder
    /// </summary>
    Task<List<SharePointDocument>> GetDocumentsInFolderAsync(string folderPath);

    /// <summary>
    /// Sync documents from SharePoint to local cache
    /// </summary>
    Task<int> SyncDocumentsAsync();

    /// <summary>
    /// Get document content by ID
    /// </summary>
    Task<string> GetDocumentContentAsync(string documentId);

    /// <summary>
    /// Search documents using semantic search
    /// </summary>
    Task<List<SharePointDocument>> SearchAsync(string query, int topResults = 5);

    /// <summary>
    /// Check if the service is properly configured
    /// </summary>
    bool IsConfigured { get; }
}
