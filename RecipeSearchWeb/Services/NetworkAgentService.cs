using Azure.AI.OpenAI;
using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Specialized AI Agent for Network/Remote Access queries
/// Tier 3: Network Specialist Agent - Handles Zscaler, VPN, connectivity issues
/// </summary>
public class NetworkAgentService
{
    private readonly ChatClient _chatClient;
    private readonly IContextService _contextService;
    private readonly ConfluenceKnowledgeService? _confluenceService;
    private readonly ILogger<NetworkAgentService> _logger;

    private const string NetworkSystemPrompt = @"Eres un Experto en Redes y Acceso Remoto del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Zscaler: Acceso remoto, configuracion, problemas de conexion
- VPN: Conexiones tradicionales, troubleshooting
- Conectividad: Problemas de red, acceso a recursos internos
- Trabajo remoto: Configuracion para trabajar desde casa

## Conocimiento Principal: Zscaler

### Que es Zscaler?
Zscaler es la solucion de acceso remoto de Grupo Antolin que permite:
- Acceder a aplicaciones corporativas (SAP, Teamcenter, etc.)
- Navegar de forma segura por internet
- Conectarse a recursos internos desde cualquier ubicacion

### Requisitos para usar Zscaler:
1. Tener instalado el cliente Zscaler (ZCC - Zscaler Client Connector)
2. Iniciar sesion con credenciales corporativas
3. Mantener el cliente activo durante el trabajo

### Problemas comunes y soluciones:
1. No conecta: Verificar credenciales, reiniciar ZCC
2. Lentitud: Verificar conexion a internet, contactar IT
3. No accede a recursos: Verificar que ZCC este conectado (icono verde)

## Formato de Respuestas

### Para problemas de conectividad:
1. Primero pregunta que aplicacion/recurso intenta acceder
2. Verifica si tiene Zscaler instalado y activo
3. Proporciona pasos de troubleshooting
4. Si no se resuelve, ofrece el ticket correspondiente

### Para solicitudes de acceso:
1. Explica el proceso
2. Proporciona el enlace al ticket correcto

## Reglas Importantes
1. Responde en el mismo idioma que el usuario (espanol/ingles)
2. No inventes soluciones - usa solo la informacion proporcionada
3. Si no puedes resolver, proporciona el enlace al ticket
4. Zscaler es la solucion principal - menciona siempre si es relevante";

    // Fallback URL only used when NO tickets found in Context_Jira_Forms.xlsx
    private const string FallbackNetworkTicketLink = "https://antolin.atlassian.net/servicedesk/customer/portal/3";

