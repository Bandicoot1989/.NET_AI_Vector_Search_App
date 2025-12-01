using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for context document search (Excel data for RAG)
/// </summary>
public interface IContextService
{
    Task InitializeAsync();
    Task<List<ContextDocument>> SearchAsync(string query, int topResults = 5);
    Task<(int imported, string message)> ImportExcelAsync(Stream fileStream, string fileName, string category, string uploadedBy);
    Task<List<ContextFile>> GetFilesAsync();
    Task<List<ContextDocument>> GetDocumentsByFileAsync(string fileName);
    Task DeleteFileAsync(string fileName);
    int GetDocumentCount();
    Task<List<string>> GetCategoriesAsync();
}
