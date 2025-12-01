namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for document processing (Word, PDF)
/// </summary>
public interface IDocumentService
{
    Task<DocumentContent> ExtractFromWordAsync(Stream documentStream);
    Task<DocumentContent> ExtractFromPdfAsync(Stream documentStream);
    string ConvertMarkdownToHtml(string markdown);
}

/// <summary>
/// Extracted content from a document
/// </summary>
public class DocumentContent
{
    public string Text { get; set; } = string.Empty;
    public string? Title { get; set; }
    public List<string> Sections { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}
