using Azure.AI.OpenAI;
using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;

namespace RecipeSearchWeb.Services;

/// <summary>
/// AI Agent service that answers questions using the Knowledge Base, Context Documents, and Confluence
/// Uses RAG (Retrieval Augmented Generation) with existing embeddings
/// </summary>
public class KnowledgeAgentService : IKnowledgeAgentService
{
    private readonly ChatClient _chatClient;
    private readonly KnowledgeSearchService _knowledgeService;
    private readonly ContextSearchService _contextService;
    private readonly ConfluenceKnowledgeService? _confluenceService;
    private readonly ILogger<KnowledgeAgentService> _logger;
    
    private const string SystemPrompt = @"You are **Operations One Centre Bot**, the AI assistant for Grupo Antolin's IT Operations team.

## Your Role
Help employees by providing useful information from Confluence/KB documentation, and guide them to open support tickets when they need IT assistance.

## CRITICAL: Response Strategy (Follow This Order)

### Step 1: Check if there's DOCUMENTATION (Confluence/KB)
- Look in the CONFLUENCE DOCUMENTATION and KNOWLEDGE BASE sections
- If you find relevant how-to guides, procedures, or explanations → USE THEM FIRST
- **Provide clear step-by-step instructions from the documentation**
- **ALWAYS include the Confluence page URL as reference**: 'Más información en: [Título de la página](URL)'

### Step 2: After providing info (or if no documentation found)
- Check the JIRA TICKET FORMS section
- If the user needs IT support/action → provide the ticket link
- If you provided documentation AND user might still need help → say 'Si necesitas más ayuda, puedes abrir un ticket aquí: [link]'

### Step 3: If NO relevant information exists
- **DO NOT INVENT OR HALLUCINATE** - this is critical
- Simply say: 'No tengo información sobre este tema en la base de conocimientos.'
- Provide the generic ticket link: 'Te recomiendo abrir un ticket en [MyTicket](https://antolin.atlassian.net/servicedesk/customer/portal/3)'

## Response Examples

### Example 1: Documentation EXISTS in Confluence
User: '¿Cómo creo un usuario en BMW B2B?'
Good Response:
'Para crear un usuario en BMW B2B, sigue estos pasos:

1. Accede al portal de BMW
2. Ve a la sección de administración de usuarios
3. Click en 'Nuevo usuario'
4. Completa los campos requeridos...

📖 Documentación completa: [BMW B2B site - New User Creation](url-de-confluence)

Si necesitas ayuda adicional, puedes [abrir un ticket de soporte](url-del-ticket).'

### Example 2: NO documentation, but ticket EXISTS
User: '¿Cómo configuro algo que no está documentado?'
Good Response:
'No tengo documentación específica sobre este tema.
Para solicitar ayuda, puedes [abrir un ticket de soporte](url-del-ticket).'

### Example 3: NOTHING found
User: '¿Cómo configuro el sistema XYZ?'
Good Response:
'No tengo información sobre este tema en la base de conocimientos.
Te recomiendo [abrir un ticket en MyTicket](https://antolin.atlassian.net/servicedesk/customer/portal/3) para que el equipo de IT pueda ayudarte.'

## Language & Formatting Rules

### Language
- **ALWAYS respond in the same language as the user's question** (Spanish → Spanish, English → English)
- Be professional, friendly, and helpful

### Link Formatting (CRITICAL)
- Format: [Descriptive Text](URL)
- Example: [Abrir ticket de Zscaler](https://antolin.atlassian.net/servicedesk/customer/portal/3/group/24/create/1985)
- NEVER format as [URL](URL) - always use descriptive text
- Copy URLs EXACTLY from the context - do not modify them
- **When showing Confluence docs, include the page URL**: 📖 [Título del documento](URL)

### Content Rules
- Use ONLY information from the provided context
- DO NOT invent procedures, steps, or URLs
- If documentation is partial, say what you know and recommend a ticket for more help
- Be concise but complete

## Special Cases

### Remote Access / VPN / Work from Home
- **Zscaler** is the primary remote access solution
- Explain what Zscaler does (access to SAP, Teamcenter, internal apps)
- Provide the Zscaler ticket link if available

### B2B Portals / Customer Extranets (BMW, VW, Ford, etc.)
- Check Confluence for specific portal documentation
- Look for user creation, access management procedures
- Provide step-by-step from documentation if available
- Include Confluence page URL as reference

### SAP Related Questions
- Check Confluence for SAP procedures (SS2, transactions, etc.)
- If specific procedure exists → explain it
- If not → direct to SAP support ticket";

    public KnowledgeAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        KnowledgeSearchService knowledgeService,
        ContextSearchService contextService,
        ConfluenceKnowledgeService? confluenceService,
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
        _confluenceService = confluenceService;
        
        _logger.LogInformation("Confluence service: {Status}", 
            confluenceService?.IsConfigured == true ? "Configured" : "Not configured");
    }

    /// <summary>
    /// Process a user question and return an AI-generated answer
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        try
        {
            // Expand the query with related terms for better ticket matching
            var expandedQuery = ExpandQueryWithSynonyms(question);
            _logger.LogInformation("Original query: {Original}, Expanded: {Expanded}", question, expandedQuery);
            
            // 1. Search the Knowledge Base for relevant articles
            var relevantArticles = await _knowledgeService.SearchArticlesAsync(question, topResults: 5);
            
            // 2. Search context documents (tickets, URLs, etc.) with BOTH original and expanded query
            // Use more results to ensure we get both reference data AND tickets
            var contextResults1 = await _contextService.SearchAsync(question, topResults: 10);
            var contextResults2 = await _contextService.SearchAsync(expandedQuery, topResults: 10);
            var contextDocs = contextResults1.Concat(contextResults2)
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .Take(15)
                .ToList();
            
            _logger.LogInformation("Context search: {Count1} from original, {Count2} from expanded, {Total} combined", 
                contextResults1.Count, contextResults2.Count, contextDocs.Count);
            
            // 3. Search Confluence KB with BOTH original and expanded query for better results
            var confluencePages = new List<ConfluencePage>();
            if (_confluenceService?.IsConfigured == true)
            {
                var results1 = await _confluenceService.SearchAsync(question, topResults: 5);
                var results2 = await _confluenceService.SearchAsync(expandedQuery, topResults: 5);
                confluencePages = results1.Concat(results2)
                    .GroupBy(p => p.Title)
                    .Select(g => g.First())
                    .Take(6)
                    .ToList();
            }
            
            // 4. Build context from all sources
            var context = BuildContext(relevantArticles, contextDocs, confluencePages);
            
            // 5. Build the messages for the chat
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
            var userMessage = $@"Context from Knowledge Base, Confluence KB, and Reference Data:
{context}

User Question: {question}

Please answer based on the context provided above. If there's a relevant ticket category or URL, include it in your response.";

            messages.Add(new UserChatMessage(userMessage));

            // 6. Get AI response
            var response = await _chatClient.CompleteChatAsync(messages);
            var answer = response.Value.Content[0].Text;

            _logger.LogInformation("Agent answered question: {Question} using {ArticleCount} KB articles, {ConfluenceCount} Confluence pages", 
                question.Substring(0, Math.Min(50, question.Length)), relevantArticles.Count, confluencePages.Count);

            // Only return articles with high relevance scores as sources
            // This prevents showing unrelated KB articles as sources
            var highRelevanceArticles = relevantArticles
                .Where(a => a.SearchScore >= 0.5) // Only articles with 50%+ relevance
                .Take(3) // Maximum 3 sources
                .ToList();

            return new AgentResponse
            {
                Answer = answer,
                RelevantArticles = highRelevanceArticles.Select(a => new ArticleReference
                {
                    KBNumber = a.KBNumber,
                    Title = a.Title,
                    Score = (float)a.SearchScore
                }).ToList(),
                ConfluenceSources = confluencePages
                    .Where(p => !string.IsNullOrEmpty(p.Content) && p.Content.Length > 100) // Only pages with real content
                    .Take(3)
                    .Select(p => new ConfluenceReference
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
        // Expand the query with related terms for better matching
        var expandedQuery = ExpandQueryWithSynonyms(question);
        
        // 1. Search the Knowledge Base for relevant articles
        var relevantArticles = await _knowledgeService.SearchArticlesAsync(question, topResults: 5);
        
        // 2. Search context documents with BOTH original and expanded query
        var contextResults1 = await _contextService.SearchAsync(question, topResults: 10);
        var contextResults2 = await _contextService.SearchAsync(expandedQuery, topResults: 10);
        var contextDocs = contextResults1.Concat(contextResults2)
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .Take(15)
            .ToList();
        
        // 3. Search Confluence KB with BOTH original and expanded query for better results
        var confluencePages = new List<ConfluencePage>();
        if (_confluenceService?.IsConfigured == true)
        {
            // Search with original question
            var results1 = await _confluenceService.SearchAsync(question, topResults: 5);
            // Search with expanded query
            var results2 = await _confluenceService.SearchAsync(expandedQuery, topResults: 5);
            
            // Combine and deduplicate by title
            confluencePages = results1.Concat(results2)
                .GroupBy(p => p.Title)
                .Select(g => g.First())
                .Take(6)
                .ToList();
        }
        
        // 4. Build context from all sources
        var context = BuildContext(relevantArticles, contextDocs, confluencePages);
        
        // 5. Build the messages for the chat
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
        var userMessage = $@"Context from Knowledge Base, Confluence KB, and Reference Data:
{context}

User Question: {question}

Please answer based on the context provided above. If there's a relevant ticket category or URL, include it in your response.";

        messages.Add(new UserChatMessage(userMessage));

        // 6. Stream the response
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
    /// Build context string from relevant articles, context documents, and Confluence pages
    /// </summary>
    private string BuildContext(List<KnowledgeArticle> articles, List<ContextDocument> contextDocs, List<ConfluencePage> confluencePages)
    {
        var sb = new StringBuilder();
        
        _logger.LogInformation("BuildContext: {ArticleCount} articles, {ContextCount} context docs, {ConfluenceCount} confluence pages",
            articles.Count, contextDocs.Count, confluencePages.Count);
        
        // Separate context documents into categories
        var jiraTickets = contextDocs.Where(d => 
            !string.IsNullOrWhiteSpace(d.Link) && 
            d.Link.Contains("atlassian.net/servicedesk")).ToList();
        
        var referenceData = contextDocs.Where(d => 
            string.IsNullOrWhiteSpace(d.Link) || 
            !d.Link.Contains("atlassian.net/servicedesk")).ToList();
        
        _logger.LogInformation("BuildContext: {JiraCount} Jira tickets, {RefCount} reference data entries", 
            jiraTickets.Count, referenceData.Count);
        
        // Log Jira tickets found for debugging
        if (jiraTickets.Any())
        {
            _logger.LogInformation("Jira tickets found: {Tickets}", 
                string.Join(", ", jiraTickets.Select(t => t.Name)));
        }
        else
        {
            _logger.LogWarning("No Jira tickets found in context documents");
        }
        
        // PRIORITY 0: Add reference data (Centres, Companies, etc.) - HIGHEST PRIORITY for lookups
        if (referenceData.Any())
        {
            sb.AppendLine("=== REFERENCE DATA (Centres, Companies, etc.) ===");
            sb.AppendLine("Use this data to answer questions about company codes, plant names, locations, etc.");
            sb.AppendLine();
            
            // Log each reference data entry for debugging
            _logger.LogInformation("Reference data entries: {Entries}", 
                string.Join(", ", referenceData.Take(10).Select(d => $"{d.Name} ({d.SourceFile})")));
            
            foreach (var doc in referenceData.Take(10)) // Include more reference data
            {
                sb.AppendLine($"ENTRY: {doc.Name}");
                if (!string.IsNullOrWhiteSpace(doc.Description))
                {
                    sb.AppendLine($"  Details: {doc.Description}");
                }
                if (!string.IsNullOrWhiteSpace(doc.Keywords))
                {
                    sb.AppendLine($"  Keywords: {doc.Keywords}");
                }
                // Include additional data (extra columns from Excel)
                if (doc.AdditionalData?.Any() == true)
                {
                    foreach (var kvp in doc.AdditionalData)
                    {
                        sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
                    }
                }
                if (!string.IsNullOrWhiteSpace(doc.Link))
                {
                    sb.AppendLine($"  Link: {doc.Link}");
                }
                sb.AppendLine($"  Source: {doc.SourceFile} ({doc.Category})");
                sb.AppendLine();
            }
        }
        
        // PRIORITY 1: Add Jira tickets for support requests
        if (jiraTickets.Any())
        {
            sb.AppendLine("=== JIRA TICKET FORMS - USE THESE FOR SUPPORT REQUESTS ===");
            sb.AppendLine("When the user needs help or has a problem, provide the relevant ticket link below.");
            sb.AppendLine("CRITICAL: Use the EXACT URL shown - do not modify it.");
            sb.AppendLine();
            foreach (var doc in jiraTickets.Take(5))
            {
                sb.AppendLine($"TICKET: {doc.Name}");
                if (!string.IsNullOrWhiteSpace(doc.Description))
                {
                    sb.AppendLine($"  Use for: {doc.Description}");
                }
                sb.AppendLine($"  URL: {doc.Link}");
                sb.AppendLine();
            }
        }
        
        // PRIORITY 2: Add Confluence pages (documentation, how-to guides) - HIGHER PRIORITY
        if (confluencePages.Any())
        {
            sb.AppendLine("=== CONFLUENCE DOCUMENTATION (How-To Guides & Procedures) ===");
            sb.AppendLine("Use this information FIRST to explain steps and procedures to the user.");
            sb.AppendLine("IMPORTANT: Always include the 'Page URL' as a reference link in your response.");
            sb.AppendLine();
            foreach (var page in confluencePages.Take(4)) // Increased to 4 pages
            {
                sb.AppendLine($"--- {page.Title} ---");
                
                // Include the URL so the bot can provide it as reference
                if (!string.IsNullOrWhiteSpace(page.WebUrl))
                {
                    sb.AppendLine($"Page URL: {page.WebUrl}");
                }
                
                if (!string.IsNullOrWhiteSpace(page.Content))
                {
                    var content = page.Content;
                    if (content.Length > 2000) // Increased content size
                    {
                        content = content.Substring(0, 2000) + "...";
                    }
                    sb.AppendLine($"Content: {content}");
                }
                sb.AppendLine();
            }
        }
        
        // PRIORITY 3: Add KB articles context (internal procedures)
        if (articles.Any())
        {
            sb.AppendLine("=== KNOWLEDGE BASE ARTICLES (Internal Procedures) ===");
            foreach (var article in articles.Take(3))
            {
                sb.AppendLine($"--- Article: {article.KBNumber} - {article.Title} ---");
                
                if (!string.IsNullOrWhiteSpace(article.ShortDescription))
                {
                    sb.AppendLine($"Summary: {article.ShortDescription}");
                }
                
                var content = article.Content ?? "";
                if (content.Length > 1500)
                {
                    content = content.Substring(0, 1500) + "...";
                }
                sb.AppendLine($"Content: {content}");
                sb.AppendLine();
            }
        }
        
        if (!articles.Any() && !contextDocs.Any() && !confluencePages.Any())
        {
            return "No relevant information found in the Knowledge Base or reference data.";
        }

        return sb.ToString();
    }
    
    /// <summary>
    /// Expand query with synonyms and related terms for better ticket matching
    /// </summary>
    private string ExpandQueryWithSynonyms(string query)
    {
        var lowerQuery = query.ToLowerInvariant();
        var expansions = new List<string> { query };
        
        // Remote access / work from home synonyms
        if (lowerQuery.Contains("casa") || lowerQuery.Contains("home") || lowerQuery.Contains("remoto") || 
            lowerQuery.Contains("remote") || lowerQuery.Contains("conectar") || lowerQuery.Contains("connect"))
        {
            expansions.Add("remote access VPN Zscaler");
            expansions.Add("acceso remoto");
        }
        
        // VPN / Network related
        if (lowerQuery.Contains("vpn") || lowerQuery.Contains("red") || lowerQuery.Contains("network") ||
            lowerQuery.Contains("internet") || lowerQuery.Contains("conexión"))
        {
            expansions.Add("Zscaler remote access");
            expansions.Add("Internet Web Page");
        }
        
        // Zscaler specific
        if (lowerQuery.Contains("zscaler") || lowerQuery.Contains("proxy"))
        {
            expansions.Add("remote access VPN");
        }
        
        // Customer portals / Extranets - user creation and management
        if (lowerQuery.Contains("usuario") || lowerQuery.Contains("user") || lowerQuery.Contains("crear") || 
            lowerQuery.Contains("create") || lowerQuery.Contains("nuevo") || lowerQuery.Contains("new") ||
            lowerQuery.Contains("acceso") || lowerQuery.Contains("access"))
        {
            expansions.Add("user management");
            expansions.Add("Customer extranet user management");
        }
        
        // Customer portal names (VW, BMW, Ford, etc.)
        if (lowerQuery.Contains("vw") || lowerQuery.Contains("volkswagen"))
        {
            expansions.Add("B2B Portals Customer Extranets Volkswagen");
            expansions.Add("Customer extranet user management Volkswagen");
        }
        if (lowerQuery.Contains("bmw"))
        {
            expansions.Add("B2B Portals Customer Extranets BMW");
            expansions.Add("Customer extranet user management BMW");
        }
        if (lowerQuery.Contains("ford"))
        {
            expansions.Add("B2B Portals Customer Extranets Ford");
            expansions.Add("Customer extranet user management Ford");
        }
        if (lowerQuery.Contains("portal") || lowerQuery.Contains("extranet") || lowerQuery.Contains("b2b"))
        {
            expansions.Add("B2B Portals Customer Extranets");
            expansions.Add("Customer extranet user management");
        }
        
        // Connect / Access to portal
        if (lowerQuery.Contains("conectar") || lowerQuery.Contains("connect") || lowerQuery.Contains("acceder") ||
            lowerQuery.Contains("entrar") || lowerQuery.Contains("login") || lowerQuery.Contains("acceso"))
        {
            expansions.Add("B2B Portals Customer Extranets access");
        }
        
        // Centre / Plant queries - extract plant codes like IGA, IBU, etc.
        if (lowerQuery.Contains("centro") || lowerQuery.Contains("centre") || lowerQuery.Contains("plant") || 
            lowerQuery.Contains("planta") || lowerQuery.Contains("fabrica") || lowerQuery.Contains("factory"))
        {
            // Extract potential plant codes (2-4 uppercase letters)
            var words = query.Split(new[] { ' ', '?', '¿', '!', '¡', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                // Plant codes are typically 2-4 characters, may contain letters
                if (word.Length >= 2 && word.Length <= 5 && word.ToUpperInvariant() == word)
                {
                    expansions.Add(word); // Add the code directly
                }
            }
            expansions.Add("centre plant location");
        }
        
        // Email / Outlook
        if (lowerQuery.Contains("correo") || lowerQuery.Contains("email") || lowerQuery.Contains("outlook") ||
            lowerQuery.Contains("mail"))
        {
            expansions.Add("Email Outlook");
        }
        
        // SAP related - expanded for user creation
        if (lowerQuery.Contains("sap"))
        {
            expansions.Add("SAP transaction user");
            expansions.Add("SAP new user creation");
            expansions.Add("SAP usuario nuevo");
            // If asking about creating users
            if (lowerQuery.Contains("usuario") || lowerQuery.Contains("user") || lowerQuery.Contains("crear") || 
                lowerQuery.Contains("create") || lowerQuery.Contains("nuevo") || lowerQuery.Contains("new") ||
                lowerQuery.Contains("abrir") || lowerQuery.Contains("ticket"))
            {
                expansions.Add("SAP User Request");
                expansions.Add("New SAP User");
                expansions.Add("SAP Access Request");
            }
        }
        
        // New user / create user requests (generic)
        if ((lowerQuery.Contains("nuevo") || lowerQuery.Contains("new") || lowerQuery.Contains("crear") || lowerQuery.Contains("create")) &&
            (lowerQuery.Contains("usuario") || lowerQuery.Contains("user")))
        {
            expansions.Add("new user creation request");
            expansions.Add("user access request");
            expansions.Add("crear usuario");
        }
        
        // Ticket / request related
        if (lowerQuery.Contains("ticket") || lowerQuery.Contains("abrir") || lowerQuery.Contains("solicitar") ||
            lowerQuery.Contains("request") || lowerQuery.Contains("open"))
        {
            expansions.Add("ticket request form");
            expansions.Add("service request");
        }
        
        // Computer / PC issues
        if (lowerQuery.Contains("ordenador") || lowerQuery.Contains("computador") || lowerQuery.Contains("pc") ||
            lowerQuery.Contains("laptop") || lowerQuery.Contains("computer"))
        {
            expansions.Add("help with my computer");
        }
        
        // Printer
        if (lowerQuery.Contains("impresora") || lowerQuery.Contains("printer") || lowerQuery.Contains("imprimir"))
        {
            expansions.Add("Printer");
        }
        
        // Teams
        if (lowerQuery.Contains("teams") || lowerQuery.Contains("reunión") || lowerQuery.Contains("meeting") ||
            lowerQuery.Contains("videoconferencia"))
        {
            expansions.Add("Teams");
        }
        
        return string.Join(" ", expansions);
    }
}

/// <summary>
/// Response from the Knowledge Agent
/// </summary>
public class AgentResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<ArticleReference> RelevantArticles { get; set; } = new();
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
/// Reference to a Confluence page used in the response
/// </summary>
public class ConfluenceReference
{
    public string Title { get; set; } = string.Empty;
    public string SpaceKey { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
}
