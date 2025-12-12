namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Service for looking up and analyzing specific Jira tickets in real-time.
/// Enables the chatbot to answer questions about specific tickets (e.g., "Help me with MT-799225").
/// </summary>
public interface ITicketLookupService
{
    /// <summary>
    /// Checks if the query contains a ticket reference (e.g., MT-12345, MTT-12345)
    /// </summary>
    /// <param name="query">User's question</param>
    /// <returns>True if a ticket reference is detected</returns>
    bool ContainsTicketReference(string query);

    /// <summary>
    /// Extracts all ticket IDs from a query string
    /// </summary>
    /// <param name="query">User's question</param>
    /// <returns>List of extracted ticket IDs (e.g., ["MT-12345", "MTT-67890"])</returns>
    IReadOnlyList<string> ExtractTicketIds(string query);

    /// <summary>
    /// Looks up a specific ticket and generates context for the AI agent.
    /// Includes ticket details and similar solved tickets for reference.
    /// </summary>
    /// <param name="ticketId">The ticket ID to look up (e.g., MT-12345)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Ticket lookup result with context for the AI</returns>
    Task<TicketLookupResult> LookupTicketAsync(string ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up multiple tickets and generates combined context.
    /// </summary>
    /// <param name="ticketIds">List of ticket IDs to look up</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Combined lookup results</returns>
    Task<TicketLookupResult> LookupTicketsAsync(IEnumerable<string> ticketIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a ticket lookup operation
/// </summary>
public class TicketLookupResult
{
    /// <summary>
    /// Whether the lookup was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if lookup failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The tickets that were found
    /// </summary>
    public List<TicketInfo> Tickets { get; set; } = new();

    /// <summary>
    /// Similar solved tickets that might help
    /// </summary>
    public List<SimilarTicketInfo> SimilarSolutions { get; set; } = new();

    /// <summary>
    /// Pre-formatted context string for the AI agent
    /// </summary>
    public string ContextForAgent { get; set; } = string.Empty;

    /// <summary>
    /// Suggested response hints for the AI
    /// </summary>
    public string SuggestedApproach { get; set; } = string.Empty;
}

/// <summary>
/// Information about a looked-up ticket
/// </summary>
public class TicketInfo
{
    public string TicketId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? Resolution { get; set; }
    public string? Assignee { get; set; }
    public string? Reporter { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Resolved { get; set; }
    public List<TicketComment> Comments { get; set; } = new();
    public string JiraUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Detected system/application (SAP, Network, etc.)
    /// </summary>
    public string DetectedSystem { get; set; } = string.Empty;
}

/// <summary>
/// Simplified comment from a ticket
/// </summary>
public class TicketComment
{
    public string Author { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime Created { get; set; }
}

/// <summary>
/// Information about a similar solved ticket
/// </summary>
public class SimilarTicketInfo
{
    public string TicketId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Solution { get; set; } = string.Empty;
    public double SimilarityScore { get; set; }
    public string JiraUrl { get; set; } = string.Empty;
}
