using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RecipeSearchWeb.Models;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for rendering Markdown content to HTML with enhanced image support
/// Uses Markdig for professional Markdown processing
/// </summary>
public class MarkdownRenderService
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderService()
    {
        // Configure Markdig with all useful extensions
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()      // Tables, footnotes, task lists, etc.
            .UseEmojiAndSmiley()          // Emoji support
            .UseSoftlineBreakAsHardlineBreak() // Preserve line breaks
            .Build();
    }

    /// <summary>
    /// Render Markdown content to HTML with KB images properly integrated
    /// </summary>
    public string RenderToHtml(string markdown, List<KBImage>? images = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            markdown = ""; // Allow empty markdown if we have images

        images ??= new List<KBImage>();

        // Pre-process: Replace image placeholders with actual image URLs
        var processedMarkdown = PreProcessImagePlaceholders(markdown, images);

        // Parse the Markdown document
        var document = Markdown.Parse(processedMarkdown, _pipeline);

        // Render to HTML
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForInline = true,
            EnableHtmlForBlock = true
        };

        // Configure renderer to add classes to elements
        renderer.ObjectRenderers.FindExact<CodeBlockRenderer>()?.BlocksAsDiv.Clear();
        
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        var html = writer.ToString();

        // Post-process: Enhance images with click handlers and styling
        html = PostProcessImages(html, images);

        // ALWAYS append image gallery if we have images with valid URLs
        // This ensures images are shown even if markdown parsing failed to find them
        if (images.Any(img => !string.IsNullOrEmpty(img.BlobUrl)))
        {
            html = AppendImageGallery(html, images);
        }

        // Wrap in container for proper styling
        return $"<div class=\"kb-content\">{html}</div>";
    }

    /// <summary>
    /// Append an image gallery section at the end of the content
    /// </summary>
    private string AppendImageGallery(string html, List<KBImage> images)
    {
        var validImages = images.Where(img => !string.IsNullOrEmpty(img.BlobUrl)).ToList();
        if (!validImages.Any()) return html;

        var sb = new StringBuilder(html);
        sb.AppendLine("<div class=\"kb-image-gallery\">");
        sb.AppendLine("<h4>📷 Document Images</h4>");
        sb.AppendLine("<div class=\"gallery-grid\">");

        for (int i = 0; i < validImages.Count; i++)
        {
            var img = validImages[i];
            var caption = !string.IsNullOrEmpty(img.Caption) ? img.Caption 
                : !string.IsNullOrEmpty(img.AltText) ? img.AltText 
                : $"Figure {i + 1}";

            sb.AppendLine($"<figure class=\"gallery-item\">");
            sb.AppendLine($"  <img src=\"{img.BlobUrl}\" alt=\"{caption}\" onclick=\"openImageFullscreen('{img.BlobUrl}', '{caption.Replace("'", "\\'")}')\" />");
            sb.AppendLine($"  <figcaption>{caption}</figcaption>");
            sb.AppendLine($"</figure>");
        }

        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        return sb.ToString();
    }

    /// <summary>
    /// Pre-process Markdown to replace various image placeholder formats with standard Markdown images
    /// </summary>
    private string PreProcessImagePlaceholders(string markdown, List<KBImage> images)
    {
        var result = markdown;

        // Create a map of images by various identifiers
        var imageMap = new Dictionary<string, KBImage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            imageMap[$"{i}"] = img;
            imageMap[$"{i + 1}"] = img; // 1-based index
            imageMap[img.Id] = img;
            if (!string.IsNullOrEmpty(img.FileName))
            {
                imageMap[img.FileName] = img;
                imageMap[Path.GetFileNameWithoutExtension(img.FileName)] = img;
            }
        }

        // Pattern: ![Image X from PDF](url) - already correct Markdown, just ensure URL is valid
        result = Regex.Replace(result, 
            @"!\[([^\]]*)\]\(([^)]+)\)", 
            match => ProcessMarkdownImage(match, images),
            RegexOptions.IgnoreCase);

        // Pattern: [image:X], [img:X], [Image X], etc.
        result = Regex.Replace(result,
            @"\[(?:image|img|imagen|figure|figura)[:\s]*(\d+)\]",
            match => ReplaceImagePlaceholder(match.Groups[1].Value, images),
            RegexOptions.IgnoreCase);

        // Pattern: {image1}, {img1}
        result = Regex.Replace(result,
            @"\{(?:image|img)(\d+)\}",
            match => ReplaceImagePlaceholder(match.Groups[1].Value, images),
            RegexOptions.IgnoreCase);

        // Pattern: <<image1>>, <<screenshot1>>
        result = Regex.Replace(result,
            @"<<(?:image|screenshot|img)[\s]*(\d+)>>",
            match => ReplaceImagePlaceholder(match.Groups[1].Value, images),
            RegexOptions.IgnoreCase);

        return result;
    }

    /// <summary>
    /// Process an existing Markdown image to ensure URL is correct
    /// </summary>
    private string ProcessMarkdownImage(Match match, List<KBImage> images)
    {
        var altText = match.Groups[1].Value;
        var url = match.Groups[2].Value;

        // Try to find matching image by any reference (filename, URL part, index)
        var image = FindImageByReference(url, images);
        if (image != null && !string.IsNullOrEmpty(image.BlobUrl))
        {
            // Always use the BlobUrl from the image list (which has SAS token)
            return $"![{altText}]({image.BlobUrl})";
        }

        // If no match but URL looks like blob storage, try to find by filename in URL
        if (url.Contains("blob.core.windows.net"))
        {
            var fileName = url.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
            if (!string.IsNullOrEmpty(fileName))
            {
                image = images.FirstOrDefault(i => 
                    i.FileName?.Equals(fileName, StringComparison.OrdinalIgnoreCase) == true ||
                    i.BlobUrl?.Contains(fileName) == true);
                    
                if (image != null)
                {
                    return $"![{altText}]({image.BlobUrl})";
                }
            }
        }

        // Keep original if no match found
        return match.Value;
    }

    /// <summary>
    /// Replace an image placeholder with actual Markdown image syntax
    /// </summary>
    private string ReplaceImagePlaceholder(string reference, List<KBImage> images)
    {
        var image = FindImageByReference(reference, images);
        if (image != null)
        {
            var altText = !string.IsNullOrEmpty(image.AltText) ? image.AltText : $"Image {reference}";
            return $"\n\n![{altText}]({image.BlobUrl})\n\n";
        }
        return $"[Image {reference} not found]";
    }

    /// <summary>
    /// Find an image by various reference types (index, ID, filename)
    /// </summary>
    private KBImage? FindImageByReference(string reference, List<KBImage> images)
    {
        if (images == null || !images.Any())
            return null;

        // Try as 0-based index
        if (int.TryParse(reference, out int index))
        {
            if (index >= 0 && index < images.Count)
                return images[index];
            // Try 1-based
            if (index > 0 && index <= images.Count)
                return images[index - 1];
        }

        // Try by ID
        var byId = images.FirstOrDefault(i => 
            i.Id.Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        // Try by filename
        var byFilename = images.FirstOrDefault(i => 
            i.FileName.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(i.FileName).Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (byFilename != null) return byFilename;

        // Try by partial URL match
        var byUrl = images.FirstOrDefault(i => 
            i.BlobUrl.Contains(reference, StringComparison.OrdinalIgnoreCase));

        return byUrl;
    }

    /// <summary>
    /// Post-process HTML to enhance images with click-to-expand and proper styling
    /// </summary>
    private string PostProcessImages(string html, List<KBImage> images)
    {
        // Find all img tags and enhance them
        var result = Regex.Replace(html, 
            @"<img\s+([^>]*?)src=""([^""]+)""([^>]*?)>",
            match => EnhanceImageTag(match, images),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return result;
    }

    /// <summary>
    /// Enhance an img tag with figure wrapper, click handler, and styling
    /// </summary>
    private string EnhanceImageTag(Match match, List<KBImage> images)
    {
        var beforeSrc = match.Groups[1].Value;
        var src = match.Groups[2].Value;
        var afterSrc = match.Groups[3].Value;

        // Extract alt text if present
        var altMatch = Regex.Match(beforeSrc + afterSrc, @"alt=""([^""]*)""", RegexOptions.IgnoreCase);
        var altText = altMatch.Success ? altMatch.Groups[1].Value : "Image";

        // Find matching KBImage for caption
        var kbImage = images.FirstOrDefault(i => 
            i.BlobUrl.Equals(src, StringComparison.OrdinalIgnoreCase) ||
            src.Contains(i.Id, StringComparison.OrdinalIgnoreCase));

        var caption = kbImage?.Caption ?? kbImage?.AltText;
        var captionHtml = !string.IsNullOrEmpty(caption) 
            ? $"<figcaption>{HttpUtility.HtmlEncode(caption)}</figcaption>" 
            : "";

        // Build enhanced image with figure wrapper
        var imageId = kbImage?.Id ?? Guid.NewGuid().ToString("N")[..8];
        
        return $@"<figure class=""kb-image"" data-image-id=""{imageId}"">
            <img src=""{src}"" alt=""{HttpUtility.HtmlEncode(altText)}"" loading=""lazy"" class=""clickable-image"" onclick=""openImageFullscreen(this.src, '{HttpUtility.JavaScriptStringEncode(altText)}')"" />
            {captionHtml}
        </figure>";
    }

    /// <summary>
    /// Convert plain text to HTML paragraphs (for non-Markdown content)
    /// </summary>
    public string TextToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var html = new StringBuilder();
        var currentParagraph = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentParagraph.Length > 0)
                {
                    html.AppendLine($"<p>{HttpUtility.HtmlEncode(currentParagraph.ToString().Trim())}</p>");
                    currentParagraph.Clear();
                }
            }
            else
            {
                if (currentParagraph.Length > 0)
                    currentParagraph.Append(' ');
                currentParagraph.Append(line.Trim());
            }
        }

        if (currentParagraph.Length > 0)
        {
            html.AppendLine($"<p>{HttpUtility.HtmlEncode(currentParagraph.ToString().Trim())}</p>");
        }

        return html.ToString();
    }
}
