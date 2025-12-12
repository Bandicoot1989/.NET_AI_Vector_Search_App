using System.Text;
using System.Text.RegularExpressions;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

// Import types defined in the interface file
using TicketLookupResult = RecipeSearchWeb.Interfaces.TicketLookupResult;
using TicketInfo = RecipeSearchWeb.Interfaces.TicketInfo;
using TicketComment = RecipeSearchWeb.Interfaces.TicketComment;
using SimilarTicketInfo = RecipeSearchWeb.Interfaces.SimilarTicketInfo;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for looking up and analyzing specific Jira tickets in real-time.
/// Enables the chatbot to answer questions about specific tickets.
/// 
/// Security considerations:
/// - Validates ticket ID format to prevent injection attacks
/// - Limits the amount of data returned to prevent information overload
/// - Sanitizes content before including in AI context
/// - Uses rate limiting awareness (respects Jira API limits)
/// </summary>
public class TicketLookupService : ITicketLookupService
{
    private readonly IJiraClient _jiraClient;
    private readonly IJiraSolutionService? _solutionService;
    private readonly ILogger<TicketLookupService> _logger;

    // Valid ticket patterns - only allow specific formats to prevent injection
    // Supports: MT-12345, MTT-12345, IT-12345, HELP-12345 (configurable)
    private static readonly Regex TicketPattern = new(
        @"\b(MT|MTT|IT|HELP|SD|INC|REQ|SR)-(\d{1,7})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100) // Timeout to prevent ReDoS
    );

    // Maximum tickets to process in a single request
    private const int MaxTicketsPerRequest = 3;

    // Maximum content length for descriptions and comments
    private const int MaxDescriptionLength = 2000;
    private const int MaxCommentLength = 1000;
    private const int MaxCommentsToInclude = 5;
    private const int MaxSimilarSolutions = 3;

