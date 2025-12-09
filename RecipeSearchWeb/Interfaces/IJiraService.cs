using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for Jira API client operations
/// Supports both Jira Cloud and Jira Server/Data Center
/// </summary>
public interface IJiraClient
{
    /// <summary>
    /// Test the connection to Jira
    /// </summary>
    Task<bool> TestConnectionAsync();
    
    /// <summary>
    /// Get resolved/closed tickets from the last N days
    /// </summary>
    /// <param name="days">Number of days to look back</param>
    /// <param name="projectKeys">Optional: specific project keys to filter</param>
    /// <param name="maxResults">Maximum number of tickets to retrieve</param>
    Task<List<JiraTicket>> GetResolvedTicketsAsync(int days = 30, List<string>? projectKeys = null, int maxResults = 100);
    
    /// <summary>
    /// Get a single ticket by its key
    /// </summary>
    Task<JiraTicket?> GetTicketAsync(string ticketKey);
    
    /// <summary>
    /// Get comments for a specific ticket
    /// </summary>
    Task<List<JiraComment>> GetTicketCommentsAsync(string ticketKey);
    
    /// <summary>
    /// Search tickets using JQL (Jira Query Language)
    /// </summary>
    Task<List<JiraTicket>> SearchTicketsAsync(string jql, int maxResults = 50);
    
    /// <summary>
    /// Check if a ticket has already been harvested (to avoid duplicates)
    /// </summary>
    Task<bool> IsTicketAlreadyHarvestedAsync(string ticketKey);
}

/// <summary>
/// Interface for the Jira Solution Harvester service
/// Processes raw tickets into clean knowledge snippets
/// </summary>
public interface IJiraSolutionHarvester
{
    /// <summary>
    /// Process a single ticket and extract a clean solution snippet
    /// </summary>
    /// <param name="ticket">Raw Jira ticket data</param>
    /// <returns>Processed solution or null if ticket doesn't have a valid solution</returns>
    Task<JiraSolution?> HarvestSolutionAsync(JiraTicket ticket);
    
    /// <summary>
    /// Process multiple tickets in batch
    /// </summary>
    Task<List<JiraSolution>> HarvestSolutionsAsync(List<JiraTicket> tickets, IProgress<int>? progress = null);
    
    /// <summary>
    /// Run a full harvest cycle: fetch resolved tickets, process, and store
    /// </summary>
    /// <param name="days">Number of days to look back</param>
    Task<HarvestResult> RunHarvestCycleAsync(int days = 30);
}

/// <summary>
/// Result of a harvest operation
/// </summary>
public class HarvestResult
{
    public int TicketsProcessed { get; set; }
    public int SolutionsExtracted { get; set; }
    public int SkippedDuplicates { get; set; }
    public int FailedExtractions { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Interface for searching Jira solutions
/// </summary>
public interface IJiraSolutionService
{
    /// <summary>
    /// Initialize the service (load solutions from storage)
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Search for similar solutions using semantic search
    /// </summary>
    /// <param name="query">User query</param>
    /// <param name="topK">Number of results to return</param>
    Task<List<JiraSolutionSearchResult>> SearchSolutionsAsync(string query, int topK = 5);
    
    /// <summary>
    /// Search formatted for agent context (returns string for LLM prompt)
    /// </summary>
    Task<string> SearchForAgentAsync(string query, int topK = 3);
    
    /// <summary>
    /// Get a solution by ticket ID
    /// </summary>
    Task<JiraSolution?> GetSolutionByTicketIdAsync(string ticketId);
    
    /// <summary>
    /// Increment the validation count for a solution (user found it helpful)
    /// </summary>
    Task ValidateSolutionAsync(string ticketId);
    
    /// <summary>
    /// Get solutions that have been validated multiple times (candidates for promotion)
    /// </summary>
    Task<List<JiraSolution>> GetPromotionCandidatesAsync(int minValidations = 5);
    
    /// <summary>
    /// Mark a solution as promoted to official documentation
    /// </summary>
    Task MarkAsPromotedAsync(string ticketId);
    
    /// <summary>
    /// Get statistics about the solution database
    /// </summary>
    Task<JiraSolutionStats> GetStatsAsync();
}

/// <summary>
/// Search result with relevance score
/// </summary>
public class JiraSolutionSearchResult
{
    public JiraSolution Solution { get; set; } = null!;
    public float RelevanceScore { get; set; }
    public float SimilarityScore { get; set; }
    
    /// <summary>
    /// Adjusted score including validation boost
    /// </summary>
    public float BoostedScore => RelevanceScore * (1 + (Solution.ValidationCount * 0.05f));
}

/// <summary>
/// Statistics about the Jira solution database
/// </summary>
public class JiraSolutionStats
{
    public int TotalSolutions { get; set; }
    public int ValidatedSolutions { get; set; }
    public int PromotedSolutions { get; set; }
    public Dictionary<string, int> SolutionsBySystem { get; set; } = new();
    public Dictionary<string, int> SolutionsByCategory { get; set; } = new();
    public DateTime? LastHarvestDate { get; set; }
    public DateTime? OldestSolution { get; set; }
    public DateTime? NewestSolution { get; set; }
}
