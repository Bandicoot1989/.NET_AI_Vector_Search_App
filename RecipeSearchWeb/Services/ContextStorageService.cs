using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using RecipeSearchWeb.Models;
using System.Text.Json;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for storing and retrieving context documents from Azure Blob Storage
/// </summary>
public class ContextStorageService
{
    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<ContextStorageService> _logger;
    private const string ContainerName = "agent-context";
    private const string DocumentsBlob = "context-documents.json";
    private const string FilesBlob = "context-files.json";
    private bool _isAvailable = false;

    public ContextStorageService(IConfiguration configuration, ILogger<ContextStorageService> logger)
    {
        _logger = logger;
        try
        {
            // Try both configuration keys for compatibility
            var connectionString = configuration["AzureStorage:ConnectionString"] 
                ?? configuration["AZURE_STORAGE_CONNECTION_STRING"];
            
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogWarning("Azure Storage connection string not configured - context service disabled");
                return;
            }
            
            var blobServiceClient = new BlobServiceClient(connectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);
            _isAvailable = true;
            _logger.LogInformation("ContextStorageService initialized with container: {Container}", ContainerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ContextStorageService");
        }
    }

    /// <summary>
    /// Initialize the container
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_isAvailable || _containerClient == null) return;
        
        try
        {
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            _logger.LogInformation("Context storage container initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create context container");
            _isAvailable = false;
        }
    }

    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Save all context documents
    /// </summary>
    public async Task SaveDocumentsAsync(List<ContextDocument> documents)
    {
        if (!_isAvailable || _containerClient == null) return;
        
        var storageModels = documents.Select(d => new ContextDocumentStorageModel
        {
            Id = d.Id,
            SourceFile = d.SourceFile,
            Category = d.Category,
            Name = d.Name,
            Description = d.Description,
            Keywords = d.Keywords,
            Link = d.Link,
            AdditionalData = d.AdditionalData,
            Embedding = d.Embedding.ToArray(),
            ImportedAt = d.ImportedAt
        }).ToList();

        var json = JsonSerializer.Serialize(storageModels, new JsonSerializerOptions { WriteIndented = true });
        var blobClient = _containerClient.GetBlobClient(DocumentsBlob);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
        
        _logger.LogInformation("Saved {Count} context documents to storage", documents.Count);
    }

    /// <summary>
    /// Load all context documents
    /// </summary>
    public async Task<List<ContextDocument>> LoadDocumentsAsync()
    {
        if (!_isAvailable || _containerClient == null) 
            return new List<ContextDocument>();
        
        var blobClient = _containerClient.GetBlobClient(DocumentsBlob);
        
        if (!await blobClient.ExistsAsync())
        {
            return new List<ContextDocument>();
        }

        var response = await blobClient.DownloadContentAsync();
        var json = response.Value.Content.ToString();
        
        var storageModels = JsonSerializer.Deserialize<List<ContextDocumentStorageModel>>(json) 
            ?? new List<ContextDocumentStorageModel>();

        return storageModels.Select(s => new ContextDocument
        {
            Id = s.Id,
            SourceFile = s.SourceFile,
            Category = s.Category,
            Name = s.Name,
            Description = s.Description,
            Keywords = s.Keywords,
            Link = s.Link,
            AdditionalData = s.AdditionalData ?? new(),
            Embedding = s.Embedding,
            ImportedAt = s.ImportedAt
        }).ToList();
    }

    /// <summary>
    /// Save context files metadata
    /// </summary>
    public async Task SaveFilesAsync(List<ContextFile> files)
    {
        if (!_isAvailable || _containerClient == null) return;
        
        var json = JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true });
        var blobClient = _containerClient.GetBlobClient(FilesBlob);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    /// <summary>
    /// Load context files metadata
    /// </summary>
    public async Task<List<ContextFile>> LoadFilesAsync()
    {
        if (!_isAvailable || _containerClient == null) 
            return new List<ContextFile>();
        
        var blobClient = _containerClient.GetBlobClient(FilesBlob);
        
        if (!await blobClient.ExistsAsync())
        {
            return new List<ContextFile>();
        }

        var response = await blobClient.DownloadContentAsync();
        var json = response.Value.Content.ToString();
        
        return JsonSerializer.Deserialize<List<ContextFile>>(json) ?? new List<ContextFile>();
    }

    /// <summary>
    /// Delete documents from a specific source file
    /// </summary>
    public async Task DeleteBySourceFileAsync(string sourceFileName, List<ContextDocument> allDocuments)
    {
        if (!_isAvailable || _containerClient == null) return;
        
        var filtered = allDocuments.Where(d => d.SourceFile != sourceFileName).ToList();
        await SaveDocumentsAsync(filtered);
    }

    /// <summary>
    /// Storage model for serialization (handles float[] instead of ReadOnlyMemory<float>)
    /// </summary>
    private class ContextDocumentStorageModel
    {
        public string Id { get; set; } = string.Empty;
        public string SourceFile { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Keywords { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public Dictionary<string, string>? AdditionalData { get; set; }
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public DateTime ImportedAt { get; set; }
    }
}
