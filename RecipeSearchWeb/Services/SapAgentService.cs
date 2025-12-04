using Azure.AI.OpenAI;
using OpenAI.Chat;
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
    private readonly ILogger<SapAgentService> _logger;

    private const string SapSystemPrompt = @"Eres un **Experto en SAP** del equipo de IT Operations de Grupo Antolin.

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
3. Para **solicitar nuevos accesos SAP**, indica que deben abrir un ticket de 'SAP User Request'
4. **Responde en el mismo idioma** que el usuario (español/inglés)
5. Si los datos proporcionados no son suficientes, indica qué información adicional necesitarías
6. **No inventes** códigos o transacciones que no estén en los datos proporcionados

## Enlace para Tickets SAP
Si el usuario necesita solicitar acceso o tiene problemas, proporciona:
[Abrir ticket de SAP](https://antolin.atlassian.net/servicedesk/customer/portal/3/group/24/create/1984)";

    public SapAgentService(
        AzureOpenAIClient azureClient,
        IConfiguration configuration,
        SapLookupService lookupService,
        ILogger<SapAgentService> logger)
    {
        var chatModel = configuration["AZURE_OPENAI_CHAT_NAME"] ?? "gpt-4o-mini";
        _chatClient = azureClient.GetChatClient(chatModel);
        _lookupService = lookupService;
        _logger = logger;
        
        _logger.LogInformation("SapAgentService initialized with model: {Model}", chatModel);
    }

    /// <summary>
    /// Process a SAP-related question
    /// </summary>
    public async Task<AgentResponse> AskSapAsync(string question)
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
            _logger.LogInformation("SAP Query detected: Type={Type}, Question='{Question}'", 
                queryType, question.Length > 50 ? question.Substring(0, 50) + "..." : question);

            // Perform lookup
            var lookupResult = _lookupService.PerformLookup(question, queryType);
            _logger.LogInformation("SAP Lookup result: Found={Found}, {Summary}", 
                lookupResult.Found, lookupResult.Summary);

            // Build optimized context
            var context = BuildSapContext(lookupResult, queryType);

            // Build messages
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SapSystemPrompt),
                new UserChatMessage($@"## Datos SAP Relevantes
{context}

## Pregunta del Usuario
{question}

Por favor, responde basándote en los datos SAP proporcionados arriba.")
            };

            // Get AI response
            var response = await _chatClient.CompleteChatAsync(messages);
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

    #endregion
}
