using Azure.AI.OpenAI;
using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Router service that directs queries to the appropriate specialized agent
/// Tier 3: Multi-Agent Architecture
/// Routes to: SAP Agent, Network Agent, or General Agent
/// Includes LLM fallback for ambiguous queries
/// </summary>
public class AgentRouterService : IKnowledgeAgentService
{
    private readonly KnowledgeAgentService _generalAgent;
    private readonly SapAgentService _sapAgent;
    private readonly NetworkAgentService _networkAgent;
    private readonly SapLookupService _sapLookup;
    private readonly ChatClient _chatClient;
    private readonly ILogger<AgentRouterService> _logger;

    // Agent types for routing
    private enum AgentType { General, SAP, Network }
    
    // LLM Classification prompt (minimal tokens for cost efficiency)
    private const string ClassificationPrompt = @"Classify this IT support query into ONE category.
Categories:
- SAP: SAP transactions, roles, authorizations, Fiori, SAP GUI
- NETWORK: VPN, Zscaler, remote access, internet, connectivity, work from home
- GENERAL: Everything else

Query: {0}

Reply with ONLY one word: SAP, NETWORK, or GENERAL";

    // SAP detection keywords
    private static readonly HashSet<string> SapKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spanish (with and without accents)
        "transaccion", "transacción", "transacciones", "t-code", "tcode",
        "autorizacion", "autorización", "autorizaciones",
        // English
        "transaction", "transactions", "authorization", "authorizations",
        // SAP specific
        "sap", "sapgui", "sap gui", "fiori",
        // Role/Position keywords combined with SAP context
        "rol sap", "role sap", "roles sap", "posicion sap", "posición sap", "position sap"
    };

    // Network detection keywords
    private static readonly HashSet<string> NetworkKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "zscaler", "vpn", "remote", "remoto", "trabajo desde casa", "work from home",
        "conectar", "conecto", "conectarme", "conexion", "conexión", "connect", "network", "red", "acceso remoto",
        "internet", "wifi", "proxy", "firewall", "bloqueado", "blocked",
        "zcc", "zscaler client", "desde casa", "from home", "teletrabajo", "homeoffice"
    };

    // Common SAP transaction patterns
    private static readonly string[] SapTransactionPatterns = new[]
    {
        @"^[A-Z]{2}\d{2}$",           // SM35, MM01, QM01
        @"^[A-Z]{2}\d{2}[A-Z]$",      // SM35X
        @"^[A-Z]{3,4}\d{0,2}$",       // FQUS, SCMA, SBWP
        @"^SO\d{2}[A-Z]?$",           // SO01, SO02X
        @"^S[A-Z]\d{2}$",             // SU01, SP02
    };

    // Common SAP role patterns
    private static readonly string[] SapRolePatterns = new[]
    {
        @"^[A-Z]{2}\d{2}$",           // SY01, MM01
        @"^[A-Z]{2}\d{2}\.[A-Z]+$",   // SD05.JI.SA
    };

    // Common SAP position patterns
    private static readonly string[] SapPositionPatterns = new[]
    {
        @"^[A-Z]{4}\d{2}$",           // INCA01, INGM01
    };

    public AgentRouterService(
        KnowledgeAgentService generalAgent,
        SapAgentService sapAgent,
        NetworkAgentService networkAgent,
        SapLookupService sapLookup,
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        ILogger<AgentRouterService> logger)
    {
        _generalAgent = generalAgent;
        _sapAgent = sapAgent;
        _networkAgent = networkAgent;
        _sapLookup = sapLookup;
        _logger = logger;
        
        // Initialize chat client for LLM classification fallback
        var chatModel = configuration["AZURE_OPENAI_CHAT_NAME"] ?? "gpt-4o-mini";
        _chatClient = azureClient.GetChatClient(chatModel);
        
        _logger.LogInformation("AgentRouterService initialized with General, SAP, Network agents + LLM fallback router");
    }

    /// <summary>
    /// Route query to appropriate agent - NEW UNIFIED APPROACH
    /// All queries use KnowledgeAgentService's full search capabilities
    /// Specialist agents add specific prompts and data, not replace search
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Determine specialist type (for prompts and extra context)
        var agentType = await DetermineAgentAsync(question, conversationHistory);
        
        _logger.LogInformation("Query routing: Agent={Agent}, Question='{Question}'", 
            agentType, question.Length > 50 ? question.Substring(0, 50) + "..." : question);

        AgentResponse response;
        
        // Map internal AgentType to interface SpecialistType
        var specialistType = agentType switch
        {
            AgentType.SAP => SpecialistType.SAP,
            AgentType.Network => SpecialistType.Network,
            _ => SpecialistType.General
        };
        
        // Get specialist-specific context (e.g., SAP lookup data)
        string? specialistContext = null;
        
        if (agentType == AgentType.SAP)
        {
            specialistContext = await GetSapSpecialistContextAsync(question);
        }
        
        // === NEW UNIFIED APPROACH ===
        // Always use KnowledgeAgentService (full search) + specialist prompts
        response = await _generalAgent.AskWithSpecialistAsync(
            question, 
            specialistType, 
            specialistContext, 
            conversationHistory);
        
        response.AgentType = specialistType.ToString();

        stopwatch.Stop();
        _logger.LogInformation("Query completed: Agent={Agent}, Success={Success}, Time={Ms}ms",
            specialistType, response.Success, stopwatch.ElapsedMilliseconds);

        return response;
    }
    
    /// <summary>
    /// Get SAP-specific context (transactions, roles, positions lookup)
    /// Enhanced to include related data (position → roles → transactions)
    /// </summary>
    private async Task<string?> GetSapSpecialistContextAsync(string question)
    {
        try
        {
            await _sapLookup.InitializeAsync();
            if (!_sapLookup.IsAvailable) return null;
            
            var sb = new StringBuilder();
            var words = question.Split(new[] { ' ', ',', '?', '!', '.', ':', ';', '"', '\'', '(', ')' }, 
                StringSplitOptions.RemoveEmptyEntries);
            
            var foundPositions = new List<string>();
            var foundRoles = new List<string>();
            
            foreach (var word in words)
            {
                var clean = word.Trim().ToUpperInvariant();
                if (clean.Length < 2 || clean.Length > 10) continue;
                
                // Check for transaction
                var transaction = _sapLookup.GetTransaction(clean);
                if (transaction != null)
                {
                    sb.AppendLine($"### SAP Transaction: {clean}");
                    sb.AppendLine($"- Description: {transaction.Description}");
                    if (!string.IsNullOrEmpty(transaction.RoleId))
                        sb.AppendLine($"- Associated Role: {transaction.RoleId}");
                    sb.AppendLine();
                }
                
                // Check for role
                var role = _sapLookup.GetRole(clean);
                if (role != null)
                {
                    foundRoles.Add(clean);
                    sb.AppendLine($"### SAP Role: {clean}");
                    sb.AppendLine($"- Description: {role.Description}");
                    sb.AppendLine();
                }
                
                // Check for position
                var position = _sapLookup.GetPosition(clean);
                if (position != null)
                {
                    foundPositions.Add(clean);
                    sb.AppendLine($"### SAP Position: {clean}");
                    sb.AppendLine($"- Name: {position.Name}");
                    sb.AppendLine();
                }
            }
            
            // For each found position, get associated roles and transactions
            foreach (var positionId in foundPositions)
            {
                // Get roles for this position
                var positionRoles = _sapLookup.GetRolesForPosition(positionId);
                if (positionRoles.Any())
                {
                    sb.AppendLine($"### Roles assigned to position {positionId}:");
                    foreach (var roleId in positionRoles.Take(20))
                    {
                        var roleInfo = _sapLookup.GetRole(roleId);
                        sb.AppendLine($"- {roleId}: {roleInfo?.Description ?? ""}");
                    }
                    sb.AppendLine();
                }
                
                // Get transactions for this position
                var positionTransactions = _sapLookup.GetTransactionsByPosition(positionId);
                if (positionTransactions.Any())
                {
                    sb.AppendLine($"### Transactions available for position {positionId}:");
                    foreach (var trans in positionTransactions.Take(30))
                    {
                        sb.AppendLine($"- {trans.Code}: {trans.Description}");
                    }
                    sb.AppendLine();
                }
            }
            
            // For each found role, get associated transactions
            foreach (var roleId in foundRoles)
            {
                var roleTransactions = _sapLookup.GetTransactionsByRole(roleId);
                if (roleTransactions.Any())
                {
                    sb.AppendLine($"### Transactions in role {roleId}:");
                    foreach (var trans in roleTransactions.Take(20))
                    {
                        sb.AppendLine($"- {trans.Code}: {trans.Description}");
                    }
                    sb.AppendLine();
                }
            }
            
            var result = sb.ToString();
            if (!string.IsNullOrEmpty(result))
            {
                _logger.LogInformation("SAP Context found: {Length} chars, Positions: {Pos}, Roles: {Roles}", 
                    result.Length, foundPositions.Count, foundRoles.Count);
            }
            
            return result.Length > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting SAP specialist context");
            return null;
        }
    }
    
    /// <summary>
    /// Direct specialist call (implements interface, delegates to general agent)
    /// </summary>
    public Task<AgentResponse> AskWithSpecialistAsync(string question, SpecialistType specialist, string? specialistContext = null, List<ChatMessage>? conversationHistory = null)
    {
        return _generalAgent.AskWithSpecialistAsync(question, specialist, specialistContext, conversationHistory);
    }

    /// <summary>
    /// Stream response from appropriate agent
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var agentType = await DetermineAgentAsync(question);
        
        switch (agentType)
        {
            case AgentType.SAP:
                // SAP agent doesn't support streaming yet, return full response
                var sapResponse = await _sapAgent.AskSapAsync(question);
                yield return sapResponse.Answer;
                break;
                
            case AgentType.Network:
                // Network agent doesn't support streaming yet, return full response
                var networkResponse = await _networkAgent.AskNetworkAsync(question);
                yield return networkResponse.Answer;
                break;
                
            default:
                await foreach (var chunk in _generalAgent.AskStreamingAsync(question, conversationHistory))
                {
                    yield return chunk;
                }
                break;
        }
    }

    /// <summary>
    /// Determine which agent should handle the query
    /// Priority: SAP > Network > General
    /// Now includes conversation history for context-aware routing
    /// </summary>
    private async Task<AgentType> DetermineAgentAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var lower = question.ToLowerInvariant();
        // Normalize accents for matching
        var normalized = lower
            .Replace("á", "a").Replace("é", "e").Replace("í", "i")
            .Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");

        // === CONTEXT-AWARE ROUTING ===
        // For short/ambiguous queries like "abre un ticket", check conversation history
        var isAmbiguousQuery = IsAmbiguousQuery(lower);
        if (isAmbiguousQuery && conversationHistory?.Any() == true)
        {
            var contextAgent = DetectContextFromHistory(conversationHistory);
            if (contextAgent != AgentType.General)
            {
                _logger.LogInformation("Context-aware routing: '{Query}' -> {Agent} (based on conversation history)", 
                    question.Length > 30 ? question.Substring(0, 30) + "..." : question, contextAgent);
                return contextAgent;
            }
        }

        // 1. Check for Network keywords FIRST (higher priority for explicit network terms)
        if (IsNetworkQuery(lower))
        {
            _logger.LogDebug("Network Agent selected: keyword match");
            return AgentType.Network;
        }

        // 2. Check for explicit SAP keywords (including normalized)
        var explicitSapKeywords = new[] { 
            "sap", "transaccion sap", "transacción sap", "transaction sap", 
            "t-code", "tcode", "sapgui", "sap gui", "fiori",
            "problema con sap", "problemas con sap", "error sap", "error de sap",
            "problema de sap", "problemas de sap",
            "transaccion de sap", "transacción de sap", "transacciones de sap",
            "posicion", "posición", "position", "que posicion", "qué posición",
            "rol sap", "role sap", "roles sap"
        };
        if (explicitSapKeywords.Any(keyword => lower.Contains(keyword) || normalized.Contains(keyword.Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u"))))
        {
            _logger.LogDebug("SAP Agent selected: explicit keyword match");
            return AgentType.SAP;
        }

        // 3. Check for SAP code patterns in the query
        var words = question.Split(new[] { ' ', ',', '?', '!', '.', ':', ';', '"', '\'', '(', ')' }, 
            StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            var clean = word.Trim().ToUpperInvariant();
            
            // Check transaction patterns
            if (SapTransactionPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                _logger.LogDebug("SAP Agent selected: transaction pattern match for '{Code}'", clean);
                return AgentType.SAP;
            }
            
            // Check role patterns
            if (SapRolePatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                await _sapLookup.InitializeAsync();
                if (_sapLookup.IsAvailable && _sapLookup.GetRole(clean) != null)
                {
                    _logger.LogDebug("SAP Agent selected: known role code '{Code}'", clean);
                    return AgentType.SAP;
                }
            }
            
            // Check position patterns - if matches pattern, route to SAP even if not in lookup
            if (SapPositionPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                _logger.LogDebug("SAP Agent selected: position pattern match for '{Code}'", clean);
                return AgentType.SAP;
            }
        }

        // 4. Check if any word is a known SAP code
        await _sapLookup.InitializeAsync();
        if (_sapLookup.IsAvailable)
        {
            foreach (var word in words)
            {
                var clean = word.Trim().ToUpperInvariant();
                if (clean.Length >= 2 && clean.Length <= 8)
                {
                    if (_sapLookup.GetTransaction(clean) != null ||
                        _sapLookup.GetRole(clean) != null ||
                        _sapLookup.GetPosition(clean) != null)
                    {
                        _logger.LogDebug("SAP Agent selected: known code lookup '{Code}'", clean);
                        return AgentType.SAP;
                    }
                }
            }
        }

        // === LLM FALLBACK: Use AI classification for ambiguous queries ===
        // This catches queries like "no puedo entrar a la herramienta de finanzas" → SAP
        var llmClassification = await ClassifyWithLlmAsync(question);
        if (llmClassification != AgentType.General)
        {
            _logger.LogInformation("LLM Router fallback: Classified as {Agent} for query '{Query}'", 
                llmClassification, question.Length > 40 ? question.Substring(0, 40) + "..." : question);
            return llmClassification;
        }

        // Default to general agent
        return AgentType.General;
    }
    
    /// <summary>
    /// Use LLM to classify ambiguous queries
    /// Cost-efficient: uses minimal tokens (~50 input, ~5 output)
    /// </summary>
    private async Task<AgentType> ClassifyWithLlmAsync(string question)
    {
        try
        {
            var prompt = string.Format(ClassificationPrompt, question);
            var messages = new List<ChatMessage>
            {
                new UserChatMessage(prompt)
            };
            
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 10, // Only need 1 word
                Temperature = 0.0f        // Deterministic
            };
            
            var response = await _chatClient.CompleteChatAsync(messages, options);
            var classification = response.Value.Content[0].Text.Trim().ToUpperInvariant();
            
            _logger.LogDebug("LLM classification result: '{Result}' for query", classification);
            
            return classification switch
            {
                "SAP" => AgentType.SAP,
                "NETWORK" => AgentType.Network,
                _ => AgentType.General
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM classification failed, defaulting to General agent");
            return AgentType.General;
        }
    }

    /// <summary>
    /// Check if query is network-related
    /// </summary>
    private bool IsNetworkQuery(string lowerQuestion)
    {
        // Check explicit network keywords
        if (NetworkKeywords.Any(keyword => lowerQuestion.Contains(keyword)))
        {
            return true;
        }
        
        // Check for work-from-home / remote work patterns
        var remotePatterns = new[] {
            "trabajar desde casa", "work from home", "acceso desde casa",
            "conectarme desde", "no me conecta", "no puedo acceder",
            "conecto desde casa", "conectar desde casa", "acceder desde casa",
            "entrar desde casa", "acceso a antolin", "conectar a antolin",
            "conectarme a antolin", "conecto a antolin", "acceder a antolin"
        };
        
        if (remotePatterns.Any(p => lowerQuestion.Contains(p)))
        {
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if query is too short/ambiguous and needs context from conversation history
    /// </summary>
    private bool IsAmbiguousQuery(string lowerQuestion)
    {
        // Short queries that typically need context
        var ambiguousPatterns = new[] {
            "abre un ticket", "abrir ticket", "open ticket", "crear ticket",
            "abre ticket", "necesito ticket", "quiero ticket",
            "mas informacion", "más información", "more info",
            "ayuda", "help", "soporte", "support",
            "si", "sí", "yes", "no", "ok", "vale", "de acuerdo",
            "como hago", "cómo hago", "que hago", "qué hago",
            "siguiente paso", "next step", "y ahora", "and now"
        };
        
        // Query is ambiguous if it matches patterns or is very short
        return ambiguousPatterns.Any(p => lowerQuestion.Contains(p)) || 
               lowerQuestion.Split(' ').Length <= 4;
    }
    
    /// <summary>
    /// Detect the context/topic from conversation history to route follow-up questions correctly
    /// </summary>
    private AgentType DetectContextFromHistory(List<ChatMessage> history)
    {
        // Analyze the last few messages to detect the topic
        var recentMessages = history.TakeLast(6).ToList(); // Last 3 exchanges
        
        var combinedText = string.Join(" ", recentMessages.Select(m => 
        {
            if (m is UserChatMessage ucm) return ucm.Content.FirstOrDefault()?.Text ?? "";
            if (m is AssistantChatMessage acm) return acm.Content.FirstOrDefault()?.Text ?? "";
            return "";
        })).ToLowerInvariant();
        
        // Check for Network context indicators
        var networkIndicators = new[] { 
            "zscaler", "vpn", "remote", "remoto", "conectar", "connection",
            "trabajo desde casa", "work from home", "acceso remoto"
        };
        if (networkIndicators.Any(k => combinedText.Contains(k)))
        {
            _logger.LogDebug("Context from history: NETWORK (found: {Indicators})", 
                string.Join(", ", networkIndicators.Where(k => combinedText.Contains(k))));
            return AgentType.Network;
        }
        
        // Check for SAP context indicators
        var sapIndicators = new[] { 
            "sap", "transaccion", "transacción", "transaction", "t-code", "fiori",
            "rol sap", "posicion", "posición", "position", "autorización"
        };
        if (sapIndicators.Any(k => combinedText.Contains(k)))
        {
            _logger.LogDebug("Context from history: SAP (found: {Indicators})", 
                string.Join(", ", sapIndicators.Where(k => combinedText.Contains(k))));
            return AgentType.SAP;
        }
        
        return AgentType.General;
    }
}
