using Azure.AI.OpenAI;
using OpenAI.Chat;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using System.Text;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Specialized AI Agent for SAP-related queries
/// Tier 3: SAP Specialist Agent - AI Layer
/// </summary>
public class SapAgentService
{
    private readonly ChatClient _chatClient;
    private readonly SapLookupService _lookupService;
    private readonly IContextService _contextService;
    private readonly ILogger<SapAgentService> _logger;

    // Base system prompt without ticket links (those will be added dynamically)
    private const string SapSystemPromptBase = @"Eres un **Experto en SAP** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Transacciones SAP (T-codes)
- Roles y autorizaciones
- Posiciones y sus accesos
- Permisos necesarios para tareas específicas

## Datos que tienes disponibles
Se te proporcionará información estructurada de:
- **Transacciones**: Código y descripción de cada T-code
- **Roles técnicos**: ID del rol, nombre completo y descripción
- **Posiciones**: ID de posición y nombre del puesto
- **Mapeos**: Qué transacciones tiene cada rol, qué roles tiene cada posición

## Formato de Respuestas

### Para listados de transacciones (más de 5), usa tablas:
| Transacción | Descripción |
|-------------|-------------|
| SM35 | Batch Input Monitoring |
| MM01 | Create / Modify Buying Request |

### Para información de roles:
**Rol:** SY01 - User System Operations Basic U
**Nombre completo:** SY01:=07:MNG:USER_BASIC
**Transacciones incluidas:** X transacciones

### Para información de posiciones:
**Posición:** INCA01 - Quality Manager
**Roles asignados:** X roles
**Total transacciones:** Y transacciones únicas

### Para comparaciones, usa tablas comparativas:
| Aspecto | INCA01 | INGM01 |
|---------|--------|--------|
| Nombre | Quality Manager | Materials & Logistic Manager |
| Roles | 3 | 5 |
| Transacciones | 120 | 85 |

## Reglas Importantes
1. **Sé preciso** con los códigos - son case-sensitive en SAP
2. Si no encuentras un código exacto, **sugiere códigos similares**
3. Para **solicitar nuevos accesos SAP**, indica que deben abrir el ticket correspondiente
4. **Responde en el mismo idioma** que el usuario (español/inglés)
5. Si los datos proporcionados no son suficientes, indica qué información adicional necesitarías
6. **No inventes** códigos o transacciones que no estén en los datos proporcionados";

    // Fallback ticket link if no dynamic tickets found
    private const string FallbackSapTicketLink = "https://antolin.atlassian.net/servicedesk/customer/portal/3/group/25/create/236";

