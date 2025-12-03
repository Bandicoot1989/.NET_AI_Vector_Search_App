using Azure.AI.OpenAI;
using RecipeSearchWeb.Components;
using RecipeSearchWeb.Extensions;
using RecipeSearchWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ============================================================================
// Operations One Centre - Clean Architecture Services Registration
// ============================================================================

// Configure Azure OpenAI
var endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"] ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not set");
var model = builder.Configuration["AZURE_OPENAI_GPT_NAME"] ?? throw new InvalidOperationException("AZURE_OPENAI_GPT_NAME not set");
var apiKey = builder.Configuration["AZURE_OPENAI_API_KEY"] ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not set");

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey));
var embeddingClient = azureClient.GetEmbeddingClient(model);

// Register Azure AI clients
builder.Services.AddSingleton(azureClient);
builder.Services.AddSingleton(embeddingClient);

// Add HttpContextAccessor first (needed by some services)
builder.Services.AddHttpContextAccessor();

// Add all Operations One Centre services with clean architecture pattern
builder.Services.AddStorageServices();     // Azure Blob Storage services
// builder.Services.AddSharePointServices();  // SharePoint KB integration (disabled)
builder.Services.AddConfluenceServices();  // Confluence KB integration
builder.Services.AddSearchServices();       // Vector search with embeddings
builder.Services.AddAgentServices();        // AI RAG Agent
builder.Services.AddAuthServices();         // Azure Easy Auth
builder.Services.AddDocumentServices();     // Word/PDF processing

var app = builder.Build();

// Initialize all services that require startup initialization
await app.Services.InitializeServicesWithLoggingAsync(app.Logger);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

// Diagnostic endpoint to check Confluence configuration
app.MapGet("/api/confluence-status", (ConfluenceKnowledgeService confluenceService, IConfiguration config) =>
{
    var baseUrl = config["Confluence:BaseUrl"];
    var email = config["Confluence:Email"];
    var spaceKeys = config["Confluence:SpaceKeys"];
    var tokenBase64 = config["Confluence:ApiTokenBase64"];
    var tokenLegacy = config["Confluence:ApiToken"];
    
    return Results.Ok(new
    {
        IsConfigured = confluenceService.IsConfigured,
        PageCount = confluenceService.GetCachedPageCount(),
        Config = new
        {
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? "NOT SET" : baseUrl,
            Email = string.IsNullOrEmpty(email) ? "NOT SET" : email,
            SpaceKeys = string.IsNullOrEmpty(spaceKeys) ? "NOT SET" : spaceKeys,
            ApiTokenBase64 = string.IsNullOrEmpty(tokenBase64) ? "NOT SET" : $"SET ({tokenBase64.Length} chars)",
            ApiTokenLegacy = string.IsNullOrEmpty(tokenLegacy) ? "NOT SET" : $"SET ({tokenLegacy.Length} chars)"
        }
    });
});

