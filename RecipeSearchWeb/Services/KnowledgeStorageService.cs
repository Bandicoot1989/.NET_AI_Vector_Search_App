using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text.Json;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for persisting Knowledge Base articles to Azure Blob Storage
/// Also supports loading from local JSON file for initial seeding
/// </summary>
public class KnowledgeStorageService : IKnowledgeStorageService
{
    private readonly BlobContainerClient _containerClient;
    private readonly IWebHostEnvironment _environment;
    private const string BlobName = "knowledge-articles.json";
    private const string LocalFileName = "knowledge-articles.json";
    private readonly JsonSerializerOptions _jsonOptions;

    public KnowledgeStorageService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _environment = environment;
        var connectionString = configuration["AzureStorage:ConnectionString"];
        var containerName = configuration["AzureStorage:KnowledgeContainerName"] ?? "knowledge";

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Azure Storage connection string not configured");
        }

        var blobServiceClient = new BlobServiceClient(connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Initialize the storage container (create if not exists)
    /// </summary>
    public async Task InitializeAsync()
    {
        await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
    }

    /// <summary>
    /// Save all articles to blob storage
    /// </summary>
    public async Task SaveArticlesAsync(List<KnowledgeArticle> articles)
    {
        var blobClient = _containerClient.GetBlobClient(BlobName);
        
        // Create storage models without embeddings
        var storageModels = articles.Select(a => new KnowledgeArticleStorageModel
        {
            Id = a.Id,
            KBNumber = a.KBNumber,
            Title = a.Title,
            ShortDescription = a.ShortDescription,
            Purpose = a.Purpose,
            Context = a.Context,
            AppliesTo = a.AppliesTo,
            Content = a.Content,
            KBGroup = a.KBGroup,
            KBOwner = a.KBOwner,
            TargetReaders = a.TargetReaders,
            Language = a.Language,
            Tags = a.Tags,
            IsActive = a.IsActive,
            CreatedDate = a.CreatedDate,
            LastUpdated = a.LastUpdated,
            Author = a.Author,
            Images = a.Images,
            SourceDocument = a.SourceDocument,
            OriginalPdfUrl = a.OriginalPdfUrl
        }).ToList();

        var json = JsonSerializer.Serialize(storageModels, _jsonOptions);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    /// <summary>
    /// Load articles from blob storage, falling back to local file if blob is empty
    /// </summary>
    public async Task<List<KnowledgeArticle>> LoadArticlesAsync()
    {
        var articles = new List<KnowledgeArticle>();
        
        // First, try to load from Azure Blob Storage
        try
        {
            var blobClient = _containerClient.GetBlobClient(BlobName);
            if (await blobClient.ExistsAsync())
            {
                var response = await blobClient.DownloadContentAsync();
                var json = response.Value.Content.ToString();
                var storageModels = JsonSerializer.Deserialize<List<KnowledgeArticleStorageModel>>(json, _jsonOptions);
                
                if (storageModels != null && storageModels.Count > 0)
                {
                    articles = storageModels.Select(MapToArticle).ToList();
                }
            }
        }
        catch (Exception)
        {
            // Blob storage failed, will try local file
        }

        // If no articles from blob, try to load from local file and seed to blob
        if (articles.Count == 0)
        {
            articles = await LoadFromLocalFileAsync();
            
            // If we loaded from local file, seed to blob storage
            if (articles.Count > 0)
            {
                try
                {
                    await SaveArticlesAsync(articles);
                }
                catch
                {
                    // Failed to seed, but we still have local articles
                }
            }
        }

        return articles;
    }

    /// <summary>
    /// Load articles from local JSON file in KnowledgeBase folder
    /// </summary>
    private async Task<List<KnowledgeArticle>> LoadFromLocalFileAsync()
    {
        try
        {
            // Look for the file in the project root's KnowledgeBase folder
            var contentRoot = _environment.ContentRootPath;
            var localFilePath = Path.Combine(contentRoot, "..", "KnowledgeBase", LocalFileName);
            
            if (!File.Exists(localFilePath))
            {
                // Also try in the current directory
                localFilePath = Path.Combine(contentRoot, "KnowledgeBase", LocalFileName);
            }

            if (!File.Exists(localFilePath))
            {
                return new List<KnowledgeArticle>();
            }

            var json = await File.ReadAllTextAsync(localFilePath);
            var storageModels = JsonSerializer.Deserialize<List<KnowledgeArticleStorageModel>>(json, _jsonOptions);
            
            if (storageModels == null)
            {
                return new List<KnowledgeArticle>();
            }

            return storageModels.Select(MapToArticle).ToList();
        }
        catch (Exception)
        {
            return new List<KnowledgeArticle>();
        }
    }

    private KnowledgeArticle MapToArticle(KnowledgeArticleStorageModel s) => new()
    {
        Id = s.Id,
        KBNumber = s.KBNumber,
        Title = s.Title,
        ShortDescription = s.ShortDescription,
        Purpose = s.Purpose,
        Context = s.Context,
        AppliesTo = s.AppliesTo,
        Content = s.Content,
        KBGroup = s.KBGroup,
        KBOwner = s.KBOwner,
        TargetReaders = s.TargetReaders,
        Language = s.Language,
        Tags = s.Tags,
        IsActive = s.IsActive,
        CreatedDate = s.CreatedDate,
        LastUpdated = s.LastUpdated,
        Author = s.Author,
        Images = s.Images ?? new(),
        SourceDocument = s.SourceDocument,
        OriginalPdfUrl = s.OriginalPdfUrl
    };
}

/// <summary>
/// Storage model for Knowledge Article (without embedding vectors)
/// </summary>
public class KnowledgeArticleStorageModel
{
    public int Id { get; set; }
    public string KBNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string AppliesTo { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string KBGroup { get; set; } = string.Empty;
    public string KBOwner { get; set; } = string.Empty;
    public string TargetReaders { get; set; } = string.Empty;
    public string Language { get; set; } = "English";
    public List<string> Tags { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Author { get; set; } = string.Empty;
    public List<KBImage> Images { get; set; } = new();
    public string? SourceDocument { get; set; }
    public string? OriginalPdfUrl { get; set; }
}
