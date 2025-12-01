using RecipeSearchWeb.Models;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Graphics.Colors;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service to convert PDF documents to KB articles with Markdown content
/// Extracts text content, images, and attempts to identify structure
/// </summary>
public class PdfDocumentService
{
    private readonly ILogger<PdfDocumentService> _logger;
    private readonly KnowledgeImageService _imageService;

    public PdfDocumentService(ILogger<PdfDocumentService> logger, KnowledgeImageService imageService)
    {
        _logger = logger;
        _imageService = imageService;
    }

    /// <summary>
    /// Process a PDF document stream and extract KB article data
    /// </summary>
    public async Task<PdfDocumentResult> ProcessDocumentAsync(Stream documentStream, string fileName)
    {
        var result = new PdfDocumentResult
        {
            FileName = fileName,
            Success = false
        };

        try
        {
            // Copy stream to memory for processing
            using var memoryStream = new MemoryStream();
            await documentStream.CopyToAsync(memoryStream);
            
            // Create a separate copy for PDF upload (the original stream will be used by PdfPig)
            var pdfBytes = memoryStream.ToArray();
            
            memoryStream.Position = 0;
            using var document = PdfDocument.Open(memoryStream);
            
            if (document.NumberOfPages == 0)
            {
                result.ErrorMessage = "Invalid PDF document: no pages found";
                return result;
            }

            // Extract metadata first to get KB number for images
            var content = ExtractAllText(document);
            var metadata = ExtractMetadata(document, content);
            var kbNumber = metadata.GetValueOrDefault("KBNumber", GenerateKBNumber());

            // Upload the ORIGINAL PDF for embedded viewing using the byte array copy
            string? originalPdfUrl = null;
            try
            {
                using var pdfUploadStream = new MemoryStream(pdfBytes);
                originalPdfUrl = await _imageService.UploadOriginalPdfAsync(kbNumber, pdfUploadStream, fileName);
                _logger.LogInformation("Uploaded original PDF for KB {KBNumber}: {Url}", kbNumber, originalPdfUrl ?? "FAILED");
            }
            catch (Exception pdfEx)
            {
                _logger.LogError(pdfEx, "Failed to upload original PDF for KB {KBNumber}", kbNumber);
            }

            // Extract and upload images (for thumbnails/search)
            var extractedImages = await ExtractAndUploadImagesAsync(document, kbNumber);

            // Create content with images embedded for fallback display
            string articleContent;
            if (!string.IsNullOrEmpty(originalPdfUrl))
            {
                // If we have PDF, just use text for search indexing
                articleContent = ConvertToTextOnly(content);
            }
            else
            {
                // No PDF available, create rich content with images embedded
                articleContent = ConvertToMarkdownWithImages(content, extractedImages);
                _logger.LogWarning("PDF upload failed for KB {KBNumber}, using embedded images fallback", kbNumber);
            }

            // Build the article
            result.Article = new KnowledgeArticle
            {
                KBNumber = kbNumber,
                Title = metadata.GetValueOrDefault("Title", Path.GetFileNameWithoutExtension(fileName)),
                ShortDescription = metadata.GetValueOrDefault("ShortDescription", ExtractFirstParagraph(content)),
                Purpose = ExtractSection(content, "PURPOSE", "OBJETIVO"),
                Context = ExtractSection(content, "CONTEXT", "CONTEXTO", "BACKGROUND"),
                AppliesTo = metadata.GetValueOrDefault("AppliesTo", ""),
                Content = articleContent,
                OriginalPdfUrl = originalPdfUrl, // Link to original PDF for viewing (may be null)
                KBGroup = metadata.GetValueOrDefault("KBGroup", "Documentation"),
                KBOwner = metadata.GetValueOrDefault("Author", ""),
                TargetReaders = "Users",
                Language = DetectLanguage(content),
                Tags = ExtractKeywords(content),
                SourceDocument = fileName,
                CreatedDate = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Author = metadata.GetValueOrDefault("Author", "System"),
                Images = extractedImages
            };

            result.PageCount = document.NumberOfPages;
            result.ImageCount = extractedImages.Count;
            result.Success = true;

            _logger.LogInformation("Successfully processed PDF: {FileName}, Pages: {Pages}, Images: {Images}, OriginalPdf: {HasPdf}", 
                fileName, result.PageCount, result.ImageCount, !string.IsNullOrEmpty(originalPdfUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PDF document: {FileName}", fileName);
            result.ErrorMessage = $"Error processing PDF: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Convert content to clean text for search indexing only
    /// </summary>
    private string ConvertToTextOnly(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var sb = new StringBuilder();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;

            // Detect headers/sections
            if (IsLikelyHeading(trimmedLine))
            {
                sb.AppendLine();
                sb.AppendLine($"## {trimmedLine}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(trimmedLine);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Extract all text from PDF document
    /// </summary>
    private string ExtractAllText(PdfDocument document)
    {
        var fullText = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            var pageText = ContentOrderTextExtractor.GetText(page);
            fullText.AppendLine(pageText);
            fullText.AppendLine(); // Page break
        }

        return fullText.ToString();
    }

    /// <summary>
    /// Extract and upload images from PDF to Azure Blob Storage
    /// </summary>
    private async Task<List<KBImage>> ExtractAndUploadImagesAsync(PdfDocument document, string kbNumber)
    {
        var images = new List<KBImage>();
        var imageIndex = 0;

        foreach (var page in document.GetPages())
        {
            try
            {
                foreach (var image in page.GetImages())
                {
                    try
                    {
                        // Try to get raw image bytes
                        byte[]? imageBytes = null;
                        string contentType = "image/png";
                        string extension = ".png";

                        // Try different methods to get the image data
                        if (image.TryGetPng(out var pngBytes))
                        {
                            imageBytes = pngBytes;
                            contentType = "image/png";
                            extension = ".png";
                        }
                        else 
                        {
                            // Try to get raw bytes (may be JPEG, JPEG2000, or other formats)
                            var rawBytes = image.RawBytes;
                            if (rawBytes.Length > 0)
                            {
                                // Raw bytes might be JPEG or other format
                                imageBytes = rawBytes.ToArray();
                            
                                // Detect format from magic bytes
                                if (imageBytes.Length > 2)
                                {
                                    if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
                                    {
                                        contentType = "image/jpeg";
                                        extension = ".jpg";
                                    }
                                    else if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50)
                                    {
                                        contentType = "image/png";
                                        extension = ".png";
                                    }
                                }
                            }
                        }

                        if (imageBytes != null && imageBytes.Length > 100) // Skip tiny images
                        {
                            var fileName = $"pdf_image_{imageIndex++}{extension}";
                            
                            using var imageStream = new MemoryStream(imageBytes);
                            var uploadedImage = await _imageService.UploadImageAsync(
                                kbNumber, 
                                imageStream, 
                                fileName, 
                                contentType,
                                $"Image {imageIndex} from PDF"
                            );

                            if (uploadedImage != null)
                            {
                                images.Add(uploadedImage);
                                _logger.LogInformation("Extracted and uploaded image {Index} from PDF page {Page}", 
                                    imageIndex, page.Number);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract individual image from page {Page}", page.Number);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process images from page {Page}", page.Number);
            }
        }

        _logger.LogInformation("Total images extracted from PDF: {Count}", images.Count);
        return images;
    }

    /// <summary>
    /// Convert content to Markdown and include image references
    /// Since PDF images don't have position info, we add them at logical points
    /// The images are added inline after the main content, before metadata sections
    /// </summary>
    private string ConvertToMarkdownWithImages(string content, List<KBImage> images)
    {
        if (!images.Any())
        {
            return ConvertToMarkdown(content);
        }

        var markdown = ConvertToMarkdown(content);
        
        // Find where the metadata/footer sections begin
        var metadataPatterns = new[] { "\n## Metadata", "\n## Purpose\n", "\n## Short Description", "\nShort Description:" };
        var insertPosition = markdown.Length;
        
        foreach (var pattern in metadataPatterns)
        {
            var idx = markdown.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx < insertPosition)
            {
                insertPosition = idx;
            }
        }
        
        // Build the images section - inline, not in a separate "gallery" section
        var imageSection = new StringBuilder();
        imageSection.AppendLine();
        imageSection.AppendLine("---");
        imageSection.AppendLine();
        
        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            // Use numbered captions for reference
            var caption = $"Figure {i + 1}";
            if (!string.IsNullOrEmpty(img.Caption))
            {
                caption = img.Caption;
            }
            else if (!string.IsNullOrEmpty(img.AltText) && !img.AltText.Contains("from PDF"))
            {
                caption = img.AltText;
            }
            
            imageSection.AppendLine($"![{caption}]({img.BlobUrl})");
            imageSection.AppendLine($"*{caption}*");
            imageSection.AppendLine();
        }
        
        // Insert images before metadata
        if (insertPosition < markdown.Length)
        {
            return markdown.Substring(0, insertPosition) + imageSection.ToString() + markdown.Substring(insertPosition);
        }
        else
        {
            return markdown + imageSection.ToString();
        }
    }

    /// <summary>
    /// Extract metadata from PDF properties and content patterns
    /// </summary>
    private Dictionary<string, string> ExtractMetadata(PdfDocument document, string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Try to get metadata from PDF properties
        var info = document.Information;
        
        if (!string.IsNullOrWhiteSpace(info.Title))
            metadata["Title"] = info.Title;
        
        if (!string.IsNullOrWhiteSpace(info.Author))
            metadata["Author"] = info.Author;
        
        if (!string.IsNullOrWhiteSpace(info.Subject))
            metadata["ShortDescription"] = info.Subject;
        
        if (!string.IsNullOrWhiteSpace(info.Keywords))
            metadata["Keywords"] = info.Keywords;

        // Try to extract KB number from content
        var kbMatch = Regex.Match(content, @"KB[-\s]?(\d{4,})", RegexOptions.IgnoreCase);
        if (kbMatch.Success)
        {
            metadata["KBNumber"] = $"KB{kbMatch.Groups[1].Value}";
        }

        // Try to extract title from first line if not in metadata
        if (!metadata.ContainsKey("Title"))
        {
            var firstLine = content.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && l.Length > 5 && l.Length < 200);
            
            if (firstLine != null)
            {
                metadata["Title"] = CleanTitle(firstLine);
            }
        }

        // Try to detect category/group from content
        var groupPatterns = new Dictionary<string, string[]>
        {
            { "My WorkPlace", new[] { "workplace", "workstation", "desktop", "laptop" } },
            { "Network", new[] { "network", "vpn", "wifi", "connection", "red" } },
            { "Security", new[] { "security", "password", "authentication", "seguridad" } },
            { "Software", new[] { "software", "application", "install", "aplicación" } },
            { "Hardware", new[] { "hardware", "printer", "monitor", "impresora" } },
            { "Email", new[] { "email", "outlook", "correo", "mail" } },
            { "SAP", new[] { "sap", "fiori", "abap" } },
            { "Azure", new[] { "azure", "cloud", "microsoft 365", "office 365" } }
        };

        var lowerContent = content.ToLower();
        foreach (var group in groupPatterns)
        {
            if (group.Value.Any(keyword => lowerContent.Contains(keyword)))
            {
                metadata["KBGroup"] = group.Key;
                break;
            }
        }

        return metadata;
    }

    /// <summary>
    /// Convert raw PDF text to Markdown format
    /// </summary>
    private string ConvertToMarkdown(string content)
    {
        var lines = content.Split('\n');
        var markdown = new StringBuilder();
        var inList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip empty lines but preserve paragraph breaks
            if (string.IsNullOrWhiteSpace(line))
            {
                if (inList)
                {
                    inList = false;
                }
                markdown.AppendLine();
                continue;
            }

            // Detect headings (ALL CAPS lines, short lines ending with colon, numbered sections)
            if (IsLikelyHeading(line))
            {
                markdown.AppendLine();
                markdown.AppendLine($"## {FormatHeading(line)}");
                markdown.AppendLine();
                inList = false;
                continue;
            }

            // Detect sub-headings
            if (IsLikelySubHeading(line))
            {
                markdown.AppendLine();
                markdown.AppendLine($"### {FormatHeading(line)}");
                markdown.AppendLine();
                inList = false;
                continue;
            }

            // Detect bullet points
            if (line.StartsWith("•") || line.StartsWith("-") || line.StartsWith("*") || 
                Regex.IsMatch(line, @"^[○●◦▪▸►]\s"))
            {
                var cleanLine = Regex.Replace(line, @"^[•\-*○●◦▪▸►]\s*", "");
                markdown.AppendLine($"- {cleanLine}");
                inList = true;
                continue;
            }

            // Detect numbered lists
            if (Regex.IsMatch(line, @"^\d+[\.\)]\s"))
            {
                var cleanLine = Regex.Replace(line, @"^\d+[\.\)]\s*", "");
                markdown.AppendLine($"- {cleanLine}");
                inList = true;
                continue;
            }

            // Detect code blocks (lines starting with common code indicators)
            if (line.StartsWith("$") || line.StartsWith(">") || line.StartsWith("PS ") ||
                line.StartsWith("C:\\") || Regex.IsMatch(line, @"^[A-Z]:\\"))
            {
                markdown.AppendLine($"`{line}`");
                continue;
            }

            // Regular paragraph text
            markdown.AppendLine(line);
        }

        return CleanMarkdown(markdown.ToString());
    }

    /// <summary>
    /// Check if a line is likely a heading
    /// </summary>
    private bool IsLikelyHeading(string line)
    {
        // All uppercase and not too long
        if (line.Length >= 3 && line.Length <= 80 && line == line.ToUpper() && 
            Regex.IsMatch(line, @"[A-Z]") && !Regex.IsMatch(line, @"^\d"))
        {
            return true;
        }

        // Numbered section like "1. INTRODUCTION" or "1 INTRODUCTION"
        if (Regex.IsMatch(line, @"^\d+\.?\s+[A-Z]") && line.Length <= 80)
        {
            return true;
        }

        // Lines ending with colon that are short
        if (line.EndsWith(":") && line.Length <= 60 && !line.Contains("  "))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a line is likely a sub-heading
    /// </summary>
    private bool IsLikelySubHeading(string line)
    {
        // Sub-numbered sections like "1.1" or "a)"
        if (Regex.IsMatch(line, @"^\d+\.\d+\.?\s") && line.Length <= 80)
        {
            return true;
        }

        // Lines with letter numbering
        if (Regex.IsMatch(line, @"^[a-z]\)\s", RegexOptions.IgnoreCase) && line.Length <= 80)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Format heading text (remove numbering, proper case)
    /// </summary>
    private string FormatHeading(string line)
    {
        // Remove leading numbers and formatting
        var heading = Regex.Replace(line, @"^[\d\.]+\s*", "");
        heading = heading.TrimEnd(':');

        // Convert to title case if all uppercase
        if (heading == heading.ToUpper() && heading.Length > 3)
        {
            heading = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(heading.ToLower());
        }

        return heading.Trim();
    }

    /// <summary>
    /// Extract a specific section from content - only extracts 1-3 sentences max
    /// </summary>
    private string ExtractSection(string content, params string[] sectionNames)
    {
        foreach (var sectionName in sectionNames)
        {
            // Look for section header followed by content
            var pattern = $@"(?:{sectionName})\s*:?\s*\n([\s\S]*?)(?=\n\s*\n|\n[A-Z]{{3,}}|\n#+\s|\n\d+\.\s+[A-Z]|$)";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups[1].Value.Trim().Length > 10)
            {
                var sectionContent = match.Groups[1].Value.Trim();
                
                // Get only the first 1-2 sentences (up to ~300 chars or first period/newline)
                var firstSentences = ExtractFirstSentences(sectionContent, 2, 300);
                if (!string.IsNullOrEmpty(firstSentences))
                {
                    return firstSentences;
                }
            }
        }
        return "";
    }

    /// <summary>
    /// Extract first N sentences from text, limited by max characters
    /// </summary>
    private string ExtractFirstSentences(string text, int maxSentences, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Clean up the text
        var cleanText = Regex.Replace(text, @"\s+", " ").Trim();
        
        // Split by sentence endings
        var sentences = Regex.Split(cleanText, @"(?<=[.!?])\s+");
        
        var result = new StringBuilder();
        var sentenceCount = 0;
        
        foreach (var sentence in sentences)
        {
            if (sentenceCount >= maxSentences || result.Length + sentence.Length > maxChars)
                break;
                
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                if (result.Length > 0)
                    result.Append(" ");
                result.Append(sentence.Trim());
                sentenceCount++;
            }
        }
        
        var finalResult = result.ToString().Trim();
        
        // If still too long, truncate at last complete word
        if (finalResult.Length > maxChars)
        {
            finalResult = finalResult.Substring(0, maxChars);
            var lastSpace = finalResult.LastIndexOf(' ');
            if (lastSpace > maxChars / 2)
            {
                finalResult = finalResult.Substring(0, lastSpace) + "...";
            }
        }
        
        return finalResult;
    }

    /// <summary>
    /// Extract the first meaningful paragraph
    /// </summary>
    private string ExtractFirstParagraph(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Skip title-like lines and find first paragraph
        for (int i = 1; i < Math.Min(10, lines.Count); i++)
        {
            var line = lines[i];
            if (line.Length > 50 && line.Length < 500 && !IsLikelyHeading(line))
            {
                return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
            }
        }

        return "";
    }

    /// <summary>
    /// Extract keywords from content
    /// </summary>
    private List<string> ExtractKeywords(string content)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowerContent = content.ToLower();

        // Technical keywords to look for
        var techKeywords = new[] 
        { 
            "powershell", "windows", "azure", "office", "outlook", "teams", "sharepoint",
            "vpn", "network", "security", "password", "email", "printer", "software",
            "install", "configuration", "troubleshoot", "error", "fix", "solution",
            "sap", "fiori", "active directory", "exchange", "onedrive"
        };

        foreach (var keyword in techKeywords)
        {
            if (lowerContent.Contains(keyword))
            {
                keywords.Add(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(keyword));
            }
        }

        return keywords.Take(10).ToList();
    }

    /// <summary>
    /// Detect document language
    /// </summary>
    private string DetectLanguage(string content)
    {
        var lowerContent = content.ToLower();
        
        // Spanish indicators
        var spanishWords = new[] { " el ", " la ", " los ", " las ", " de ", " en ", " que ", " por ", " para ", " con " };
        var spanishCount = spanishWords.Count(w => lowerContent.Contains(w));
        
        // English indicators
        var englishWords = new[] { " the ", " and ", " for ", " with ", " that ", " this ", " from ", " are ", " have " };
        var englishCount = englishWords.Count(w => lowerContent.Contains(w));

        return spanishCount > englishCount ? "Spanish" : "English";
    }

    /// <summary>
    /// Generate a KB number
    /// </summary>
    private string GenerateKBNumber()
    {
        return $"KB{DateTime.Now:yyMMddHHmm}";
    }

    /// <summary>
    /// Clean title text
    /// </summary>
    private string CleanTitle(string title)
    {
        // Remove common prefixes
        title = Regex.Replace(title, @"^(KB\d+\s*[-:]\s*)", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"^(Document\s*[-:]\s*)", "", RegexOptions.IgnoreCase);
        
        // Clean up spacing
        title = Regex.Replace(title, @"\s+", " ").Trim();
        
        return title.Length > 200 ? title.Substring(0, 200) : title;
    }

    /// <summary>
    /// Clean and normalize Markdown output
    /// </summary>
    private string CleanMarkdown(string markdown)
    {
        // Remove excessive blank lines
        markdown = Regex.Replace(markdown, @"\n{4,}", "\n\n\n");
        
        // Remove trailing whitespace
        markdown = Regex.Replace(markdown, @"[ \t]+\n", "\n");
        
        // Ensure proper spacing around headers
        markdown = Regex.Replace(markdown, @"(##[^\n]+)\n([^\n#])", "$1\n\n$2");
        
        return markdown.Trim();
    }
}

/// <summary>
/// Result of PDF document processing
/// </summary>
public class PdfDocumentResult
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public KnowledgeArticle? Article { get; set; }
    public int PageCount { get; set; }
    public int ImageCount { get; set; }
}
