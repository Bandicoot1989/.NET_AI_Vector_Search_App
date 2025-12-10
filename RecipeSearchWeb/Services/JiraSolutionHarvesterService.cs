using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Azure.Storage.Blobs;
using System.Text.Json;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services
{
    public class JiraSolutionHarvesterService : BackgroundService
    {
        private readonly IJiraClient _jiraClient;
        private readonly BlobContainerClient _blobContainer;
        private readonly ILogger<JiraSolutionHarvesterService> _logger;
        private readonly TimeSpan _interval;
        private const string ProcessedTicketsBlob = "harvested-tickets.json";
        private HashSet<string> _processedTickets = new();
        private bool _containerInitialized = false;

        public JiraSolutionHarvesterService(
            IJiraClient jiraClient, 
            [FromKeyedServices("harvested-solutions")] BlobContainerClient blobContainer, 
            ILogger<JiraSolutionHarvesterService> logger)
        {
            _jiraClient = jiraClient;
            _blobContainer = blobContainer;
            _logger = logger;
            _interval = TimeSpan.FromHours(6); // Configurable
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JiraSolutionHarvesterService started");
            
            // Initialize container on first run (async, won't block startup)
            if (!_containerInitialized)
            {
                try
                {
                    await _blobContainer.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    _containerInitialized = true;
                    _logger.LogInformation("Blob container initialized");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize blob container - harvesting disabled");
                    return;
                }
            }
            
            await LoadProcessedTicketsAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await HarvestSolutionsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in JiraSolutionHarvesterService");
                }
                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task HarvestSolutionsAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Harvesting Jira solutions...");
            var tickets = await _jiraClient.GetResolvedTicketsAsync(7, null, 50); // Últimos 7 días, configurable
            var harvested = new List<HarvestedSolution>();
            int skipped = 0;
            foreach (var ticket in tickets)
            {
                if (_processedTickets.Contains(ticket.Key))
                {
                    skipped++;
                    continue;
                }
                var solution = ExtractSolutionFromTicket(ticket);
                if (solution != null)
                {
                    harvested.Add(solution);
                    _processedTickets.Add(ticket.Key);
                }
            }
            if (harvested.Count > 0)
            {
                await SaveSolutionsAsync(harvested, cancellationToken);
                await SaveProcessedTicketsAsync(cancellationToken);
                _logger.LogInformation("{Count} solutions harvested and saved. {Skipped} tickets skipped (already processed).", harvested.Count, skipped);
            }
            else
            {
                _logger.LogInformation("No new solutions found. {Skipped} tickets skipped (already processed).", skipped);
            }
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
                _logger.LogInformation("Saved {Count} processed Jira tickets.", _processedTickets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save processed tickets list.");
            }
        }

        private HarvestedSolution? ExtractSolutionFromTicket(JiraTicket ticket)
        {
            if (ticket.Comments == null || ticket.Comments.Count == 0)
                return null;
            var keywords = new[] { "solución", "solucion", "resuelto", "fixed", "resolved", "pasos:", "steps:", "to fix:", "para resolver:" };
            foreach (var comment in ticket.Comments)
            {
                if (keywords.Any(k => comment.Body != null && comment.Body.ToLower().Contains(k)))
                {
                    return new HarvestedSolution
                    {
                        Id = Guid.NewGuid().ToString(),
                        TicketKey = ticket.Key,
                        Problem = ticket.Summary,
                        Context = ticket.Description,
                        Solution = comment.Body ?? string.Empty,
                        Category = ticket.Project,
                        Tags = Array.Empty<string>(),
                        ExtractedAt = DateTime.UtcNow,
                        SourceUrl = $"https://antolin.atlassian.net/browse/{ticket.Key}"
                    };
                }
            }
            return null;
        }

        private async Task SaveSolutionsAsync(List<HarvestedSolution> solutions, CancellationToken cancellationToken)
        {
            var blobName = $"harvested-solutions/{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            var blobClient = _blobContainer.GetBlobClient(blobName);
            var json = JsonSerializer.Serialize(solutions, new JsonSerializerOptions { WriteIndented = true });
            using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(ms, overwrite: true, cancellationToken);
        }
    }
}
