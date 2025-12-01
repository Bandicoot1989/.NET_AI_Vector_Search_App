namespace RecipeSearchWeb.Models;

/// <summary>
/// Represents a context document entry (from Excel files like ticket categories, reference data, etc.)
/// </summary>
public class ContextDocument
{
    /// <summary>
    /// Unique identifier for this entry
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Source file name (e.g., "ticket_categories.xlsx")
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;
    
    /// <summary>
    /// Category/type of context (e.g., "ServiceDesk Tickets", "URLs", "Contacts")
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Name/Title of the entry
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Description of the entry
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Keywords/Tags for search (comma-separated or list)
    /// </summary>
    public string Keywords { get; set; } = string.Empty;
    
    /// <summary>
    /// URL/Link associated with this entry
    /// </summary>
    public string Link { get; set; } = string.Empty;
    
    /// <summary>
    /// Additional columns from Excel stored as key-value pairs
    /// </summary>
    public Dictionary<string, string> AdditionalData { get; set; } = new();
    
    /// <summary>
    /// Vector embedding for semantic search
    /// </summary>
    public ReadOnlyMemory<float> Embedding { get; set; }
    
    /// <summary>
    /// Search score (populated during search)
    /// </summary>
    public double SearchScore { get; set; }
    
    /// <summary>
    /// When the entry was imported
    /// </summary>
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Get searchable text for embedding generation
    /// </summary>
    public string GetSearchableText()
    {
        var parts = new List<string>();
        
        if (!string.IsNullOrWhiteSpace(Name))
            parts.Add($"Name: {Name}");
        
        if (!string.IsNullOrWhiteSpace(Description))
            parts.Add($"Description: {Description}");
        
        if (!string.IsNullOrWhiteSpace(Keywords))
            parts.Add($"Keywords: {Keywords}");
        
        if (!string.IsNullOrWhiteSpace(Category))
            parts.Add($"Category: {Category}");
        
        // Include additional data
        foreach (var kvp in AdditionalData.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
        {
            parts.Add($"{kvp.Key}: {kvp.Value}");
        }
        
        return string.Join(". ", parts);
    }
}

/// <summary>
/// Represents an uploaded context file
/// </summary>
public class ContextFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string UploadedBy { get; set; } = string.Empty;
}
