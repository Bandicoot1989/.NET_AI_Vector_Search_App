using System.Text.Json.Serialization;

namespace RecipeSearchWeb.Models;

/// <summary>
/// Represents a Knowledge Base article
/// </summary>
public class KnowledgeArticle
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("kbNumber")]
    public string KBNumber { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("shortDescription")]
    public string ShortDescription { get; set; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("appliesTo")]
    public string AppliesTo { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("kbGroup")]
    public string KBGroup { get; set; } = string.Empty;

    [JsonPropertyName("kbOwner")]
    public string KBOwner { get; set; } = string.Empty;

    [JsonPropertyName("targetReaders")]
    public string TargetReaders { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "English";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("createdDate")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// List of image attachments (screenshots, diagrams, etc.)
    /// </summary>
    [JsonPropertyName("images")]
    public List<KBImage> Images { get; set; } = new();

    /// <summary>
    /// Original Word document filename (if uploaded from Word)
    /// </summary>
    [JsonPropertyName("sourceDocument")]
    public string? SourceDocument { get; set; }

    /// <summary>
    /// URL to the original PDF document in Azure Blob Storage (for embedded viewing)
    /// </summary>
    [JsonPropertyName("originalPdfUrl")]
    public string? OriginalPdfUrl { get; set; }

    /// <summary>
    /// Search score for ranking results (not persisted)
    /// </summary>
    [JsonIgnore]
    public double SearchScore { get; set; }
}

/// <summary>
/// Represents an image attachment in a KB article
/// </summary>
public class KBImage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("blobUrl")]
    public string BlobUrl { get; set; } = string.Empty;

    [JsonPropertyName("altText")]
    public string AltText { get; set; } = string.Empty;

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("uploadedDate")]
    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>
/// Available KB Groups/Categories
/// </summary>
public static class KBGroups
{
    public const string MyWorkPlace = "My WorkPlace";
    public const string Security = "Security";
    public const string Network = "Network";
    public const string Software = "Software";
    public const string Hardware = "Hardware";
    public const string Procedures = "Procedures";
    public const string Policies = "Policies";
    public const string Troubleshooting = "Troubleshooting";

    public static readonly string[] All = new[]
    {
        MyWorkPlace,
        Security,
        Network,
        Software,
        Hardware,
        Procedures,
        Policies,
        Troubleshooting
    };
}
