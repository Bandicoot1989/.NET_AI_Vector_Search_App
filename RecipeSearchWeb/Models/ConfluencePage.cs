using System.Text.Json.Serialization;

namespace RecipeSearchWeb.Models;

/// <summary>
/// Represents a page from Atlassian Confluence
/// </summary>
public class ConfluencePage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("spaceKey")]
    public string SpaceKey { get; set; } = string.Empty;

    [JsonPropertyName("spaceName")]
    public string SpaceName { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; set; } = string.Empty;

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("modifiedBy")]
    public string ModifiedBy { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "current";

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = new();

    [JsonPropertyName("ancestors")]
    public List<string> Ancestors { get; set; } = new();

    /// <summary>
    /// Vector embedding for semantic search
    /// </summary>
    [JsonIgnore]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>
    /// Get searchable text combining all relevant fields
    /// </summary>
    public string GetSearchableText()
    {
        return $"{Title} {SpaceName} {Content} {Excerpt} {string.Join(" ", Labels)}";
    }
}

/// <summary>
/// Storage model without embeddings for JSON persistence
/// </summary>
public class ConfluencePageStorageModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("spaceKey")]
    public string SpaceKey { get; set; } = string.Empty;

    [JsonPropertyName("spaceName")]
    public string SpaceName { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; set; } = string.Empty;

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("modifiedBy")]
    public string ModifiedBy { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "current";

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = new();

    [JsonPropertyName("ancestors")]
    public List<string> Ancestors { get; set; } = new();
}
