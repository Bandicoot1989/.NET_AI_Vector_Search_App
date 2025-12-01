using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for accessing SharePoint Digitalization KB library and syncing documents
/// Uses Microsoft Graph API with application permissions
/// </summary>
public class SharePointKnowledgeService : ISharePointService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EmbeddingClient _embeddingClient;
    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<SharePointKnowledgeService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private List<SharePointDocument> _documents = new();
    private GraphServiceClient? _graphClient;
    
    private const string CacheBlobName = "sharepoint-kb-cache.json";
    
    // SharePoint configuration
    private readonly string? _siteId;
    private readonly string? _libraryName;
    private readonly string? _tenantId;
    private readonly string? _clientId;
    private readonly string? _clientSecret;

    public bool IsConfigured => !string.IsNullOrEmpty(_tenantId) && 
                                 !string.IsNullOrEmpty(_clientId) && 
                                 !string.IsNullOrEmpty(_clientSecret);

    public SharePointKnowledgeService(
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        EmbeddingClient embeddingClient,
        ILogger<SharePointKnowledgeService> logger)
    {
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _embeddingClient = embeddingClient;
        _logger = logger;

        // SharePoint configuration
        _siteId = configuration["SharePoint:SiteId"];
        _libraryName = configuration["SharePoint:LibraryName"] ?? "KBU";
        _tenantId = configuration["SharePoint:TenantId"];
        _clientId = configuration["SharePoint:ClientId"];
        _clientSecret = configuration["SharePoint:ClientSecret"];

        // Azure Blob Storage for caching
        var connectionString = configuration["AzureStorage:ConnectionString"];
        var containerName = configuration["AzureStorage:SharePointCacheContainer"] ?? "sharepoint-cache";

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
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("SharePoint service not configured - skipping initialization. Set SharePoint:TenantId, ClientId, and ClientSecret");
            return;
        }

        try
        {
            // Create container if not exists
            if (_containerClient != null)
            {
                await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            }

            // Initialize Graph client
            InitializeGraphClient();

            // Load cached documents
            await LoadCachedDocumentsAsync();

            _logger.LogInformation("SharePointKnowledgeService initialized with {Count} cached documents", _documents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SharePointKnowledgeService");
        }
    }

    private void InitializeGraphClient()
    {
        if (string.IsNullOrEmpty(_tenantId) || string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
        {
            _logger.LogWarning("SharePoint client credentials not configured - using cached data only");
            return;
        }

        if (_tenantId == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION" || 
            _clientId == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION" ||
            _clientSecret == "SET_IN_AZURE_APP_SERVICE_CONFIGURATION")
        {
            _logger.LogWarning("SharePoint credentials are placeholder values - service will use cached data only");
            return;
        }

        try
        {
            var credential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            
            _logger.LogInformation("Graph client initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Graph client");
        }
    }

    public async Task<List<SharePointDocument>> GetAllDocumentsAsync()
    {
        if (_documents.Any())
        {
            return _documents;
        }

        await LoadCachedDocumentsAsync();
        return _documents;
    }

    public async Task<List<SharePointDocument>> GetDocumentsInFolderAsync(string folderPath)
    {
        var allDocs = await GetAllDocumentsAsync();
        return allDocs.Where(d => d.FolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<int> SyncDocumentsAsync()
    {
        if (_graphClient == null || string.IsNullOrEmpty(_siteId))
        {
            _logger.LogWarning("Graph client not available or SiteId not configured - cannot sync SharePoint documents");
            return 0;
        }

        try
        {
            var newDocuments = new List<SharePointDocument>();
            
            // Get all drives (document libraries) from the site
            var drives = await _graphClient.Sites[_siteId].Drives.GetAsync();
            
            if (drives?.Value == null)
            {
                _logger.LogWarning("No drives found in SharePoint site");
                return 0;
            }

            // Find the target library
            var targetDrive = drives.Value.FirstOrDefault(d => 
                d.Name?.Equals(_libraryName, StringComparison.OrdinalIgnoreCase) == true);

            if (targetDrive == null)
            {
                _logger.LogWarning("Library '{LibraryName}' not found in SharePoint site", _libraryName);
                return 0;
            }

            // Get all items from the drive recursively
            await GetDriveItemsRecursiveAsync(targetDrive.Id!, null, "", newDocuments);

            // Generate embeddings for new documents
            await GenerateEmbeddingsAsync(newDocuments);

            _documents = newDocuments;

            // Cache to blob storage
            await SaveCachedDocumentsAsync();

            _logger.LogInformation("Synced {Count} documents from SharePoint", newDocuments.Count);
            return newDocuments.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing SharePoint documents");
            return 0;
        }
    }

    private async Task GetDriveItemsRecursiveAsync(string driveId, string? itemId, string currentPath, List<SharePointDocument> documents)
    {
        try
        {
            DriveItemCollectionResponse? response;
            
            if (string.IsNullOrEmpty(itemId))
            {
                // Get root items - use GetAsync() on Root first, then Children
                var rootItem = await _graphClient!.Drives[driveId].Root.GetAsync();
                if (rootItem?.Id == null) return;
                response = await _graphClient!.Drives[driveId].Items[rootItem.Id].Children.GetAsync();
            }
            else
            {
                // Get children of specific folder
                response = await _graphClient!.Drives[driveId].Items[itemId].Children.GetAsync();
            }

            if (response?.Value == null) return;

            foreach (var item in response.Value)
            {
                var itemPath = string.IsNullOrEmpty(currentPath) ? item.Name : $"{currentPath}/{item.Name}";

                if (item.Folder != null)
                {
                    // Recursively process folder
                    await GetDriveItemsRecursiveAsync(driveId, item.Id, itemPath!, documents);
                }
                else if (item.File != null && IsDocumentFile(item.Name))
                {
                    // Process document file
                    var doc = await CreateDocumentFromDriveItemAsync(item, driveId, currentPath);
                    if (doc != null)
                    {
                        documents.Add(doc);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting items from path: {Path}", currentPath);
        }
    }

    private async Task<SharePointDocument?> CreateDocumentFromDriveItemAsync(DriveItem item, string driveId, string folderPath)
    {
        try
        {
            var doc = new SharePointDocument
            {
                Id = item.Id ?? Guid.NewGuid().ToString(),
                Name = item.Name ?? "Unknown",
                Title = Path.GetFileNameWithoutExtension(item.Name) ?? "Unknown",
                WebUrl = item.WebUrl ?? string.Empty,
                ContentType = item.File?.MimeType ?? "unknown",
                Created = item.CreatedDateTime?.DateTime ?? DateTime.UtcNow,
                Modified = item.LastModifiedDateTime?.DateTime ?? DateTime.UtcNow,
                CreatedBy = item.CreatedBy?.User?.DisplayName ?? "Unknown",
                ModifiedBy = item.LastModifiedBy?.User?.DisplayName ?? "Unknown",
                FolderPath = folderPath
            };

            // Extract folder name from path
            var pathParts = folderPath.Split('/');
            doc.Folder = pathParts.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "Root";
            
            // Add folder as tag
            doc.Tags.Add(doc.Folder);

            // Try to get document content
            doc.Content = await GetDocumentContentFromDriveAsync(driveId, item);

            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating document from DriveItem {ItemName}", item.Name);
            return null;
        }
    }

    private async Task<string> GetDocumentContentFromDriveAsync(string driveId, DriveItem item)
    {
        try
        {
            if (item.File == null || item.Id == null) return string.Empty;

            var extension = Path.GetExtension(item.Name)?.ToLowerInvariant();
            
            // Get the file content stream
            var stream = await _graphClient!.Drives[driveId].Items[item.Id].Content.GetAsync();

            if (stream == null) return string.Empty;

            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            return extension switch
            {
                ".docx" => ExtractWordContent(memoryStream),
                ".txt" => await ExtractTextContent(memoryStream),
                ".md" => await ExtractTextContent(memoryStream),
                _ => $"[Document: {item.Name}]"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not extract content from {FileName}", item.Name);
            return string.Empty;
        }
    }

    private string ExtractWordContent(Stream stream)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            
            if (body == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var para in body.Elements<Paragraph>())
            {
                sb.AppendLine(para.InnerText);
            }
            
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting Word document content");
            return string.Empty;
        }
    }

    private async Task<string> ExtractTextContent(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private bool IsDocumentFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".docx" or ".doc" or ".pdf" or ".txt" or ".md" or ".xlsx";
    }

    public async Task<string> GetDocumentContentAsync(string documentId)
    {
        var doc = _documents.FirstOrDefault(d => d.Id == documentId);
        return doc?.Content ?? string.Empty;
    }

    public async Task<List<SharePointDocument>> SearchAsync(string query, int topResults = 5)
    {
        if (!_documents.Any())
        {
            await LoadCachedDocumentsAsync();
        }

        if (!_documents.Any() || string.IsNullOrWhiteSpace(query))
        {
            return new List<SharePointDocument>();
        }

        try
        {
            // Generate embedding for the query
            var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query);
            var queryVector = queryEmbedding.Value.ToFloats();

            // Calculate cosine similarity for each document
            var results = _documents
                .Where(d => d.Embedding.Length > 0)
                .Select(d => new
                {
                    Document = d,
                    Similarity = CosineSimilarity(queryVector.Span, d.Embedding.Span)
                })
                .OrderByDescending(x => x.Similarity)
                .Take(topResults)
                .Select(x => x.Document)
                .ToList();

            _logger.LogInformation("SharePoint search for '{Query}' returned {Count} results", 
                query.Substring(0, Math.Min(30, query.Length)), results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching SharePoint documents");
            return new List<SharePointDocument>();
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

    private async Task GenerateEmbeddingsAsync(List<SharePointDocument> documents)
    {
        foreach (var doc in documents)
        {
            try
            {
                var text = doc.GetSearchableText();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Truncate to avoid token limits
                if (text.Length > 8000)
                {
                    text = text.Substring(0, 8000);
                }

                var embedding = await _embeddingClient.GenerateEmbeddingAsync(text);
                doc.Embedding = embedding.Value.ToFloats();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate embedding for document {DocId}", doc.Id);
            }
        }
    }

    private async Task LoadCachedDocumentsAsync()
    {
        if (_containerClient == null) return;

        try
        {
            var blobClient = _containerClient.GetBlobClient(CacheBlobName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No cached SharePoint documents found");
                return;
            }

            var response = await blobClient.DownloadContentAsync();
            var storageModels = JsonSerializer.Deserialize<List<SharePointDocumentStorageModel>>(
                response.Value.Content.ToString(), _jsonOptions);

            if (storageModels == null) return;

            _documents = storageModels.Select(s => new SharePointDocument
            {
                Id = s.Id,
                Name = s.Name,
                Title = s.Title,
                KBNumber = s.KBNumber,
                Folder = s.Folder,
                FolderPath = s.FolderPath,
                WebUrl = s.WebUrl,
                Content = s.Content,
                ContentType = s.ContentType,
                CreatedBy = s.CreatedBy,
                ModifiedBy = s.ModifiedBy,
                Created = s.Created,
                Modified = s.Modified,
                ApprovalStatus = s.ApprovalStatus,
                AIProcessed = s.AIProcessed,
                Tags = s.Tags
            }).ToList();

            // Regenerate embeddings
            await GenerateEmbeddingsAsync(_documents);

            _logger.LogInformation("Loaded {Count} SharePoint documents from cache", _documents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cached SharePoint documents");
        }
    }

    private async Task SaveCachedDocumentsAsync()
    {
        if (_containerClient == null) return;

        try
        {
            var blobClient = _containerClient.GetBlobClient(CacheBlobName);

            var storageModels = _documents.Select(d => new SharePointDocumentStorageModel
            {
                Id = d.Id,
                Name = d.Name,
                Title = d.Title,
                KBNumber = d.KBNumber,
                Folder = d.Folder,
                FolderPath = d.FolderPath,
                WebUrl = d.WebUrl,
                Content = d.Content,
                ContentType = d.ContentType,
                CreatedBy = d.CreatedBy,
                ModifiedBy = d.ModifiedBy,
                Created = d.Created,
                Modified = d.Modified,
                ApprovalStatus = d.ApprovalStatus,
                AIProcessed = d.AIProcessed,
                Tags = d.Tags
            }).ToList();

            var json = JsonSerializer.Serialize(storageModels, _jsonOptions);
            
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("Cached {Count} SharePoint documents to blob storage", _documents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving SharePoint documents cache");
        }
    }
}
