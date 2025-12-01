using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for accessing Atlassian Confluence pages as knowledge base
/// Uses Confluence REST API with Basic Auth or API Token
/// </summary>
public class ConfluenceKnowledgeService : IConfluenceService
{
    private readonly IConfiguration _configuration;
    private readonly EmbeddingClient _embeddingClient;
    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<ConfluenceKnowledgeService> _logger;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private List<ConfluencePage> _pages = new();
    
    private const string CacheBlobName = "confluence-kb-cache.json";
    
    // Confluence configuration
    private readonly string? _baseUrl;
    private readonly string? _username;
    private readonly string? _apiToken;
    private readonly string[]? _spaceKeys;

    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl) && 
                                 !string.IsNullOrEmpty(_username) && 
                                 !string.IsNullOrEmpty(_apiToken);

    public ConfluenceKnowledgeService(
        IConfiguration configuration,
        EmbeddingClient embeddingClient,
        ILogger<ConfluenceKnowledgeService> logger)
    {
        _configuration = configuration;
        _embeddingClient = embeddingClient;
        _logger = logger;
        _httpClient = new HttpClient();

        // Confluence configuration
        _baseUrl = configuration["Confluence:BaseUrl"]?.TrimEnd('/');
        _username = configuration["Confluence:Username"];
        _apiToken = configuration["Confluence:ApiToken"];
        
        // Space keys to sync (comma-separated in config)
        var spaceKeysConfig = configuration["Confluence:SpaceKeys"];
        _spaceKeys = string.IsNullOrEmpty(spaceKeysConfig) 
            ? null 
            : spaceKeysConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Azure Blob Storage for caching
        var connectionString = configuration["AzureStorage:ConnectionString"];
        var containerName = configuration["AzureStorage:ConfluenceCacheContainer"] ?? "confluence-cache";

        if (!string.IsNullOrEmpty(connectionString) && connectionString != "SET_IN_AZURE_APP_SERVICE_CONFIGURATION")
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Configure HTTP client for Confluence API
        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (!IsConfigured) return;

        if (_baseUrl == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION" ||
            _username == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION" ||
            _apiToken == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION")
        {
            return;
        }

        // Basic Auth with API Token (email:api_token for Cloud, username:password for Server)
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_apiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.BaseAddress = new Uri(_baseUrl!);
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Confluence service not configured - skipping initialization. Set Confluence:BaseUrl, Username, and ApiToken");
            return;
        }

        try
        {
            // Create container if not exists
            if (_containerClient != null)
            {
                await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            }

            // Load cached pages
            await LoadCachedPagesAsync();

            _logger.LogInformation("ConfluenceKnowledgeService initialized with {Count} cached pages", _pages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ConfluenceKnowledgeService");
        }
    }

    public async Task<List<ConfluencePage>> GetAllPagesAsync()
    {
        if (_pages.Any())
        {
            return _pages;
        }

        await LoadCachedPagesAsync();
        return _pages;
    }

    public async Task<List<ConfluencePage>> GetPagesInSpaceAsync(string spaceKey)
    {
        var allPages = await GetAllPagesAsync();
        return allPages.Where(p => p.SpaceKey.Equals(spaceKey, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<int> SyncPagesAsync()
    {
        if (!IsConfigured || _httpClient.BaseAddress == null)
        {
            _logger.LogWarning("Confluence client not configured - cannot sync pages");
            return 0;
        }

        try
        {
            var newPages = new List<ConfluencePage>();
            
            if (_spaceKeys == null || !_spaceKeys.Any())
            {
                _logger.LogWarning("No Confluence space keys configured");
                return 0;
            }

            foreach (var spaceKey in _spaceKeys)
            {
                var spacePagse = await FetchPagesFromSpaceAsync(spaceKey);
                newPages.AddRange(spacePagse);
            }

            // Generate embeddings for new pages
            await GenerateEmbeddingsAsync(newPages);

            _pages = newPages;

            // Cache to blob storage
            await SaveCachedPagesAsync();

            _logger.LogInformation("Synced {Count} pages from Confluence", newPages.Count);
            return newPages.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing Confluence pages");
            return 0;
        }
    }

    private async Task<List<ConfluencePage>> FetchPagesFromSpaceAsync(string spaceKey)
    {
        var pages = new List<ConfluencePage>();
        var start = 0;
        var limit = 50;
        var hasMore = true;

        try
        {
            while (hasMore)
            {
                // Confluence Cloud API v2 or REST API v1
                var url = $"/wiki/rest/api/content?spaceKey={spaceKey}&type=page&status=current&expand=body.storage,version,ancestors,metadata.labels&start={start}&limit={limit}";
                
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch pages from space {SpaceKey}: {StatusCode}", spaceKey, response.StatusCode);
                    break;
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonDocument.Parse(json);
                var root = result.RootElement;

                if (root.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        var page = ParseConfluencePage(item, spaceKey);
                        if (page != null)
                        {
                            pages.Add(page);
                        }
                    }
                }

                // Check for pagination
                if (root.TryGetProperty("size", out var size) && size.GetInt32() < limit)
                {
                    hasMore = false;
                }
                else
                {
                    start += limit;
                }

                // Avoid rate limiting
                await Task.Delay(100);
            }

            _logger.LogInformation("Fetched {Count} pages from space {SpaceKey}", pages.Count, spaceKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pages from space {SpaceKey}", spaceKey);
        }

        return pages;
    }

    private ConfluencePage? ParseConfluencePage(JsonElement item, string spaceKey)
    {
        try
        {
            var page = new ConfluencePage
            {
                Id = item.GetProperty("id").GetString() ?? string.Empty,
                Title = item.GetProperty("title").GetString() ?? string.Empty,
                SpaceKey = spaceKey,
                Status = item.TryGetProperty("status", out var status) ? status.GetString() ?? "current" : "current"
            };

            // Get content body
            if (item.TryGetProperty("body", out var body) && body.TryGetProperty("storage", out var storage))
            {
                var htmlContent = storage.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
                page.Content = StripHtmlTags(htmlContent);
            }

            // Get version info
            if (item.TryGetProperty("version", out var version))
            {
                page.Version = version.TryGetProperty("number", out var num) ? num.GetInt32() : 1;
                
                if (version.TryGetProperty("when", out var when))
                {
                    page.Modified = DateTime.Parse(when.GetString() ?? DateTime.UtcNow.ToString());
                }
                
                if (version.TryGetProperty("by", out var by) && by.TryGetProperty("displayName", out var displayName))
                {
                    page.ModifiedBy = displayName.GetString() ?? "Unknown";
                }
            }

            // Get labels
            if (item.TryGetProperty("metadata", out var metadata) && 
                metadata.TryGetProperty("labels", out var labels) &&
                labels.TryGetProperty("results", out var labelResults))
            {
                foreach (var label in labelResults.EnumerateArray())
                {
                    if (label.TryGetProperty("name", out var labelName))
                    {
                        page.Labels.Add(labelName.GetString() ?? "");
                    }
                }
            }

            // Get ancestors (parent pages)
            if (item.TryGetProperty("ancestors", out var ancestors))
            {
                foreach (var ancestor in ancestors.EnumerateArray())
                {
                    if (ancestor.TryGetProperty("title", out var ancestorTitle))
                    {
                        page.Ancestors.Add(ancestorTitle.GetString() ?? "");
                    }
                }
            }

            // Build web URL
            if (item.TryGetProperty("_links", out var links) && links.TryGetProperty("webui", out var webui))
            {
                page.WebUrl = $"{_baseUrl}/wiki{webui.GetString()}";
            }

            return page;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing Confluence page");
            return null;
        }
    }

    private string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Remove script and style elements
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Remove all HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        
        // Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);
        
        // Normalize whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();

        return html;
    }

    public async Task<string> GetPageContentAsync(string pageId)
    {
        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        return page?.Content ?? string.Empty;
    }

    public async Task<List<ConfluencePage>> SearchAsync(string query, int topResults = 5)
    {
        if (!_pages.Any())
        {
            await LoadCachedPagesAsync();
        }

        if (!_pages.Any() || string.IsNullOrWhiteSpace(query))
        {
            return new List<ConfluencePage>();
        }

        try
        {
            // Generate embedding for the query
            var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query);
            var queryVector = queryEmbedding.Value.ToFloats();

            // Calculate cosine similarity for each page
            var results = _pages
                .Where(p => p.Embedding.Length > 0)
                .Select(p => new
                {
                    Page = p,
                    Similarity = CosineSimilarity(queryVector.Span, p.Embedding.Span)
                })
                .OrderByDescending(x => x.Similarity)
                .Take(topResults)
                .Select(x => x.Page)
                .ToList();

            _logger.LogInformation("Confluence search for '{Query}' returned {Count} results", 
                query.Substring(0, Math.Min(30, query.Length)), results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Confluence pages");
            return new List<ConfluencePage>();
        }
    }

    private float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }

    private async Task GenerateEmbeddingsAsync(List<ConfluencePage> pages)
    {
        foreach (var page in pages)
        {
            try
            {
                var text = page.GetSearchableText();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Truncate to avoid token limits
                if (text.Length > 8000)
                {
                    text = text.Substring(0, 8000);
                }

                var embedding = await _embeddingClient.GenerateEmbeddingAsync(text);
                page.Embedding = embedding.Value.ToFloats();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate embedding for page {PageId}", page.Id);
            }
        }
    }

    private async Task LoadCachedPagesAsync()
    {
        if (_containerClient == null) return;

        try
        {
            var blobClient = _containerClient.GetBlobClient(CacheBlobName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No cached Confluence pages found");
                return;
            }

            var response = await blobClient.DownloadContentAsync();
            var storageModels = JsonSerializer.Deserialize<List<ConfluencePageStorageModel>>(
                response.Value.Content.ToString(), _jsonOptions);

            if (storageModels == null) return;

            _pages = storageModels.Select(s => new ConfluencePage
            {
                Id = s.Id,
                Title = s.Title,
                SpaceKey = s.SpaceKey,
                SpaceName = s.SpaceName,
                Content = s.Content,
                Excerpt = s.Excerpt,
                WebUrl = s.WebUrl,
                CreatedBy = s.CreatedBy,
                ModifiedBy = s.ModifiedBy,
                Created = s.Created,
                Modified = s.Modified,
                Version = s.Version,
                Status = s.Status,
                Labels = s.Labels,
                Ancestors = s.Ancestors
            }).ToList();

            // Regenerate embeddings
            await GenerateEmbeddingsAsync(_pages);

            _logger.LogInformation("Loaded {Count} Confluence pages from cache", _pages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cached Confluence pages");
        }
    }

    private async Task SaveCachedPagesAsync()
    {
        if (_containerClient == null) return;

        try
        {
            var blobClient = _containerClient.GetBlobClient(CacheBlobName);

            var storageModels = _pages.Select(p => new ConfluencePageStorageModel
            {
                Id = p.Id,
                Title = p.Title,
                SpaceKey = p.SpaceKey,
                SpaceName = p.SpaceName,
                Content = p.Content,
                Excerpt = p.Excerpt,
                WebUrl = p.WebUrl,
                CreatedBy = p.CreatedBy,
                ModifiedBy = p.ModifiedBy,
                Created = p.Created,
                Modified = p.Modified,
                Version = p.Version,
                Status = p.Status,
                Labels = p.Labels,
                Ancestors = p.Ancestors
            }).ToList();

            var json = JsonSerializer.Serialize(storageModels, _jsonOptions);
            
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("Cached {Count} Confluence pages to blob storage", _pages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Confluence pages cache");
        }
    }
}