// Diagnostic endpoint to force sync and see errors
app.MapGet("/api/confluence-sync", async (ConfluenceKnowledgeService confluenceService) =>
{
    try
    {
        if (!confluenceService.IsConfigured)
        {
            return Results.Ok(new { Success = false, Error = "Confluence is not configured" });
        }
        
        // Force a full sync from Confluence (will overwrite cache)
        var syncedCount = await confluenceService.SyncPagesAsync();
        
        return Results.Ok(new 
        { 
            Success = true, 
            PageCount = confluenceService.GetCachedPageCount(),
            Message = $"Sync completed successfully - {syncedCount} pages synced with content"
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new 
        { 
            Success = false, 
            Error = ex.Message,
            StackTrace = ex.StackTrace,
            InnerError = ex.InnerException?.Message
        });
    }
});

// Sync a single space (to avoid timeout)
app.MapGet("/api/confluence-sync/{spaceKey}", async (ConfluenceKnowledgeService confluenceService, string spaceKey) =>
{
    try
    {
        if (!confluenceService.IsConfigured)
        {
            return Results.Ok(new { Success = false, Error = "Confluence is not configured" });
        }
        
        var (count, message) = await confluenceService.SyncSingleSpaceAsync(spaceKey.ToUpperInvariant());
        
        return Results.Ok(new 
        { 
            Success = count > 0, 
            SpaceKey = spaceKey.ToUpperInvariant(),
            PageCount = count,
            TotalCached = confluenceService.GetCachedPageCount(),
            Message = message
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new 
        { 
            Success = false, 
            Error = ex.Message
        });
    }
});

// List configured spaces
app.MapGet("/api/confluence-spaces", (ConfluenceKnowledgeService confluenceService) =>
{
    var spaces = confluenceService.GetConfiguredSpaceKeys();
    var cachedPages = confluenceService.GetAllCachedPages();
    
    var spaceStats = spaces.Select(s => new 
    {
        SpaceKey = s,
        CachedPageCount = cachedPages.Count(p => p.SpaceKey.Equals(s, StringComparison.OrdinalIgnoreCase))
    }).ToList();
    
    return Results.Ok(new 
    { 
        ConfiguredSpaces = spaces,
        SpaceStats = spaceStats,
        TotalCachedPages = cachedPages.Count
    });
});

// Diagnostic endpoint to test search and see scores
app.MapGet("/api/confluence-search", async (ConfluenceKnowledgeService confluenceService, string q) =>
{
    try
    {
        var results = await confluenceService.SearchWithScoresAsync(q, topResults: 10);
        return Results.Ok(new 
        { 
            Query = q,
            TotalPages = confluenceService.GetCachedPageCount(),
            Results = results.Select(r => new 
            { 
                Title = r.Page.Title,
                SpaceKey = r.Page.SpaceKey,
                Score = r.Similarity,
                ContentPreview = r.Page.Content?.Substring(0, Math.Min(200, r.Page.Content?.Length ?? 0)) + "..."
            })
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { Error = ex.Message });
    }
});

// Diagnostic endpoint to see full content of a page by title
app.MapGet("/api/confluence-page", async (ConfluenceKnowledgeService confluenceService, string title) =>
{
    try
    {
        var results = await confluenceService.SearchWithScoresAsync(title, topResults: 1);
        var page = results.FirstOrDefault().Page;
        if (page == null)
        {
            return Results.Ok(new { Error = "Page not found" });
        }
        return Results.Ok(new 
        { 
            Title = page.Title,
            SpaceKey = page.SpaceKey,
            ContentLength = page.Content?.Length ?? 0,
            HasEmbedding = page.Embedding.Length > 0,
            Content = page.Content // Full content
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { Error = ex.Message });
    }
});

// List all cached pages (for debugging)
app.MapGet("/api/confluence-list", (ConfluenceKnowledgeService confluenceService, string? search) =>
{
    var pages = confluenceService.GetAllCachedPages();
    
    if (!string.IsNullOrEmpty(search))
    {
        pages = pages.Where(p => p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    
    return Results.Ok(new
    {
        TotalCount = confluenceService.GetCachedPageCount(),
        FilteredCount = pages.Count,
        Pages = pages.Take(50).Select(p => new 
        { 
            p.Id, 
            p.Title, 
            ContentLength = p.Content?.Length ?? 0,
            HasEmbedding = p.Embedding.Length > 0
        })
    });
});

// Debug endpoint to get raw API response for a page
app.MapGet("/api/confluence-debug", async (ConfluenceKnowledgeService confluenceService, string title) =>
{
    try
    {
        var pageId = confluenceService.GetPageIdByTitle(title);
        if (string.IsNullOrEmpty(pageId))
        {
            return Results.Ok(new { Error = $"Page not found with title containing: {title}" });
        }
        
        var rawResponse = await confluenceService.GetRawPageContentAsync(pageId);
        return Results.Ok(new 
        { 
            PageId = pageId,
            RawResponse = rawResponse
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { Error = ex.Message });
    }
});

// Debug endpoint to see what context is found for a query
app.MapGet("/api/context-debug", async (ContextSearchService contextService, string q) =>
{
    try
    {
        var results = await contextService.SearchAsync(q, topResults: 10);
        return Results.Ok(new 
        { 
            Query = q,
            ResultCount = results.Count,
            Results = results.Select(r => new 
            { 
                r.Name, 
                r.Category, 
                r.Description,
                r.Keywords,
                Link = r.Link ?? "NO LINK",
                AdditionalDataCount = r.AdditionalData?.Count ?? 0,
                AdditionalData = r.AdditionalData
            })
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { Error = ex.Message });
    }
}).AllowAnonymous();

// Debug endpoint to see ALL context documents  
app.MapGet("/api/context-all", async (ContextSearchService contextService) =>
{
    try
    {
        var files = await contextService.GetFilesAsync();
        var allDocs = new List<object>();
        
        foreach (var file in files)
        {
            var docs = await contextService.GetDocumentsByFileAsync(file.FileName);
            foreach (var r in docs)
            {
                allDocs.Add(new 
                { 
                    r.Name, 
                    r.Category, 
                    Link = string.IsNullOrEmpty(r.Link) ? "EMPTY" : r.Link,
                    r.Description
                });
            }
        }
        
        return Results.Ok(new 
        { 
            TotalDocuments = allDocs.Count,
            Documents = allDocs
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { Error = ex.Message });
    }
}).AllowAnonymous();

// ==============================================================
// AUTHENTICATION DEBUG ENDPOINT
// ==============================================================
app.MapGet("/api/auth-status", (HttpContext httpContext, AzureAuthService authService, IWebHostEnvironment env, IConfiguration config) =>
{
    var user = authService.GetCurrentUser();
    var easyAuthName = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
    var easyAuthId = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
    var claimsPrincipal = httpContext.User;
    
    var adminEmails = config.GetSection("Authorization:AdminEmails").Get<string[]>() ?? Array.Empty<string>();
    
    return Results.Ok(new
    {
        Environment = env.EnvironmentName,
        IsDevelopment = env.IsDevelopment(),
        EasyAuth = new
        {
            PrincipalName = easyAuthName ?? "NOT SET",
            PrincipalId = easyAuthId ?? "NOT SET"
        },
        ClaimsPrincipal = new
        {
            IsAuthenticated = claimsPrincipal?.Identity?.IsAuthenticated ?? false,
            Name = claimsPrincipal?.Identity?.Name ?? "null",
            AuthenticationType = claimsPrincipal?.Identity?.AuthenticationType ?? "null",
            Claims = claimsPrincipal?.Claims?.Select(c => new { c.Type, c.Value }).ToList()
        },
        CurrentUser = user == null ? null : new
        {
            user.Username,
            user.FullName,
            user.Role,
            user.IsAdmin
        },
        ConfiguredAdmins = adminEmails,
        DevelopmentConfig = new
        {
            SimulatedEmail = config["Development:SimulatedUserEmail"] ?? "NOT SET",
            SimulatedName = config["Development:SimulatedUserName"] ?? "NOT SET"
        }
    });
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Extension method for initialization with logging
namespace RecipeSearchWeb.Extensions
{
    public static class ServiceInitializationExtensions
    {
        public static async Task InitializeServicesWithLoggingAsync(this IServiceProvider serviceProvider, ILogger logger)
        {
            // Initialize scripts
            var scriptService = serviceProvider.GetRequiredService<ScriptSearchService>();
            await scriptService.InitializeAsync();
            logger.LogInformation("ScriptSearchService initialized");

            // Initialize knowledge base
            var knowledgeService = serviceProvider.GetRequiredService<KnowledgeSearchService>();
            await knowledgeService.InitializeAsync();
            logger.LogInformation("KnowledgeSearchService initialized");

            // Initialize image service (non-blocking)
            try
            {
                var imageService = serviceProvider.GetRequiredService<KnowledgeImageService>();
                await imageService.InitializeAsync();
                logger.LogInformation("KnowledgeImageService initialized");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize KnowledgeImageService - images may not work correctly");
            }

            // Initialize context service (non-blocking)
            try
            {
                var contextService = serviceProvider.GetRequiredService<ContextSearchService>();
                await contextService.InitializeAsync();
                logger.LogInformation("ContextSearchService initialized");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize ContextSearchService - agent context may not work correctly");
            }

            // Initialize Confluence KB service (non-blocking)
            try
            {
                var confluenceService = serviceProvider.GetRequiredService<ConfluenceKnowledgeService>();
                if (confluenceService.IsConfigured)
                {
                    await confluenceService.InitializeAsync();
                    logger.LogInformation("ConfluenceKnowledgeService initialized");
                }
                else
                {
                    logger.LogInformation("ConfluenceKnowledgeService not configured - skipping initialization");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize ConfluenceKnowledgeService - Confluence KB may not work correctly");
            }
        }
    }
}
