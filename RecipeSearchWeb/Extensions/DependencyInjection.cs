using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Services;

namespace RecipeSearchWeb.Extensions;

/// <summary>
/// Clean Architecture Dependency Injection Extensions
/// Centralizes all service registrations for better maintainability
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Add Jira Solution Harvester background service and blob client
    /// </summary>
    public static IServiceCollection AddJiraSolutionServices(this IServiceCollection services, IConfiguration configuration)
    {
        // First register all Jira client and storage services
        services.AddSingleton<JiraClient>();
        services.AddSingleton<Interfaces.IJiraClient>(sp => sp.GetRequiredService<JiraClient>());
        services.AddSingleton<JiraSolutionStorageService>();
        services.AddSingleton<JiraSolutionSearchService>();
        services.AddSingleton<Interfaces.IJiraSolutionService>(sp => sp.GetRequiredService<JiraSolutionSearchService>());
        services.AddSingleton<JiraHarvesterService>();
        services.AddSingleton<Interfaces.IJiraSolutionHarvester>(sp => sp.GetRequiredService<JiraHarvesterService>());
        
        // BlobContainerClient for harvested solutions - use keyed service to avoid conflict with other containers
        services.AddKeyedSingleton<Azure.Storage.Blobs.BlobContainerClient>("harvested-solutions", (sp, key) =>
        {
            var connectionString = configuration["AzureStorage:ConnectionString"] ?? throw new InvalidOperationException("AzureStorage:ConnectionString not set");
            var containerName = configuration["AzureStorage:HarvestedSolutionsContainer"] ?? "harvested-solutions";
            return new Azure.Storage.Blobs.BlobContainerClient(connectionString, containerName);
        });
        
        // Register background service for automatic harvesting
        services.AddHostedService<JiraSolutionHarvesterService>();
        return services;
    }
    /// <summary>
    /// Add all infrastructure services to the DI container
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Azure OpenAI Client
        services.AddSingleton(sp =>
        {
            var endpoint = configuration["AZURE_OPENAI_ENDPOINT"] ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not configured");
            return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        });

        // Embedding Client
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<AzureOpenAIClient>();
            var model = configuration["AZURE_OPENAI_GPT_NAME"] ?? "text-embedding-ada-002";
            return client.GetEmbeddingClient(model);
        });

        return services;
    }

    /// <summary>
    /// Add storage services (Azure Blob Storage)
    /// NOTE: Services are registered as both concrete types AND interfaces for backwards compatibility
    /// </summary>
    public static IServiceCollection AddStorageServices(this IServiceCollection services)
    {
        // Register concrete types (needed for internal dependencies)
        services.AddSingleton<ScriptStorageService>();
        services.AddSingleton<KnowledgeStorageService>();
        services.AddSingleton<KnowledgeImageService>();
        services.AddSingleton<ContextStorageService>();

        // Register interfaces pointing to same instances
        services.AddSingleton<IScriptStorageService>(sp => sp.GetRequiredService<ScriptStorageService>());
        services.AddSingleton<IKnowledgeStorageService>(sp => sp.GetRequiredService<KnowledgeStorageService>());
        services.AddSingleton<IImageStorageService>(sp => sp.GetRequiredService<KnowledgeImageService>());

        return services;
    }

    /// <summary>
    /// Add SharePoint integration services
    /// </summary>
    public static IServiceCollection AddSharePointServices(this IServiceCollection services)
    {
        services.AddSingleton<SharePointKnowledgeService>();
        services.AddSingleton<ISharePointService>(sp => sp.GetRequiredService<SharePointKnowledgeService>());

        return services;
    }

    /// <summary>
    /// Add Confluence integration services
    /// </summary>
    public static IServiceCollection AddConfluenceServices(this IServiceCollection services)
    {
        services.AddSingleton<ConfluenceKnowledgeService>();
        services.AddSingleton<IConfluenceService>(sp => sp.GetRequiredService<ConfluenceKnowledgeService>());

        return services;
    }

    /// <summary>
    /// Add search services (vector search with embeddings)
    /// NOTE: Services are registered as both concrete types AND interfaces for backwards compatibility
    /// </summary>
    public static IServiceCollection AddSearchServices(this IServiceCollection services)
    {
        // Register concrete types (needed for internal dependencies)
        services.AddSingleton<ScriptSearchService>();
        services.AddSingleton<KnowledgeSearchService>();
        services.AddSingleton<ContextSearchService>();

        // Register interfaces pointing to same instances
        services.AddSingleton<IScriptService>(sp => sp.GetRequiredService<ScriptSearchService>());
        services.AddSingleton<IKnowledgeService>(sp => sp.GetRequiredService<KnowledgeSearchService>());
        services.AddSingleton<IContextService>(sp => sp.GetRequiredService<ContextSearchService>());

        return services;
    }

    /// <summary>
    /// Add caching services (Tier 2 Optimization with Semantic Cache)
    /// </summary>
    public static IServiceCollection AddCachingServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        
        // Register QueryCacheService with EmbeddingClient for semantic caching
        services.AddSingleton<QueryCacheService>(sp =>
        {
            var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var logger = sp.GetRequiredService<ILogger<QueryCacheService>>();
            var embeddingClient = sp.GetService<EmbeddingClient>(); // Optional - may be null
            return new QueryCacheService(cache, logger, embeddingClient);
        });
        
        return services;
    }

    /// <summary>
    /// Add SAP specialist services (Tier 3 Optimization)
    /// </summary>
    public static IServiceCollection AddSapServices(this IServiceCollection services)
    {
        services.AddSingleton<SapKnowledgeService>();
        services.AddSingleton<SapLookupService>();
        services.AddSingleton<SapAgentService>();
        
        return services;
    }

    /// <summary>
    /// Add Network specialist services (Tier 3 Multi-Agent)
    /// </summary>
    public static IServiceCollection AddNetworkServices(this IServiceCollection services)
    {
        services.AddSingleton<NetworkAgentService>();
        
        return services;
    }

    /// <summary>
    /// Add Feedback services for bot training and improvement
    /// </summary>
    public static IServiceCollection AddFeedbackServices(this IServiceCollection services)
    {
        services.AddSingleton<FeedbackService>();
        
        return services;
    }

    /// <summary>
    /// Add AI agent services (including Tier 3 Agent Router)
    /// </summary>
    public static IServiceCollection AddAgentServices(this IServiceCollection services)
    {
        // Register the base Knowledge Agent
        services.AddSingleton<KnowledgeAgentService>();
        
        // Register the Agent Router as the primary IKnowledgeAgentService
        // This routes queries to SAP Agent, Network Agent, or General Agent based on content
        services.AddSingleton<AgentRouterService>();
        services.AddSingleton<IKnowledgeAgentService>(sp => sp.GetRequiredService<AgentRouterService>());

        return services;
    }

    /// <summary>
    /// Add authentication services
    /// </summary>
    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AzureAuthService>();
        services.AddScoped<IAuthService>(sp => sp.GetRequiredService<AzureAuthService>());
        services.AddScoped<UserStateService>();

        return services;
    }

    /// <summary>
    /// Add document processing services
    /// </summary>
    public static IServiceCollection AddDocumentServices(this IServiceCollection services)
    {
        services.AddSingleton<WordDocumentService>();
        services.AddSingleton<PdfDocumentService>();
        services.AddSingleton<MarkdownRenderService>();

        return services;
    }

    /// <summary>
    /// Add all Operations One Centre services (combines all of the above)
    /// </summary>
    public static IServiceCollection AddOperationsOneCentre(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInfrastructureServices(configuration);
        services.AddStorageServices();
        services.AddSharePointServices();  // SharePoint KB integration
        services.AddSearchServices();
        services.AddCachingServices();     // Semantic cache
        services.AddJiraSolutionServices(configuration); // Learning from Jira tickets
        services.AddAgentServices();
        services.AddFeedbackServices();    // Feedback for bot training
        services.AddAuthServices();
        services.AddDocumentServices();

        return services;
    }

    /// <summary>
    /// Initialize all services that require startup initialization
    /// </summary>
    public static async Task InitializeServicesAsync(this IServiceProvider serviceProvider)
    {
        // Initialize storage services first
        var scriptStorage = serviceProvider.GetRequiredService<ScriptStorageService>();
        await scriptStorage.InitializeAsync();

        var knowledgeStorage = serviceProvider.GetRequiredService<KnowledgeStorageService>();
        await knowledgeStorage.InitializeAsync();

        var imageStorage = serviceProvider.GetRequiredService<KnowledgeImageService>();
        await imageStorage.InitializeAsync();

        var contextStorage = serviceProvider.GetRequiredService<ContextStorageService>();
        await contextStorage.InitializeAsync();

        // Initialize search services
        var scriptService = serviceProvider.GetRequiredService<ScriptSearchService>();
        await scriptService.InitializeAsync();

        var knowledgeService = serviceProvider.GetRequiredService<KnowledgeSearchService>();
        await knowledgeService.InitializeAsync();

        var contextService = serviceProvider.GetRequiredService<ContextSearchService>();
        await contextService.InitializeAsync();

        // Initialize SharePoint service
        var sharePointService = serviceProvider.GetRequiredService<SharePointKnowledgeService>();
        await sharePointService.InitializeAsync();

        // Initialize Jira Solution service
        var jiraSolutionService = serviceProvider.GetRequiredService<JiraSolutionSearchService>();
        await jiraSolutionService.InitializeAsync();
    }
}
