using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Router service that directs queries to the appropriate specialized agent
/// Tier 3: Multi-Agent Architecture
/// </summary>
public class AgentRouterService : IKnowledgeAgentService
{
    private readonly KnowledgeAgentService _generalAgent;
    private readonly SapAgentService _sapAgent;
    private readonly SapLookupService _sapLookup;
    private readonly ILogger<AgentRouterService> _logger;

    // SAP detection keywords
    private static readonly HashSet<string> SapKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spanish
        "transacción", "transacciones", "transaccion", "t-code", "tcode",
        "autorización", "autorizaciones", "autorizacion",
        // English
        "transaction", "transactions", "authorization", "authorizations",
        // SAP specific
        "sap", "sapgui", "sap gui", "fiori",
        // Role/Position keywords combined with SAP context
        "rol sap", "role sap", "roles sap", "posición sap", "position sap"
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
        @"^[A-Z]{4}\d{2}$",           // INDR01, INES01
    };

    public AgentRouterService(
        KnowledgeAgentService generalAgent,
        SapAgentService sapAgent,
        SapLookupService sapLookup,
        ILogger<AgentRouterService> logger)
    {
        _generalAgent = generalAgent;
        _sapAgent = sapAgent;
        _sapLookup = sapLookup;
        _logger = logger;
        
        _logger.LogInformation("AgentRouterService initialized with General and SAP agents");
    }

    /// <summary>
    /// Route query to appropriate agent
    /// </summary>
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Determine which agent to use
        var isSapQuery = await IsSapQueryAsync(question);
        
        _logger.LogInformation("Query routing: IsSAP={IsSap}, Question='{Question}'", 
            isSapQuery, question.Length > 50 ? question.Substring(0, 50) + "..." : question);

        AgentResponse response;
        
        if (isSapQuery)
        {
            response = await _sapAgent.AskSapAsync(question);
            response.Answer = response.Answer; // SAP agent handles the response
        }
        else
        {
            response = await _generalAgent.AskAsync(question, conversationHistory);
        }

        stopwatch.Stop();
        _logger.LogInformation("Query completed: Agent={Agent}, Success={Success}, Time={Ms}ms",
            isSapQuery ? "SAP" : "General", response.Success, stopwatch.ElapsedMilliseconds);

        return response;
    }

    /// <summary>
    /// Stream response from appropriate agent
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var isSapQuery = await IsSapQueryAsync(question);
        
        if (isSapQuery)
        {
            // SAP agent doesn't support streaming yet, return full response
            var response = await _sapAgent.AskSapAsync(question);
            yield return response.Answer;
        }
        else
        {
            await foreach (var chunk in _generalAgent.AskStreamingAsync(question, conversationHistory))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Determine if a query is SAP-related
    /// </summary>
    private async Task<bool> IsSapQueryAsync(string question)
    {
        var lower = question.ToLowerInvariant();

        // 1. Check for explicit SAP keywords
        if (SapKeywords.Any(keyword => lower.Contains(keyword)))
        {
            _logger.LogDebug("SAP detected: keyword match");
            return true;
        }

        // 2. Check for SAP code patterns in the query
        var words = question.Split(new[] { ' ', ',', '?', '¿', '!', '¡', '.', ':', ';', '"', '\'', '(', ')' }, 
            StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            var clean = word.Trim().ToUpperInvariant();
            
            // Check transaction patterns
            if (SapTransactionPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                _logger.LogDebug("SAP detected: transaction pattern match for '{Code}'", clean);
                return true;
            }
            
            // Check role patterns
            if (SapRolePatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                // Verify it's actually a known role
                await _sapLookup.InitializeAsync();
                if (_sapLookup.IsAvailable && _sapLookup.GetRole(clean) != null)
                {
                    _logger.LogDebug("SAP detected: known role code '{Code}'", clean);
                    return true;
                }
            }
            
            // Check position patterns
            if (SapPositionPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(clean, p)))
            {
                // Verify it's actually a known position
                await _sapLookup.InitializeAsync();
                if (_sapLookup.IsAvailable && _sapLookup.GetPosition(clean) != null)
                {
                    _logger.LogDebug("SAP detected: known position code '{Code}'", clean);
                    return true;
                }
            }
        }

        // 3. Check if any word is a known SAP code
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
                        _logger.LogDebug("SAP detected: known code lookup '{Code}'", clean);
                        return true;
                    }
                }
            }
        }

        // 4. Contextual SAP indicators (rol/role + position context might be SAP)
        if ((lower.Contains("rol ") || lower.Contains("role ") || 
             lower.Contains("posición") || lower.Contains("position")) &&
            (lower.Contains("acceso") || lower.Contains("access") ||
             lower.Contains("permiso") || lower.Contains("permission")))
        {
            // Check if there are uppercase codes that could be SAP codes
            var upperCodes = words.Where(w => 
                w.Length >= 2 && w.Length <= 8 && 
                w.ToUpperInvariant() == w &&
                w.Any(char.IsLetter) && w.Any(char.IsDigit));
            
            if (upperCodes.Any())
            {
                _logger.LogDebug("SAP detected: contextual indicators with potential codes");
                return true;
            }
        }

        return false;
    }
}
