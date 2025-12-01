using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RecipeSearchWeb.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service to convert Word documents (.docx) to KB articles with Markdown content
/// Extracts metadata from GA KB document structure and converts content to Markdown
/// </summary>
public class WordDocumentService
{
    private readonly ILogger<WordDocumentService> _logger;

    public WordDocumentService(ILogger<WordDocumentService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process a Word document stream and extract KB article data
    /// </summary>
    public async Task<WordDocumentResult> ProcessDocumentAsync(Stream documentStream, string fileName)
    {
        var result = new WordDocumentResult
        {
            FileName = fileName,
            Success = false
        };

        try
        {
            // Copy stream to memory for processing
            using var memoryStream = new MemoryStream();
            await documentStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var doc = WordprocessingDocument.Open(memoryStream, false);
            var mainPart = doc.MainDocumentPart;
            if (mainPart?.Document?.Body == null)
            {
                result.ErrorMessage = "Invalid Word document: no content found";
                return result;
            }

            var body = mainPart.Document.Body;

            // Extract metadata from tables (GA KB format has metadata in tables)
            var metadata = ExtractMetadata(body);
            
            // Extract main content and convert to Markdown
            var content = ExtractContent(body);

            // Extract embedded images
            var images = await ExtractImagesAsync(mainPart);

            // Build the article
            result.Article = new KnowledgeArticle
            {
                KBNumber = metadata.GetValueOrDefault("KB Number", GenerateKBNumber()),
                Title = metadata.GetValueOrDefault("Title", Path.GetFileNameWithoutExtension(fileName)),
                ShortDescription = metadata.GetValueOrDefault("Short description", ""),
                Purpose = ExtractSection(content, "PURPOSE"),
                Context = ExtractSection(content, "CONTEXT"),
                AppliesTo = metadata.GetValueOrDefault("Applies to", ""),
                Content = CleanContent(content),
                KBGroup = MapToKBGroup(metadata.GetValueOrDefault("KB Group", "My WorkPlace")),
                KBOwner = metadata.GetValueOrDefault("KB owner", ""),
                TargetReaders = metadata.GetValueOrDefault("Target readers", "Users"),
                Language = metadata.GetValueOrDefault("Language", "English"),
                Tags = ParseTags(metadata.GetValueOrDefault("Meta", "") + " " + metadata.GetValueOrDefault("Tags", "")),
                SourceDocument = fileName,
                CreatedDate = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Author = metadata.GetValueOrDefault("KB owner", "System")
            };

            result.ExtractedImages = images;
            result.Success = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Word document: {FileName}", fileName);
            result.ErrorMessage = $"Error processing document: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Extract metadata from document tables (GA KB format)
    /// </summary>
    private Dictionary<string, string> ExtractMetadata(Body body)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in body.Elements<Table>())
        {
            foreach (var row in table.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>().ToList();
                if (cells.Count >= 2)
                {
                    var key = GetCellText(cells[0]).Trim().TrimEnd(':');
                    var value = GetCellText(cells[1]).Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    {
                        metadata[key] = value;
                    }
                }
            }
        }

        // Also try to extract title from first heading
        var firstHeading = body.Descendants<Paragraph>()
            .FirstOrDefault(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.Contains("Heading") == true ||
                                 p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.Contains("Title") == true);
        
        if (firstHeading != null && !metadata.ContainsKey("Title"))
        {
            var titleText = GetParagraphText(firstHeading);
            if (!string.IsNullOrWhiteSpace(titleText))
            {
                metadata["Title"] = titleText;
            }
        }

        return metadata;
    }

    /// <summary>
    /// Extract main content and convert to Markdown
    /// </summary>
    private string ExtractContent(Body body)
    {
        var markdown = new StringBuilder();
        var inList = false;
        var tableCount = 0;

        foreach (var element in body.Elements())
        {
            // Skip metadata tables (usually first 1-2 tables)
            if (element is Table)
            {
                tableCount++;
                if (tableCount <= 2) continue; // Skip metadata tables
                
                // Convert content tables to Markdown
                markdown.AppendLine();
                markdown.AppendLine(ConvertTableToMarkdown((Table)element));
                markdown.AppendLine();
                continue;
            }

            if (element is Paragraph para)
            {
                var text = GetParagraphText(para);
                var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";

                // Skip empty paragraphs
                if (string.IsNullOrWhiteSpace(text)) 
                {
                    if (inList)
                    {
                        inList = false;
                        markdown.AppendLine();
                    }
                    continue;
                }

                // Headings
                if (styleId.Contains("Heading1") || IsBoldParagraph(para) && text.Length < 100)
                {
                    markdown.AppendLine();
                    markdown.AppendLine($"## {text}");
                    markdown.AppendLine();
                    inList = false;
                    continue;
                }

                if (styleId.Contains("Heading2") || styleId.Contains("Heading3"))
                {
                    markdown.AppendLine();
                    markdown.AppendLine($"### {text}");
                    markdown.AppendLine();
                    inList = false;
                    continue;
                }

                // Lists
                var numProps = para.ParagraphProperties?.NumberingProperties;
                if (numProps != null || text.TrimStart().StartsWith("•") || text.TrimStart().StartsWith("-"))
                {
                    var cleanText = text.TrimStart('•', '-', ' ', '\t');
                    markdown.AppendLine($"- {cleanText}");
                    inList = true;
                    continue;
                }

                // Regular paragraphs
                if (inList)
                {
                    markdown.AppendLine();
                    inList = false;
                }

                // Handle bold/emphasis in text
                var formattedText = FormatParagraphWithStyles(para);
                markdown.AppendLine(formattedText);
                markdown.AppendLine();
            }
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Extract a specific section (PURPOSE, CONTEXT, etc.) from content
    /// </summary>
    private string ExtractSection(string content, string sectionName)
    {
        var pattern = $@"##\s*{sectionName}\s*\n(.*?)(?=##|$)";
        var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// Clean content by removing PURPOSE/CONTEXT sections (already extracted to separate fields)
    /// </summary>
    private string CleanContent(string content)
    {
        // Remove PURPOSE and CONTEXT sections as they're stored separately
        content = Regex.Replace(content, @"##\s*PURPOSE\s*\n.*?(?=##|$)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        content = Regex.Replace(content, @"##\s*CONTEXT\s*\n.*?(?=##|$)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        // Clean up multiple newlines
        content = Regex.Replace(content, @"\n{3,}", "\n\n");
        
        return content.Trim();
    }

    /// <summary>
    /// Extract embedded images from the document
    /// </summary>
    private async Task<List<ExtractedImage>> ExtractImagesAsync(MainDocumentPart mainPart)
    {
        var images = new List<ExtractedImage>();
        var imageIndex = 0;

        foreach (var imagePart in mainPart.ImageParts)
        {
            try
            {
                using var imageStream = imagePart.GetStream();
                using var memoryStream = new MemoryStream();
                await imageStream.CopyToAsync(memoryStream);

                var contentType = imagePart.ContentType;
                var extension = GetImageExtension(contentType);
                var fileName = $"image_{imageIndex++}{extension}";

                images.Add(new ExtractedImage
                {
                    FileName = fileName,
                    ContentType = contentType,
                    Data = memoryStream.ToArray()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract image from document");
            }
        }

        return images;
    }

    private string GetImageExtension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private string GetCellText(TableCell cell)
    {
        return string.Join(" ", cell.Descendants<Text>().Select(t => t.Text));
    }

    private string GetParagraphText(Paragraph para)
    {
        return string.Join("", para.Descendants<Text>().Select(t => t.Text));
    }

    private bool IsBoldParagraph(Paragraph para)
    {
        var runs = para.Descendants<Run>().ToList();
        if (!runs.Any()) return false;
        
        return runs.All(r => r.RunProperties?.Bold != null);
    }

    private string FormatParagraphWithStyles(Paragraph para)
    {
        var result = new StringBuilder();

        foreach (var run in para.Descendants<Run>())
        {
            var text = string.Join("", run.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrEmpty(text)) continue;

            var isBold = run.RunProperties?.Bold != null;
            var isItalic = run.RunProperties?.Italic != null;
            var isCode = run.RunProperties?.RunFonts?.Ascii?.Value?.Contains("Consolas") == true ||
                        run.RunProperties?.RunFonts?.Ascii?.Value?.Contains("Courier") == true;

            if (isCode)
                result.Append($"`{text}`");
            else if (isBold && isItalic)
                result.Append($"***{text}***");
            else if (isBold)
                result.Append($"**{text}**");
            else if (isItalic)
                result.Append($"*{text}*");
            else
                result.Append(text);
        }

        return result.ToString();
    }

    private string ConvertTableToMarkdown(Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (!rows.Any()) return "";

        var markdown = new StringBuilder();
        var isHeader = true;

        foreach (var row in rows)
        {
            var cells = row.Elements<TableCell>().Select(c => GetCellText(c).Trim()).ToList();
            markdown.AppendLine($"| {string.Join(" | ", cells)} |");

            if (isHeader)
            {
                markdown.AppendLine($"| {string.Join(" | ", cells.Select(_ => "---"))} |");
                isHeader = false;
            }
        }

        return markdown.ToString();
    }

    private List<string> ParseTags(string tagString)
    {
        if (string.IsNullOrWhiteSpace(tagString)) return new List<string>();

        return tagString
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
    }

    private string MapToKBGroup(string group)
    {
        var groupLower = group.ToLowerInvariant();
        
        if (groupLower.Contains("workplace") || groupLower.Contains("work place"))
            return KBGroups.MyWorkPlace;
        if (groupLower.Contains("security"))
            return KBGroups.Security;
        if (groupLower.Contains("network"))
            return KBGroups.Network;
        if (groupLower.Contains("software"))
            return KBGroups.Software;
        if (groupLower.Contains("hardware"))
            return KBGroups.Hardware;
        if (groupLower.Contains("procedure"))
            return KBGroups.Procedures;
        if (groupLower.Contains("polic"))
            return KBGroups.Policies;
        if (groupLower.Contains("troubleshoot"))
            return KBGroups.Troubleshooting;
        if (groupLower.Contains("email") || groupLower.Contains("internet"))
            return "Email & Internet";

        return KBGroups.MyWorkPlace;
    }

    private string GenerateKBNumber()
    {
        var random = new Random();
        return $"KB{random.Next(1000000, 9999999)}";
    }
}

/// <summary>
/// Result of processing a Word document
/// </summary>
public class WordDocumentResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string FileName { get; set; } = string.Empty;
    public KnowledgeArticle? Article { get; set; }
    public List<ExtractedImage> ExtractedImages { get; set; } = new();
}

/// <summary>
/// Represents an image extracted from a Word document
/// </summary>
public class ExtractedImage
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
