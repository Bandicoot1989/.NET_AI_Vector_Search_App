using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Router service that directs queries to the appropriate specialized agent
/// Tier 3: Multi-Agent Architecture
/// Routes to: SAP Agent, Network Agent, or General Agent
/// </summary>
public class AgentRouterService : IKnowledgeAgentService
{
    private readonly KnowledgeAgentService _generalAgent;
    private readonly SapAgentService _sapAgent;
    private readonly NetworkAgentService _networkAgent;
    private readonly SapLookupService _sapLookup;
    private readonly ILogger<AgentRouterService> _logger;

    // Agent types for routing
    private enum AgentType { General, SAP, Network }

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
        "conectar", "conexion", "connect", "network", "red", "acceso remoto",
        "internet", "wifi", "proxy", "firewall", "bloqueado", "blocked",
        "zcc", "zscaler client"
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
        ILogger<AgentRouterService> logger)
    {
        _generalAgent = generalAgent;
        _sapAgent = sapAgent;
        _networkAgent = networkAgent;
        _sapLookup = sapLookup;
        _logger = logger;
        
        _logger.LogInformation("AgentRouterService initialized with General, SAP, and Network agents");
    }

    /// <summary>
    /// Route query to appropriate agent
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Determine which agent to use
        var agentType = await DetermineAgentAsync(question);
        
        _logger.LogInformation("Query routing: Agent={Agent}, Question='{Question}'", 
            agentType, question.Length > 50 ? question.Substring(0, 50) + "..." : question);

        AgentResponse response;
        
        switch (agentType)
        {
            case AgentType.SAP:
                response = await _sapAgent.AskSapAsync(question, conversationHistory);
                
                // If SAP agent didn't find data or failed, fallback to general agent
                if (!response.Success || response.Answer.Contains("no esta disponible") || 
                    response.Answer.Contains("no encuentro informacion"))
                {
                    _logger.LogInformation("SAP Agent had no data, falling back to General Agent");
                    response = await _generalAgent.AskAsync(question, conversationHistory);
                }
                break;
                
            case AgentType.Network:
                response = await _networkAgent.AskNetworkAsync(question, conversationHistory);
                
                // If Network agent failed, fallback to general agent
                if (!response.Success)
                {
                    _logger.LogInformation("Network Agent failed, falling back to General Agent");
                    response = await _generalAgent.AskAsync(question, conversationHistory);
                }
                break;
                
            default:
                response = await _generalAgent.AskAsync(question, conversationHistory);
                break;
        }

        stopwatch.Stop();
        _logger.LogInformation("Query completed: Agent={Agent}, Success={Success}, Time={Ms}ms",
            agentType, response.Success, stopwatch.ElapsedMilliseconds);

        return response;
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
    /// </summary>
    private async Task<AgentType> DetermineAgentAsync(string question)
    {
        var lower = question.ToLowerInvariant();
        // Normalize accents for matching
        var normalized = lower
            .Replace("á", "a").Replace("é", "e").Replace("í", "i")
            .Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");

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
            "transaccion de sap", "transacción de sap", "transacciones de sap"
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
            
            // Check position patterns
            if (SapPositionPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                await _sapLookup.InitializeAsync();
                if (_sapLookup.IsAvailable && _sapLookup.GetPosition(clean) != null)
                {
                    _logger.LogDebug("SAP Agent selected: known position code '{Code}'", clean);
                    return AgentType.SAP;
                }
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

        // Default to general agent
        return AgentType.General;
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
            "conectarme desde", "no me conecta", "no puedo acceder"
        };
        
        if (remotePatterns.Any(p => lowerQuestion.Contains(p)))
        {
            return true;
        }
        
        return false;
    }
}
