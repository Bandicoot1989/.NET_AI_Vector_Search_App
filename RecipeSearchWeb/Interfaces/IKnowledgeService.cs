using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Knowledge Base article operations
/// </summary>
public interface IKnowledgeService
{
    Task InitializeAsync();
    Task<List<KnowledgeArticle>> SearchArticlesAsync(string query, int topResults = 10);
    List<KnowledgeArticle> GetAllArticles();
    List<KnowledgeArticle> GetAllArticlesIncludingInactive();
    List<KnowledgeArticle> GetArticlesByGroup(string group);
    KnowledgeArticle? GetArticleByKBNumber(string kbNumber);
    KnowledgeArticle? GetArticleById(int id);
    Dictionary<string, int> GetGroupsWithCounts();
    Task AddArticleAsync(KnowledgeArticle article);
    Task UpdateArticleAsync(KnowledgeArticle article);
    Task DeleteArticleAsync(int id);
    int GetNextAvailableId();
}
