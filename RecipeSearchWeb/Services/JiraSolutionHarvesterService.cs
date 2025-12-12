using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Azure.Storage.Blobs;
using System.Text.Json;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services
{
    /// <summary>
    /// Background service that automatically harvests solutions from resolved Jira tickets.
    /// Fase 4: Generates embeddings and integrates with the search system.
    /// </summary>
    public class JiraSolutionHarvesterService : BackgroundService
    {
        private readonly IJiraClient _jiraClient;
        private readonly BlobContainerClient _blobContainer;
        private readonly JiraSolutionStorageService _storageService;
        private readonly HarvesterStatsService _statsService;
        private readonly EmbeddingClient _embeddingClient;
        private readonly ILogger<JiraSolutionHarvesterService> _logger;
        private readonly TimeSpan _interval;
        private const string ProcessedTicketsBlob = "harvested-tickets.json";
        private HashSet<string> _processedTickets = new();
        private bool _containerInitialized = false;

        public JiraSolutionHarvesterService(
            IJiraClient jiraClient, 
            [FromKeyedServices("harvested-solutions")] BlobContainerClient blobContainer,
            JiraSolutionStorageService storageService,
            HarvesterStatsService statsService,
            EmbeddingClient embeddingClient,
            ILogger<JiraSolutionHarvesterService> logger)
        {
            _jiraClient = jiraClient;
            _blobContainer = blobContainer;
            _storageService = storageService;
            _statsService = statsService;
            _embeddingClient = embeddingClient;
            _logger = logger;
            _interval = TimeSpan.FromHours(6); // Harvest every 6 hours
            
            // Initialize static run state
            HarvesterStatsService.UpdateRunState(s => {
                s.IsConfigured = _jiraClient.IsConfigured;
                s.HarvestInterval = _interval;
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JiraSolutionHarvesterService started - Fase 4: Full integration with search");
            
            // Update state: service is running
            HarvesterStatsService.UpdateRunState(s => {
                s.IsRunning = true;
                s.IsConfigured = _jiraClient.IsConfigured;
            });
            
            // Wait a bit for other services to initialize
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            
            // Initialize container on first run
            if (!_containerInitialized)
            {
                try
                {
                    await _blobContainer.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    await _storageService.InitializeAsync();
                    _containerInitialized = true;
                    _logger.LogInformation("Storage containers initialized");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize storage containers - harvesting disabled");
                    HarvesterStatsService.UpdateRunState(s => {
                        s.IsRunning = false;
                        s.LastRunSuccess = false;
                        s.LastRunError = "Failed to initialize storage: " + ex.Message;
                    });
                    return;
                }
            }
            
            await LoadProcessedTicketsAsync(stoppingToken);
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await HarvestSolutionsAsync(stoppingToken);
                    
                    // Update next scheduled harvest
                    HarvesterStatsService.UpdateRunState(s => {
                        s.NextScheduledHarvest = DateTime.UtcNow.Add(_interval);
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in JiraSolutionHarvesterService");
                    HarvesterStatsService.UpdateRunState(s => {
                        s.LastRunSuccess = false;
                        s.LastRunError = ex.Message;
                    });
                }
                await Task.Delay(_interval, stoppingToken);
            }
            
            HarvesterStatsService.UpdateRunState(s => s.IsRunning = false);
        }

        private async Task HarvestSolutionsAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Harvesting Jira solutions...");
            
            // Get resolved tickets from last 7 days
            var tickets = await _jiraClient.GetResolvedTicketsAsync(7, null, 50);
            _logger.LogInformation("Found {Count} resolved tickets from Jira", tickets.Count);
            
            var newSolutions = new List<JiraSolution>();
            int skipped = 0;
            int noSolution = 0;
            
            foreach (var ticket in tickets)
            {
                if (_processedTickets.Contains(ticket.Key))
                {
                    skipped++;
                    continue;
                }
                
                var solution = await ExtractAndProcessSolutionAsync(ticket, cancellationToken);
                if (solution != null)
                {
                    newSolutions.Add(solution);
                    _processedTickets.Add(ticket.Key);
                }
                else
                {
                    noSolution++;
                    // Still mark as processed to avoid re-checking
                    _processedTickets.Add(ticket.Key);
                }
            }
            
            stopwatch.Stop();
            
            // Record run statistics
            var runRecord = new HarvesterRunRecord
            {
                Timestamp = DateTime.UtcNow,
                TicketsFound = tickets.Count,
                NewSolutions = newSolutions.Count,
                Skipped = skipped,
                NoSolution = noSolution,
                Duration = stopwatch.Elapsed,
                Success = true
            };
            
            // Update in-memory state
            HarvesterStatsService.UpdateRunState(s => {
                s.LastHarvestTime = DateTime.UtcNow;
                s.LastRunTicketsFound = tickets.Count;
                s.LastRunNewSolutions = newSolutions.Count;
                s.LastRunSkipped = skipped;
                s.LastRunNoSolution = noSolution;
                s.LastRunDuration = stopwatch.Elapsed;
                s.LastRunSuccess = true;
                s.LastRunError = null;
                
                // Update totals
                s.TotalTicketsProcessed += tickets.Count - skipped;
                s.TotalSolutionsHarvested += newSolutions.Count;
                s.TotalTicketsSkipped += skipped;
                s.TotalTicketsNoSolution += noSolution;
            });
            
            if (newSolutions.Count > 0)
            {
                // Load existing solutions and merge
                var existingSolutions = await _storageService.LoadSolutionsAsync();
                existingSolutions.AddRange(newSolutions);
                
                // Save merged solutions
                await _storageService.SaveSolutionsAsync(existingSolutions);
                await SaveProcessedTicketsAsync(cancellationToken);
                
                _logger.LogInformation(
                    "Harvesting complete: {New} new solutions added (total: {Total}). {Skipped} skipped, {NoSolution} without solution. Duration: {Duration:F1}s", 
                    newSolutions.Count, existingSolutions.Count, skipped, noSolution, stopwatch.Elapsed.TotalSeconds);
            }
            else
            {
                await SaveProcessedTicketsAsync(cancellationToken);
                _logger.LogInformation(
                    "No new solutions found. {Skipped} skipped (already processed), {NoSolution} without solution. Duration: {Duration:F1}s", 
                    skipped, noSolution, stopwatch.Elapsed.TotalSeconds);
            }
            
            // Record run in persistent history
            await _statsService.RecordRunAsync(runRecord);
        }

        /// <summary>
        /// Extract solution from ticket and generate embedding
        /// </summary>
        private async Task<JiraSolution?> ExtractAndProcessSolutionAsync(JiraTicket ticket, CancellationToken cancellationToken)
        {
            // Look for solution in comments
            var solutionText = ExtractSolutionText(ticket);
            if (string.IsNullOrWhiteSpace(solutionText))
            {
                return null;
            }

            try
            {
                // Create the JiraSolution object
                var solution = new JiraSolution
                {
                    TicketId = ticket.Key,
                    TicketTitle = ticket.Summary,
                    Problem = ticket.Summary,
                    RootCause = "", // Could be extracted with LLM in future
                    Solution = solutionText,
                    Steps = ExtractSteps(solutionText),
                    System = DetectSystem(ticket.Summary + " " + ticket.Description),
                    Category = ticket.Project,
                    Keywords = ExtractKeywords(ticket.Summary + " " + solutionText),
                    Priority = ticket.Priority ?? "Medium",
                    ResolvedDate = ticket.Resolved ?? DateTime.UtcNow,
                    HarvestedDate = DateTime.UtcNow,
                    ValidationCount = 0,
                    IsPromoted = false,
                    JiraUrl = $"https://antolin.atlassian.net/browse/{ticket.Key}"
                };
                
                // Generate embedding for semantic search
                var searchableText = solution.GetSearchableText();
                var embeddingResult = await _embeddingClient.GenerateEmbeddingAsync(searchableText, cancellationToken: cancellationToken);
                solution.Embedding = embeddingResult.Value.ToFloats();
                
                _logger.LogDebug("Generated embedding for {TicketId}: {TextLen} chars -> {EmbeddingLen} dims", 
                    ticket.Key, searchableText.Length, solution.Embedding.Length);
                
                return solution;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process solution from ticket {TicketId}", ticket.Key);
                return null;
            }
        }

        /// <summary>
        /// Extract solution text from ticket comments
        /// </summary>
        private string? ExtractSolutionText(JiraTicket ticket)
        {
            if (ticket.Comments == null || ticket.Comments.Count == 0)
                return null;
            
            var solutionKeywords = new[] 
            { 
                "solución", "solucion", "solved", "resuelto", "fixed", "resolved", 
                "pasos:", "steps:", "to fix:", "para resolver:", "solution:",
                "se resolvió", "se solucionó", "done", "completado", "completed"
            };
            
            // First pass: look for comments with explicit solution keywords
            foreach (var comment in ticket.Comments.OrderByDescending(c => c.Created))
            {
                if (string.IsNullOrWhiteSpace(comment.Body)) continue;
                
                var bodyLower = comment.Body.ToLower();
                if (solutionKeywords.Any(k => bodyLower.Contains(k)))
                {
                    return CleanCommentText(comment.Body);
                }
            }
            
            // Second pass: if no explicit solution, use the last non-trivial comment
            var lastComment = ticket.Comments
                .Where(c => !string.IsNullOrWhiteSpace(c.Body) && c.Body.Length > 50)
                .OrderByDescending(c => c.Created)
                .FirstOrDefault();
            
            if (lastComment != null)
            {
                return CleanCommentText(lastComment.Body);
            }
            
            return null;
        }

        /// <summary>
        /// Clean and normalize comment text
        /// </summary>
        private string CleanCommentText(string text)
        {
            // Remove ADF formatting artifacts if any
            var cleaned = text
                .Replace("\\n", "\n")
                .Replace("\\r", "")
                .Trim();
            
            // Limit length for embedding
            if (cleaned.Length > 2000)
            {
                cleaned = cleaned.Substring(0, 2000) + "...";
            }
            
            return cleaned;
        }

        /// <summary>
        /// Extract step-by-step instructions from solution text
        /// </summary>
        private List<string> ExtractSteps(string solutionText)
        {
            var steps = new List<string>();
            var lines = solutionText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // Look for numbered steps or bullet points
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^(\d+[\.\)\-]|[\-\*\•])"))
                {
                    var step = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^(\d+[\.\)\-]|[\-\*\•])\s*", "");
                    if (!string.IsNullOrWhiteSpace(step))
                    {
                        steps.Add(step);
                    }
                }
            }
            
            return steps.Take(7).ToList(); // Max 7 steps
        }

        /// <summary>
        /// Detect which system/application the ticket relates to
        /// </summary>
        private string DetectSystem(string text)
        {
            var textLower = text.ToLower();
            
            if (textLower.Contains("sap") || textLower.Contains("bpc") || textLower.Contains("hana"))
                return "SAP";
            if (textLower.Contains("vpn") || textLower.Contains("zscaler") || textLower.Contains("network") || textLower.Contains("red"))
                return "Network";
            if (textLower.Contains("sharepoint") || textLower.Contains("onedrive") || textLower.Contains("teams"))
                return "Microsoft 365";
            if (textLower.Contains("email") || textLower.Contains("correo") || textLower.Contains("outlook"))
                return "Email";
            if (textLower.Contains("printer") || textLower.Contains("impresora"))
                return "Hardware";
            if (textLower.Contains("password") || textLower.Contains("contraseña") || textLower.Contains("acceso"))
                return "Access";
            
            return "General";
        }

        /// <summary>
        /// Extract relevant keywords for search optimization
        /// </summary>
        private List<string> ExtractKeywords(string text)
        {
            var keywords = new List<string>();
            var textLower = text.ToLower();
            
            var commonKeywords = new[]
            {
                "sap", "bpc", "vpn", "zscaler", "sharepoint", "onedrive", "teams", "outlook",
                "password", "contraseña", "acceso", "permiso", "error", "problema",
                "usuario", "cuenta", "conexión", "red", "impresora", "instalación"
            };
            
            foreach (var keyword in commonKeywords)
            {
                if (textLower.Contains(keyword))
                {
                    keywords.Add(keyword);
                }
            }
            
            return keywords.Distinct().Take(10).ToList();
        }

        private async Task LoadProcessedTicketsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var blobClient = _blobContainer.GetBlobClient(ProcessedTicketsBlob);
                if (await blobClient.ExistsAsync(cancellationToken))
                {
                    var download = await blobClient.DownloadContentAsync(cancellationToken);
                    var json = download.Value.Content.ToString();
                    var list = JsonSerializer.Deserialize<HashSet<string>>(json);
                    if (list != null)
                        _processedTickets = list;
                }
                _logger.LogInformation("Loaded {Count} processed Jira tickets.", _processedTickets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load processed tickets list. Will start fresh.");
            }
        }

        private async Task SaveProcessedTicketsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var blobClient = _blobContainer.GetBlobClient(ProcessedTicketsBlob);
                var json = JsonSerializer.Serialize(_processedTickets, new JsonSerializerOptions { WriteIndented = true });
                using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                await blobClient.UploadAsync(ms, overwrite: true, cancellationToken);
                _logger.LogDebug("Saved {Count} processed Jira tickets.", _processedTickets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save processed tickets list.");
            }
        }
    }
}
