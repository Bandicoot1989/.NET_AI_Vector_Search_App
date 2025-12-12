using Azure.Storage.Blobs;
using RecipeSearchWeb.Models;
using System.Text.Json;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for tracking and persisting Jira Harvester statistics
/// </summary>
public class HarvesterStatsService
{
    private readonly BlobContainerClient? _containerClient;
    private readonly JiraSolutionStorageService _storageService;
    private readonly ILogger<HarvesterStatsService> _logger;
    private const string StatsBlob = "harvester-stats.json";
    private const string RunHistoryBlob = "harvester-run-history.json";
    private const int MaxRunHistoryRecords = 100;
    
    // In-memory state (updated by the harvester service)
    private static HarvesterRunState _currentRunState = new();
    private static readonly object _stateLock = new();

    public HarvesterStatsService(
        IConfiguration configuration,
        JiraSolutionStorageService storageService,
        ILogger<HarvesterStatsService> logger)
    {
        _storageService = storageService;
        _logger = logger;
        
        try
        {
            var connectionString = configuration["AzureStorage:ConnectionString"] 
                ?? configuration["AZURE_STORAGE_CONNECTION_STRING"];
            
            if (!string.IsNullOrEmpty(connectionString))
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                _containerClient = blobServiceClient.GetBlobContainerClient("jira-solutions");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize HarvesterStatsService");
        }
    }
    
    /// <summary>
    /// Update the current run state (called by harvester during execution)
    /// </summary>
    public static void UpdateRunState(Action<HarvesterRunState> update)
    {
        lock (_stateLock)
        {
            update(_currentRunState);
        }
    }
    
    /// <summary>
    /// Get the current run state snapshot
    /// </summary>
    public static HarvesterRunState GetRunStateSnapshot()
    {
        lock (_stateLock)
        {
            return new HarvesterRunState
            {
                IsRunning = _currentRunState.IsRunning,
                IsConfigured = _currentRunState.IsConfigured,
                LastHarvestTime = _currentRunState.LastHarvestTime,
                NextScheduledHarvest = _currentRunState.NextScheduledHarvest,
                HarvestInterval = _currentRunState.HarvestInterval,
                TotalTicketsProcessed = _currentRunState.TotalTicketsProcessed,
                TotalSolutionsHarvested = _currentRunState.TotalSolutionsHarvested,
                TotalTicketsSkipped = _currentRunState.TotalTicketsSkipped,
                TotalTicketsNoSolution = _currentRunState.TotalTicketsNoSolution,
                LastRunTicketsFound = _currentRunState.LastRunTicketsFound,
                LastRunNewSolutions = _currentRunState.LastRunNewSolutions,
                LastRunSkipped = _currentRunState.LastRunSkipped,
                LastRunNoSolution = _currentRunState.LastRunNoSolution,
                LastRunDuration = _currentRunState.LastRunDuration,
                LastRunSuccess = _currentRunState.LastRunSuccess,
                LastRunError = _currentRunState.LastRunError
            };
        }
    }
    
