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
- Provide a ticket link from the JIRA TICKET FORMS section if available

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
Te recomiendo abrir un ticket en el portal de soporte para que el equipo de IT pueda ayudarte.'

## Language & Formatting Rules

### Language
- **ALWAYS respond in the same language as the user's question** (Spanish → Spanish, English → English)
- Be professional, friendly, and helpful

### Link Formatting (CRITICAL)
- Format: [Descriptive Text](URL)
- Use descriptive text for links, e.g.: [Abrir ticket de soporte](URL)
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
- If not → direct to SAP support ticket

### Historical Ticket Solutions (JIRA SOLUTIONS section)
- These are suggestions from resolved incidents, NOT official documentation
- Use them as troubleshooting hints when no official documentation exists
- Always mention they are based on previous tickets
- Format: 'Basado en incidencias previas (Ticket #ID), este problema suele resolverse...'

### Specific Ticket Queries (When user asks about MT-12345, MTT-67890, etc.)
- If the user references a specific ticket number (e.g., 'ayuda con MT-799225')
- You will receive real-time ticket information directly from Jira
- Summarize the ticket status, priority, and description clearly
- If similar solved tickets are provided, suggest how those solutions might apply
- Include the direct link to the Jira ticket
- Be proactive: suggest next steps based on the ticket status and history";

    private readonly QueryCacheService? _cacheService;
    private readonly FeedbackService? _feedbackService;
    private readonly IJiraSolutionService? _jiraSolutionService;
    private readonly ITicketLookupService? _ticketLookupService;

    // Confidence threshold for feedback loop - if best score is below this, suggest opening a ticket
    private const double ConfidenceThreshold = 0.65;
    private const string LowConfidenceResponse = "No encuentro información específica sobre este tema en mi base de conocimientos. Te recomiendo abrir un ticket de soporte para que el equipo de IT pueda ayudarte con tu consulta.";
    private const string FallbackTicketLink = "https://antolin.atlassian.net/servicedesk/customer/portal/3";

    public KnowledgeAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        KnowledgeSearchService knowledgeService,
        ContextSearchService contextService,
        ConfluenceKnowledgeService? confluenceService,
        QueryCacheService? cacheService,
        FeedbackService? feedbackService,
        IJiraSolutionService? jiraSolutionService,
        ITicketLookupService? ticketLookupService,
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
        _cacheService = cacheService;
        _feedbackService = feedbackService;
        _jiraSolutionService = jiraSolutionService;
        _ticketLookupService = ticketLookupService;
        
        _logger.LogInformation("Services: Confluence={Confluence}, Cache={Cache}, Feedback={Feedback}, JiraSolutions={Jira}, TicketLookup={TicketLookup}", 
            confluenceService?.IsConfigured == true ? "Configured" : "Not configured",
            cacheService != null ? "Enabled" : "Disabled",
            feedbackService != null ? "Enabled" : "Disabled",
            jiraSolutionService != null ? "Enabled" : "Disabled",
            ticketLookupService != null ? "Enabled" : "Disabled");
    }

    #region Query Analysis (Tier 1 Optimizations)
    
    /// <summary>
    /// Detected intent type for the query
    /// </summary>
    private enum QueryIntent
    {
        TicketRequest,      // User wants to open a ticket
        TicketLookup,       // User is asking about a specific ticket (e.g., MT-12345)
        HowTo,              // User wants step-by-step instructions
        Information,        // User wants general information
        Lookup,             // User wants to look up a specific code/name (centre, company)
        Troubleshooting,    // User has a problem to solve
        General             // General question
    }
    
    /// <summary>
    /// Search weights based on intent
    /// </summary>
    private class SearchWeights
    {
        public double JiraTicketWeight { get; set; } = 1.0;
        public double ConfluenceWeight { get; set; } = 1.0;
        public double KBWeight { get; set; } = 1.0;
        public double ReferenceDataWeight { get; set; } = 1.0;
        public int JiraTopResults { get; set; } = 5;
        public int ConfluenceTopResults { get; set; } = 5;
    }
    
    /// <summary>
    /// Detect the intent of the user's query
    /// </summary>
    private QueryIntent DetectIntent(string query)
    {
        var lower = query.ToLowerInvariant();
        
        // === TICKET LOOKUP: Check if user is asking about a specific ticket (MT-12345, MTT-12345, etc.) ===
        // This takes priority as it's a very specific request
        if (_ticketLookupService?.ContainsTicketReference(query) == true)
        {
            return QueryIntent.TicketLookup;
        }
        
        // Ticket request indicators (user wants to OPEN a ticket, not look one up)
        if (lower.Contains("ticket") || lower.Contains("abrir") || lower.Contains("solicitar") ||
            lower.Contains("request") || lower.Contains("open") || lower.Contains("crear solicitud") ||
            lower.Contains("formulario") || lower.Contains("form"))
        {
            return QueryIntent.TicketRequest;
        }
        
        // How-to indicators
        if (lower.Contains("cómo") || lower.Contains("como") || lower.Contains("how") ||
            lower.Contains("pasos") || lower.Contains("steps") || lower.Contains("proceso") ||
            lower.Contains("procedimiento") || lower.Contains("procedure") || lower.Contains("tutorial"))
        {
            return QueryIntent.HowTo;
        }
        
        // Lookup indicators (short codes, "qué es", "what is")
        if (lower.Contains("qué es") || lower.Contains("que es") || lower.Contains("what is") ||
            lower.Contains("qué centro") || lower.Contains("que centro") || lower.Contains("qué planta") ||
            lower.Contains("qué compañía") || lower.Contains("que compañia"))
        {
            return QueryIntent.Lookup;
        }
        
        // Troubleshooting indicators
        if (lower.Contains("error") || lower.Contains("problema") || lower.Contains("problem") ||
            lower.Contains("no funciona") || lower.Contains("not working") || lower.Contains("falla") ||
            lower.Contains("ayuda") || lower.Contains("help") || lower.Contains("issue"))
        {
            return QueryIntent.Troubleshooting;
        }
        
        return QueryIntent.General;
    }
    
    /// <summary>
    /// Get search weights based on detected intent
    /// </summary>
    private SearchWeights GetSearchWeights(QueryIntent intent)
    {
        return intent switch
        {
            QueryIntent.TicketLookup => new SearchWeights
            {
                JiraTicketWeight = 0.5,      // We already have the ticket data, lower priority for forms
                ConfluenceWeight = 2.0,      // Want related documentation
                KBWeight = 1.5,              // Want KB solutions
                ReferenceDataWeight = 0.3,
                JiraTopResults = 3,
                ConfluenceTopResults = 5
            },
            QueryIntent.TicketRequest => new SearchWeights
            {
                JiraTicketWeight = 2.5,      // Strong priority for tickets
                ConfluenceWeight = 0.5,
                KBWeight = 0.3,
                ReferenceDataWeight = 0.2,
                JiraTopResults = 10,
                ConfluenceTopResults = 3
            },
            QueryIntent.HowTo => new SearchWeights
            {
                JiraTicketWeight = 0.5,
                ConfluenceWeight = 2.5,      // Strong priority for documentation
                KBWeight = 1.5,
                ReferenceDataWeight = 0.3,
                JiraTopResults = 3,
                ConfluenceTopResults = 8
            },
            QueryIntent.Lookup => new SearchWeights
            {
                JiraTicketWeight = 0.2,
                ConfluenceWeight = 0.5,
                KBWeight = 0.3,
                ReferenceDataWeight = 3.0,   // Strong priority for reference data
                JiraTopResults = 2,
                ConfluenceTopResults = 2
            },
            QueryIntent.Troubleshooting => new SearchWeights
            {
                JiraTicketWeight = 1.5,      // Need both docs and tickets
                ConfluenceWeight = 2.0,
                KBWeight = 1.5,
                ReferenceDataWeight = 0.3,
                JiraTopResults = 5,
                ConfluenceTopResults = 6
            },
            _ => new SearchWeights() // Default weights (all 1.0)
        };
    }
    
    /// <summary>
    /// Decompose complex queries into sub-queries
    /// </summary>
    private List<string> DecomposeQuery(string query)
    {
        var subQueries = new List<string> { query }; // Always include original
        var lower = query.ToLowerInvariant();
        
        // Check for compound questions (and/y, or/o)
        if (lower.Contains(" y ") || lower.Contains(" and "))
        {
            var parts = query.Split(new[] { " y ", " Y ", " and ", " AND " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                subQueries.AddRange(parts.Select(p => p.Trim()));
            }
        }
        
        // Check for multiple questions (?, multiple verbs)
        var questionMarks = query.Count(c => c == '?');
        if (questionMarks > 1)
        {
            var parts = query.Split('?', StringSplitOptions.RemoveEmptyEntries);
            subQueries.AddRange(parts.Select(p => p.Trim() + "?"));
        }
        
        // Extract specific entity queries (SAP, VPN, etc.)
        var entities = ExtractEntities(query);
        foreach (var entity in entities)
        {
            if (!subQueries.Any(q => q.Contains(entity, StringComparison.OrdinalIgnoreCase)))
            {
                // Create entity-specific sub-query
                if (lower.Contains("ticket"))
                    subQueries.Add($"ticket {entity}");
                if (lower.Contains("cómo") || lower.Contains("como") || lower.Contains("how"))
                    subQueries.Add($"how to {entity}");
            }
        }
        
        _logger.LogInformation("Query decomposition: Original='{Original}', SubQueries=[{SubQueries}]", 
            query, string.Join(", ", subQueries));
        
        return subQueries.Distinct().ToList();
    }
    
    /// <summary>
    /// Extract known entities from query
    /// </summary>
    private List<string> ExtractEntities(string query)
    {
        var entities = new List<string>();
        var lower = query.ToLowerInvariant();
        
        // Technology/System entities
        var knownSystems = new[] { "sap", "vpn", "zscaler", "teams", "outlook", "sharepoint", 
            "confluence", "jira", "teamcenter", "bmw", "volkswagen", "vw", "ford", "b2b" };
        
        foreach (var system in knownSystems)
        {
            if (lower.Contains(system))
                entities.Add(system.ToUpperInvariant());
        }
        
        // Extract potential plant codes (2-4 uppercase letters)
        var words = query.Split(new[] { ' ', '?', '¿', '!', '¡', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.Length >= 2 && word.Length <= 4 && word.All(char.IsLetter) && word == word.ToUpperInvariant())
            {
                entities.Add(word);
            }
        }
        
        return entities.Distinct().ToList();
    }
    
    #endregion

    #region Auto-Learning: Few-Shot Prompting
    
    /// <summary>
    /// Get few-shot examples from successful responses cache (Level 3 Auto-Learning)
    /// This teaches the AI how to respond based on previously successful Q&A pairs
    /// </summary>
    private async Task<string> GetFewShotExamplesAsync(string query)
    {
        if (_feedbackService == null)
            return string.Empty;
        
        try
        {
            // Try to find a semantically similar successful response
            var cachedResponse = await _feedbackService.GetCachedResponseAsync(query);
            
            if (cachedResponse != null)
            {
                _logger.LogInformation("Few-Shot: Found cached successful response for similar query (UseCount: {Count})", 
                    cachedResponse.UseCount);
                
                // Format as a few-shot example to guide the AI
                return $@"

## EJEMPLO DE RESPUESTA EXITOSA (usa este estilo y formato):
**Pregunta anterior similar:** {cachedResponse.Query}
**Respuesta que fue útil (👍):** {cachedResponse.Response}
---
Usa el ejemplo anterior como guía para el tono, formato y nivel de detalle.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting few-shot examples from FeedbackService");
        }
        
        return string.Empty;
    }
    
    /// <summary>
    /// Apply feedback boost to search results (Level 1 Auto-Learning)
    /// Documents that received positive feedback for similar queries get a score boost
    /// </summary>
    private async Task ApplyFeedbackBoostAsync(string query, List<ContextDocument> results)
    {
        if (_feedbackService == null || !results.Any())
            return;
        
        try
        {
            // Get feedback stats to identify documents that worked well
            var stats = await _feedbackService.GetStatsAsync();
            
            // Get all positive feedback to build a boost map
            var allFeedback = await _feedbackService.GetAllFeedbackAsync();
            var positiveFeedback = allFeedback.Where(f => f.IsHelpful).ToList();
            
            if (!positiveFeedback.Any())
                return;
            
            // Extract keywords from current query
            var queryKeywords = query.ToLowerInvariant()
                .Split(new[] { ' ', '?', '!', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3)
                .ToHashSet();
            
            // Find feedback entries with similar keywords
            var relevantFeedback = positiveFeedback
                .Where(f => f.ExtractedKeywords.Any(k => queryKeywords.Contains(k.ToLowerInvariant())))
                .ToList();
            
            if (!relevantFeedback.Any())
                return;
            
            // Apply boost to matching documents
            var boostedCount = 0;
            foreach (var result in results)
            {
                // Check if this document's content matches keywords from successful responses
                var docText = $"{result.Name} {result.Description} {result.Keywords}".ToLowerInvariant();
                
                foreach (var feedback in relevantFeedback)
                {
                    var matchScore = feedback.ExtractedKeywords
                        .Count(k => docText.Contains(k.ToLowerInvariant()));
                    
                    if (matchScore >= 2) // At least 2 keyword matches
                    {
                        // Apply 15% boost per matching feedback entry (max 45% boost)
                        var boostFactor = Math.Min(1.45, 1.0 + (matchScore * 0.15));
                        result.SearchScore *= boostFactor;
                        boostedCount++;
                        break; // Only apply one boost per document
                    }
                }
            }
            
            if (boostedCount > 0)
            {
                _logger.LogInformation("Feedback Boost: Applied boost to {Count} documents based on {FeedbackCount} positive feedback entries",
                    boostedCount, relevantFeedback.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error applying feedback boost");
        }
    }
    
    #endregion

    /// <summary>
    /// Process a user question and return an AI-generated answer
    /// Uses Tier 1 optimizations: Intent Detection, Weighted Search, Query Decomposition
    /// Uses Tier 2 optimizations: Caching (String + Semantic), Parallel Search
    /// Uses Feedback Loop: Confidence threshold for low-relevance responses
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // === TIER 2 OPTIMIZATION: Check Cache First (String-based) ===
            if (_cacheService != null && conversationHistory == null) // Only cache single-turn queries
            {
                // Try exact match cache first
                var cached = _cacheService.GetCachedResponse(question);
                if (cached != null)
                {
                    _logger.LogInformation("Cache HIT (string match) - returning cached response (age: {Age}s)", 
                        (DateTime.UtcNow - cached.CachedAt).TotalSeconds);
                    return new AgentResponse
                    {
                        Answer = cached.Response,
                        RelevantArticles = new List<ArticleReference>(),
                        ConfluenceSources = new List<ConfluenceReference>(),
                        Success = true,
                        FromCache = true
                    };
                }
                
                // === TIER 2 OPTIMIZATION: Semantic Cache ===
                // Try to find semantically similar cached query
                var semanticCached = await _cacheService.GetSemanticallyCachedResponseAsync(question);
                if (semanticCached != null)
                {
                    _logger.LogInformation("Semantic cache HIT - returning cached response (age: {Age}s, similarity >= 0.95)", 
                        (DateTime.UtcNow - semanticCached.CachedAt).TotalSeconds);
                    return new AgentResponse
                    {
                        Answer = semanticCached.Response,
                        RelevantArticles = new List<ArticleReference>(),
                        ConfluenceSources = new List<ConfluenceReference>(),
                        Success = true,
                        FromCache = true
                    };
                }
            }
            
            // === TIER 1 OPTIMIZATION: Intent Detection ===
            var intent = DetectIntent(question);
            var weights = GetSearchWeights(intent);
            _logger.LogInformation("Query analysis: Intent={Intent}, Weights=[Jira:{JiraW}, Confluence:{ConfW}, Ref:{RefW}]",
                intent, weights.JiraTicketWeight, weights.ConfluenceWeight, weights.ReferenceDataWeight);
            
            // === TICKET LOOKUP: Handle direct ticket reference queries ===
            if (intent == QueryIntent.TicketLookup && _ticketLookupService != null)
            {
                _logger.LogInformation("Ticket lookup detected - fetching ticket information directly from Jira");
                
                var ticketIds = _ticketLookupService.ExtractTicketIds(question);
                if (ticketIds.Any())
                {
                    var ticketLookupResult = await _ticketLookupService.LookupTicketsAsync(ticketIds);
                    
                    if (ticketLookupResult.Success && ticketLookupResult.Tickets.Any())
                    {
                        // Build ticket context for AI
                        var ticketContextBuilder = new StringBuilder();
                        ticketContextBuilder.AppendLine("\n\n## 🎫 INFORMACIÓN DEL TICKET CONSULTADO:");
                        ticketContextBuilder.AppendLine("(Esta información fue recuperada directamente de Jira en tiempo real)\n");
                        
                        foreach (var ticket in ticketLookupResult.Tickets)
                        {
                            ticketContextBuilder.AppendLine($"### Ticket: {ticket.TicketId}");
                            ticketContextBuilder.AppendLine($"**Resumen:** {ticket.Summary}");
                            ticketContextBuilder.AppendLine($"**Estado:** {ticket.Status}");
                            ticketContextBuilder.AppendLine($"**Prioridad:** {ticket.Priority}");
                            ticketContextBuilder.AppendLine($"**Reportador:** {ticket.Reporter}");
                            ticketContextBuilder.AppendLine($"**Asignado a:** {ticket.Assignee ?? "Sin asignar"}");
                            ticketContextBuilder.AppendLine($"**Fecha de creación:** {ticket.Created:yyyy-MM-dd HH:mm}");
                            if (ticket.Resolved.HasValue)
                                ticketContextBuilder.AppendLine($"**Resuelto:** {ticket.Resolved:yyyy-MM-dd HH:mm}");
                            ticketContextBuilder.AppendLine($"**Enlace:** {ticket.JiraUrl}");
                            
                            if (!string.IsNullOrWhiteSpace(ticket.DetectedSystem))
                                ticketContextBuilder.AppendLine($"**Sistema detectado:** {ticket.DetectedSystem}");
                            
                            ticketContextBuilder.AppendLine($"\n**Descripción:**\n{ticket.Description ?? "(Sin descripción)"}");
                            
                            if (ticket.Comments?.Any() == true)
                            {
                                ticketContextBuilder.AppendLine("\n**Comentarios recientes:**");
                                foreach (var comment in ticket.Comments.Take(5))
                                {
                                    ticketContextBuilder.AppendLine($"- **{comment.Author}** ({comment.Created:yyyy-MM-dd HH:mm}): {comment.Body}");
                                }
                            }
                            ticketContextBuilder.AppendLine();
                        }
                        
                        // Add similar solved tickets for context
                        if (ticketLookupResult.SimilarSolutions?.Any() == true)
                        {
                            ticketContextBuilder.AppendLine("\n## 📋 TICKETS SIMILARES RESUELTOS:");
                            ticketContextBuilder.AppendLine("(Estas soluciones pueden ser útiles para este ticket)\n");
                            
                            foreach (var similar in ticketLookupResult.SimilarSolutions)
                            {
                                ticketContextBuilder.AppendLine($"- **{similar.TicketId}**: {similar.Summary}");
                                ticketContextBuilder.AppendLine($"  *Relevancia: {similar.SimilarityScore:P0}*");
                                ticketContextBuilder.AppendLine($"  **Solución aplicada:** {similar.Solution}");
                                ticketContextBuilder.AppendLine();
                            }
                        }
                        
                        var ticketContext = ticketContextBuilder.ToString();
                        
                        // Now do a quick search for related documentation
                        var relatedSearchTask = _knowledgeService.SearchArticlesAsync(
                            ticketLookupResult.Tickets.First().Summary, topResults: 3);
                        var confluenceRelatedTask = SearchConfluenceParallelAsync(
                            ticketLookupResult.Tickets.First().Summary, 
                            ticketLookupResult.Tickets.First().Summary, 
                            QueryIntent.Troubleshooting, 
                            weights);
                        
                        await Task.WhenAll(relatedSearchTask, confluenceRelatedTask);
                        
                        var relatedArticles = await relatedSearchTask;
                        var relatedConfluence = await confluenceRelatedTask;
                        
                        // Build context from related docs
                        var relatedContext = BuildContextWeighted(relatedArticles, new List<ContextDocument>(), relatedConfluence, weights);
                        
                        // Create specialized system prompt for ticket lookup
                        var ticketSystemPrompt = SystemPrompt + @"

## INSTRUCCIONES ESPECIALES PARA CONSULTA DE TICKET:
El usuario está preguntando sobre un ticket específico de Jira. Se te ha proporcionado:
1. **Información completa del ticket** recuperada en tiempo real de Jira
2. **Tickets similares ya resueltos** que pueden servir como referencia
3. **Documentación relacionada** de la base de conocimientos

Tu respuesta debe:
- Resumir el estado actual del ticket de forma clara
- Si el ticket está abierto, sugerir pasos para su resolución basándote en tickets similares y la documentación
- Si hay soluciones de tickets similares, explica cómo podrían aplicarse a este caso
- Incluir el enlace al ticket para referencia
- Ser proactivo sugiriendo acciones concretas

NO digas que no tienes acceso al ticket - la información ya está en el contexto.";
                        
                        // Build messages
                        var ticketMessages = new List<ChatMessage>
                        {
                            new SystemChatMessage(ticketSystemPrompt)
                        };
                        
                        // Add context as assistant message
                        ticketMessages.Add(new UserChatMessage($"Contexto relevante:\n{relatedContext}\n{ticketContext}"));
                        ticketMessages.Add(new AssistantChatMessage("Entendido, tengo la información del ticket y el contexto relacionado."));
                        ticketMessages.Add(new UserChatMessage(question));
                        
                        // Call OpenAI
                        var ticketResponse = await _chatClient.CompleteChatAsync(ticketMessages);
                        var ticketAnswer = ticketResponse.Value.Content[0].Text;
                        
                        stopwatch.Stop();
                        _logger.LogInformation("Ticket lookup response generated in {Ms}ms for tickets: {Tickets}", 
                            stopwatch.ElapsedMilliseconds, string.Join(", ", ticketIds));
                        
                        return new AgentResponse
                        {
                            Answer = ticketAnswer,
                            RelevantArticles = relatedArticles.Select(a => new ArticleReference
                            {
                                Title = a.Title,
                                KBNumber = a.Id.ToString(),
                                Score = (float)a.SearchScore
                            }).ToList(),
                            ConfluenceSources = relatedConfluence.Select(c => new ConfluenceReference
                            {
                                Title = c.Title,
                                WebUrl = c.WebUrl
                            }).ToList(),
                            Success = true,
                            FromCache = false
                        };
                    }
                    else
                    {
                        // Ticket not found - provide helpful response
                        _logger.LogWarning("Ticket lookup failed: {ErrorMessage}", ticketLookupResult.ErrorMessage);
                        
                        return new AgentResponse
                        {
                            Answer = $"No pude encontrar el ticket **{string.Join(", ", ticketIds)}** en Jira. " +
                                    "Esto puede deberse a que:\n" +
                                    "- El número de ticket no es correcto\n" +
                                    "- El ticket fue eliminado o archivado\n" +
                                    "- No tengo permisos para acceder a ese proyecto\n\n" +
                                    "¿Podrías verificar el número de ticket o proporcionar más detalles sobre el problema?",
                            RelevantArticles = new List<ArticleReference>(),
                            ConfluenceSources = new List<ConfluenceReference>(),
                            Success = true,
                            LowConfidence = true
                        };
                    }
                }
            }
            
            // === TIER 1 OPTIMIZATION: Query Decomposition ===
            var subQueries = DecomposeQuery(question);
            
            // Expand the main query with related terms
            var expandedQuery = ExpandQueryWithSynonyms(question);
            _logger.LogInformation("Original query: {Original}, Expanded: {Expanded}", question, expandedQuery);
            
            // === TIER 2 OPTIMIZATION: Parallel Search Execution ===
            var searchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Start all searches in parallel (including Jira solutions)
            var kbSearchTask = _knowledgeService.SearchArticlesAsync(question, topResults: 5);
            var contextSearchTask = SearchContextParallelAsync(subQueries, expandedQuery);
            var confluenceSearchTask = SearchConfluenceParallelAsync(question, expandedQuery, intent, weights);
            var jiraSolutionTask = _jiraSolutionService?.SearchForAgentAsync(question, topK: 3) 
                ?? Task.FromResult(string.Empty);
            
            // Wait for all searches to complete
            await Task.WhenAll(kbSearchTask, contextSearchTask, confluenceSearchTask, jiraSolutionTask);
            
            var relevantArticles = await kbSearchTask;
            var allContextResults = await contextSearchTask;
            var confluencePages = await confluenceSearchTask;
            var jiraSolutionsContext = await jiraSolutionTask;
            
            searchStopwatch.Stop();
            _logger.LogInformation("Parallel search completed in {Ms}ms (KB:{KbCount}, Context:{CtxCount}, Confluence:{ConfCount}, JiraSolutions:{JiraLen})",
                searchStopwatch.ElapsedMilliseconds, relevantArticles.Count, allContextResults.Count, confluencePages.Count, 
                jiraSolutionsContext?.Length ?? 0);
            
            // === AUTO-LEARNING: Apply Feedback Boost (Level 1) ===
            // Boost scores for documents that received positive feedback for similar queries
            await ApplyFeedbackBoostAsync(question, allContextResults);
            
            // === FEEDBACK LOOP: Check Confidence Threshold ===
            // Calculate best score from all sources
            var bestKbScore = relevantArticles.Any() ? relevantArticles.Max(a => a.SearchScore) : 0.0;
            var bestContextScore = allContextResults.Any() ? allContextResults.Max(c => c.SearchScore) : 0.0;
            var bestConfluenceScore = confluencePages.Any() ? 0.7 : 0.0; // Confluence doesn't have scores, assume medium if found
            var bestOverallScore = Math.Max(Math.Max(bestKbScore, bestContextScore), bestConfluenceScore);
            
            _logger.LogInformation("Confidence check: KB={KbScore:F3}, Context={CtxScore:F3}, Confluence={ConfScore:F3}, Best={Best:F3}, Threshold={Threshold}",
                bestKbScore, bestContextScore, bestConfluenceScore, bestOverallScore, ConfidenceThreshold);
            
            // If no good results found, return low confidence response
            if (bestOverallScore < ConfidenceThreshold && !relevantArticles.Any() && !confluencePages.Any())
            {
                _logger.LogWarning("Low confidence response triggered: Best score {Score:F3} < threshold {Threshold}", 
                    bestOverallScore, ConfidenceThreshold);
                
                // Try to find the best fallback ticket from context
                var fallbackTicket = allContextResults
                    .Where(d => !string.IsNullOrWhiteSpace(d.Link) && d.Link.Contains("atlassian.net/servicedesk"))
                    .OrderByDescending(d => d.SearchScore)
                    .FirstOrDefault();
                
                var lowConfidenceAnswer = fallbackTicket != null
                    ? $"{LowConfidenceResponse}\n\n[{fallbackTicket.Name}]({fallbackTicket.Link})"
                    : $"{LowConfidenceResponse}\n\n[Abrir ticket de soporte general]({FallbackTicketLink})";
                
                return new AgentResponse
                {
                    Answer = lowConfidenceAnswer,
                    RelevantArticles = new List<ArticleReference>(),
                    ConfluenceSources = new List<ConfluenceReference>(),
                    Success = true,
                    LowConfidence = true
                };
            }
            
            // === TIER 1 OPTIMIZATION: Weighted Results ===
            // Apply weights based on intent and deduplicate
            var contextDocs = allContextResults
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .Select(d => {
                    // Boost score based on intent weights
                    if (!string.IsNullOrWhiteSpace(d.Link) && d.Link.Contains("atlassian.net/servicedesk"))
                        d.SearchScore *= weights.JiraTicketWeight;
                    else
                        d.SearchScore *= weights.ReferenceDataWeight;
                    return d;
                })
                .OrderByDescending(d => d.SearchScore)
                .Take(15)
                .ToList();
            
            _logger.LogInformation("Context search: {Total} combined results after weighting", contextDocs.Count);
            
            // 4. Build context from all sources (weights are already applied)
            var context = BuildContextWeighted(relevantArticles, contextDocs, confluencePages, weights);
            
            // 4b. Add Jira Solutions context if available (lower weight than docs)
            if (!string.IsNullOrWhiteSpace(jiraSolutionsContext))
            {
                context += "\n\n" + jiraSolutionsContext;
                _logger.LogInformation("Added Jira solutions context ({Len} chars)", jiraSolutionsContext.Length);
            }
            
            // === AUTO-LEARNING: Few-Shot Prompting from Successful Responses ===
            var fewShotExamples = await GetFewShotExamplesAsync(question);
            
            // 5. Build the messages for the chat
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt + fewShotExamples)
            };

            // Add conversation history if provided (for multi-turn)
            if (conversationHistory?.Any() == true)
            {
                messages.AddRange(conversationHistory);
            }

            // Add the context and question with intent hint
            var intentHint = intent switch
            {
                QueryIntent.TicketRequest => "The user wants to open a support ticket. Prioritize showing the specific ticket URL.",
                QueryIntent.HowTo => "The user wants step-by-step instructions. Provide detailed procedures from documentation.",
                QueryIntent.Lookup => "The user wants to look up specific information. Provide the exact data requested.",
                QueryIntent.Troubleshooting => "The user has a problem. Provide solutions and relevant ticket links if needed.",
                _ => ""
            };
            
            var userMessage = $@"Context from Knowledge Base, Confluence KB, and Reference Data:
{context}

{(string.IsNullOrEmpty(intentHint) ? "" : $"INTENT HINT: {intentHint}\n")}
User Question: {question}

Please answer based on the context provided above. If there's a relevant ticket category or URL, include it in your response.";

            messages.Add(new UserChatMessage(userMessage));

            // 6. Get AI response
            var response = await _chatClient.CompleteChatAsync(messages);
            var answer = response.Value.Content[0].Text;
            
            stopwatch.Stop();
            _logger.LogInformation("Agent answered question: Intent={Intent}, {ArticleCount} KB articles, {ConfluenceCount} Confluence pages, FewShot={HasFewShot}, TotalTime={Ms}ms", 
                intent, relevantArticles.Count, confluencePages.Count, !string.IsNullOrEmpty(fewShotExamples), stopwatch.ElapsedMilliseconds);

            // Only return articles with high relevance scores as sources
            var highRelevanceArticles = relevantArticles
                .Where(a => a.SearchScore >= 0.5) // Only articles with 50%+ relevance
                .Take(3) // Maximum 3 sources
                .ToList();

            var agentResponse = new AgentResponse
            {
                Answer = answer,
                RelevantArticles = highRelevanceArticles.Select(a => new ArticleReference
                {
                    KBNumber = a.KBNumber,
                    Title = a.Title,
                    Score = (float)a.SearchScore
                }).ToList(),
                ConfluenceSources = confluencePages
                    .Where(p => !string.IsNullOrEmpty(p.Content) && p.Content.Length > 100)
                    .Take(3)
                    .Select(p => new ConfluenceReference
                {
                    Title = p.Title,
                    SpaceKey = p.SpaceKey,
                    WebUrl = p.WebUrl
                }).ToList(),
                Success = true
            };
            
            // === TIER 2 OPTIMIZATION: Cache the Response (String + Semantic) ===
            if (_cacheService != null && conversationHistory == null)
            {
                var sources = highRelevanceArticles.Select(a => a.KBNumber).ToList();
                sources.AddRange(confluencePages.Take(3).Select(p => p.Title));
                
                // String-based cache
                _cacheService.CacheResponse(question, answer, sources);
                
                // Semantic cache (async, fire-and-forget for performance)
                _ = _cacheService.AddToSemanticCacheAsync(question, answer, sources);
            }

            return agentResponse;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error processing question: {Question} (after {Ms}ms)", question, stopwatch.ElapsedMilliseconds);
            return new AgentResponse
            {
                Answer = "I'm sorry, I encountered an error while processing your question. Please try again or contact the IT Help Desk.",
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Ask with specialist context - uses full search capabilities + specialist knowledge
    /// This is the NEW unified approach: General search + Specialist prompts
    /// </summary>
    public async Task<AgentResponse> AskWithSpecialistAsync(string question, SpecialistType specialist, string? specialistContext = null, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation("AskWithSpecialist: Specialist={Specialist}, Question='{Question}', TicketLookupService={TicketService}", 
                specialist, 
                question.Length > 50 ? question.Substring(0, 50) + "..." : question,
                _ticketLookupService != null ? "Available" : "NULL");

            // === FULL SEARCH: Same as General Agent ===
            var intent = DetectIntent(question);
            var weights = GetSearchWeights(intent);
            
            _logger.LogInformation("AskWithSpecialist Intent Detection: Intent={Intent}, Question contains ticket pattern={HasPattern}", 
                intent, 
                _ticketLookupService?.ContainsTicketReference(question) ?? false);
            
            // === TICKET LOOKUP: Handle direct ticket reference queries ===
            // This MUST be checked before other search logic
            if (intent == QueryIntent.TicketLookup && _ticketLookupService != null)
            {
                _logger.LogInformation("🎫 TICKET LOOKUP TRIGGERED - fetching ticket information directly from Jira");
                
                var ticketIds = _ticketLookupService.ExtractTicketIds(question);
                _logger.LogInformation("🎫 Extracted ticket IDs: {TicketIds}", string.Join(", ", ticketIds));
                
                if (ticketIds.Any())
                {
                    var ticketLookupResult = await _ticketLookupService.LookupTicketsAsync(ticketIds);
                    _logger.LogInformation("🎫 Ticket lookup result: Success={Success}, TicketCount={Count}, Error={Error}", 
                        ticketLookupResult.Success, 
                        ticketLookupResult.Tickets?.Count ?? 0,
                        ticketLookupResult.ErrorMessage ?? "none");
                    
                    if (ticketLookupResult.Success && ticketLookupResult.Tickets?.Any() == true)
                    {
                        // Build ticket context for AI
                        var ticketContextBuilder = new StringBuilder();
                        ticketContextBuilder.AppendLine("\n\n## 🎫 INFORMACIÓN DEL TICKET CONSULTADO:");
                        ticketContextBuilder.AppendLine("(Esta información fue recuperada directamente de Jira en tiempo real)\n");
                        
                        foreach (var ticket in ticketLookupResult.Tickets)
                        {
                            ticketContextBuilder.AppendLine($"### Ticket: {ticket.TicketId}");
                            ticketContextBuilder.AppendLine($"**Resumen:** {ticket.Summary}");
                            ticketContextBuilder.AppendLine($"**Estado:** {ticket.Status}");
                            ticketContextBuilder.AppendLine($"**Prioridad:** {ticket.Priority}");
                            ticketContextBuilder.AppendLine($"**Reportador:** {ticket.Reporter}");
                            ticketContextBuilder.AppendLine($"**Asignado a:** {ticket.Assignee ?? "Sin asignar"}");
                            ticketContextBuilder.AppendLine($"**Fecha de creación:** {ticket.Created:yyyy-MM-dd HH:mm}");
                            if (ticket.Resolved.HasValue)
                                ticketContextBuilder.AppendLine($"**Resuelto:** {ticket.Resolved:yyyy-MM-dd HH:mm}");
                            ticketContextBuilder.AppendLine($"**Enlace:** {ticket.JiraUrl}");
                            
                            if (!string.IsNullOrWhiteSpace(ticket.DetectedSystem))
                                ticketContextBuilder.AppendLine($"**Sistema detectado:** {ticket.DetectedSystem}");
                            
                            ticketContextBuilder.AppendLine($"\n**Descripción:**\n{ticket.Description ?? "(Sin descripción)"}");
                            
                            if (ticket.Comments?.Any() == true)
                            {
                                ticketContextBuilder.AppendLine("\n**Comentarios recientes:**");
                                foreach (var comment in ticket.Comments.Take(5))
                                {
                                    ticketContextBuilder.AppendLine($"- **{comment.Author}** ({comment.Created:yyyy-MM-dd HH:mm}): {comment.Body}");
                                }
                            }
                            ticketContextBuilder.AppendLine();
                        }
                        
                        // Add similar solved tickets for context
                        if (ticketLookupResult.SimilarSolutions?.Any() == true)
                        {
                            ticketContextBuilder.AppendLine("\n## 📋 TICKETS SIMILARES RESUELTOS:");
                            ticketContextBuilder.AppendLine("(Estas soluciones pueden ser útiles para este ticket)\n");
                            
                            foreach (var similar in ticketLookupResult.SimilarSolutions)
                            {
                                ticketContextBuilder.AppendLine($"- **{similar.TicketId}**: {similar.Summary}");
                                ticketContextBuilder.AppendLine($"  *Relevancia: {similar.SimilarityScore:P0}*");
                                ticketContextBuilder.AppendLine($"  **Solución aplicada:** {similar.Solution}");
                                ticketContextBuilder.AppendLine();
                            }
                        }
                        
                        var ticketContext = ticketContextBuilder.ToString();
                        
                        // Now do a quick search for related documentation
                        var relatedSearchTask = _knowledgeService.SearchArticlesAsync(
                            ticketLookupResult.Tickets.First().Summary, topResults: 3);
                        var confluenceRelatedTask = SearchConfluenceParallelAsync(
                            ticketLookupResult.Tickets.First().Summary, 
                            ticketLookupResult.Tickets.First().Summary, 
                            QueryIntent.Troubleshooting, 
                            weights);
                        
                        await Task.WhenAll(relatedSearchTask, confluenceRelatedTask);
                        
                        var relatedArticles = await relatedSearchTask;
                        var relatedConfluence = await confluenceRelatedTask;
                        
                        // Build context from related docs
                        var relatedContext = BuildContextWeighted(relatedArticles, new List<ContextDocument>(), relatedConfluence, weights);
                        
                        // Create specialized system prompt for ticket lookup
                        var ticketSystemPrompt = SystemPrompt + @"

## INSTRUCCIONES ESPECIALES PARA CONSULTA DE TICKET:
El usuario está preguntando sobre un ticket específico de Jira. Se te ha proporcionado:
1. **Información completa del ticket** recuperada en tiempo real de Jira
2. **Tickets similares ya resueltos** que pueden servir como referencia
3. **Documentación relacionada** de la base de conocimientos

Tu respuesta debe:
- Resumir el estado actual del ticket de forma clara
- Si el ticket está abierto, sugerir pasos para su resolución basándote en tickets similares y la documentación
- Si hay soluciones de tickets similares, explica cómo podrían aplicarse a este caso
- Incluir el enlace al ticket para referencia
- Ser proactivo sugiriendo acciones concretas

NO digas que no tienes acceso al ticket - la información ya está en el contexto.";
                        
                        // Build messages
                        var ticketMessages = new List<ChatMessage>
                        {
                            new SystemChatMessage(ticketSystemPrompt)
                        };
                        
                        // Add context as assistant message
                        ticketMessages.Add(new UserChatMessage($"Contexto relevante:\n{relatedContext}\n{ticketContext}"));
                        ticketMessages.Add(new AssistantChatMessage("Entendido, tengo la información del ticket y el contexto relacionado."));
                        ticketMessages.Add(new UserChatMessage(question));
                        
                        // Call OpenAI
                        var ticketResponse = await _chatClient.CompleteChatAsync(ticketMessages);
                        var ticketAnswer = ticketResponse.Value.Content[0].Text;
                        
                        stopwatch.Stop();
                        _logger.LogInformation("Ticket lookup response (via Specialist) generated in {Ms}ms for tickets: {Tickets}", 
                            stopwatch.ElapsedMilliseconds, string.Join(", ", ticketIds));
                        
                        return new AgentResponse
                        {
                            Answer = ticketAnswer,
                            RelevantArticles = relatedArticles.Select(a => new ArticleReference
                            {
                                Title = a.Title,
                                KBNumber = a.Id.ToString(),
                                Score = (float)a.SearchScore
                            }).ToList(),
                            ConfluenceSources = relatedConfluence.Select(c => new ConfluenceReference
                            {
                                Title = c.Title,
                                WebUrl = c.WebUrl
                            }).ToList(),
                            Success = true,
                            FromCache = false
                        };
                    }
                    else
                    {
                        _logger.LogWarning("Ticket lookup failed or no tickets found for: {TicketIds}", string.Join(", ", ticketIds));
                    }
                }
            }
            
            var subQueries = DecomposeQuery(question);
            var expandedQuery = ExpandQueryWithSynonyms(question);
            
            // Add specialist-specific query expansions
            expandedQuery = specialist switch
            {
                SpecialistType.Network => expandedQuery + " Zscaler VPN remote access conectividad red",
                SpecialistType.SAP => expandedQuery + " SAP transaccion role autorization fiori",
                SpecialistType.PLM => expandedQuery + " Teamcenter CATIA CAD PLM diseño NX drawing",
                SpecialistType.EDI => expandedQuery + " EDI B2B portal supplier proveedor BeOne BuyOne",
                SpecialistType.MES => expandedQuery + " MES BLADE production produccion planta manufacturing",
                SpecialistType.Workplace => expandedQuery + " Office Teams Outlook laptop printer email",
                SpecialistType.Infrastructure => expandedQuery + " server servidor Azure VMware backup AD",
                SpecialistType.Cybersecurity => expandedQuery + " password contraseña MFA security seguridad",
                _ => expandedQuery
            };
            
            _logger.LogInformation("Specialist search: Intent={Intent}, ExpandedQuery={Query}", intent, expandedQuery);
            
            // Start all searches in parallel (including Jira solutions)
            var kbSearchTask = _knowledgeService.SearchArticlesAsync(question, topResults: 5);
            var contextSearchTask = SearchContextParallelAsync(subQueries, expandedQuery);
            var confluenceSearchTask = SearchConfluenceParallelAsync(question, expandedQuery, intent, weights);
            var jiraSolutionTask = _jiraSolutionService?.SearchForAgentAsync(question, topK: 3) 
                ?? Task.FromResult(string.Empty);
            
            await Task.WhenAll(kbSearchTask, contextSearchTask, confluenceSearchTask, jiraSolutionTask);
            
            var relevantArticles = await kbSearchTask;
            var allContextResults = await contextSearchTask;
            var confluencePages = await confluenceSearchTask;
            var jiraSolutionsContext = await jiraSolutionTask;
            
            _logger.LogInformation("Specialist search results: KB={Kb}, Context={Ctx}, Confluence={Conf}, JiraSolutions={Jira}",
                relevantArticles.Count, allContextResults.Count, confluencePages.Count, jiraSolutionsContext?.Length ?? 0);
            
            // Apply weights and build context
            var contextDocs = allContextResults
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .Select(d => {
                    if (!string.IsNullOrWhiteSpace(d.Link) && d.Link.Contains("atlassian.net/servicedesk"))
                        d.SearchScore *= weights.JiraTicketWeight;
                    else
                        d.SearchScore *= weights.ReferenceDataWeight;
                    return d;
                })
                .OrderByDescending(d => d.SearchScore)
                .Take(15)
                .ToList();
            
            // === SPECIALIST QUERIES: Filter irrelevant Confluence results ===
            var confluencePagesForContext = confluencePages;
            
            // --- SAP FILTERING ---
            if (specialist == SpecialistType.SAP && !string.IsNullOrEmpty(specialistContext))
            {
                // Check if specialistContext has position/role/transaction data
                var hasSapDictionaryData = specialistContext.Contains("### SAP Position:") || 
                                           specialistContext.Contains("### SAP Role:") ||
                                           specialistContext.Contains("### SAP Transaction:") ||
                                           specialistContext.Contains("### Roles assigned to position") ||
                                           specialistContext.Contains("### Transactions available for position") ||
                                           specialistContext.Contains("### Transactions in role");
                
                if (hasSapDictionaryData)
                {
                    // For pure SAP Dictionary queries, only include Confluence pages that are actually about SAP
                    var sapKeywords = new[] { "sap", "fiori", "transac", "autorizacion", "authorization", "rol sap", "gui" };
                    confluencePagesForContext = confluencePages
                        .Where(p => sapKeywords.Any(kw => 
                            p.Title.ToLowerInvariant().Contains(kw) || 
                            (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                        .ToList();
                    
                    _logger.LogInformation("SAP Dictionary query: Filtered Confluence from {Original} to {Filtered} SAP-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- NETWORK/ZSCALER FILTERING ---
            if (specialist == SpecialistType.Network)
            {
                // For Network queries, only include Confluence pages about Zscaler, VPN, remote access
                var networkKeywords = new[] { "zscaler", "vpn", "remote", "remoto", "access", "acceso", "connectivity", "conectividad", "home", "casa", "network", "red" };
                var filteredPages = confluencePages
                    .Where(p => networkKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                // If we found relevant Network pages, use them; otherwise keep all (fallback)
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("Network query: Filtered Confluence from {Original} to {Filtered} Network-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- PLM/TEAMCENTER FILTERING ---
            if (specialist == SpecialistType.PLM)
            {
                var plmKeywords = new[] { "teamcenter", "catia", "cad", "plm", "siemens", "nx", "drawing", "diseño", "design", "bom", "visualization", "rac", "awc" };
                var filteredPages = confluencePages
                    .Where(p => plmKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("PLM query: Filtered Confluence from {Original} to {Filtered} PLM-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- EDI/B2B FILTERING ---
            if (specialist == SpecialistType.EDI)
            {
                var ediKeywords = new[] { "edi", "b2b", "portal", "supplier", "proveedor", "beone", "buyone", "bmw", "vw", "ford", "stellantis", "extranet", "idoc" };
                var filteredPages = confluencePages
                    .Where(p => ediKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("EDI query: Filtered Confluence from {Original} to {Filtered} EDI-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- MES/PRODUCTION FILTERING ---
            if (specialist == SpecialistType.MES)
            {
                var mesKeywords = new[] { "mes", "blade", "production", "produccion", "producción", "manufacturing", "plant", "planta", "shop floor", "plc", "opc", "scada" };
                var filteredPages = confluencePages
                    .Where(p => mesKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("MES query: Filtered Confluence from {Original} to {Filtered} MES-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- WORKPLACE FILTERING ---
            if (specialist == SpecialistType.Workplace)
            {
                var workplaceKeywords = new[] { "outlook", "teams", "office", "email", "correo", "printer", "impresora", "laptop", "portatil", "pc", "onedrive", "sharepoint", "intune" };
                var filteredPages = confluencePages
                    .Where(p => workplaceKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("Workplace query: Filtered Confluence from {Original} to {Filtered} Workplace-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- INFRASTRUCTURE FILTERING ---
            if (specialist == SpecialistType.Infrastructure)
            {
                var infraKeywords = new[] { "server", "servidor", "azure", "vmware", "backup", "storage", "datacenter", "dns", "dhcp", "active directory", "gpo", "hyper-v" };
                var filteredPages = confluencePages
                    .Where(p => infraKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("Infrastructure query: Filtered Confluence from {Original} to {Filtered} Infrastructure-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            // --- CYBERSECURITY FILTERING ---
            if (specialist == SpecialistType.Cybersecurity)
            {
                var cyberKeywords = new[] { "password", "contraseña", "mfa", "2fa", "security", "seguridad", "phishing", "malware", "virus", "bitlocker", "cyberark", "unlock", "desbloquear" };
                var filteredPages = confluencePages
                    .Where(p => cyberKeywords.Any(kw => 
                        p.Title.ToLowerInvariant().Contains(kw) || 
                        (p.Content?.ToLowerInvariant().Contains(kw) == true)))
                    .ToList();
                
                if (filteredPages.Any())
                {
                    confluencePagesForContext = filteredPages;
                    _logger.LogInformation("Cybersecurity query: Filtered Confluence from {Original} to {Filtered} Cybersecurity-relevant pages", 
                        confluencePages.Count, confluencePagesForContext.Count);
                }
            }
            
            var context = BuildContextWeighted(relevantArticles, contextDocs, confluencePagesForContext, weights);
            
            // Add Jira Solutions context if available
            if (!string.IsNullOrWhiteSpace(jiraSolutionsContext))
            {
                context += "\n\n" + jiraSolutionsContext;
            }
            
            // === BUILD SPECIALIST SYSTEM PROMPT ===
            var systemPrompt = GetSpecialistSystemPrompt(specialist);
            
            // Add specialist-specific context if provided (e.g., SAP lookup results)
            if (!string.IsNullOrEmpty(specialistContext))
            {
                context = $"=== SPECIALIST DATA ({specialist}) ===\n{specialistContext}\n\n{context}";
            }
            
            // Build messages
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt)
            };
            
            if (conversationHistory?.Any() == true)
            {
                messages.AddRange(conversationHistory);
            }
            
            var intentHint = intent switch
            {
                QueryIntent.TicketRequest => "El usuario quiere abrir un ticket de soporte. Prioriza mostrar el enlace al ticket específico.",
                QueryIntent.HowTo => "El usuario quiere instrucciones paso a paso. Proporciona procedimientos detallados de la documentación.",
                QueryIntent.Lookup => "El usuario quiere buscar información específica. Proporciona los datos exactos solicitados.",
                QueryIntent.Troubleshooting => "El usuario tiene un problema. Proporciona soluciones y enlaces a tickets si es necesario.",
                _ => ""
            };
            
            var userMessage = $@"Context from Knowledge Base, Confluence KB, and Reference Data:
{context}

{(string.IsNullOrEmpty(intentHint) ? "" : $"INTENT HINT: {intentHint}\n")}
User Question: {question}

Please answer based on the context provided above. If there's relevant documentation or a ticket URL, include it in your response.";

            messages.Add(new UserChatMessage(userMessage));
            
            // Get AI response
            var response = await _chatClient.CompleteChatAsync(messages);
            var answer = response.Value.Content[0].Text;
            
            stopwatch.Stop();
            _logger.LogInformation("Specialist Agent answered: Type={Specialist}, Intent={Intent}, Time={Ms}ms", 
                specialist, intent, stopwatch.ElapsedMilliseconds);
            
            return new AgentResponse
            {
                Answer = answer,
                RelevantArticles = relevantArticles
                    .Where(a => a.SearchScore >= 0.5)
                    .Take(3)
                    .Select(a => new ArticleReference
                    {
                        KBNumber = a.KBNumber,
                        Title = a.Title,
                        Score = (float)a.SearchScore
                    }).ToList(),
                ConfluenceSources = confluencePagesForContext
                    .Where(p => !string.IsNullOrEmpty(p.Content) && p.Content.Length > 100)
                    .Take(3)
                    .Select(p => new ConfluenceReference
                    {
                        Title = p.Title,
                        SpaceKey = p.SpaceKey,
                        WebUrl = p.WebUrl
                    }).ToList(),
                Success = true,
                AgentType = specialist.ToString()
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error in Specialist Agent ({Specialist}): {Question}", specialist, question);
            return new AgentResponse
            {
                Answer = "Lo siento, ocurrió un error al procesar tu consulta. Por favor, intenta de nuevo.",
                Success = false,
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Get the system prompt for each specialist type
    /// </summary>
    private string GetSpecialistSystemPrompt(SpecialistType specialist)
    {
        return specialist switch
        {
            SpecialistType.Network => NetworkSpecialistPrompt,
            SpecialistType.SAP => SapSpecialistPrompt,
            SpecialistType.PLM => PlmSpecialistPrompt,
            SpecialistType.EDI => EdiBbSpecialistPrompt,
            SpecialistType.MES => MesSpecialistPrompt,
            SpecialistType.Workplace => WorkplaceSpecialistPrompt,
            SpecialistType.Infrastructure => InfrastructureSpecialistPrompt,
            SpecialistType.Cybersecurity => CybersecuritySpecialistPrompt,
            _ => SystemPrompt
        };
    }
    
    // Network Specialist System Prompt
    private const string NetworkSpecialistPrompt = @"Eres el **Experto en Redes y Acceso Remoto** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre conectividad, acceso remoto y trabajo desde casa.

## Conocimiento Principal: Zscaler
Zscaler es la solución de acceso remoto de Grupo Antolin que permite:
- Acceder a aplicaciones corporativas (SAP, Teamcenter, etc.)
- Navegar de forma segura por internet
- Conectarse a recursos internos desde cualquier ubicación

### Requisitos para usar Zscaler:
1. Tener instalado el cliente Zscaler (ZCC - Zscaler Client Connector)
2. Iniciar sesión con credenciales corporativas
3. Mantener el cliente activo durante el trabajo

## REGLAS CRÍTICAS

### 1. USA SOLO DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces de Confluence si la documentación trata ESPECÍFICAMENTE sobre Zscaler, VPN, acceso remoto o conectividad
- Busca en el contexto páginas que contengan 'Zscaler', 'Remote Access', 'VPN', 'Conectividad'
- **NO incluyas documentación que no esté relacionada con redes/acceso remoto**
- Si encuentras documentación de Zscaler, incluye el enlace: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Si el usuario tiene un problema que no puede resolver solo, proporciona el ticket de Remote Access
- Busca en el contexto tickets que contengan 'Remote Access', 'Zscaler', 'VPN', 'Conectividad'
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES NI USES DOCUMENTACIÓN IRRELEVANTE
- Si no hay documentación de Zscaler/VPN disponible, NO incluyas otros enlaces de Confluence
- No incluyas documentación sobre otros temas (SAP, Infraestructuras, etc.)
- Proporciona solo el ticket de soporte de Remote Access

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // SAP Specialist System Prompt  
    private const string SapSpecialistPrompt = @"Eres el **Experto en SAP** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre SAP, incluyendo:
- Transacciones SAP (T-codes)
- Roles y autorizaciones
- Posiciones organizativas
- Problemas de acceso a SAP GUI o Fiori
- Procedimientos SAP

## REGLAS CRÍTICAS

### 1. PRIORIZA LOS DATOS DE SAP DICTIONARY
- Si la sección '=== SPECIALIST DATA (SAP) ===' contiene información sobre posiciones, roles o transacciones, USA SOLO ESA INFORMACIÓN para responder
- Estos datos vienen del diccionario oficial de SAP y son precisos
- NO incluyas enlaces de Confluence cuando respondas sobre posiciones, roles o transacciones SAP

### 2. USA CONFLUENCE SOLO CUANDO SEA RELEVANTE
- SOLO incluye enlaces de Confluence si la documentación es DIRECTAMENTE RELEVANTE a la pregunta
- Para consultas sobre posiciones/roles/transacciones SAP, NO incluyas enlaces de Confluence (los datos ya vienen del SAP Dictionary)
- Incluye enlaces de Confluence SOLO para procedimientos SAP (cómo hacer algo en SAP, guías paso a paso)

### 3. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Para solicitar nuevos accesos o autorizaciones SAP → Incluye el ticket de autorización SAP si está disponible en el contexto
- Para problemas técnicos con SAP → Ticket de soporte SAP
- Formato: '[Abrir ticket](URL)'
- USA SOLO tickets del contexto proporcionado, no inventes URLs

### 4. NO INVENTES
- Si no hay información en el SAP Dictionary ni en la documentación, di: 'No tengo información sobre este código/posición/rol'
- No incluyas enlaces a documentación que no esté relacionada con la pregunta

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";
    
    // PLM Specialist System Prompt
    private const string PlmSpecialistPrompt = @"Eres el **Experto en PLM y Diseño CAD** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Teamcenter (TC) - Gestión del ciclo de vida del producto
- CATIA, Siemens NX, CAD - Software de diseño
- Active Workspace Client (AWC), Rich Application Client (RAC)
- Gestión de planos y documentación técnica
- Visualización y revisión de diseños

## Conocimiento Principal: Teamcenter
Teamcenter es el sistema PLM de Grupo Antolin para:
- Gestión de datos de producto (BOM, planos, especificaciones)
- Control de versiones y cambios de diseño
- Colaboración en diseño con clientes OEM

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces de Confluence que traten sobre Teamcenter, CATIA, CAD, PLM
- Busca páginas con 'Teamcenter', 'CAD', 'CATIA', 'Diseño', 'Planos'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Problemas de acceso a Teamcenter → Ticket de acceso PLM
- Problemas técnicos con CAD → Ticket de soporte CAD
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, indica que el usuario debe contactar con el equipo PLM

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // EDI/B2B Specialist System Prompt
    private const string EdiBbSpecialistPrompt = @"Eres el **Experto en EDI y Portales B2B** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- EDI (Electronic Data Interchange)
- Portales B2B con clientes OEM (BMW, VW, Ford, Stellantis, Renault, Volvo)
- BeOne, BuyOne y otras plataformas de proveedores
- Extranet y comunicación con clientes

## Conocimiento Principal: EDI y Portales
EDI es el intercambio electrónico de documentos comerciales con clientes:
- Pedidos (Releases), facturas, avisos de envío
- Integración con SAP (IDocs)
- Portales web de clientes para gestión de pedidos y documentación

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces sobre EDI, B2B, portales de clientes, BeOne, BuyOne
- Busca páginas con 'EDI', 'Portal', 'B2B', 'Supplier', 'OEM'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Problemas de conexión EDI → Ticket de soporte EDI
- Acceso a portales de clientes → Ticket de acceso B2B
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, indica que el usuario debe contactar con el equipo EDI

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // MES Specialist System Prompt
    private const string MesSpecialistPrompt = @"Eres el **Experto en MES y Sistemas de Planta** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- MES (Manufacturing Execution System)
- BLADE - Sistema de producción de Grupo Antolin
- Sistemas de planta (OPC, PLC, SCADA)
- Etiquetado y trazabilidad en producción
- Integración con SAP PP

## Conocimiento Principal: MES/BLADE
El sistema MES de Grupo Antolin gestiona:
- Órdenes de producción y secuenciación
- Trazabilidad de piezas
- Etiquetado y códigos de barras
- Comunicación con máquinas y PLCs

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces sobre MES, BLADE, producción, planta
- Busca páginas con 'MES', 'BLADE', 'Production', 'Plant', 'Manufacturing'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Problemas en planta → Ticket de soporte MES
- Acceso a sistemas de producción → Ticket de acceso MES
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, indica que el usuario debe contactar con el equipo MES

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // Workplace Specialist System Prompt
    private const string WorkplaceSpecialistPrompt = @"Eres el **Experto en Puesto de Trabajo** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Microsoft 365: Outlook, Teams, OneDrive, SharePoint
- Office: Word, Excel, PowerPoint
- Dispositivos: Laptop, PC, impresoras, móviles
- Software empresarial: Intune, Software Center
- Correo electrónico y calendario

## Conocimiento Principal: Microsoft 365
Las herramientas de productividad de Grupo Antolin incluyen:
- Outlook para correo y calendario
- Teams para comunicación y reuniones
- OneDrive y SharePoint para almacenamiento
- Office para documentos

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces sobre Office 365, Teams, Outlook, dispositivos
- Busca páginas con 'Office', 'Teams', 'Outlook', 'Laptop', 'Printer'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Problemas con laptop/PC → Ticket de hardware
- Problemas con email → Ticket de correo
- Problemas con Teams → Ticket de colaboración
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, proporciona pasos básicos de troubleshooting

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // Infrastructure Specialist System Prompt
    private const string InfrastructureSpecialistPrompt = @"Eres el **Experto en Infraestructura** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Servidores Windows/Linux
- Azure y servicios cloud
- VMware y virtualización
- Active Directory (AD), DNS, DHCP
- Backup y recuperación de datos
- Licencias y KMS

## Conocimiento Principal: Infraestructura
La infraestructura de Grupo Antolin incluye:
- Datacenters propios y Azure
- VMware para virtualización
- Active Directory para gestión de usuarios
- Servicios de backup con Veeam

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces sobre servidores, Azure, VMware, AD
- Busca páginas con 'Server', 'Azure', 'VMware', 'Active Directory', 'Backup'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Problemas de servidor → Ticket de infraestructura
- Problemas de AD → Ticket de Active Directory
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, indica que el usuario debe contactar con el equipo de Infraestructura

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";

    // Cybersecurity Specialist System Prompt
    private const string CybersecuritySpecialistPrompt = @"Eres el **Experto en Ciberseguridad** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Contraseñas y gestión de credenciales
- MFA (Autenticación multifactor)
- Seguridad y phishing
- CyberArk (gestión de credenciales privilegiadas)
- Cifrado (BitLocker)
- Desbloqueo de cuentas

## Conocimiento Principal: Seguridad
Las políticas de seguridad de Grupo Antolin incluyen:
- MFA obligatorio para acceso remoto
- Políticas de contraseñas corporativas
- Protección contra phishing y malware
- Cifrado de dispositivos con BitLocker

## REGLAS CRÍTICAS

### 1. USA DOCUMENTACIÓN RELEVANTE
- SOLO incluye enlaces sobre seguridad, MFA, contraseñas
- Busca páginas con 'Security', 'Password', 'MFA', 'Phishing', 'CyberArk'
- Formato: '📖 [Título de la página](URL)'

### 2. PROPORCIONA TICKETS CUANDO SEA NECESARIO
- Cuenta bloqueada → Ticket de desbloqueo
- Problemas de MFA → Ticket de MFA
- Incidente de seguridad → Ticket de seguridad
- Formato: '[Abrir ticket de soporte](URL)'

### 3. NO INVENTES
- Si no hay documentación disponible, proporciona consejos básicos de seguridad
- Para incidentes de seguridad, siempre recomienda contactar con el equipo de Ciberseguridad

## Idioma
Responde en el mismo idioma que el usuario (español o inglés).";
    
    #region Tier 2: Parallel Search Helpers
    
    /// <summary>
    /// Search context documents in parallel
    /// </summary>
    private async Task<List<ContextDocument>> SearchContextParallelAsync(List<string> subQueries, string expandedQuery)
    {
        var tasks = new List<Task<List<ContextDocument>>>();
        
        // Search with sub-queries (limit to 3)
        foreach (var subQuery in subQueries.Take(3))
        {
            tasks.Add(_contextService.SearchAsync(subQuery, topResults: 8));
        }
        
        // Search with expanded query
        tasks.Add(_contextService.SearchAsync(expandedQuery, topResults: 8));
        
        // Wait for all tasks
        var results = await Task.WhenAll(tasks);
        
        // Flatten and return
        return results.SelectMany(r => r).ToList();
    }
    
    /// <summary>
    /// Search Confluence in parallel
    /// </summary>
    private async Task<List<ConfluencePage>> SearchConfluenceParallelAsync(string question, string expandedQuery, QueryIntent intent, SearchWeights weights)
    {
        if (_confluenceService?.IsConfigured != true)
            return new List<ConfluencePage>();
        
        var tasks = new List<Task<List<ConfluencePage>>>();
        
        // Search with original and expanded queries
        tasks.Add(_confluenceService.SearchAsync(question, topResults: weights.ConfluenceTopResults));
        tasks.Add(_confluenceService.SearchAsync(expandedQuery, topResults: weights.ConfluenceTopResults));
        
        // Also search with entity-specific queries for HowTo/Troubleshooting intent
        if (intent == QueryIntent.HowTo || intent == QueryIntent.Troubleshooting)
        {
            var entities = ExtractEntities(question);
            foreach (var entity in entities.Take(2))
            {
                tasks.Add(_confluenceService.SearchAsync(entity, topResults: 3));
            }
        }
        
        // Wait for all tasks
        var results = await Task.WhenAll(tasks);
        
        // Flatten, deduplicate, and return
        return results.SelectMany(r => r)
            .GroupBy(p => p.Title)
            .Select(g => g.First())
            .Take(8)
            .ToList();
    }
    
    #endregion

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
    /// Build context string with weighted priorities based on detected intent
    /// </summary>
    private string BuildContextWeighted(List<KnowledgeArticle> articles, List<ContextDocument> contextDocs, 
        List<ConfluencePage> confluencePages, SearchWeights weights)
    {
        var sb = new StringBuilder();
        
        _logger.LogInformation("BuildContextWeighted: {ArticleCount} articles, {ContextCount} context docs, {ConfluenceCount} confluence pages",
            articles.Count, contextDocs.Count, confluencePages.Count);
        
        // Separate context documents into categories
        var jiraTickets = contextDocs.Where(d => 
            !string.IsNullOrWhiteSpace(d.Link) && 
            d.Link.Contains("atlassian.net/servicedesk"))
            .OrderByDescending(d => d.SearchScore)
            .ToList();
        
        var referenceData = contextDocs.Where(d => 
            string.IsNullOrWhiteSpace(d.Link) || 
            !d.Link.Contains("atlassian.net/servicedesk"))
            .OrderByDescending(d => d.SearchScore)
            .ToList();
        
        // Log detailed ticket info for debugging
        _logger.LogInformation("BuildContextWeighted: {JiraCount} Jira tickets (weight:{JiraW}), {RefCount} reference data (weight:{RefW})", 
            jiraTickets.Count, weights.JiraTicketWeight, referenceData.Count, weights.ReferenceDataWeight);
        
        foreach (var ticket in jiraTickets.Take(5))
        {
            _logger.LogInformation("  Jira ticket found: '{Name}' (Score:{Score}) -> {Link}", 
                ticket.Name, ticket.SearchScore, ticket.Link);
        }
        
        // Log Confluence pages for debugging
        foreach (var page in confluencePages.Take(5))
        {
            _logger.LogInformation("  Confluence page: '{Title}' -> {Url}", 
                page.Title, page.WebUrl ?? "NO URL");
        }
        
        // Dynamic ordering based on weights
        var sections = new List<(string Name, double Weight, Action WriteSection)>
        {
            ("Jira", weights.JiraTicketWeight, () => WriteJiraSection(sb, jiraTickets)),
            ("Confluence", weights.ConfluenceWeight, () => WriteConfluenceSection(sb, confluencePages)),
            ("Reference", weights.ReferenceDataWeight, () => WriteReferenceSection(sb, referenceData)),
            ("KB", weights.KBWeight, () => WriteKBSection(sb, articles))
        };
        
        // Write sections in order of weight (highest first)
        foreach (var section in sections.OrderByDescending(s => s.Weight))
        {
            section.WriteSection();
        }
        
        if (!articles.Any() && !contextDocs.Any() && !confluencePages.Any())
        {
            return "No relevant information found in the Knowledge Base or reference data.";
        }

        return sb.ToString();
    }
    
    private void WriteJiraSection(StringBuilder sb, List<ContextDocument> jiraTickets)
    {
        if (!jiraTickets.Any()) return;
        
        sb.AppendLine("=== JIRA TICKET FORMS - USE THESE FOR SUPPORT REQUESTS ===");
        sb.AppendLine("When the user needs help, provide the SPECIFIC ticket link that matches their problem.");
        sb.AppendLine("CRITICAL: Copy the exact markdown link below - do not use generic portal links!");
        sb.AppendLine();
        
        foreach (var doc in jiraTickets.Take(8)) // More tickets when weighted high
        {
            sb.AppendLine($"TICKET: {doc.Name}");
            if (!string.IsNullOrWhiteSpace(doc.Description))
            {
                sb.AppendLine($"  Use for: {doc.Description}");
            }
            if (!string.IsNullOrWhiteSpace(doc.Keywords))
            {
                sb.AppendLine($"  Keywords: {doc.Keywords}");
            }
            // Format as markdown for easy copy
            sb.AppendLine($"  COPY THIS LINK: [{doc.Name}]({doc.Link})");
            sb.AppendLine();
        }
    }
    
    private void WriteConfluenceSection(StringBuilder sb, List<ConfluencePage> confluencePages)
    {
        if (!confluencePages.Any()) return;
        
        sb.AppendLine("=== CONFLUENCE DOCUMENTATION (How-To Guides & Procedures) ===");
        sb.AppendLine("Use this documentation to answer user questions. INCLUDE the link in your response!");
        sb.AppendLine();
        
        foreach (var page in confluencePages.Take(6)) // More pages when weighted high
        {
            sb.AppendLine($"DOCUMENT: {page.Title}");
            
            // Make URL very prominent and in markdown format for easy copy
            if (!string.IsNullOrWhiteSpace(page.WebUrl))
            {
                sb.AppendLine($"COPY THIS LINK: [📖 {page.Title}]({page.WebUrl})");
            }
            else
            {
                _logger.LogWarning("Confluence page '{Title}' has no WebUrl", page.Title);
            }
            
            if (!string.IsNullOrWhiteSpace(page.Content))
            {
                var content = page.Content.Length > 2500 ? page.Content.Substring(0, 2500) + "..." : page.Content;
                sb.AppendLine($"Content: {content}");
            }
            sb.AppendLine();
        }
    }
    
    private void WriteReferenceSection(StringBuilder sb, List<ContextDocument> referenceData)
    {
        if (!referenceData.Any()) return;
        
        sb.AppendLine("=== REFERENCE DATA (Centres, Companies, etc.) ===");
        sb.AppendLine("Use this data to answer questions about company codes, plant names, locations, etc.");
        sb.AppendLine();
        
        foreach (var doc in referenceData.Take(12)) // More data when weighted high
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
            sb.AppendLine($"  Source: {doc.SourceFile}");
            sb.AppendLine();
        }
    }
    
    private void WriteKBSection(StringBuilder sb, List<KnowledgeArticle> articles)
    {
        if (!articles.Any()) return;
        
        sb.AppendLine("=== KNOWLEDGE BASE ARTICLES (Internal Procedures) ===");
        foreach (var article in articles.Take(4))
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
        
        // Zscaler specific - expanded for troubleshooting
        if (lowerQuery.Contains("zscaler") || lowerQuery.Contains("proxy"))
        {
            expansions.Add("remote access VPN");
            expansions.Add("Zscaler ticket");
            expansions.Add("Zscaler problem issue");
            // If it's a problem/issue with Zscaler
            if (lowerQuery.Contains("no funciona") || lowerQuery.Contains("problema") || lowerQuery.Contains("error") ||
                lowerQuery.Contains("not working") || lowerQuery.Contains("issue") || lowerQuery.Contains("falla"))
            {
                expansions.Add("Zscaler support request");
                expansions.Add("Zscaler troubleshooting");
            }
        }
        
        // Problem / Issue / Troubleshooting
        if (lowerQuery.Contains("no funciona") || lowerQuery.Contains("problema") || lowerQuery.Contains("error") ||
            lowerQuery.Contains("not working") || lowerQuery.Contains("falla") || lowerQuery.Contains("issue"))
        {
            expansions.Add("troubleshooting support request");
            expansions.Add("ticket problem issue");
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
    /// <summary>
    /// Indicates if this response was served from cache (Tier 2 optimization)
    /// </summary>
    public bool FromCache { get; set; } = false;
    /// <summary>
    /// Indicates if the search confidence was below threshold (Feedback Loop)
    /// </summary>
    public bool LowConfidence { get; set; } = false;
    /// <summary>
    /// Alias for LowConfidence (for UI compatibility)
    /// </summary>
    public bool IsLowConfidence => LowConfidence;
    /// <summary>
    /// Type of agent that handled this request (SAP, Network, General)
    /// </summary>
    public string AgentType { get; set; } = "General";
    /// <summary>
    /// Best search score achieved during context retrieval
    /// </summary>
    public double BestSearchScore { get; set; } = 0;
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
