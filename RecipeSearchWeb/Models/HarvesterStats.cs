namespace RecipeSearchWeb.Models;

/// <summary>
/// Statistics about the Jira Solution Harvester service
/// </summary>
public class HarvesterStats
{
    // Service Status
    public bool IsRunning { get; set; }
    public bool IsConfigured { get; set; }
    public DateTime? LastHarvestTime { get; set; }
    public DateTime? NextScheduledHarvest { get; set; }
    public TimeSpan HarvestInterval { get; set; } = TimeSpan.FromHours(6);
    
    // Lifetime Statistics
    public int TotalTicketsProcessed { get; set; }
    public int TotalSolutionsHarvested { get; set; }
    public int TotalTicketsSkipped { get; set; }
    public int TotalTicketsNoSolution { get; set; }
    
    // Last Run Statistics
    public int LastRunTicketsFound { get; set; }
    public int LastRunNewSolutions { get; set; }
    public int LastRunSkipped { get; set; }
    public int LastRunNoSolution { get; set; }
    public TimeSpan? LastRunDuration { get; set; }
    public bool LastRunSuccess { get; set; }
    public string? LastRunError { get; set; }
    
    // Storage Statistics
    public int SolutionsInStorage { get; set; }
    public long StorageSizeBytes { get; set; }
    public string StorageSizeFormatted => FormatBytes(StorageSizeBytes);
    
    // Solutions by System/Category
    public Dictionary<string, int> SolutionsBySystem { get; set; } = new();
    public Dictionary<string, int> SolutionsByCategory { get; set; } = new();
    
    // Recent harvested solutions (last 10)
    public List<HarvestedSolutionSummary> RecentSolutions { get; set; } = new();
    
    // Trend data (last 7 days)
    public List<HarvesterTrendPoint> TrendData { get; set; } = new();
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

/// <summary>
/// Summary of a harvested solution for display
/// </summary>
public class HarvestedSolutionSummary
{
    public string TicketId { get; set; } = "";
    public string Title { get; set; } = "";
    public string System { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime HarvestedDate { get; set; }
    public DateTime ResolvedDate { get; set; }
    public int ValidationCount { get; set; }
    public bool IsPromoted { get; set; }
    public string JiraUrl { get; set; } = "";
    public int KeywordCount { get; set; }
    public bool HasEmbedding { get; set; }
}

/// <summary>
/// Trend point for harvesting activity
/// </summary>
public class HarvesterTrendPoint
{
    public DateTime Date { get; set; }
    public string DayLabel { get; set; } = "";
    public int Harvested { get; set; }
    public int Processed { get; set; }
    public int NoSolution { get; set; }
}

/// <summary>
/// Persistent harvester run history for tracking performance over time
/// </summary>
public class HarvesterRunRecord
{
    public DateTime Timestamp { get; set; }
    public int TicketsFound { get; set; }
    public int NewSolutions { get; set; }
    public int Skipped { get; set; }
    public int NoSolution { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
