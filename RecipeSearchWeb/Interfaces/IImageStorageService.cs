using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for KB image management
/// </summary>
public interface IImageStorageService
{
    Task InitializeAsync();
    Task<KBImage?> UploadImageAsync(string kbNumber, Stream imageStream, string fileName, string contentType, string? altText = null);
    Task<KBImage?> UploadImageAsync(string kbNumber, byte[] imageData, string fileName, string contentType, string? altText = null);
    Task<bool> DeleteImageAsync(string blobUrl);
    Task<int> DeleteAllImagesForArticleAsync(string kbNumber);
    Task<int> DeleteAllFilesForArticleAsync(string kbNumber);
    Task<string?> UploadOriginalPdfAsync(string kbNumber, Stream pdfStream, string fileName);
    Task<List<KBImage>> GetImagesForArticleAsync(string kbNumber);
}
