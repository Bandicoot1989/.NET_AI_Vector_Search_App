namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for AI embedding generation and similarity calculations
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text);
    Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts);
    double CalculateCosineSimilarity(float[] vectorA, float[] vectorB);
}
