using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text.Json;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for persisting custom scripts to Azure Blob Storage
/// </summary>
public class ScriptStorageService : IScriptStorageService
{
    private readonly BlobContainerClient _containerClient;
    private const string BlobName = "custom-scripts.json";
    private readonly JsonSerializerOptions _jsonOptions;

    public ScriptStorageService(IConfiguration configuration)
    {
        var connectionString = configuration["AzureStorage:ConnectionString"];
        var containerName = configuration["AzureStorage:ContainerName"] ?? "scripts";

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
    /// Save custom scripts to blob storage
    /// </summary>
    public async Task SaveScriptsAsync(List<Script> scripts)
    {
        var blobClient = _containerClient.GetBlobClient(BlobName);
        
        // Only save custom scripts (Key >= 1000) and exclude the Vector property
        var customScripts = scripts
            .Where(s => s.Key >= 1000)
            .Select(s => new ScriptStorageModel
            {
                Key = s.Key,
                Name = s.Name,
                Category = s.Category,
                Description = s.Description,
                Purpose = s.Purpose,
                Complexity = s.Complexity,
                Code = s.Code,
                Parameters = s.Parameters,
                ViewCount = s.ViewCount,
                LastViewed = s.LastViewed
            })
            .ToList();

        var json = JsonSerializer.Serialize(customScripts, _jsonOptions);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    /// <summary>
    /// Load custom scripts from blob storage
    /// </summary>
    public async Task<List<Script>> LoadScriptsAsync()
    {
        var blobClient = _containerClient.GetBlobClient(BlobName);

        try
        {
            if (!await blobClient.ExistsAsync())
            {
                return new List<Script>();
            }

            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            
            var storageModels = JsonSerializer.Deserialize<List<ScriptStorageModel>>(json, _jsonOptions);
            
            if (storageModels == null)
            {
                return new List<Script>();
            }

            return storageModels.Select(s => new Script
            {
                Key = s.Key,
                Name = s.Name,
                Category = s.Category,
                Description = s.Description,
                Purpose = s.Purpose,
                Complexity = s.Complexity,
                Code = s.Code,
                Parameters = s.Parameters,
                ViewCount = s.ViewCount,
                LastViewed = s.LastViewed
            }).ToList();
        }
        catch (Exception)
        {
            // If blob doesn't exist or any error, return empty list
            return new List<Script>();
        }
    }

    /// <summary>
    /// Storage model without the Vector property (not needed for persistence)
    /// </summary>
    private class ScriptStorageModel
    {
        public int Key { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Complexity { get; set; } = "";
        public string Code { get; set; } = "";
        public string Parameters { get; set; } = "";
        public int ViewCount { get; set; }
        public DateTime? LastViewed { get; set; }
    }
}