    public SapAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        SapLookupService lookupService,
        IContextService contextService,
        ILogger<SapAgentService> logger)
    {
        var chatModel = configuration["AZURE_OPENAI_CHAT_NAME"] ?? "gpt-4o-mini";
        _chatClient = azureClient.GetChatClient(chatModel);
        _lookupService = lookupService;
        _contextService = contextService;
        _logger = logger;
        
        _logger.LogInformation("SapAgentService initialized with model: {Model}", chatModel);
    }

    // Store last SAP context for follow-up questions
    private string? _lastSapContext;
    private string? _lastPositionId;
    private string? _lastRoleId;
    private string? _lastTransactionCode;

    /// <summary>
    /// Process a SAP-related question with optional conversation history
    /// </summary>
    public async Task<AgentResponse> AskSapAsync(string question, List<ChatMessage>? conversationHistory = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Initialize lookup service if needed
            await _lookupService.InitializeAsync();
            
            if (!_lookupService.IsAvailable)
            {
                return new AgentResponse
                {
                    Answer = "Lo siento, el servicio de conocimiento SAP no está disponible en este momento. Por favor, intenta más tarde o contacta al equipo de IT.",
                    Success = false,
                    Error = "SAP Lookup Service not available"
                };
            }

            // Detect query type
            var queryType = DetectSapQueryType(question);
            
            // Check if this is a follow-up question that needs context from previous query
            var enrichedQuestion = EnrichQuestionWithContext(question, queryType);
            
            _logger.LogInformation("SAP Query detected: Type={Type}, Original='{Question}', Enriched='{Enriched}'", 
                queryType, 
                question.Length > 50 ? question.Substring(0, 50) + "..." : question,
                enrichedQuestion != question ? enrichedQuestion : "same");

            // Perform lookup with enriched question
            var lookupResult = _lookupService.PerformLookup(enrichedQuestion, queryType);
            
            // If no results and we have previous context, try using it directly
            if (!lookupResult.Found && !string.IsNullOrEmpty(_lastPositionId) && IsFollowUpAboutTransactions(question))
            {
                _logger.LogInformation("Follow-up detected, using last position context: {PositionId}", _lastPositionId);
                lookupResult = _lookupService.PerformLookup(_lastPositionId, SapQueryType.PositionAccess);
            }
            else if (!lookupResult.Found && !string.IsNullOrEmpty(_lastRoleId) && IsFollowUpAboutTransactions(question))
            {
                _logger.LogInformation("Follow-up detected, using last role context: {RoleId}", _lastRoleId);
                lookupResult = _lookupService.PerformLookup(_lastRoleId, SapQueryType.RoleTransactions);
            }
            
            _logger.LogInformation("SAP Lookup result: Found={Found}, {Summary}", 
                lookupResult.Found, lookupResult.Summary);

            // Get SAP-related tickets dynamically from context FIRST
            // This ensures we always have the correct ticket links
            var sapTickets = await GetSapTicketsAsync(question);
            
            // Log which tickets were found
            _logger.LogInformation("SAP Tickets found: {Count} - {Tickets}", 
                sapTickets.Count,
                string.Join(", ", sapTickets.Take(3).Select(t => $"{t.Name} -> {t.Link}")));

            // For generic SAP queries without specific data, still provide helpful response
            // DON'T fallback to general agent - handle it here with proper SAP ticket
            string context;
            if (!lookupResult.Found && queryType == SapQueryType.General)
            {
                _logger.LogInformation("Generic SAP query - will provide SAP ticket without dictionary data");
                context = "No se encontraron datos específicos en el diccionario SAP para esta consulta.";
            }
            else
            {
                // Build optimized context from lookup
                context = BuildSapContext(lookupResult, queryType);
            }

            // Store context for follow-up questions
            _lastSapContext = context;
            _lastPositionId = lookupResult.PositionId;
            _lastRoleId = lookupResult.RoleId;
            _lastTransactionCode = lookupResult.TransactionCode;

            // Build system prompt with dynamic tickets
            var systemPrompt = BuildDynamicSystemPrompt(sapTickets);

            // Build messages with conversation history
            var aiMessages = new List<ChatMessage>();
            aiMessages.Add(new SystemChatMessage(systemPrompt));
            
            // Add conversation history if provided (for follow-up questions)
            if (conversationHistory != null && conversationHistory.Count > 0)
            {
                // Add previous context
                aiMessages.Add(new UserChatMessage($"## Contexto SAP Previo\n{_lastSapContext ?? "Sin contexto previo"}"));
                
                // Add relevant history (last 4 messages)
                foreach (var histMsg in conversationHistory.TakeLast(4))
                {
                    aiMessages.Add(histMsg);
                }
            }
            
            // Build ticket reminder for the user message
            var ticketReminder = "";
            if (sapTickets.Any())
            {
                var primaryTicket = sapTickets.First();
                ticketReminder = $@"

## RECORDATORIO DE TICKET
Si el usuario necesita abrir un ticket de SAP, usa EXACTAMENTE este enlace:
[{primaryTicket.Name}]({primaryTicket.Link})
URL: {primaryTicket.Link}";
            }
            else
            {
                ticketReminder = $@"

## RECORDATORIO DE TICKET
Si el usuario necesita abrir un ticket de SAP, usa EXACTAMENTE este enlace:
[Abrir ticket SAP]({FallbackSapTicketLink})
URL: {FallbackSapTicketLink}";
            }

            // Add current question with SAP data
            aiMessages.Add(new UserChatMessage($@"## Datos SAP Relevantes
{context}

## Pregunta del Usuario
{question}

Por favor, responde basándote en los datos SAP proporcionados arriba.
Si el usuario menciona problemas o necesita ayuda adicional, SIEMPRE sugiere abrir un ticket con el enlace correcto.{ticketReminder}"));

            // Get AI response
            var response = await _chatClient.CompleteChatAsync(aiMessages);
            var answer = response.Value.Content[0].Text;

            stopwatch.Stop();
            _logger.LogInformation("SAP Agent answered in {Ms}ms: QueryType={Type}, Found={Found}", 
                stopwatch.ElapsedMilliseconds, queryType, lookupResult.Found);

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
            _logger.LogError(ex, "Error in SAP Agent after {Ms}ms: {Question}", 
                stopwatch.ElapsedMilliseconds, question);
            
            return new AgentResponse
            {
                Answer = "Lo siento, ocurrió un error al procesar tu consulta SAP. Por favor, intenta de nuevo o contacta al equipo de IT.",
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Detect the type of SAP query
    /// </summary>
    public SapQueryType DetectSapQueryType(string query)
    {
        var lower = query.ToLowerInvariant();

        // Compare queries
        if (lower.Contains("diferencia") || lower.Contains("difference") ||
            lower.Contains("comparar") || lower.Contains("compare") ||
            lower.Contains("vs") || lower.Contains(" o ") || lower.Contains(" or "))
        {
            // Check if comparing specific codes
            var codes = ExtractPotentialCodes(query);
            if (codes.Count >= 2)
                return SapQueryType.Compare;
        }

        // Reverse lookup (what role/position has this transaction)
        if (lower.Contains("qué rol") || lower.Contains("que rol") ||
            lower.Contains("which role") || lower.Contains("what role") ||
            lower.Contains("quién tiene") || lower.Contains("quien tiene") ||
            lower.Contains("qué posición") || lower.Contains("que posición"))
        {
            return SapQueryType.ReverseLookup;
        }

        // Check if asking about transactions of a position (CRITICAL - check this early)
        // "dime 10 transacciones de la posicion INCA01", "transacciones de INCA01", etc.
        if ((lower.Contains("transaccion") || lower.Contains("transaction") ||
             lower.Contains("t-code") || lower.Contains("tcode") || 
             lower.Contains("acceso")) &&
            (lower.Contains("posición") || lower.Contains("posicion") || 
             lower.Contains("position") || lower.Contains("de la ") ||
             lower.Contains("de esta") || lower.Contains("del ")))
        {
            if (HasPositionCode(query))
            {
                _logger.LogDebug("Detected PositionAccess query: asking for transactions of a position");
                return SapQueryType.PositionAccess;
            }
        }

        // Check if transaction is IN a position
        // "la transaccion MM02 esta dentro de la posicion INCA01?"
        if ((lower.Contains("está en") || lower.Contains("esta en") ||
             lower.Contains("está dentro") || lower.Contains("esta dentro") ||
             lower.Contains("pertenece") || lower.Contains("incluida") ||
             lower.Contains("tiene acceso")) &&
            HasPositionCode(query) && HasTransactionCode(query))
        {
            _logger.LogDebug("Detected PositionAccess query: checking if transaction is in position");
            return SapQueryType.PositionAccess;
        }

        // Position access queries
        if (lower.Contains("acceso") || lower.Contains("access") ||
            lower.Contains("necesita") || lower.Contains("need") ||
            lower.Contains("permisos de") || lower.Contains("permissions"))
        {
            // Check if it mentions a position
            if (lower.Contains("position") || lower.Contains("posición") ||
                lower.Contains("puesto") || lower.Contains("cargo") ||
                HasPositionCode(query))
            {
                return SapQueryType.PositionAccess;
            }
        }

        // Role transactions
        if ((lower.Contains("transacciones") || lower.Contains("transactions") ||
             lower.Contains("t-codes") || lower.Contains("tcodes")) &&
            (lower.Contains("rol") || lower.Contains("role") || HasRoleCode(query)))
        {
            return SapQueryType.RoleTransactions;
        }

        // Transaction info
        if (lower.Contains("transacción") || lower.Contains("transaction") ||
            lower.Contains("t-code") || lower.Contains("tcode") ||
            lower.Contains("qué es") || lower.Contains("what is") ||
            lower.Contains("para qué sirve") || lower.Contains("what does"))
        {
            if (HasTransactionCode(query))
                return SapQueryType.TransactionInfo;
        }

        // Role info
        if (lower.Contains("rol") || lower.Contains("role"))
        {
            if (HasRoleCode(query))
                return SapQueryType.RoleInfo;
        }

        // Position info
        if (lower.Contains("posición") || lower.Contains("position") ||
            lower.Contains("puesto") || lower.Contains("cargo"))
        {
            if (HasPositionCode(query))
                return SapQueryType.PositionInfo;
        }

        // If we detect any SAP code, try to figure out type
        var detectedCodes = ExtractPotentialCodes(query);
        if (detectedCodes.Any())
        {
            // Check what type of code it is
            foreach (var code in detectedCodes)
            {
                if (_lookupService.GetTransaction(code) != null)
                    return SapQueryType.TransactionInfo;
                if (_lookupService.GetRole(code) != null)
                    return SapQueryType.RoleInfo;
                if (_lookupService.GetPosition(code) != null)
                    return SapQueryType.PositionInfo;
            }
        }

        return SapQueryType.General;
    }

    /// <summary>
    /// Build optimized context for the LLM based on lookup results
    /// </summary>
    private string BuildSapContext(SapLookupResult result, SapQueryType queryType)
    {
        var sb = new StringBuilder();

        if (!result.Found)
        {
            sb.AppendLine("No se encontraron datos exactos para esta consulta.");
            sb.AppendLine("El usuario puede necesitar verificar el código o proporcionar más detalles.");
            return sb.ToString();
        }

        // Transactions
        if (result.Transactions.Any())
        {
            sb.AppendLine("### Transacciones SAP");
            if (result.Transactions.Count <= 10)
            {
                foreach (var trans in result.Transactions)
                {
                    sb.AppendLine($"- **{trans.Code}**: {trans.Description}");
                }
            }
            else
            {
                sb.AppendLine($"Total: {result.Transactions.Count} transacciones");
                sb.AppendLine("| Código | Descripción |");
                sb.AppendLine("|--------|-------------|");
                foreach (var trans in result.Transactions.Take(30)) // Limit to avoid too much context
                {
                    sb.AppendLine($"| {trans.Code} | {trans.Description} |");
                }
                if (result.Transactions.Count > 30)
                {
                    sb.AppendLine($"| ... | (y {result.Transactions.Count - 30} más) |");
                }
            }
            sb.AppendLine();
        }

        // Roles
        if (result.Roles.Any())
        {
            sb.AppendLine("### Roles SAP");
            foreach (var role in result.Roles.Take(10))
            {
                sb.AppendLine($"- **{role.RoleId}**: {role.Description}");
                if (!string.IsNullOrEmpty(role.FullName))
                    sb.AppendLine($"  Nombre completo: {role.FullName}");
                
                // Add transaction count for this role
                var transCount = _lookupService.GetTransactionsByRole(role.RoleId).Count;
                if (transCount > 0)
                    sb.AppendLine($"  Transacciones: {transCount}");
            }
            sb.AppendLine();
        }

        // Positions
        if (result.Positions.Any())
        {
            sb.AppendLine("### Posiciones SAP");
            foreach (var pos in result.Positions.Take(10))
            {
                sb.AppendLine($"- **{pos.PositionId}**: {pos.Name}");
                
                // Add role and transaction counts
                var roles = _lookupService.GetRolesForPosition(pos.PositionId);
                var transCount = _lookupService.GetTransactionsByPosition(pos.PositionId).Count;
                if (roles.Any())
                    sb.AppendLine($"  Roles: {roles.Count} ({string.Join(", ", roles.Take(5))}{(roles.Count > 5 ? "..." : "")})");
                if (transCount > 0)
                    sb.AppendLine($"  Total transacciones: {transCount}");
            }
            sb.AppendLine();
        }

        // For compare queries, add comparison table
        if (queryType == SapQueryType.Compare)
        {
            if (result.Positions.Count >= 2)
            {
                sb.AppendLine("### Comparación de Posiciones");
                sb.AppendLine("| Aspecto | " + string.Join(" | ", result.Positions.Take(3).Select(p => p.PositionId)) + " |");
                sb.AppendLine("|---------|" + string.Join("|", result.Positions.Take(3).Select(_ => "-------")) + "|");
                sb.AppendLine("| Nombre | " + string.Join(" | ", result.Positions.Take(3).Select(p => p.Name)) + " |");
                sb.AppendLine("| Roles | " + string.Join(" | ", result.Positions.Take(3).Select(p => _lookupService.GetRolesForPosition(p.PositionId).Count.ToString())) + " |");
                sb.AppendLine("| Transacciones | " + string.Join(" | ", result.Positions.Take(3).Select(p => _lookupService.GetTransactionsByPosition(p.PositionId).Count.ToString())) + " |");
                sb.AppendLine();
            }
            else if (result.Roles.Count >= 2)
            {
                sb.AppendLine("### Comparación de Roles");
                sb.AppendLine("| Aspecto | " + string.Join(" | ", result.Roles.Take(3).Select(r => r.RoleId)) + " |");
                sb.AppendLine("|---------|" + string.Join("|", result.Roles.Take(3).Select(_ => "-------")) + "|");
                sb.AppendLine("| Descripción | " + string.Join(" | ", result.Roles.Take(3).Select(r => r.Description.Length > 30 ? r.Description.Substring(0, 30) + "..." : r.Description)) + " |");
                sb.AppendLine("| Transacciones | " + string.Join(" | ", result.Roles.Take(3).Select(r => _lookupService.GetTransactionsByRole(r.RoleId).Count.ToString())) + " |");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    #region Helper Methods

    private List<string> ExtractPotentialCodes(string query)
    {
        var codes = new List<string>();
        var words = query.Split(new[] { ' ', ',', '?', '¿', '!', '¡', '.', ':', ';', '"', '\'', '(', ')' }, 
            StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            var clean = word.Trim().ToUpperInvariant();
            
            // Pattern for SAP codes: 2-6 alphanumeric characters
            if (clean.Length >= 2 && clean.Length <= 8 &&
                System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Z0-9_]+$"))
            {
                // Exclude common words
                if (!IsCommonWord(clean))
                    codes.Add(clean);
            }
        }
        
        return codes.Distinct().ToList();
    }

    private bool IsCommonWord(string word)
    {
        var commonWords = new HashSet<string> { 
            "SAP", "THE", "FOR", "AND", "QUE", "DEL", "LOS", "LAS", "UNA", "UNO", 
            "PARA", "CON", "POR", "SIN", "COMO", "TIENE", "ROLE", "ROLES" 
        };
        return commonWords.Contains(word);
    }

    private bool HasTransactionCode(string query)
    {
        var codes = ExtractPotentialCodes(query);
        return codes.Any(c => _lookupService.GetTransaction(c) != null);
    }

    private bool HasRoleCode(string query)
    {
        var codes = ExtractPotentialCodes(query);
        return codes.Any(c => _lookupService.GetRole(c) != null);
    }

    private bool HasPositionCode(string query)
    {
        var codes = ExtractPotentialCodes(query);
        return codes.Any(c => _lookupService.GetPosition(c) != null);
    }

    /// <summary>
    /// Enrich question with context from previous queries for follow-ups
    /// </summary>
    private string EnrichQuestionWithContext(string question, SapQueryType queryType)
    {
        var lower = question.ToLowerInvariant();
        
        // Check if this is a follow-up about "this position" or "this role"
        var referencePatterns = new[] { 
            "esta posición", "esta posicion", "this position",
            "este rol", "this role", 
            "esa posición", "esa posicion", "that position",
            "ese rol", "that role",
            "la posición", "la posicion", "the position",
            "el rol", "the role"
        };
        
        var hasReference = referencePatterns.Any(p => lower.Contains(p));
        
        if (hasReference)
        {
            // Substitute the reference with the actual code
            if (!string.IsNullOrEmpty(_lastPositionId) && 
                (lower.Contains("posición") || lower.Contains("posicion") || lower.Contains("position")))
            {
                _logger.LogDebug("Enriching question with position context: {PositionId}", _lastPositionId);
                return question + $" (Posición: {_lastPositionId})";
            }
            
            if (!string.IsNullOrEmpty(_lastRoleId) && 
                (lower.Contains("rol") || lower.Contains("role")))
            {
                _logger.LogDebug("Enriching question with role context: {RoleId}", _lastRoleId);
                return question + $" (Rol: {_lastRoleId})";
            }
        }
        
        return question;
    }

    /// <summary>
    /// Detect if the question is a follow-up asking about transactions
    /// </summary>
    private bool IsFollowUpAboutTransactions(string question)
    {
        var lower = question.ToLowerInvariant();
        
        var transactionKeywords = new[] {
            "transacción", "transacciones", "transaccion", 
            "transaction", "transactions",
            "accesos", "acceso", "access",
            "primeras", "first", "lista", "list", "dime", "muestra", "show"
        };
        
        var followUpIndicators = new[] {
            "esta", "este", "esa", "ese", "la ", "el ", "relacionad", "de esta", "de este"
        };
        
        var hasTransactionKeyword = transactionKeywords.Any(k => lower.Contains(k));
        var hasFollowUpIndicator = followUpIndicators.Any(f => lower.Contains(f));
        
        // No explicit SAP code in the question but asking about transactions
        var codes = ExtractPotentialCodes(question);
        var noExplicitCode = !codes.Any(c => 
            _lookupService.GetPosition(c) != null || 
            _lookupService.GetRole(c) != null);
        
        return hasTransactionKeyword && (hasFollowUpIndicator || noExplicitCode);
    }

    #endregion

    #region Dynamic Ticket Resolution - CONTEXT ONLY

    /// <summary>
    /// Get SAP-related tickets ONLY from Context_Jira_Forms.xlsx
    /// NO hardcoded URLs - everything comes from the context file
    /// </summary>
    private async Task<List<ContextDocument>> GetSapTicketsAsync(string question)
    {
        var results = new List<ContextDocument>();
        var questionLower = question.ToLowerInvariant();
        
        _logger.LogInformation("GetSapTicketsAsync: Searching ONLY in context for SAP tickets. Question: '{Question}'", question);
        
        try
        {
            await _contextService.InitializeAsync();
            
            // Build search terms based on the question intent
            var searchTerms = BuildSapSearchTerms(questionLower);
            _logger.LogInformation("SAP ticket search terms: '{Terms}'", searchTerms);
            
            // Search in context with SAP-related terms
            var contextResults = await _contextService.SearchAsync(searchTerms, topResults: 20);
            
            _logger.LogInformation("Context search returned {Count} total results", contextResults.Count);
            
            // Filter to ONLY Jira servicedesk tickets that are SAP-related
            var sapTickets = contextResults
                .Where(d => !string.IsNullOrWhiteSpace(d.Link) && 
                           d.Link.Contains("atlassian.net/servicedesk"))
                .Where(d => 
                {
                    var text = $"{d.Name} {d.Description ?? ""} {d.Keywords ?? ""}".ToLowerInvariant();
                    return text.Contains("sap");
                })
                .ToList();
            
            _logger.LogInformation("Found {Count} SAP tickets in context: {Tickets}", 
                sapTickets.Count,
                string.Join(" | ", sapTickets.Select(t => $"{t.Name}: {t.Link}")));
            
            if (sapTickets.Any())
            {
                // Score and prioritize tickets based on question intent
                var scoredTickets = ScoreTicketsForQuestion(sapTickets, questionLower);
                results.AddRange(scoredTickets);
            }
            else
            {
                _logger.LogWarning("No SAP tickets found in Context_Jira_Forms.xlsx!");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for SAP tickets in context");
        }
        
        _logger.LogInformation("GetSapTicketsAsync: Returning {Count} SAP tickets from context", results.Count);
        
        return results.Take(5).ToList();
    }
    
    /// <summary>
    /// Build search terms based on question intent
    /// </summary>
    private string BuildSapSearchTerms(string questionLower)
    {
        var terms = new List<string> { "SAP" };
        
        // Add specific terms based on intent
        if (questionLower.Contains("usuario") || questionLower.Contains("user") ||
            questionLower.Contains("crear") || questionLower.Contains("nuevo"))
        {
            terms.AddRange(new[] { "user", "creation", "nuevo", "usuario" });
        }
        
        if (questionLower.Contains("acceso") || questionLower.Contains("access") ||
            questionLower.Contains("autoriza") || questionLower.Contains("permiso"))
        {
            terms.AddRange(new[] { "access", "authorization", "acceso", "permiso" });
        }
        
        if (questionLower.Contains("transac") || questionLower.Contains("t-code") ||
            questionLower.Contains("problema") || questionLower.Contains("error"))
        {
            terms.AddRange(new[] { "transaction", "help", "issue", "T-code" });
        }
        
        if (questionLower.Contains("reporte") || questionLower.Contains("report") ||
            questionLower.Contains("bi"))
        {
            terms.AddRange(new[] { "report", "BI", "reporting" });
        }
        
        if (questionLower.Contains("impresora") || questionLower.Contains("printer"))
        {
            terms.AddRange(new[] { "printer", "impresora" });
        }
        
        return string.Join(" ", terms.Distinct());
    }
    
    /// <summary>
    /// Score tickets based on how well they match the question intent
    /// </summary>
    private List<ContextDocument> ScoreTicketsForQuestion(List<ContextDocument> tickets, string questionLower)
    {
        return tickets
            .Select(t => new
            {
                Ticket = t,
                Score = CalculateTicketScore(t, questionLower)
            })
            .OrderByDescending(x => x.Score)
            .Select(x => 
            {
                x.Ticket.SearchScore = x.Score;
                return x.Ticket;
            })
            .ToList();
    }
    
    /// <summary>
    /// Calculate relevance score for a ticket based on question
    /// </summary>
    private double CalculateTicketScore(ContextDocument ticket, string questionLower)
    {
        var score = ticket.SearchScore; // Start with semantic search score
        var ticketText = $"{ticket.Name} {ticket.Description ?? ""} {ticket.Keywords ?? ""}".ToLowerInvariant();
        
        // User creation intent
        if (questionLower.Contains("usuario") || questionLower.Contains("user") ||
            questionLower.Contains("crear") || questionLower.Contains("nuevo") ||
            questionLower.Contains("necesito un"))
        {
            if (ticketText.Contains("user") && (ticketText.Contains("creation") || ticketText.Contains("new")))
                score += 0.5;
        }
        
        // Access/authorization intent  
        if (questionLower.Contains("acceso") || questionLower.Contains("access") ||
            questionLower.Contains("autoriza") || questionLower.Contains("permiso"))
        {
            if (ticketText.Contains("access") || ticketText.Contains("authorization"))
                score += 0.5;
        }
        
        // Problem/help intent
        if (questionLower.Contains("problema") || questionLower.Contains("error") ||
            questionLower.Contains("ayuda") || questionLower.Contains("help"))
        {
            if (ticketText.Contains("help") || ticketText.Contains("issue") || ticketText.Contains("problem"))
                score += 0.5;
        }
        
        // Transaction intent
        if (questionLower.Contains("transac") || questionLower.Contains("t-code"))
        {
            if (ticketText.Contains("transaction") || ticketText.Contains("t-code"))
                score += 0.5;
        }
        
        return score;
    }

    /// <summary>
    /// Build the system prompt with dynamic ticket links from context
    /// </summary>
    private string BuildDynamicSystemPrompt(List<ContextDocument> sapTickets)
    {
        var promptBuilder = new StringBuilder(SapSystemPromptBase);
        
        promptBuilder.AppendLine();
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("## IMPORTANTE: Enlaces de Tickets SAP");
        promptBuilder.AppendLine("**DEBES usar ÚNICAMENTE estos enlaces cuando el usuario necesite ayuda o soporte:**");
        promptBuilder.AppendLine();
        
        if (sapTickets.Any())
        {
            // First ticket is the recommended one - emphasize strongly
            var recommended = sapTickets.First();
            promptBuilder.AppendLine($"### 🎯 TICKET PRINCIPAL PARA ESTA CONSULTA:");
            promptBuilder.AppendLine($"**OBLIGATORIO usar este enlace cuando el usuario tenga problemas con SAP:**");
            promptBuilder.AppendLine($"👉 [{recommended.Name}]({recommended.Link})");
            if (!string.IsNullOrWhiteSpace(recommended.Description))
            {
                promptBuilder.AppendLine($"   Descripción: {recommended.Description}");
            }
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("⚠️ NO uses ningún otro enlace para tickets de SAP. El enlace correcto es:");
            promptBuilder.AppendLine($"   {recommended.Link}");
            promptBuilder.AppendLine();
            
            // Other options
            if (sapTickets.Count > 1)
            {
                promptBuilder.AppendLine("### Opciones adicionales (solo si aplican específicamente):");
                foreach (var ticket in sapTickets.Skip(1).Take(3))
                {
                    promptBuilder.AppendLine($"- [{ticket.Name}]({ticket.Link})");
                    if (!string.IsNullOrWhiteSpace(ticket.Description))
                    {
                        promptBuilder.AppendLine($"  → {ticket.Description}");
                    }
                }
            }
        }
        else
        {
            // Fallback to the correct SAP ticket link
            promptBuilder.AppendLine("### 🎯 TICKET SAP:");
            promptBuilder.AppendLine($"**OBLIGATORIO usar este enlace para cualquier solicitud SAP:**");
            promptBuilder.AppendLine($"👉 [Abrir ticket SAP]({FallbackSapTicketLink})");
            promptBuilder.AppendLine();
            promptBuilder.AppendLine($"⚠️ URL exacta del ticket: {FallbackSapTicketLink}");
        }
        
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("## Regla Final");
        promptBuilder.AppendLine("Cuando sugieras abrir un ticket, SIEMPRE incluye el enlace exacto proporcionado arriba.");
        promptBuilder.AppendLine("NUNCA inventes URLs ni uses portal/1 - usa EXACTAMENTE los enlaces de esta sección.");
        
        return promptBuilder.ToString();
    }

    #endregion
}
