using Azure.AI.OpenAI;
using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;

namespace RecipeSearchWeb.Services;

/// <summary>
/// AI Agent service that answers questions using the Knowledge Base, Context Documents, SharePoint KB, and Confluence
/// Uses RAG (Retrieval Augmented Generation) with existing embeddings
/// </summary>
public class KnowledgeAgentService : IKnowledgeAgentService
{
    private readonly ChatClient _chatClient;
    private readonly KnowledgeSearchService _knowledgeService;
    private readonly ContextSearchService _contextService;
    private readonly SharePointKnowledgeService _sharePointService;
    private readonly ConfluenceKnowledgeService _confluenceService;
    private readonly ILogger<KnowledgeAgentService> _logger;
    
    private const string SystemPrompt = @"You are a helpful IT Operations assistant for the company's internal Knowledge Base and ServiceDesk.
Your role is to help employees find information, answer questions, and guide them to the right resources.

Guidelines:
- Answer questions accurately based on the provided context from the Knowledge Base, SharePoint KB, Confluence, and reference data
- If a ServiceDesk ticket category is relevant, provide the direct link to create a ticket
- If the information is not available in the context, say so clearly
- Be concise but complete in your answers
- If a procedure has steps, list them clearly
- Reference the KB article number when relevant (e.g., 'According to KB0013350...')
- If multiple articles are relevant, synthesize the information
- When suggesting to open a ticket, always include the direct URL if available
- For SharePoint KB documents, reference the folder category (Business Solutions, IT Operations, Technology, etc.)
- For Confluence pages, reference the space and page title
- Respond in the same language as the user's question
- Be professional and helpful

If you cannot find relevant information, suggest the user contact the IT Help Desk or search for related topics.";

    public KnowledgeAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        KnowledgeSearchService knowledgeService,
        ContextSearchService contextService,
        SharePointKnowledgeService sharePointService,
        ConfluenceKnowledgeService confluenceService,
        ILogger<KnowledgeAgentService> logger)
    {
        // IMPORTANT: GPT_NAME is for embeddings, CHAT_NAME is for chat completions
        // Default to gpt-4o-mini if no chat model is configured
        var chatModel = configuration["AZURE_OPENAI_CHAT_NAME"] ?? "gpt-4o-mini";
        
        _logger = logger;
        _logger.LogInformation("Initializing KnowledgeAgentService with chat model: {Model}", chatModel);
        
        _chatClient = azureClient.GetChatClient(chatModel);
        _knowledgeService = knowledgeService;
        _contextService = contextService;
        _sharePointService = sharePointService;
        _confluenceService = confluenceService;
    }

    /// <summary>
    /// Process a user question and return an AI-generated answer
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        try
        {
            // 1. Search the Knowledge Base for relevant articles
            var relevantArticles = await _knowledgeService.SearchArticlesAsync(question, topResults: 5);
            
            // 2. Search context documents (tickets, URLs, etc.)
            var contextDocs = await _contextService.SearchAsync(question, topResults: 5);
            
            // 3. Search SharePoint Digitalization KB
            var sharePointDocs = await _sharePointService.SearchAsync(question, topResults: 5);
            
            // 4. Search Confluence KB
            var confluencePages = await _confluenceService.SearchAsync(question, topResults: 5);
            
            // 5. Build context from all sources
            var context = BuildContext(relevantArticles, contextDocs, sharePointDocs, confluencePages);
            
            // 6. Build the messages for the chat
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt)
            };

            // Add conversation history if provided (for multi-turn)
            if (conversationHistory?.Any() == true)
            {
                messages.AddRange(conversationHistory);
            }

            // Add the context and question
            var userMessage = $@"Context from Knowledge Base, SharePoint KB, Confluence KB, and Reference Data:
{context}

User Question: {question}

Please answer based on the context provided above. If there's a relevant ticket category or URL, include it in your response.";

            messages.Add(new UserChatMessage(userMessage));

            // 7. Get AI response
            var response = await _chatClient.CompleteChatAsync(messages);
            var answer = response.Value.Content[0].Text;

            _logger.LogInformation("Agent answered question: {Question} using {ArticleCount} KB articles, {SharePointCount} SharePoint docs, {ConfluenceCount} Confluence pages", 
                question.Substring(0, Math.Min(50, question.Length)), relevantArticles.Count, sharePointDocs.Count, confluencePages.Count);

            return new AgentResponse
            {
                Answer = answer,
                RelevantArticles = relevantArticles.Select(a => new ArticleReference
                {
                    KBNumber = a.KBNumber,
                    Title = a.Title,
                    Score = (float)a.SearchScore
                }).ToList(),
                SharePointSources = sharePointDocs.Select(d => new SharePointReference
                {
                    Title = d.Title,
                    Folder = d.Folder,
                    WebUrl = d.WebUrl
                }).ToList(),
                ConfluenceSources = confluencePages.Select(p => new ConfluenceReference
                {
                    Title = p.Title,
                    SpaceKey = p.SpaceKey,
                    WebUrl = p.WebUrl
                }).ToList(),
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing question: {Question}", question);
            return new AgentResponse
            {
                Answer = "I'm sorry, I encountered an error while processing your question. Please try again or contact the IT Help Desk.",
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Stream the response for a better UX
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        // 1. Search the Knowledge Base for relevant articles
        var relevantArticles = await _knowledgeService.SearchArticlesAsync(question, topResults: 5);
        
        // 2. Search context documents
        var contextDocs = await _contextService.SearchAsync(question, topResults: 5);
        
        // 3. Search SharePoint KB
        var sharePointDocs = await _sharePointService.SearchAsync(question, topResults: 5);
        
        // 4. Search Confluence KB
        var confluencePages = await _confluenceService.SearchAsync(question, topResults: 5);
        
        // 5. Build context from all sources
        var context = BuildContext(relevantArticles, contextDocs, sharePointDocs, confluencePages);
        
        // 6. Build the messages for the chat
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt)
        };

        // Add conversation history if provided
        if (conversationHistory?.Any() == true)
        {
            messages.AddRange(conversationHistory);
        }

        // Add the context and question
        var userMessage = $@"Context from Knowledge Base, SharePoint KB, Confluence KB, and Reference Data:
{context}