    /// <summary>
    /// Get comprehensive harvester statistics
    /// </summary>
    public async Task<HarvesterStats> GetStatsAsync()
    {
        var state = GetRunStateSnapshot();
        var stats = new HarvesterStats
        {
            IsRunning = state.IsRunning,
            IsConfigured = state.IsConfigured,
            LastHarvestTime = state.LastHarvestTime,
            NextScheduledHarvest = state.NextScheduledHarvest,
            HarvestInterval = state.HarvestInterval,
            
            TotalTicketsProcessed = state.TotalTicketsProcessed,
            TotalSolutionsHarvested = state.TotalSolutionsHarvested,
            TotalTicketsSkipped = state.TotalTicketsSkipped,
            TotalTicketsNoSolution = state.TotalTicketsNoSolution,
            
            LastRunTicketsFound = state.LastRunTicketsFound,
            LastRunNewSolutions = state.LastRunNewSolutions,
            LastRunSkipped = state.LastRunSkipped,
            LastRunNoSolution = state.LastRunNoSolution,
            LastRunDuration = state.LastRunDuration,
            LastRunSuccess = state.LastRunSuccess,
            LastRunError = state.LastRunError
        };
        
        // Get storage statistics
        try
        {
            var (sizeBytes, solutionCount) = await _storageService.GetStorageStatsAsync();
            stats.StorageSizeBytes = sizeBytes;
            stats.SolutionsInStorage = solutionCount;
            
            // Load solutions for breakdown and recent list
            var solutions = await _storageService.LoadSolutionsAsync();
            
            // Group by system
            stats.SolutionsBySystem = solutions
                .GroupBy(s => s.System ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count());
            
            // Group by category
            stats.SolutionsByCategory = solutions
                .GroupBy(s => s.Category ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count());
            
            // Recent solutions (last 10)
            stats.RecentSolutions = solutions
                .OrderByDescending(s => s.HarvestedDate)
                .Take(10)
                .Select(s => new HarvestedSolutionSummary
                {
                    TicketId = s.TicketId,
                    Title = TruncateText(s.TicketTitle, 60),
                    System = s.System,
                    Category = s.Category,
                    HarvestedDate = s.HarvestedDate,
                    ResolvedDate = s.ResolvedDate,
                    ValidationCount = s.ValidationCount,
                    IsPromoted = s.IsPromoted,
                    JiraUrl = s.JiraUrl,
                    KeywordCount = s.Keywords?.Count ?? 0,
                    HasEmbedding = s.Embedding.Length > 0
                })
                .ToList();
            
            // Build trend data from solutions
            stats.TrendData = BuildTrendData(solutions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load solution statistics");
        }
        
        // Try to load run history for more accurate totals
        try
        {
            var history = await LoadRunHistoryAsync();
            if (history.Any())
            {
                // Update totals from history if we don't have in-memory state
                if (stats.TotalTicketsProcessed == 0)
                {
                    stats.TotalTicketsProcessed = history.Sum(h => h.TicketsFound);
                    stats.TotalSolutionsHarvested = history.Sum(h => h.NewSolutions);
                    stats.TotalTicketsSkipped = history.Sum(h => h.Skipped);
                    stats.TotalTicketsNoSolution = history.Sum(h => h.NoSolution);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load run history");
        }
        
        return stats;
    }
    
    /// <summary>
    /// Record a harvester run completion
    /// </summary>
    public async Task RecordRunAsync(HarvesterRunRecord record)
    {
        if (_containerClient == null) return;
        
        try
        {
            var history = await LoadRunHistoryAsync();
            history.Insert(0, record);
            
            // Keep only the last N records
            if (history.Count > MaxRunHistoryRecords)
            {
                history = history.Take(MaxRunHistoryRecords).ToList();
            }
            
            await SaveRunHistoryAsync(history);
            _logger.LogDebug("Recorded harvester run: {NewSolutions} new solutions", record.NewSolutions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record harvester run");
        }
    }
    
    /// <summary>
    /// Load run history from storage
    /// </summary>
    public async Task<List<HarvesterRunRecord>> LoadRunHistoryAsync()
    {
        if (_containerClient == null) return new List<HarvesterRunRecord>();
        
        try
        {
            var blobClient = _containerClient.GetBlobClient(RunHistoryBlob);
            if (!await blobClient.ExistsAsync())
            {
                return new List<HarvesterRunRecord>();
            }
            
            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            return JsonSerializer.Deserialize<List<HarvesterRunRecord>>(json) ?? new List<HarvesterRunRecord>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load run history");
            return new List<HarvesterRunRecord>();
        }
    }
    
    private async Task SaveRunHistoryAsync(List<HarvesterRunRecord> history)
    {
        if (_containerClient == null) return;
        
        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
        var blobClient = _containerClient.GetBlobClient(RunHistoryBlob);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
    }
    
    private List<HarvesterTrendPoint> BuildTrendData(List<JiraSolution> solutions)
    {
        var trend = new List<HarvesterTrendPoint>();
        var today = DateTime.UtcNow.Date;
        
        for (int i = 6; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var daysSolutions = solutions.Where(s => s.HarvestedDate.Date == date).ToList();
            
            trend.Add(new HarvesterTrendPoint
            {
                Date = date,
                DayLabel = i == 0 ? "Today" : i == 1 ? "Yesterday" : date.ToString("ddd"),
                Harvested = daysSolutions.Count,
                Processed = daysSolutions.Count, // Same for now
                NoSolution = 0 // Would need to track this separately
            });
        }
        
        return trend;
    }
    
    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length > maxLength ? text.Substring(0, maxLength - 3) + "..." : text;
    }
}

/// <summary>
/// In-memory state of the harvester service
/// </summary>
public class HarvesterRunState
{
    public bool IsRunning { get; set; }
    public bool IsConfigured { get; set; }
    public DateTime? LastHarvestTime { get; set; }
    public DateTime? NextScheduledHarvest { get; set; }
    public TimeSpan HarvestInterval { get; set; } = TimeSpan.FromHours(6);
    
    public int TotalTicketsProcessed { get; set; }
    public int TotalSolutionsHarvested { get; set; }
    public int TotalTicketsSkipped { get; set; }
    public int TotalTicketsNoSolution { get; set; }
    
    public int LastRunTicketsFound { get; set; }
    public int LastRunNewSolutions { get; set; }
    public int LastRunSkipped { get; set; }
    public int LastRunNoSolution { get; set; }
    public TimeSpan? LastRunDuration { get; set; }
    public bool LastRunSuccess { get; set; }
    public string? LastRunError { get; set; }
}
