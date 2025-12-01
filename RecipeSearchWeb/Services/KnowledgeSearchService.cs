using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service that handles Knowledge Base article searching using AI embeddings
/// </summary>
public class KnowledgeSearchService : IKnowledgeService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly KnowledgeStorageService _storageService;
    private readonly KnowledgeImageService _imageService;
    private List<KnowledgeArticle> _articles = new();
    private Dictionary<int, float[]> _articleEmbeddings = new();
    private bool _isInitialized = false;

    public KnowledgeSearchService(EmbeddingClient embeddingClient, KnowledgeStorageService storageService, KnowledgeImageService imageService)
    {
        _embeddingClient = embeddingClient;
        _storageService = storageService;
        _imageService = imageService;
    }

    /// <summary>
    /// Initialize the service by loading articles and generating embeddings
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Initialize storage
        await _storageService.InitializeAsync();

        // Load articles from storage
        _articles = await _storageService.LoadArticlesAsync();

        // Generate embeddings for all articles
        foreach (var article in _articles)
        {
            if (!_articleEmbeddings.ContainsKey(article.Id))
            {
                var embeddingText = GetEmbeddingText(article);
                var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
                _articleEmbeddings[article.Id] = embeddingResponse.Value[0].ToFloats().ToArray();
            }
        }

        _isInitialized = true;
    }

    /// <summary>
    /// Get text used for generating embeddings (combines searchable fields)
    /// </summary>
    private string GetEmbeddingText(KnowledgeArticle article)
    {
        return $"{article.Title} {article.ShortDescription} {article.Purpose} {string.Join(" ", article.Tags)}";
    }

    /// <summary>
    /// Search for articles based on a natural language query using AI embeddings
    /// </summary>
    public async Task<List<KnowledgeArticle>> SearchArticlesAsync(string query, int topResults = 10)
    {
        if (!_isInitialized) await InitializeAsync();

        if (string.IsNullOrWhiteSpace(query))
            return _articles.Where(a => a.IsActive).Take(topResults).ToList();

        var queryEmbeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { query });
        var queryVector = queryEmbeddingResponse.Value[0].ToFloats().ToArray();

        var results = _articles
            .Where(a => a.IsActive)
            .Select(article => new
            {
                Article = article,
                Score = _articleEmbeddings.ContainsKey(article.Id) 
                    ? CosineSimilarity(queryVector, _articleEmbeddings[article.Id]) 
                    : 0
            })
            .OrderByDescending(x => x.Score)
            .Take(topResults)
            .Select(x =>
            {
                x.Article.SearchScore = x.Score;
                return x.Article;
            })
            .ToList();

        return results;
    }

    /// <summary>
    /// Filter articles by group/category
    /// </summary>
    public List<KnowledgeArticle> GetArticlesByGroup(string group)
    {
        return _articles
            .Where(a => a.IsActive && a.KBGroup.Equals(group, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.LastUpdated)
            .ToList();
    }

    /// <summary>
    /// Get all active articles
    /// </summary>
    public List<KnowledgeArticle> GetAllArticles()
    {
        return _articles.Where(a => a.IsActive).OrderByDescending(a => a.LastUpdated).ToList();
    }

    /// <summary>
    /// Get ALL articles including inactive (for admin panel)
    /// </summary>
    public List<KnowledgeArticle> GetAllArticlesIncludingInactive()
    {
        return _articles.OrderByDescending(a => a.LastUpdated).ToList();
    }

    /// <summary>
    /// Get all available groups with article counts
    /// </summary>
    public Dictionary<string, int> GetGroupsWithCounts()
    {
        return _articles
            .Where(a => a.IsActive)
            .GroupBy(a => a.KBGroup)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Get article by KB Number
    /// </summary>
    public KnowledgeArticle? GetArticleByKBNumber(string kbNumber)
    {
        return _articles.FirstOrDefault(a => a.KBNumber.Equals(kbNumber, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get article by ID
    /// </summary>
    public KnowledgeArticle? GetArticleById(int id)
    {
        return _articles.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// Add a new article
    /// </summary>
    public async Task AddArticleAsync(KnowledgeArticle article)
    {
        // Generate embedding
        var embeddingText = GetEmbeddingText(article);
        var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
        _articleEmbeddings[article.Id] = embeddingResponse.Value[0].ToFloats().ToArray();

        // Add to collection
        _articles.Add(article);

        // Persist
        await _storageService.SaveArticlesAsync(_articles);
    }

    /// <summary>
    /// Update an existing article
    /// </summary>
    public async Task UpdateArticleAsync(KnowledgeArticle article)
    {
        var existingIndex = _articles.FindIndex(a => a.Id == article.Id);
        if (existingIndex >= 0)
        {
            _articles[existingIndex] = article;
            article.LastUpdated = DateTime.UtcNow;

            // Regenerate embedding
            var embeddingText = GetEmbeddingText(article);
            var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
            _articleEmbeddings[article.Id] = embeddingResponse.Value[0].ToFloats().ToArray();

            await _storageService.SaveArticlesAsync(_articles);
        }
    }

    /// <summary>
    /// Delete an article (soft delete - sets IsActive to false)
    /// </summary>
    public async Task DeleteArticleAsync(int id)
    {
        var article = _articles.FirstOrDefault(a => a.Id == id);
        if (article != null)
        {
            article.IsActive = false;
            await _storageService.SaveArticlesAsync(_articles);
        }
    }

    /// <summary>
    /// Permanently delete an article by KB number (also deletes associated files from storage)
    /// </summary>
    public async Task DeleteArticleAsync(string kbNumber)
    {
        var article = _articles.FirstOrDefault(a => a.KBNumber.Equals(kbNumber, StringComparison.OrdinalIgnoreCase));
        if (article != null)
        {
            // Delete associated files from Azure Storage (images and PDFs)
            await _imageService.DeleteAllFilesForArticleAsync(kbNumber);
            
            // Remove from collection
            _articles.Remove(article);
            await _storageService.SaveArticlesAsync(_articles);
        }
    }

    /// <summary>
    /// Get the next available ID for a new article
    /// </summary>
    public int GetNextAvailableId()
    {
        return _articles.Any() ? _articles.Max(a => a.Id) + 1 : 1;
    }

    /// <summary>
    /// Generate next KB number
    /// </summary>
    public string GenerateNextKBNumber()
    {
        var existingNumbers = _articles
            .Select(a => a.KBNumber)
            .Where(n => n.StartsWith("KB") && n.Length > 2)
            .Select(n => int.TryParse(n[2..], out var num) ? num : 0)
            .ToList();

        var nextNum = existingNumbers.Any() ? existingNumbers.Max() + 1 : 1;
        return $"KB{nextNum:D7}";
    }

    /// <summary>
    /// Force reload articles from storage
    /// </summary>
    public async Task ReloadArticlesAsync()
    {
        _articles = await _storageService.LoadArticlesAsync();
        _articleEmbeddings.Clear();
        
        // Regenerate embeddings
        foreach (var article in _articles)
        {
            if (!_articleEmbeddings.ContainsKey(article.Id))
            {
                try
                {
                    var embeddingText = GetEmbeddingText(article);
                    var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
                    _articleEmbeddings[article.Id] = embeddingResponse.Value[0].ToFloats().ToArray();
                }
                catch
                {
                    // Skip embedding generation on failure
                }
            }
        }
    }

    /// <summary>
    /// Cosine similarity between two vectors
    /// </summary>
    private double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length) return 0;
        
        var dotProduct = vectorA.Zip(vectorB, (a, b) => a * b).Sum();
        var magnitudeA = Math.Sqrt(vectorA.Sum(a => a * a));
        var magnitudeB = Math.Sqrt(vectorB.Sum(b => b * b));
        
        if (magnitudeA == 0 || magnitudeB == 0) return 0;
        
        return dotProduct / (magnitudeA * magnitudeB);
    }

    /// <summary>
    /// Quick text-based search (fallback when embeddings not available)
    /// </summary>
    public List<KnowledgeArticle> QuickSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _articles.Where(a => a.IsActive).Take(10).ToList();

        var queryLower = query.ToLowerInvariant();
        var queryTerms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return _articles
            .Where(a => a.IsActive)
            .Select(article =>
            {
                var score = 0.0;
                var searchableText = $"{article.Title} {article.ShortDescription} {article.Purpose} {article.KBNumber} {string.Join(" ", article.Tags)}".ToLowerInvariant();

                foreach (var term in queryTerms)
                {
                    if (article.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score += 10;
                    if (article.KBNumber.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score += 15;
                    if (article.ShortDescription.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score += 5;
                    if (article.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)))
                        score += 8;
                    if (article.Purpose.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score += 3;
                }

                return new { Article = article, Score = score };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(10)
            .Select(x =>
            {
                x.Article.SearchScore = x.Score;
                return x.Article;
            })
            .ToList();
    }
}