User Question: {question}

Please answer based on the context provided above. If there's a relevant ticket category or URL, include it in your response.";

        messages.Add(new UserChatMessage(userMessage));

        // 7. Stream the response
        await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return part.Text;
                }
            }
        }
    }

    /// <summary>
    /// Build context string from relevant articles, context documents, SharePoint docs, and Confluence pages
    /// </summary>
    private string BuildContext(List<KnowledgeArticle> articles, List<ContextDocument> contextDocs, List<SharePointDocument> sharePointDocs, List<ConfluencePage> confluencePages)
    {
        var sb = new StringBuilder();
        
        // Add KB articles context
        if (articles.Any())
        {
            sb.AppendLine("=== KNOWLEDGE BASE ARTICLES ===");
            foreach (var article in articles.Take(3)) // Limit to top 3 for context window
            {
                sb.AppendLine($"--- Article: {article.KBNumber} - {article.Title} ---");
                
                if (!string.IsNullOrWhiteSpace(article.Purpose))
                {
                    sb.AppendLine($"Purpose: {article.Purpose}");
                }
                
                if (!string.IsNullOrWhiteSpace(article.ShortDescription))
                {
                    sb.AppendLine($"Summary: {article.ShortDescription}");
                }
                
                // Include content but limit length
                var content = article.Content ?? "";
                if (content.Length > 2000)
                {
                    content = content.Substring(0, 2000) + "...";
                }
                sb.AppendLine($"Content: {content}");
                
                sb.AppendLine();
            }
        }
        
        // Add context documents (tickets, URLs, etc.)
        if (contextDocs.Any())
        {
            sb.AppendLine("=== REFERENCE DATA (Ticket Categories, URLs, etc.) ===");
            foreach (var doc in contextDocs.Take(5))
            {
                sb.AppendLine($"--- {doc.Category}: {doc.Name} ---");
                
                if (!string.IsNullOrWhiteSpace(doc.Description))
                {
                    sb.AppendLine($"Description: {doc.Description}");
                }
                
                if (!string.IsNullOrWhiteSpace(doc.Keywords))
                {
                    sb.AppendLine($"Keywords: {doc.Keywords}");
                }
                
                if (!string.IsNullOrWhiteSpace(doc.Link))
                {
                    sb.AppendLine($"URL/Link: {doc.Link}");
                }
                
                // Include any additional data
                foreach (var kvp in doc.AdditionalData)
                {
                    sb.AppendLine($"{kvp.Key}: {kvp.Value}");
                }
                
                sb.AppendLine();
            }
        }

        // Add SharePoint KB documents
        if (sharePointDocs.Any())
        {
            sb.AppendLine("=== SHAREPOINT DIGITALIZATION KB ===");
            foreach (var doc in sharePointDocs.Take(3))
            {
                sb.AppendLine($"--- {doc.Folder}: {doc.Title} ---");
                
                if (!string.IsNullOrWhiteSpace(doc.Content))
                {
                    var content = doc.Content;
                    if (content.Length > 2000)
                    {
                        content = content.Substring(0, 2000) + "...";
                    }
                    sb.AppendLine($"Content: {content}");
                }
                
                if (!string.IsNullOrWhiteSpace(doc.WebUrl))
                {
                    sb.AppendLine($"Source: {doc.WebUrl}");
                }
                
                sb.AppendLine();
            }
        }

        // Add Confluence pages
        if (confluencePages.Any())
        {
            sb.AppendLine("=== CONFLUENCE KNOWLEDGE BASE ===");
            foreach (var page in confluencePages.Take(3))
            {
                sb.AppendLine($"--- [{page.SpaceKey}] {page.Title} ---");
                
                if (!string.IsNullOrWhiteSpace(page.Content))
                {
                    var content = page.Content;
                    if (content.Length > 2000)
                    {
                        content = content.Substring(0, 2000) + "...";
                    }
                    sb.AppendLine($"Content: {content}");
                }
                
                if (page.Labels?.Any() == true)
                {
                    sb.AppendLine($"Labels: {string.Join(", ", page.Labels)}");
                }
                
                if (!string.IsNullOrWhiteSpace(page.WebUrl))
                {
                    sb.AppendLine($"Source: {page.WebUrl}");
                }
                
                sb.AppendLine();
            }
        }
        
        if (!articles.Any() && !contextDocs.Any() && !sharePointDocs.Any() && !confluencePages.Any())
        {
            return "No relevant information found in the Knowledge Base, SharePoint KB, Confluence KB, or reference data.";
        }

        return sb.ToString();
    }
}

/// <summary>
/// Response from the Knowledge Agent
/// </summary>
public class AgentResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<ArticleReference> RelevantArticles { get; set; } = new();
    public List<SharePointReference> SharePointSources { get; set; } = new();
    public List<ConfluenceReference> ConfluenceSources { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Reference to a KB article used in the response
/// </summary>
public class ArticleReference
{
    public string KBNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public float Score { get; set; }
}

/// <summary>
/// Reference to a SharePoint document used in the response
/// </summary>
public class SharePointReference
{
    public string Title { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
}

/// <summary>
/// Reference to a Confluence page used in the response
/// </summary>
public class ConfluenceReference
{
    public string Title { get; set; } = string.Empty;
    public string SpaceKey { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
}
