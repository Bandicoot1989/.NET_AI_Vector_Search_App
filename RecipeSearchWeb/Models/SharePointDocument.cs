using System.Text.Json.Serialization;

namespace RecipeSearchWeb.Models;

/// <summary>
/// Represents a document from SharePoint Digitalization KB library
/// </summary>
public class SharePointDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("kbNumber")]
    public string? KBNumber { get; set; }

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("modifiedBy")]
    public string ModifiedBy { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; }

    [JsonPropertyName("approvalStatus")]
    public string ApprovalStatus { get; set; } = string.Empty;

    [JsonPropertyName("aiProcessed")]
    public bool AIProcessed { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

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
        return $"{Title} {Name} {Folder} {Content} {string.Join(" ", Tags)}";
    }
}

/// <summary>
/// Storage model without embeddings for JSON persistence
/// </summary>
public class SharePointDocumentStorageModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("kbNumber")]
    public string? KBNumber { get; set; }

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("modifiedBy")]
    public string ModifiedBy { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; }

    [JsonPropertyName("approvalStatus")]
    public string ApprovalStatus { get; set; } = string.Empty;

    [JsonPropertyName("aiProcessed")]
    public bool AIProcessed { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}
