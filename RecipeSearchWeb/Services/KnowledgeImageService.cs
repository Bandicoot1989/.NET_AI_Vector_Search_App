using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for managing KB article images in Azure Blob Storage
/// Handles upload, retrieval, and deletion of screenshots and attachments
/// Uses SAS tokens for secure access when public access is not available
/// </summary>
public class KnowledgeImageService : IImageStorageService
{
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<KnowledgeImageService> _logger;
    private readonly string _connectionString;
    private const string ImageFolderPrefix = "images/";
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5MB max
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp", "image/bmp"
    };

    public KnowledgeImageService(IConfiguration configuration, ILogger<KnowledgeImageService> logger)
    {
        _logger = logger;
        _connectionString = configuration["AzureStorage:ConnectionString"] ?? "";
        var containerName = configuration["AzureStorage:KnowledgeContainerName"] ?? "knowledge";

        if (string.IsNullOrEmpty(_connectionString))
        {
            throw new InvalidOperationException("Azure Storage connection string not configured");
        }

        var blobServiceClient = new BlobServiceClient(_connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    /// <summary>
    /// Initialize the container (ensure images folder structure exists)
    /// </summary>
    public async Task InitializeAsync()
    {
        // Create container if not exists (without public access since it's disabled at account level)
        await _containerClient.CreateIfNotExistsAsync();
    }

    /// <summary>
    /// Generate a SAS URL for a blob that expires in 5 years
    /// </summary>
    private string GenerateSasUrl(BlobClient blobClient)
    {
        // Generate SAS token valid for 5 years
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerClient.Name,
            BlobName = blobClient.Name,
            Resource = "b", // blob
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Start a bit in the past for clock skew
            ExpiresOn = DateTimeOffset.UtcNow.AddYears(5)
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        // Generate the SAS token using the connection string credentials
        var sasToken = blobClient.GenerateSasUri(sasBuilder);
        return sasToken.ToString();
    }

    /// <summary>
    /// Upload an image for a KB article
    /// </summary>
    public async Task<KBImage?> UploadImageAsync(string kbNumber, Stream imageStream, string fileName, string contentType, string? altText = null)
    {
        try
        {
            // Validate content type
            if (!AllowedContentTypes.Contains(contentType))
            {
                _logger.LogWarning("Invalid content type for image upload: {ContentType}", contentType);
                return null;
            }

            // Validate file size
            if (imageStream.Length > MaxFileSizeBytes)
            {
                _logger.LogWarning("Image too large: {Size} bytes", imageStream.Length);
                return null;
            }

            // Generate unique blob name
            var sanitizedFileName = SanitizeFileName(fileName);
            var uniqueId = Guid.NewGuid().ToString("N")[..8];
            var blobName = $"{ImageFolderPrefix}{kbNumber}/{uniqueId}_{sanitizedFileName}";

            var blobClient = _containerClient.GetBlobClient(blobName);

            // Upload with content type
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType,
                    CacheControl = "public, max-age=31536000" // Cache for 1 year
                }
            };

            await blobClient.UploadAsync(imageStream, uploadOptions);

            // Generate SAS URL for access (since public access is disabled on storage account)
            var sasUrl = GenerateSasUrl(blobClient);

            var kbImage = new KBImage
            {
                Id = uniqueId,
                FileName = sanitizedFileName,
                BlobUrl = sasUrl, // Use SAS URL instead of plain blob URL
                AltText = altText ?? Path.GetFileNameWithoutExtension(fileName),
                SizeBytes = imageStream.Length,
                UploadedDate = DateTime.UtcNow
            };

            _logger.LogInformation("Uploaded image {FileName} for KB {KBNumber} with SAS URL", fileName, kbNumber);
            return kbImage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload image {FileName} for KB {KBNumber}", fileName, kbNumber);
            return null;
        }
    }

    /// <summary>
    /// Upload an image from byte array (used for Word document extraction)
    /// </summary>
    public async Task<KBImage?> UploadImageAsync(string kbNumber, byte[] imageData, string fileName, string contentType, string? altText = null)
    {
        using var stream = new MemoryStream(imageData);
        return await UploadImageAsync(kbNumber, stream, fileName, contentType, altText);
    }

    /// <summary>
    /// Delete an image from storage
    /// </summary>
    public async Task<bool> DeleteImageAsync(string blobUrl)
    {
        try
        {
            // Extract blob name from URL
            var uri = new Uri(blobUrl);
            var blobName = uri.AbsolutePath.TrimStart('/');
            
            // Remove container name from path if present
            var containerName = _containerClient.Name;
            if (blobName.StartsWith($"{containerName}/"))
            {
                blobName = blobName.Substring(containerName.Length + 1);
            }

            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.DeleteIfExistsAsync();

            _logger.LogInformation("Deleted image: {BlobUrl}", blobUrl);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image: {BlobUrl}", blobUrl);
            return false;
        }
    }

    /// <summary>
    /// Delete all images for a KB article
    /// </summary>
    public async Task<int> DeleteAllImagesForArticleAsync(string kbNumber)
    {
        var deletedCount = 0;
        var prefix = $"{ImageFolderPrefix}{kbNumber}/";

        try
        {
            await foreach (var blob in _containerClient.GetBlobsAsync(prefix: prefix))
            {
                var blobClient = _containerClient.GetBlobClient(blob.Name);
                await blobClient.DeleteIfExistsAsync();
                deletedCount++;
            }

            _logger.LogInformation("Deleted {Count} images for KB {KBNumber}", deletedCount, kbNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete images for KB {KBNumber}", kbNumber);
        }

        return deletedCount;
    }

    /// <summary>
    /// Delete all files (images and documents) for a KB article
    /// </summary>
    public async Task<int> DeleteAllFilesForArticleAsync(string kbNumber)
    {
        var deletedCount = 0;

        // Delete images
        deletedCount += await DeleteAllImagesForArticleAsync(kbNumber);

        // Delete documents (PDFs)
        var docPrefix = $"documents/{kbNumber}/";
        try
        {
            await foreach (var blob in _containerClient.GetBlobsAsync(prefix: docPrefix))
            {
                var blobClient = _containerClient.GetBlobClient(blob.Name);
                await blobClient.DeleteIfExistsAsync();
                deletedCount++;
                _logger.LogInformation("Deleted document: {BlobName}", blob.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete documents for KB {KBNumber}", kbNumber);
        }

        _logger.LogInformation("Deleted {Count} total files for KB {KBNumber}", deletedCount, kbNumber);
        return deletedCount;
    }

    /// <summary>
    /// Upload the original PDF document for embedded viewing
    /// </summary>
    public async Task<string?> UploadOriginalPdfAsync(string kbNumber, Stream pdfStream, string fileName)
    {
        try
        {
            var sanitizedFileName = SanitizeFileName(fileName);
            var blobName = $"documents/{kbNumber}/{sanitizedFileName}";

            var blobClient = _containerClient.GetBlobClient(blobName);

            // Upload with PDF content type
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/pdf",
                    ContentDisposition = $"inline; filename=\"{sanitizedFileName}\""
                }
            };

            pdfStream.Position = 0;
            await blobClient.UploadAsync(pdfStream, uploadOptions);

            // Generate SAS URL for access
            var sasUrl = GenerateSasUrl(blobClient);

            _logger.LogInformation("Uploaded original PDF {FileName} for KB {KBNumber}", fileName, kbNumber);
            return sasUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload PDF {FileName} for KB {KBNumber}", fileName, kbNumber);
            return null;
        }
    }

    /// <summary>
    /// Get all images for a KB article from blob storage
    /// </summary>
    public async Task<List<KBImage>> GetImagesForArticleAsync(string kbNumber)
    {
        var images = new List<KBImage>();
        var prefix = $"{ImageFolderPrefix}{kbNumber}/";

        try
        {
            await foreach (var blob in _containerClient.GetBlobsAsync(prefix: prefix))
            {
                var blobClient = _containerClient.GetBlobClient(blob.Name);
                images.Add(new KBImage
                {
                    Id = Path.GetFileNameWithoutExtension(blob.Name).Split('_')[0],
                    FileName = Path.GetFileName(blob.Name),
                    BlobUrl = blobClient.Uri.ToString(),
                    SizeBytes = blob.Properties.ContentLength ?? 0,
                    UploadedDate = blob.Properties.CreatedOn?.UtcDateTime ?? DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get images for KB {KBNumber}", kbNumber);
        }

        return images;
    }

    /// <summary>
    /// Generate a Markdown image reference for inserting into content
    /// </summary>
    public static string GenerateMarkdownImage(KBImage image)
    {
        return $"![{image.AltText}]({image.BlobUrl})";
    }

    /// <summary>
    /// Generate an HTML image tag with lazy loading
    /// </summary>
    public static string GenerateHtmlImage(KBImage image, string? cssClass = null)
    {
        var classAttr = !string.IsNullOrEmpty(cssClass) ? $" class=\"{cssClass}\"" : "";
        return $"<img src=\"{image.BlobUrl}\" alt=\"{System.Web.HttpUtility.HtmlEncode(image.AltText)}\" loading=\"lazy\"{classAttr}>";
    }

    private string SanitizeFileName(string fileName)
    {
        // Remove invalid characters and limit length
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(c => !invalidChars.Contains(c)).ToArray());
        
        // Limit to 100 characters
        if (sanitized.Length > 100)
        {
            var extension = Path.GetExtension(sanitized);
            var name = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = name[..(100 - extension.Length)] + extension;
        }

        return sanitized.ToLowerInvariant();
    }
}
