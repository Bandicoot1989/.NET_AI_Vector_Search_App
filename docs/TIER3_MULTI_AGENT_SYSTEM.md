# 🤖 Tier 3: Sistema Multi-Agente - Documentación Técnica

## Estado: ✅ IMPLEMENTADO (Diciembre 2025)

## Índice
1. [Resumen Ejecutivo](#resumen-ejecutivo)
2. [Arquitectura Multi-Agente](#arquitectura-multi-agente)
3. [Agentes Especializados](#agentes-especializados)
4. [Router de Agentes](#router-de-agentes)
5. [Resolución Dinámica de Tickets](#resolución-dinámica-de-tickets)
6. [Flujo de Datos](#flujo-de-datos)
7. [Archivos del Sistema](#archivos-del-sistema)
8. [Configuración](#configuración)

---

## Resumen Ejecutivo

El sistema Tier 3 implementa una **arquitectura multi-agente** donde diferentes agentes especializados manejan consultas según su dominio de conocimiento. Esto mejora:

- **Precisión**: Cada agente tiene conocimiento específico de su área
- **Rendimiento**: Lookups O(1) para SAP en lugar de búsqueda semántica
- **Escalabilidad**: Fácil añadir nuevos agentes especializados
- **Mantenibilidad**: Código separado por dominio

### Principio Fundamental
> **Todos los tickets sugeridos deben venir de `Context_Jira_Forms.xlsx`**. 
> Los agentes NO deben inventar URLs de tickets.

---

## Arquitectura Multi-Agente

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              User Query                                      │
│                  "¿Qué transacciones tiene la posición INCA01?"             │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         AgentRouterService                                   │
│                                                                              │
│   ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐            │
│   │ IsNetworkQuery? │  │  IsSapQuery?    │  │ Default:        │            │
│   │                 │  │                 │  │ General Agent   │            │
│   │ • zscaler       │  │ • transacción   │  │                 │            │
│   │ • vpn           │  │ • rol SAP       │  │                 │            │
│   │ • remoto        │  │ • posición      │  │                 │            │
│   │ • red           │  │ • t-code        │  │                 │            │
│   └────────┬────────┘  └────────┬────────┘  └────────┬────────┘            │
│            │                    │                    │                      │
└────────────┼────────────────────┼────────────────────┼──────────────────────┘
             │                    │                    │
             ▼                    ▼                    ▼
┌────────────────────┐ ┌────────────────────┐ ┌────────────────────────────────┐
│ NetworkAgentService│ │  SapAgentService   │ │   KnowledgeAgentService        │
│                    │ │                    │ │                                │
│ • Zscaler          │ │ • SAP_Dictionary   │ │ • Knowledge Base Local         │
│ • VPN              │ │ • Lookups O(1)     │ │ • Confluence API               │
│ • Conectividad     │ │ • Prompt SAP       │ │ • Context Documents            │
│ • Trabajo Remoto   │ │ • Tablas SAP       │ │ • Jira Ticket Forms            │
│                    │ │                    │ │                                │
│ 📄 Tickets desde   │ │ 📄 Tickets desde   │ │ 📄 Tickets desde               │
│ Context_Jira_Forms │ │ Context_Jira_Forms │ │ Context_Jira_Forms             │
└────────────────────┘ └────────────────────┘ └────────────────────────────────┘
             │                    │                    │
             └────────────────────┼────────────────────┘
                                  ▼
                     ┌────────────────────────┐
                     │    Response to User    │
                     │    (con ticket link)   │
                     └────────────────────────┘
```

---

## Agentes Especializados

### 1. NetworkAgentService (Agente de Red)

**Propósito**: Manejar consultas sobre acceso remoto, Zscaler, VPN y conectividad.

**Archivo**: `Services/NetworkAgentService.cs`

**Keywords de Detección**:
```csharp
"zscaler", "vpn", "remote", "remoto", "casa", "home",
"conectar", "conexion", "connect", "network", "red",
"internet", "wifi", "proxy", "firewall", "bloqueado"
```

**Conocimiento Especializado**:
- Zscaler Client Connector (ZCC)
- Configuración de trabajo remoto
- Troubleshooting de conectividad
- Acceso a aplicaciones corporativas

**Fuentes de Datos**:
| Fuente | Propósito |
|--------|-----------|
| Confluence | Documentación técnica de Zscaler/VPN |
| Context_Jira_Forms.xlsx | URLs de tickets de red |

---

### 2. SapAgentService (Agente SAP)

**Propósito**: Manejar consultas sobre transacciones, roles y posiciones SAP.

**Archivo**: `Services/SapAgentService.cs`

**Keywords de Detección**:
```csharp
// Códigos SAP (regex)
[A-Z]{2,4}\d{2,3}  // MM01, SY01, QM01, INCA01...

// Keywords
"transacción", "transaction", "t-code", "tcode",
"rol sap", "role sap", "posición", "position",
"autorización", "authorization", "acceso sap"
```

**Tipos de Query SAP**:
| Tipo | Ejemplo | Método |
|------|---------|--------|
| TransactionInfo | "¿Qué es MM01?" | GetTransactionInfo |
| RoleTransactions | "Transacciones del rol SY01" | GetTransactionsByRole |
| PositionAccess | "Transacciones de INCA01" | GetTransactionsByPosition |
| RoleInfo | "Info del rol MM01" | GetRoleInfo |
| PositionInfo | "Info de posición INGM01" | GetPositionInfo |
| Compare | "Diferencia entre SY01 y MM01" | Compare |
| ReverseLookup | "¿Qué rol tiene MM02?" | ReverseLookup |

**Fuentes de Datos**:
| Fuente | Propósito |
|--------|-----------|
| SAP_Dictionary.xlsx | Datos de transacciones, roles, posiciones |
| SapLookupService | Búsquedas O(1) en memoria |
| Context_Jira_Forms.xlsx | URLs de tickets SAP |

---

### 3. KnowledgeAgentService (Agente General)

**Propósito**: Manejar consultas generales que no son de red ni SAP.

**Archivo**: `Services/KnowledgeAgentService.cs`

**Capacidades**:
- Búsqueda en Knowledge Base local
- Integración con Confluence API
- Context Documents (Excel imports)
- Intent Detection
- Query Decomposition
- Parallel Search
- Weighted Search

**Fuentes de Datos**:
| Fuente | Propósito |
|--------|-----------|
| Knowledge Base | Artículos KB locales |
| Confluence API | Documentación en Confluence |
| Context Documents | Documentos de contexto importados |
| Context_Jira_Forms.xlsx | URLs de tickets generales |

---

## Router de Agentes

### AgentRouterService

**Archivo**: `Services/AgentRouterService.cs`

**Responsabilidad**: Detectar el tipo de query y enrutar al agente apropiado.

```csharp
public async Task<AgentResponse> RouteQueryAsync(string question, List<ChatMessage>? history)
{
    // 1. Check Network queries first (specific)
    if (await IsNetworkQueryAsync(question))
    {
        return await _networkAgent.AskNetworkAsync(question, history);
    }
    
    // 2. Check SAP queries
    if (await IsSapQueryAsync(question))
    {
        return await _sapAgent.AskSapAsync(question, history);
    }
    
    // 3. Default to General Agent
    return await _knowledgeAgent.AskAsync(question, history);
}
```

**Orden de Prioridad**:
1. **Network Agent** - Keywords específicos de red/Zscaler
2. **SAP Agent** - Códigos SAP o keywords SAP
3. **General Agent** - Todo lo demás

---

## Resolución Dinámica de Tickets

### Principio Fundamental

> **NUNCA hardcodear URLs de tickets**. Todos los tickets deben venir de `Context_Jira_Forms.xlsx`.

### Implementación

Cada agente implementa un método similar para obtener tickets del contexto:

```csharp
private async Task<List<ContextDocument>> GetTicketsAsync(string question)
{
    var results = new List<ContextDocument>();
    
    try
    {
        // 1. Buscar en el contexto (Context_Jira_Forms.xlsx)
        await _contextService.InitializeAsync();
        var searchTerms = "...términos relevantes...";
        var contextResults = await _contextService.SearchAsync(searchTerms, topResults: 15);
        
        // 2. Filtrar solo tickets de Jira ServiceDesk
        var tickets = contextResults.Where(d => 
            !string.IsNullOrWhiteSpace(d.Link) && 
            d.Link.Contains("atlassian.net/servicedesk"))
            .Where(d => /* filtros específicos del agente */)
            .ToList();
        
        if (tickets.Any())
        {
            results.AddRange(tickets);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error getting tickets from context");
    }
    
    // 3. Solo usar fallback genérico si NO hay nada en el contexto
    if (!results.Any())
    {
        results.Add(new ContextDocument
        {
            Name = "Support Ticket",
            Description = "Abrir ticket de soporte",
            Link = FallbackTicketLink, // URL genérica del portal
            Keywords = "support"
        });
    }
    
    return results.Take(5).ToList();
}
```

### Flujo de Búsqueda de Tickets

```
┌─────────────────────────────────────────────────────────────────┐
│                     User Query                                   │
│              "Necesito acceso a SAP"                            │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│              ContextSearchService.SearchAsync()                  │
│                                                                  │
│   Busca en Context_Jira_Forms.xlsx:                             │
│   • Name contiene "SAP"?                                        │
│   • Description contiene "acceso"?                              │
│   • Keywords contiene términos relacionados?                    │
└─────────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┴───────────────┐
              │                               │
        Encontrado                      No encontrado
              │                               │
              ▼                               ▼
┌─────────────────────────┐    ┌─────────────────────────────────┐
│ Usar ticket del contexto│    │ Usar FallbackTicketLink         │
│                         │    │ (Portal genérico sin ticket     │
│ Name: "SAP Request"     │    │  específico)                    │
│ Link: /group/25/create/ │    │                                 │
│       236               │    │ Link: /servicedesk/customer/    │
└─────────────────────────┘    │       portal/3                  │
                               └─────────────────────────────────┘
```

---

## Flujo de Datos

### Inicialización (Startup)

```
Program.cs
    │
    ├── AddSapServices()
    │   ├── SapKnowledgeService (Singleton)
    │   ├── SapLookupService (Singleton)
    │   └── SapAgentService (Scoped)
    │
    ├── AddNetworkServices()
    │   └── NetworkAgentService (Scoped)
    │
    ├── AddKnowledgeServices()
    │   ├── KnowledgeAgentService (Scoped)
    │   └── AgentRouterService (Scoped)
    │
    └── Background Initialization
        ├── ContextSearchService.InitializeAsync()
        │   └── Load Context_Jira_Forms.xlsx
        │
        └── SapKnowledgeService.InitializeAsync()
            └── Load SAP_Dictionary.xlsx
```

### Procesamiento de Query

```
KnowledgeChat.razor
    │
    └── AgentRouterService.RouteQueryAsync(question)
            │
            ├── IsNetworkQuery? ──yes──► NetworkAgentService.AskNetworkAsync()
            │                               │
            │                               ├── GetConfluenceContextAsync()
            │                               ├── GetNetworkTicketsAsync() ◄── Context_Jira_Forms
            │                               └── ChatClient.CompleteChatAsync()
            │
            ├── IsSapQuery? ──yes──► SapAgentService.AskSapAsync()
            │                           │
            │                           ├── DetectSapQueryType()
            │                           ├── SapLookupService queries
            │                           ├── GetSapTicketsAsync() ◄── Context_Jira_Forms
            │                           └── ChatClient.CompleteChatAsync()
            │
            └── Default ──► KnowledgeAgentService.AskAsync()
                                │
                                ├── Parallel Search (KB + Confluence + Context)
                                ├── GetJiraTicketsFromContext() ◄── Context_Jira_Forms
                                └── ChatClient.CompleteChatAsync()
```

---

## Archivos del Sistema

### Archivos Creados/Modificados

| Archivo | Tipo | Propósito |
|---------|------|-----------|
| `Services/NetworkAgentService.cs` | **NUEVO** | Agente especializado en red/Zscaler |
| `Services/SapAgentService.cs` | Modificado | Tickets dinámicos desde contexto |
| `Services/AgentRouterService.cs` | Modificado | Incluye NetworkAgent en routing |
| `Services/KnowledgeAgentService.cs` | Modificado | Eliminadas URLs hardcodeadas |
| `Extensions/DependencyInjection.cs` | Modificado | AddNetworkServices() |
| `Program.cs` | Modificado | Inicialización de NetworkServices |

### Estructura de Servicios

```
Services/
├── Agentes/
│   ├── KnowledgeAgentService.cs    # Agente General (IKnowledgeAgentService)
│   ├── SapAgentService.cs          # Agente SAP
│   ├── NetworkAgentService.cs      # Agente Network
│   └── AgentRouterService.cs       # Router (IKnowledgeAgentService)
│
├── SAP/
│   ├── SapKnowledgeService.cs      # Carga Excel SAP
│   └── SapLookupService.cs         # Búsquedas O(1)
│
├── Contexto/
│   ├── ContextSearchService.cs     # Búsqueda semántica en contexto
│   └── ContextStorageService.cs    # Storage Azure Blob
│
└── Confluence/
    └── ConfluenceKnowledgeService.cs # API Confluence
```

---

## Configuración

### appsettings.json

```json
{
  "AZURE_OPENAI_ENDPOINT": "https://xxx.openai.azure.com/",
  "AZURE_OPENAI_KEY": "...",
  "AZURE_OPENAI_CHAT_NAME": "gpt-4o-mini",
  "AZURE_OPENAI_EMBEDDINGS_NAME": "text-embedding-3-small",
  
  "AZURE_BLOB_STORAGE_CONNECTION_STRING": "...",
  "AZURE_CONTEXT_CONTAINER_NAME": "agent-context"
}
```

### Archivos de Contexto Requeridos

| Archivo | Container | Propósito |
|---------|-----------|-----------|
| `Context_Jira_Forms.xlsx` | agent-context | **URLs de tickets Jira** |
| `SAP_Dictionary.xlsx` | agent-context | Datos SAP (transacciones, roles) |

### Estructura de Context_Jira_Forms.xlsx

| Columna | Descripción | Ejemplo |
|---------|-------------|---------|
| Name | Nombre del ticket | "SAP User Request" |
| Description | Descripción | "Solicitar accesos SAP" |
| Keywords | Palabras clave | "SAP, acceso, transaccion" |
| Link | URL completa | `https://antolin.atlassian.net/servicedesk/customer/portal/3/group/25/create/236` |

---

## Extensibilidad

### Añadir Nuevo Agente Especializado

1. **Crear el servicio**:
```csharp
public class NewAgentService
{
    private readonly IContextService _contextService;
    
    public async Task<AgentResponse> AskAsync(string question, List<ChatMessage>? history)
    {
        // 1. Obtener tickets del contexto (SIEMPRE)
        var tickets = await GetTicketsFromContextAsync(question);
        
        // 2. Construir prompt con tickets
        var prompt = BuildPromptWithTickets(tickets);
        
        // 3. Llamar al LLM
        return await _chatClient.CompleteChatAsync(...);
    }
    
    private async Task<List<ContextDocument>> GetTicketsFromContextAsync(string question)
    {
        // SIEMPRE buscar en Context_Jira_Forms.xlsx
        // NUNCA hardcodear URLs
    }
}
```

2. **Registrar en DI**:
```csharp
// En DependencyInjection.cs
public static IServiceCollection AddNewAgentServices(this IServiceCollection services)
{
    services.AddScoped<NewAgentService>();
    return services;
}
```

3. **Añadir al Router**:
```csharp
// En AgentRouterService.cs
if (IsNewAgentQuery(question))
{
    return await _newAgent.AskAsync(question, history);
}
```

---

## Troubleshooting

### El ticket mostrado es incorrecto

1. Verificar que `Context_Jira_Forms.xlsx` contiene el ticket correcto
2. Verificar que el Name/Description/Keywords contiene términos buscables
3. Usar endpoint `/api/context-debug?q=SAP` para ver qué encuentra

### El agente no se activa correctamente

1. Verificar keywords en `IsXxxQueryAsync()`
2. Revisar logs para ver qué agente procesó la query
3. El orden de prioridad es: Network → SAP → General

### No encuentra tickets del contexto

1. Verificar que el archivo está cargado: `/api/context-all`
2. Verificar que el Link contiene "atlassian.net/servicedesk"
3. Ampliar términos de búsqueda en `GetTicketsAsync()`

---

## Changelog

### Diciembre 2025
- ✅ Implementado NetworkAgentService para queries de Zscaler/VPN
- ✅ Actualizado AgentRouterService con routing a 3 agentes
- ✅ Eliminados tickets hardcodeados de TODOS los agentes
- ✅ Implementada resolución dinámica de tickets desde Context_Jira_Forms.xlsx
- ✅ Documentación actualizada

### Noviembre 2025
- ✅ Implementado SapAgentService con lookups O(1)
- ✅ Implementado SapKnowledgeService y SapLookupService
- ✅ Implementado AgentRouterService inicial (SAP vs General)
