using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Client for Jira Cloud API (atlassian.net)
/// Uses REST API v3 with Basic Auth (email + API token)
/// </summary>
public class JiraClient : IJiraClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JiraClient> _logger;
    private readonly string? _baseUrl;
    private readonly bool _isConfigured;

    public JiraClient(IConfiguration configuration, ILogger<JiraClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        
        // Configuration from appsettings or environment variables
        _baseUrl = configuration["Jira:BaseUrl"] ?? configuration["JIRA_BASE_URL"];
        var email = configuration["Jira:Email"] ?? configuration["JIRA_EMAIL"];
        var apiToken = configuration["Jira:ApiToken"] ?? configuration["JIRA_API_TOKEN"];
        
        if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(apiToken))
        {
            _logger.LogWarning("Jira configuration incomplete. Required: BaseUrl, Email, ApiToken");
            _isConfigured = false;
            return;
        }
        
        // Normalize base URL
        _baseUrl = _baseUrl.TrimEnd('/');
        
        // Configure HTTP client with Basic Auth
        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.BaseAddress = new Uri(_baseUrl);
        
        _isConfigured = true;
        _logger.LogInformation("JiraClient configured for {BaseUrl}", _baseUrl);
    }

    public bool IsConfigured => _isConfigured;

    /// <summary>
    /// Test the connection to Jira
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        if (!_isConfigured) return false;
        
        try
        {
            var response = await _httpClient.GetAsync("/rest/api/3/myself");
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var user = JsonSerializer.Deserialize<JiraUser>(content, _jsonOptions);
                _logger.LogInformation("Jira connection successful. Authenticated as: {DisplayName} ({Email})", 
                    user?.DisplayName, user?.EmailAddress);
                return true;
            }
            
            _logger.LogError("Jira connection failed: {Status} - {Reason}", 
                response.StatusCode, response.ReasonPhrase);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Jira");
            return false;
        }
    }

    /// <summary>
    /// Raw search - returns the raw JSON response from Jira for diagnostics
    /// </summary>
    public async Task<object> RawSearchAsync(string jql, int maxResults = 5)
    {
        if (!_isConfigured)
        {
            return new { error = "Not configured", baseUrl = _baseUrl ?? "null" };
        }

        try
        {
            var requestBody = new Dictionary<string, object>
            {
                ["jql"] = jql,
                ["maxResults"] = maxResults,
                ["fields"] = new[] { "key", "summary", "status" }
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            var jsonContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/rest/api/3/search/jql", jsonContent);
            var content = await response.Content.ReadAsStringAsync();
            
            // Try to parse as JSON, otherwise return raw
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(content);
                return new 
                { 
                    statusCode = (int)response.StatusCode,
                    requestBody = jsonBody,
                    requestUrl = $"{_baseUrl}/rest/api/3/search/jql",
                    response = parsed
                };
            }
            catch
            {
                return new 
                { 
                    statusCode = (int)response.StatusCode,
                    requestBody = jsonBody,
                    rawResponse = content.Length > 2000 ? content.Substring(0, 2000) : content
                };
            }
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, stackTrace = ex.StackTrace };
        }
    }

    /// <summary>
    /// Test deserialization directly - for debugging
    /// </summary>
    public async Task<object> TestDeserializationAsync()
    {
        if (!_isConfigured)
        {
            return new { error = "Not configured" };
        }

        try
        {
            var requestBody = new Dictionary<string, object>
            {
                ["jql"] = "resolved >= -7d ORDER BY resolved DESC",
                ["maxResults"] = 3,
                ["fields"] = new[] { "key", "summary", "status", "resolution" }
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            var jsonContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/rest/api/3/search/jql", jsonContent);
            var content = await response.Content.ReadAsStringAsync();
            
            // Now try to deserialize with our model
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var searchResult = JsonSerializer.Deserialize<JiraSearchResponsePublic>(content, options);
            
            return new
            {
                success = true,
                rawContentLength = content.Length,
                rawContentPreview = content.Length > 500 ? content.Substring(0, 500) : content,
                deserializedIssuesCount = searchResult?.Issues?.Count ?? -1,
                deserializedIsLast = searchResult?.IsLast,
                issues = searchResult?.Issues?.Select(i => new { key = i.Key, summary = i.Fields?.Summary, status = i.Fields?.Status?.Name })
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, stackTrace = ex.StackTrace };
        }
    }

    // Public version of the response model for testing
    public class JiraSearchResponsePublic
    {
        public bool IsLast { get; set; }
        public string? NextPageToken { get; set; }
        public List<JiraIssueResponsePublic>? Issues { get; set; }
    }
    
    public class JiraIssueResponsePublic
    {
        public string? Key { get; set; }
        public JiraFieldsResponsePublic? Fields { get; set; }
    }
    
    public class JiraFieldsResponsePublic
    {
        public string? Summary { get; set; }
        public JiraStatusResponsePublic? Status { get; set; }
        public JiraResolutionResponsePublic? Resolution { get; set; }
    }
    
    public class JiraStatusResponsePublic
    {
        public string? Name { get; set; }
    }
    
    public class JiraResolutionResponsePublic
    {
        public string? Name { get; set; }
    }

    /// <summary>
    /// Get resolved/closed tickets from the last N days
    /// </summary>
    public async Task<List<JiraTicket>> GetResolvedTicketsAsync(int days = 30, List<string>? projectKeys = null, int maxResults = 100)
    {
        if (!_isConfigured) 
        {
            _logger.LogWarning("JiraClient not configured - returning empty list");
            return new List<JiraTicket>();
        }
        
        try
        {
            // Build JQL query - simplified to just resolved date
            var jql = $"resolved >= -{days}d ORDER BY resolved DESC";
            
            _logger.LogInformation("Searching Jira with JQL: {Jql}", jql);
            
            return await SearchTicketsAsync(jql, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resolved tickets");
            return new List<JiraTicket>();
        }
    }

    /// <summary>
    /// Get a single ticket by its key
    /// </summary>
    public async Task<JiraTicket?> GetTicketAsync(string ticketKey)
    {
        if (!_isConfigured) return null;
        
        try
        {
            var url = $"/rest/api/3/issue/{ticketKey}?expand=changelog";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get ticket {Key}: {Status}", ticketKey, response.StatusCode);
                return null;
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var jiraIssue = JsonSerializer.Deserialize<JiraIssueResponse>(content, _jsonOptions);
            
            if (jiraIssue == null) return null;
            
            var ticket = MapToTicket(jiraIssue);
            
            // Get comments separately for full detail
            ticket.Comments = await GetTicketCommentsAsync(ticketKey);
            
            return ticket;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ticket {Key}", ticketKey);
            return null;
        }
    }

    /// <summary>
    /// Get comments for a specific ticket
    /// </summary>
    public async Task<List<JiraComment>> GetTicketCommentsAsync(string ticketKey)
    {
        if (!_isConfigured) return new List<JiraComment>();
        
        try
        {
            var url = $"/rest/api/3/issue/{ticketKey}/comment";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                return new List<JiraComment>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var commentsResponse = JsonSerializer.Deserialize<JiraCommentsResponse>(content, _jsonOptions);
            
            return commentsResponse?.Comments?.Select(c => new JiraComment
            {
                Author = c.Author?.DisplayName ?? "Unknown",
                Body = ExtractTextFromAdf(c.Body),
                Created = c.Created
            }).ToList() ?? new List<JiraComment>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting comments for ticket {Key}", ticketKey);
            return new List<JiraComment>();
        }
    }

    /// <summary>
    /// Search tickets using JQL (Jira Query Language)
    /// Uses new POST /rest/api/3/search/jql endpoint (required since 2024)
    /// </summary>
    public async Task<List<JiraTicket>> SearchTicketsAsync(string jql, int maxResults = 50)
    {
        if (!_isConfigured) 
        {
            _logger.LogWarning("JiraClient not configured");
            return new List<JiraTicket>();
        }
        
        var tickets = new List<JiraTicket>();
        string? nextPageToken = null;
        
        try
        {
            while (tickets.Count < maxResults)
            {
                var batchSize = Math.Min(50, maxResults - tickets.Count);
                
                // Build request body for new POST API
                var requestBody = new Dictionary<string, object>
                {
                    ["jql"] = jql,
                    ["maxResults"] = batchSize,
                    ["fields"] = new[] { "key", "summary", "description", "status", "resolution", "priority", "project", "assignee", "reporter", "created", "resolutiondate", "comment" }
                };
                
                // Add pagination token if we have one
                if (!string.IsNullOrEmpty(nextPageToken))
                {
                    requestBody["nextPageToken"] = nextPageToken;
                }
                
                var jsonBody = JsonSerializer.Serialize(requestBody);
                _logger.LogInformation("Jira request body: {Body}", jsonBody);
                
                var jsonContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/rest/api/3/search/jql", jsonContent);
                
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Jira API response length: {Length}", content.Length);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Jira search failed: {Status} - Error: {Error}", response.StatusCode, content);
                    break;
                }
                
                // Use the same deserialization options that work in TestDeserializationAsync
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var searchResult = JsonSerializer.Deserialize<JiraSearchResponsePublic>(content, options);
                _logger.LogInformation("Deserialized: Issues={IssueCount}, IsLast={IsLast}", 
                    searchResult?.Issues?.Count ?? -1, searchResult?.IsLast);
                
                if (searchResult?.Issues == null || !searchResult.Issues.Any())
                {
                    _logger.LogInformation("No issues found in response");
                    break;
                }
                
                foreach (var issue in searchResult.Issues)
                {
                    var ticket = MapToTicketFromPublic(issue);
                    tickets.Add(ticket);
                    _logger.LogInformation("Added ticket: {Key} - {Summary}", ticket.Key, ticket.Summary);
                }
                
                // Check if there are more pages
                if (searchResult.IsLast || string.IsNullOrEmpty(searchResult.NextPageToken))
                    break;
                    
                nextPageToken = searchResult.NextPageToken;
            }
            
            _logger.LogInformation("Jira search returned {Count} tickets", tickets.Count);
            return tickets;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jira with JQL: {Jql}", jql);
            return tickets;
        }
    }

    /// <summary>
    /// Check if a ticket has already been harvested
    /// </summary>
    public async Task<bool> IsTicketAlreadyHarvestedAsync(string ticketKey)
    {
        // This is implemented in JiraSolutionStorageService
        await Task.CompletedTask;
        return false;
    }

    /// <summary>
    /// Map Jira API response to our internal model
    /// </summary>
    private JiraTicket MapToTicket(JiraIssueResponse issue)
    {
        return new JiraTicket
        {
            Key = issue.Key ?? "",
            Summary = issue.Fields?.Summary ?? "",
            Description = ExtractTextFromAdf(issue.Fields?.Description),
            Status = issue.Fields?.Status?.Name ?? "",
            Resolution = issue.Fields?.Resolution?.Name ?? "",
            Priority = issue.Fields?.Priority?.Name ?? "",
            Project = issue.Fields?.Project?.Key ?? "",
            Assignee = issue.Fields?.Assignee?.DisplayName ?? "",
            Reporter = issue.Fields?.Reporter?.DisplayName ?? "",
            Created = issue.Fields?.Created ?? DateTime.MinValue,
            Resolved = issue.Fields?.ResolutionDate,
            Comments = issue.Fields?.Comment?.Comments?.Select(c => new JiraComment
            {
                Author = c.Author?.DisplayName ?? "Unknown",
                Body = ExtractTextFromAdf(c.Body),
                Created = c.Created
            }).ToList() ?? new List<JiraComment>()
        };
    }

    /// <summary>
    /// Map JiraIssueResponsePublic (from public API) to JiraTicket
    /// </summary>
    private JiraTicket MapToTicketFromPublic(JiraIssueResponsePublic issue)
    {
        return new JiraTicket
        {
            Key = issue.Key ?? "",
            Summary = issue.Fields?.Summary ?? "",
            Description = "", // Description not included in public response fields
            Status = issue.Fields?.Status?.Name ?? "",
            Resolution = issue.Fields?.Resolution?.Name ?? "",
            Priority = "", // Not included in public response
            Project = "", // Not included in public response  
            Assignee = "", // Not included in public response
            Reporter = "", // Not included in public response
            Created = DateTime.MinValue, // Not included in public response
            Resolved = null, // Not included in public response
            Comments = new List<JiraComment>() // Not included in public response
        };
    }

    /// <summary>
    /// Extract comments from JsonElement fields
    /// </summary>
    private List<JiraComment> ExtractCommentsFromPublic(JsonElement fields)
    {
        var comments = new List<JiraComment>();
        
        if (fields.TryGetProperty("comment", out var commentContainer) && 
            commentContainer.TryGetProperty("comments", out var commentsArray) &&
            commentsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var comment in commentsArray.EnumerateArray())
            {
                comments.Add(new JiraComment
                {
                    Author = comment.TryGetProperty("author", out var author) && 
                             author.TryGetProperty("displayName", out var authorName)
                        ? authorName.GetString() ?? "Unknown" : "Unknown",
                    Body = comment.TryGetProperty("body", out var body) 
                        ? ExtractTextFromAdf(body) : "",
                    Created = comment.TryGetProperty("created", out var created) && 
                              DateTime.TryParse(created.GetString(), out var createdDate)
                        ? createdDate : DateTime.MinValue
                });
            }
        }
        
        return comments;
    }

    /// <summary>
    /// Extract plain text from Atlassian Document Format (ADF)
    /// </summary>
    private string ExtractTextFromAdf(object? adfContent)
    {
        if (adfContent == null) return "";
        
        try
        {
            // ADF is a complex JSON structure, we need to extract text nodes
            var json = adfContent is JsonElement element 
                ? element.GetRawText() 
                : JsonSerializer.Serialize(adfContent);
            
            var doc = JsonSerializer.Deserialize<AdfDocument>(json, _jsonOptions);
            if (doc?.Content == null) return json; // Fallback to raw if not ADF
            
            var sb = new StringBuilder();
            ExtractTextRecursive(doc.Content, sb);
            return sb.ToString().Trim();
        }
        catch
        {
            // If it's plain text, return as-is
            return adfContent?.ToString() ?? "";
        }
    }

    private void ExtractTextRecursive(List<AdfNode>? nodes, StringBuilder sb)
    {
        if (nodes == null) return;
        
        foreach (var node in nodes)
        {
            if (node.Type == "text" && !string.IsNullOrEmpty(node.Text))
            {
                sb.Append(node.Text);
            }
            else if (node.Type == "hardBreak" || node.Type == "paragraph")
            {
                sb.AppendLine();
            }
            
            ExtractTextRecursive(node.Content, sb);
        }
    }

    // JSON serialization options
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region Jira API Response Models
    
    private class JiraUser
    {
        public string? DisplayName { get; set; }
        public string? EmailAddress { get; set; }
        public string? AccountId { get; set; }
    }
    
    private class JiraSearchResponse
    {
        public int StartAt { get; set; }
        public int MaxResults { get; set; }
        public int Total { get; set; }
        public bool IsLast { get; set; }
        public string? NextPageToken { get; set; }
        public List<JiraIssueResponse>? Issues { get; set; }
    }
    
    private class JiraIssueResponse
    {
        public string? Key { get; set; }
        public JiraFieldsResponse? Fields { get; set; }
    }
    
    private class JiraFieldsResponse
    {
        public string? Summary { get; set; }
        public object? Description { get; set; } // Can be ADF or string
        public JiraStatusResponse? Status { get; set; }
        public JiraResolutionResponse? Resolution { get; set; }
        public JiraPriorityResponse? Priority { get; set; }
        public JiraProjectResponse? Project { get; set; }
        public JiraUserResponse? Assignee { get; set; }
        public JiraUserResponse? Reporter { get; set; }
        public DateTime Created { get; set; }
        [JsonPropertyName("resolutiondate")]
        public DateTime? ResolutionDate { get; set; }
        public JiraCommentsContainer? Comment { get; set; }
    }
    
    private class JiraStatusResponse
    {
        public string? Name { get; set; }
    }
    
    private class JiraResolutionResponse
    {
        public string? Name { get; set; }
    }
    
    private class JiraPriorityResponse
    {
        public string? Name { get; set; }
    }
    
    private class JiraProjectResponse
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
    }
    
    private class JiraUserResponse
    {
        public string? DisplayName { get; set; }
        public string? EmailAddress { get; set; }
    }
    
    private class JiraCommentsContainer
    {
        public List<JiraCommentResponse>? Comments { get; set; }
    }
    
    private class JiraCommentsResponse
    {
        public List<JiraCommentResponse>? Comments { get; set; }
    }
    
    private class JiraCommentResponse
    {
        public JiraUserResponse? Author { get; set; }
        public object? Body { get; set; } // Can be ADF or string
        public DateTime Created { get; set; }
    }
    
    // Atlassian Document Format (ADF) models
    private class AdfDocument
    {
        public string? Type { get; set; }
        public List<AdfNode>? Content { get; set; }
    }
    
    private class AdfNode
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public List<AdfNode>? Content { get; set; }
    }
    
    #endregion
}
