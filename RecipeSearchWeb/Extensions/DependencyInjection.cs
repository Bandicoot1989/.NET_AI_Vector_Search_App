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
    /// Add AI agent services
    /// </summary>
    public static IServiceCollection AddAgentServices(this IServiceCollection services)
    {
        services.AddSingleton<KnowledgeAgentService>();
        services.AddSingleton<IKnowledgeAgentService>(sp => sp.GetRequiredService<KnowledgeAgentService>());

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
        services.AddAgentServices();
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
    }
}
