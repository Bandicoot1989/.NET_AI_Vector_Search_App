using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Knowledge Base article persistence to Azure Blob Storage
/// </summary>
public interface IKnowledgeStorageService
{
    Task InitializeAsync();
    Task SaveArticlesAsync(List<KnowledgeArticle> articles);
    Task<List<KnowledgeArticle>> LoadArticlesAsync();
}