    public NetworkAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        IContextService contextService,
        ConfluenceKnowledgeService? confluenceService,
        ILogger<NetworkAgentService> logger)
    {
        var chatModel = configuration["AZURE_OPENAI_CHAT_NAME"] ?? "gpt-4o-mini";
        _chatClient = azureClient.GetChatClient(chatModel);
        _contextService = contextService;
        _confluenceService = confluenceService;
        _logger = logger;
        
        _logger.LogInformation("NetworkAgentService initialized with model: {Model}", chatModel);
    }

    /// <summary>
    /// Process a network-related question
    /// </summary>
    public async Task<AgentResponse> AskNetworkAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Get relevant documentation from Confluence
            var confluenceContext = await GetConfluenceContextAsync(question);
            
            // Get relevant tickets
            var tickets = await GetNetworkTicketsAsync(question);
            
            // Build the complete system prompt with context
            var systemPrompt = BuildSystemPromptWithContext(confluenceContext, tickets);

            // Build messages
            var aiMessages = new List<ChatMessage>();
            aiMessages.Add(new SystemChatMessage(systemPrompt));
            
            // Add conversation history if provided
            if (conversationHistory != null && conversationHistory.Count > 0)
            {
                foreach (var histMsg in conversationHistory.TakeLast(6))
                {
                    aiMessages.Add(histMsg);
                }
            }
            
            // Add current question
            aiMessages.Add(new UserChatMessage(question));

            // Get AI response
            var response = await _chatClient.CompleteChatAsync(aiMessages);
            var answer = response.Value.Content[0].Text;

            stopwatch.Stop();
            _logger.LogInformation("Network Agent answered in {Ms}ms", stopwatch.ElapsedMilliseconds);

            return new AgentResponse
            {
                Answer = answer,
                Success = true,
                RelevantArticles = new List<ArticleReference>(),
                ConfluenceSources = new List<ConfluenceReference>()
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error in Network Agent after {Ms}ms: {Question}", 
                stopwatch.ElapsedMilliseconds, question);
            
            return new AgentResponse
            {
                Answer = "Lo siento, ocurrio un error al procesar tu consulta. Por favor, intenta de nuevo.",
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Get relevant Confluence documentation for network topics
    /// </summary>
    private async Task<string> GetConfluenceContextAsync(string question)
    {
        if (_confluenceService == null || !_confluenceService.IsConfigured)
            return string.Empty;

        try
        {
            // Use the question itself plus network-specific terms
            var questionLower = question.ToLowerInvariant();
            var searchTerms = $"{question} Zscaler VPN remote access";
            
            // If asking about installation, add install keywords
            if (questionLower.Contains("instala") || questionLower.Contains("install") ||
                questionLower.Contains("descargar") || questionLower.Contains("download"))
            {
                searchTerms += " install download setup";
            }
            
            var pages = await _confluenceService.SearchAsync(searchTerms, topResults: 5);
            
            if (!pages.Any())
            {
                _logger.LogInformation("No Confluence pages found for network query: {Query}", question);
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("## Documentacion de Confluence");
            sb.AppendLine();
            
            foreach (var page in pages)
            {
                sb.AppendLine($"### {page.Title}");
                if (!string.IsNullOrWhiteSpace(page.WebUrl))
                {
                    sb.AppendLine($"📖 [Ver documentacion completa]({page.WebUrl})");
                }
                if (!string.IsNullOrWhiteSpace(page.Content))
                {
                    var content = page.Content.Length > 1500 
                        ? page.Content.Substring(0, 1500) + "..." 
                        : page.Content;
                    sb.AppendLine(content);
                }
                sb.AppendLine();
            }
            
            _logger.LogInformation("Found {Count} Confluence pages for network query", pages.Count);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Confluence context for network query");
            return string.Empty;
        }
    }

    /// <summary>
    /// Get relevant network tickets from Context_Jira_Forms.xlsx
    /// All tickets must come from the context file, not hardcoded values
    /// ONLY return tickets that are specifically network/Zscaler/VPN related
    /// </summary>
    private async Task<List<ContextDocument>> GetNetworkTicketsAsync(string question)
    {
        var results = new List<ContextDocument>();
        var lower = question.ToLowerInvariant();
        
        try
        {
            // Get tickets from context service (Context_Jira_Forms.xlsx)
            await _contextService.InitializeAsync();
            
            // Be VERY specific about network-related terms
            var searchTerms = "Zscaler VPN network connectivity internet remote access";
            var contextResults = await _contextService.SearchAsync(searchTerms, topResults: 15);
            
            _logger.LogDebug("Context search returned {Count} results for network tickets", contextResults.Count);
            
            // Filter STRICTLY to Jira tickets that are about network/Zscaler/VPN
            // NOT just any ticket that mentions "access" (like SAP access, BPC access, etc.)
            var networkTickets = contextResults.Where(d => 
                !string.IsNullOrWhiteSpace(d.Link) && 
                d.Link.Contains("atlassian.net/servicedesk"))
                .Where(d => 
                {
                    var searchableText = $"{d.Name} {d.Description ?? ""} {d.Keywords ?? ""}".ToLowerInvariant();
                    
                    // MUST contain network-specific keywords
                    // Exclude tickets that are about other systems (SAP, BPC, etc.)
                    bool isNetworkRelated = searchableText.Contains("zscaler") || 
                                           searchableText.Contains("vpn") ||
                                           searchableText.Contains("network") ||
                                           searchableText.Contains("internet") ||
                                           searchableText.Contains("connectivity") ||
                                           searchableText.Contains("remote work") ||
                                           searchableText.Contains("trabajo remoto") ||
                                           searchableText.Contains("wifi") ||
                                           searchableText.Contains("firewall");
                    
                    // Exclude tickets that are clearly NOT network related
                    bool isNotNetwork = searchableText.Contains("sap") ||
                                       searchableText.Contains("bpc") ||
                                       searchableText.Contains("consolidation") ||
                                       searchableText.Contains("user creation") ||
                                       searchableText.Contains("authorization");
                    
                    return isNetworkRelated && !isNotNetwork;
                })
                .ToList();
            
            if (networkTickets.Any())
            {
                _logger.LogInformation("Found {Count} NETWORK-SPECIFIC tickets from context: {Details}", 
                    networkTickets.Count, 
                    string.Join(" | ", networkTickets.Select(t => $"{t.Name}: {t.Link}")));
                results.AddRange(networkTickets);
            }
            else
            {
                _logger.LogWarning("No network-specific tickets found in Context_Jira_Forms.xlsx");
                // DON'T add generic tickets - only network tickets for NetworkAgent
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting network tickets from context");
        }
        
        // Log if no results found (don't use fallback - only show tickets from context)
        if (!results.Any())
        {
            _logger.LogWarning("No network tickets found in context - not suggesting any ticket");
        }
        
        return results.Take(5).ToList();
    }

    /// <summary>
    /// Build system prompt with dynamic context and tickets
    /// </summary>
    private string BuildSystemPromptWithContext(string confluenceContext, List<ContextDocument> tickets)
    {
        var sb = new StringBuilder(NetworkSystemPrompt);
        
        // Add Confluence documentation if available
        if (!string.IsNullOrWhiteSpace(confluenceContext))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Documentacion Disponible");
            sb.AppendLine(confluenceContext);
        }
        
        // Add dynamic tickets
        if (tickets.Any())
        {
            sb.AppendLine();
            sb.AppendLine("## Tickets Disponibles");
            sb.AppendLine("Usa estos enlaces segun la necesidad del usuario:");
            sb.AppendLine();
            
            var recommended = tickets.First();
            sb.AppendLine($"**RECOMENDADO:** [{recommended.Name}]({recommended.Link})");
            sb.AppendLine($"  - {recommended.Description}");
            sb.AppendLine();
            
            if (tickets.Count > 1)
            {
                sb.AppendLine("**Otras opciones:**");
                foreach (var ticket in tickets.Skip(1))
                {
                    sb.AppendLine($"- [{ticket.Name}]({ticket.Link}) - {ticket.Description}");
                }
            }
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Check if a query is network-related
    /// </summary>
    public static bool IsNetworkQuery(string query)
    {
        var lower = query.ToLowerInvariant();
        
        var networkKeywords = new[] {
            "zscaler", "vpn", "remote", "remoto", "casa", "home",
            "conectar", "conexion", "connect", "network", "red",
            "internet", "wifi", "proxy", "firewall", "bloqueado", "blocked"
        };
        
        return networkKeywords.Any(k => lower.Contains(k));
    }
}