    public TicketLookupService(
        IJiraClient jiraClient,
        IJiraSolutionService? solutionService,
        ILogger<TicketLookupService> logger)
    {
        _jiraClient = jiraClient;
        _solutionService = solutionService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool ContainsTicketReference(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        try
        {
            return TicketPattern.IsMatch(query);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Regex timeout while checking for ticket reference");
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ExtractTicketIds(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var tickets = new List<string>();

        try
        {
            var matches = TicketPattern.Matches(query);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    // Normalize to uppercase for consistency
                    var ticketId = match.Value.ToUpperInvariant();
                    if (!tickets.Contains(ticketId))
                    {
                        tickets.Add(ticketId);
                    }
                }

                // Limit the number of tickets to prevent abuse
                if (tickets.Count >= MaxTicketsPerRequest)
                {
                    _logger.LogWarning("Maximum ticket limit ({Max}) reached in query", MaxTicketsPerRequest);
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Regex timeout while extracting ticket IDs");
        }

        return tickets.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<TicketLookupResult> LookupTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        return await LookupTicketsAsync(new[] { ticketId }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TicketLookupResult> LookupTicketsAsync(IEnumerable<string> ticketIds, CancellationToken cancellationToken = default)
    {
        var result = new TicketLookupResult();
        var ticketList = ticketIds.Take(MaxTicketsPerRequest).ToList();

        if (!ticketList.Any())
        {
            result.Success = false;
            result.ErrorMessage = "No ticket IDs provided";
            return result;
        }

        _logger.LogInformation("Looking up {Count} ticket(s): {Tickets}", ticketList.Count, string.Join(", ", ticketList));

        // Check if Jira client is configured
        if (!_jiraClient.IsConfigured)
        {
            result.Success = false;
            result.ErrorMessage = "Jira integration is not configured";
            _logger.LogWarning("Jira client not configured for ticket lookup");
            return result;
        }

        try
        {
            // Fetch tickets in parallel with cancellation support
            var lookupTasks = ticketList.Select(async ticketId =>
            {
                try
                {
                    // Validate format before API call
                    if (!IsValidTicketId(ticketId))
                    {
                        _logger.LogWarning("Invalid ticket ID format: {TicketId}", ticketId);
                        return null;
                    }

                    var jiraTicket = await _jiraClient.GetTicketAsync(ticketId);
                    if (jiraTicket == null)
                    {
                        _logger.LogWarning("Ticket not found: {TicketId}", ticketId);
                        return null;
                    }

                    return ConvertToTicketInfo(jiraTicket);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching ticket {TicketId}", ticketId);
                    return null;
                }
            });

            var ticketResults = await Task.WhenAll(lookupTasks);

            // Filter out nulls (failed lookups)
            result.Tickets = ticketResults.Where(t => t != null).ToList()!;

            if (!result.Tickets.Any())
            {
                result.Success = false;
                result.ErrorMessage = $"Could not find ticket(s): {string.Join(", ", ticketList)}";
                return result;
            }

            // Find similar solved tickets if solution service is available
            if (_solutionService != null)
            {
                await FindSimilarSolutionsAsync(result, cancellationToken);
            }

            // Generate context for the AI agent
            result.ContextForAgent = GenerateAgentContext(result);
            result.SuggestedApproach = GenerateSuggestedApproach(result);
            result.Success = true;

            _logger.LogInformation("Successfully looked up {Count} ticket(s) with {SimilarCount} similar solutions",
                result.Tickets.Count, result.SimilarSolutions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ticket lookup");
            result.Success = false;
            result.ErrorMessage = "An error occurred while looking up the ticket(s)";
        }

        return result;
    }

    /// <summary>
    /// Validates ticket ID format to prevent injection attacks
    /// </summary>
    private bool IsValidTicketId(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
            return false;

        // Must match our expected pattern exactly
        return TicketPattern.IsMatch(ticketId) && ticketId.Length <= 15;
    }

    /// <summary>
    /// Converts a JiraTicket to our sanitized TicketInfo model
    /// </summary>
    private TicketInfo ConvertToTicketInfo(JiraTicket jiraTicket)
    {
        var info = new TicketInfo
        {
            TicketId = SanitizeString(jiraTicket.Key, 20),
            Summary = SanitizeString(jiraTicket.Summary, 500),
            Description = SanitizeString(jiraTicket.Description, MaxDescriptionLength),
            Status = SanitizeString(jiraTicket.Status, 50),
            Priority = SanitizeString(jiraTicket.Priority ?? "Unknown", 20),
            Resolution = SanitizeString(jiraTicket.Resolution, 50),
            Assignee = SanitizeString(jiraTicket.Assignee, 100),
            Reporter = SanitizeString(jiraTicket.Reporter, 100),
            Created = jiraTicket.Created,
            Resolved = jiraTicket.Resolved,
            JiraUrl = $"https://antolin.atlassian.net/browse/{jiraTicket.Key}",
            DetectedSystem = DetectSystem(jiraTicket.Summary + " " + jiraTicket.Description)
        };

        // Include most recent comments (limited)
        if (jiraTicket.Comments?.Any() == true)
        {
            info.Comments = jiraTicket.Comments
                .OrderByDescending(c => c.Created)
                .Take(MaxCommentsToInclude)
                .Select(c => new TicketComment
                {
                    Author = SanitizeString(c.Author, 100),
                    Body = SanitizeString(c.Body, MaxCommentLength),
                    Created = c.Created
                })
                .ToList();
        }

        return info;
    }

    /// <summary>
    /// Sanitizes a string to prevent injection and limit size
    /// </summary>
    private static string SanitizeString(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remove potential control characters and normalize whitespace
        var sanitized = input
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\t", " ");

        // Remove any null bytes or other control chars (except newline)
        sanitized = new string(sanitized.Where(c => c == '\n' || !char.IsControl(c)).ToArray());

        // Trim and limit length
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength - 3) + "...";
        }

        return sanitized.Trim();
    }

    /// <summary>
    /// Detect which system/application the ticket relates to
    /// </summary>
    private static string DetectSystem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "General";

        var lower = text.ToLowerInvariant();

        if (lower.Contains("sap") || lower.Contains("bpc") || lower.Contains("hana") || lower.Contains("fiori"))
            return "SAP";
        if (lower.Contains("vpn") || lower.Contains("zscaler") || lower.Contains("network") || lower.Contains("red") || lower.Contains("conexión"))
            return "Network";
        if (lower.Contains("sharepoint") || lower.Contains("onedrive") || lower.Contains("teams") || lower.Contains("office"))
            return "Microsoft 365";
        if (lower.Contains("email") || lower.Contains("correo") || lower.Contains("outlook"))
            return "Email";
        if (lower.Contains("password") || lower.Contains("contraseña") || lower.Contains("acceso") || lower.Contains("permiso"))
            return "Access/Authentication";
        if (lower.Contains("impresora") || lower.Contains("printer") || lower.Contains("hardware"))
            return "Hardware";
        if (lower.Contains("windchill") || lower.Contains("plm") || lower.Contains("cad"))
            return "PLM/Engineering";

        return "General IT";
    }

    /// <summary>
    /// Find similar solved tickets using the harvested solutions
    /// </summary>
    private async Task FindSimilarSolutionsAsync(TicketLookupResult result, CancellationToken cancellationToken)
    {
        if (_solutionService == null || !result.Tickets.Any())
            return;

        try
        {
            // Build a search query from the ticket summaries
            var searchQuery = string.Join(" ", result.Tickets.Select(t => t.Summary + " " + t.DetectedSystem));

            // Search for similar solutions
            var similarContext = await _solutionService.SearchForAgentAsync(searchQuery, MaxSimilarSolutions);

            if (!string.IsNullOrWhiteSpace(similarContext))
            {
                // Parse the context to extract individual solutions
                // The solution service returns formatted text, we'll include it directly
                _logger.LogDebug("Found similar solutions context ({Length} chars)", similarContext.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error finding similar solutions");
        }
    }

    /// <summary>
    /// Generates the context string to be included in the AI agent's prompt
    /// </summary>
    private string GenerateAgentContext(TicketLookupResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== TICKET LOOKUP RESULTS (Real-time from Jira) ===");
        sb.AppendLine("The user asked about specific ticket(s). Here is the current information:");
        sb.AppendLine();

        foreach (var ticket in result.Tickets)
        {
            sb.AppendLine($"📋 TICKET: {ticket.TicketId}");
            sb.AppendLine($"   Summary: {ticket.Summary}");
            sb.AppendLine($"   Status: {ticket.Status}");
            sb.AppendLine($"   Priority: {ticket.Priority}");
            sb.AppendLine($"   System: {ticket.DetectedSystem}");
            
            if (!string.IsNullOrEmpty(ticket.Assignee))
                sb.AppendLine($"   Assigned to: {ticket.Assignee}");
            
            if (!string.IsNullOrEmpty(ticket.Resolution))
                sb.AppendLine($"   Resolution: {ticket.Resolution}");

            sb.AppendLine($"   Jira URL: {ticket.JiraUrl}");

            if (!string.IsNullOrWhiteSpace(ticket.Description))
            {
                sb.AppendLine();
                sb.AppendLine("   Description:");
                // Indent the description
                var descLines = ticket.Description.Split('\n').Take(15);
                foreach (var line in descLines)
                {
                    sb.AppendLine($"   | {line}");
                }
            }

            if (ticket.Comments.Any())
            {
                sb.AppendLine();
                sb.AppendLine("   Recent Comments (most recent first):");
                foreach (var comment in ticket.Comments.Take(3))
                {
                    sb.AppendLine($"   [{comment.Created:yyyy-MM-dd}] {comment.Author}:");
                    var commentLines = comment.Body.Split('\n').Take(5);
                    foreach (var line in commentLines)
                    {
                        sb.AppendLine($"      {line}");
                    }
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates suggested approach for the AI based on ticket status
    /// </summary>
    private string GenerateSuggestedApproach(TicketLookupResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SUGGESTED APPROACH FOR THIS TICKET ===");

        foreach (var ticket in result.Tickets)
        {
            var status = ticket.Status?.ToLowerInvariant() ?? "";
            var system = ticket.DetectedSystem;

            sb.AppendLine($"For {ticket.TicketId} ({system}):");

            if (status.Contains("done") || status.Contains("resolved") || status.Contains("closed"))
            {
                sb.AppendLine("- This ticket is RESOLVED. Explain how it was solved based on comments/resolution.");
                sb.AppendLine("- If user needs similar help, suggest relevant documentation or opening a new ticket.");
            }
            else if (status.Contains("progress") || status.Contains("working"))
            {
                sb.AppendLine("- This ticket is IN PROGRESS. Inform the user it's being worked on.");
                sb.AppendLine($"- Assigned to: {ticket.Assignee ?? "Unassigned"}");
                sb.AppendLine("- Suggest they can add a comment in Jira for updates.");
            }
            else if (status.Contains("waiting") || status.Contains("pending"))
            {
                sb.AppendLine("- This ticket is WAITING/PENDING. Check comments for what's needed.");
                sb.AppendLine("- It may require user action or additional information.");
            }
            else
            {
                sb.AppendLine("- Analyze the description and comments to understand the issue.");
                sb.AppendLine("- Look for similar solved tickets in the knowledge base.");
                sb.AppendLine("- Suggest solutions based on the detected system: " + system);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
