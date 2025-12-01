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

// Add all Operations One Centre services with clean architecture pattern
builder.Services.AddStorageServices();     // Azure Blob Storage services
builder.Services.AddSharePointServices();  // SharePoint KB integration
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

            // Initialize SharePoint KB service (non-blocking)
            try
            {
                var sharePointService = serviceProvider.GetRequiredService<SharePointKnowledgeService>();
                await sharePointService.InitializeAsync();
                logger.LogInformation("SharePointKnowledgeService initialized");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize SharePointKnowledgeService - SharePoint KB may not work correctly");
            }

            // Initialize Confluence KB service (non-blocking)
            try
            {
                var confluenceService = serviceProvider.GetRequiredService<ConfluenceKnowledgeService>();
                await confluenceService.InitializeAsync();
                logger.LogInformation("ConfluenceKnowledgeService initialized");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize ConfluenceKnowledgeService - Confluence KB may not work correctly");
            }
        }
    }
}
