# 🎯 Tier 3: SAP Specialist Agent - Documentación Técnica

## Estado: ✅ IMPLEMENTADO (Diciembre 2025)

## Índice
1. [Contexto y Problema](#contexto-y-problema)
2. [Análisis de Datos SAP](#análisis-de-datos-sap)
3. [Arquitectura Implementada](#arquitectura-implementada)
4. [Componentes Implementados](#componentes-implementados)
5. [Configuración](#configuración)
6. [Tipos de Queries SAP](#tipos-de-queries-sap)
7. [Prompt Especializado SAP](#prompt-especializado-sap)

---

## Resumen de Implementación

### Archivos Creados
| Archivo | Propósito |
|---------|-----------|
| `Models/SapModels.cs` | Modelos de datos: SapTransaction, SapRole, SapPosition, SapBusinessRole, SapPositionRoleMapping |
| `Services/SapKnowledgeService.cs` | Carga Excel SAP desde Azure Blob usando ClosedXML |
| `Services/SapLookupService.cs` | Búsquedas O(1) con índices en memoria |
| `Services/SapAgentService.cs` | Agente AI con prompt especializado SAP |
| `Services/AgentRouterService.cs` | Enrutador de queries SAP vs General |

### Archivos Modificados
| Archivo | Cambio |
|---------|--------|
| `Extensions/DependencyInjection.cs` | Añadido `AddSapServices()` |
| `Program.cs` | Inicialización en background de `SapKnowledgeService` |
| `Components/KnowledgeChat.razor` | Inyecta `IKnowledgeAgentService` (router) en lugar de servicio directo |
| `Components/Pages/AgentContext.razor` | Aumentado límite de upload a 50MB |

### Datos SAP
- Archivo: `SAP_Dictionary.xlsx` (10.8 MB)
- Ubicación: Azure Blob Storage → contenedor `agent-context`
- Carga: Automática en background al iniciar la aplicación

---

## Contexto y Problema

### Situación Actual
- El bot utiliza un archivo Excel (`SAP_Roles_Transactions.xlsx`) con datos de SAP
- Este archivo se procesa como documento de contexto genérico (embeddings)
- Las búsquedas de SAP son lentas porque:
  1. Se genera embedding de la query
  2. Se compara con todos los documentos de contexto
  3. Se envía mucho contexto innecesario al LLM

### Problema
- **Códigos SAP** (SY01, MM01, SM35) no se benefician de búsqueda semántica
- **Lookups exactos** serían más eficientes que similitud coseno
- El archivo Excel tiene **estructura relacional** que se pierde en embeddings

### Solución
Crear un **SAP Specialist Agent** que:
1. Detecte queries relacionadas con SAP
2. Use **diccionarios en memoria** para lookups O(1)
3. Tenga un **prompt especializado** para respuestas SAP
4. **No degrade** el servicio general (routing inteligente)

---

## Análisis de Datos SAP

### Estructura del Excel (4 hojas)

#### Hoja 1: Dictionary_PL (Posiciones → Transacciones)
| Columna | Descripción | Ejemplo |
|---------|-------------|---------|
| Position ID | Código de posición | INCA01, PT40, INGM01 |
| BRole | Código de rol de negocio | PT40 |
| BRole name | Nombre del rol | Quality Manager |
| Role ID | ID del rol técnico | SY01 |
| Transaction | Código de transacción SAP | FQUS, SM35, MM01 |
| Transaction description | Descripción | G/L Account Queries |

#### Hoja 2: Hoja3/Roles (Roles de Negocio)
| Columna | Descripción | Ejemplo |
|---------|-------------|---------|
| BRole | Código rol negocio | AF01 |
| Name BR | Nombre técnico | <EA_FI_ROBOT> |
| Desc. BR | Descripción | External App: Financial Robot |
| Rol ID | ID rol técnico | SY01 |
| Transaction | Transacción SAP | FQUS |
| Transaction description | Descripción | G/L Account Queries |

#### Hoja 3: Positions name (Diccionario Posiciones)
| Columna | Descripción | Ejemplo |
|---------|-------------|---------|
| Position ID | Código | INCA01 |
| Position name | Nombre legible | Quality Manager |

#### Hoja 4: Roles (Roles Técnicos)
| Columna | Descripción | Ejemplo |
|---------|-------------|---------|
| Rol ID | Código rol | SY01, MM01, QM01 |
| Rol full name | Nombre completo | SY01:=07:MNG:USER_BASIC |
| Rol Text | Descripción | User System Operations Basic U |

### Relaciones Entre Hojas
```
Position ID ──┬── BRole ──── Role ID ──── Transaction
              │
              └── Position name (Hoja 3)
                      │
                      └── BRole name
                              │
                              └── Transaction description
```

---

## Arquitectura Propuesta

### Diagrama de Flujo
```
┌─────────────────────────────────────────────────────────────────────────┐
│                           User Query                                     │
│                    "¿Qué transacciones tiene SY01?"                     │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                    AgentRouterService                                    │
│                                                                          │
│   Detecta keywords SAP:                                                 │
│   • Transacción/Transaction                                             │
│   • Rol/Role (SY01, MM01, QM01...)                                     │
│   • Posición/Position (INCA01, INGM01...)                              │
│   • SAP, T-code, autorización                                          │
└─────────────────────────────────────────────────────────────────────────┘
                    │                              │
          SAP Query │                              │ General Query
                    ▼                              ▼
┌────────────────────────────┐      ┌────────────────────────────────────┐
│    SapAgentService         │      │    KnowledgeAgentService           │
│                            │      │    (Agente General - Sin cambios)  │
│  1. SapLookupService       │      │                                    │
│     • GetTransactionsByRole│      │  • KB Local                        │
│     • GetRolesByPosition   │      │  • Confluence                      │
│     • GetTransactionInfo   │      │  • Context Docs                    │
│     • GetPositionInfo      │      │  • Jira Tickets                    │
│                            │      │                                    │
│  2. Build SAP Context      │      │                                    │
│     (Solo datos relevantes)│      │                                    │
│                            │      │                                    │
│  3. SAP-Specific Prompt    │      │                                    │
│     (Formato tabular)      │      │                                    │
└────────────────────────────┘      └────────────────────────────────────┘
                    │                              │
                    └──────────────┬───────────────┘
                                   ▼
                         Response to User
```

### Beneficios

| Métrica | Sin SAP Agent | Con SAP Agent |
|---------|---------------|---------------|
| Tiempo lookup | ~500ms (embedding + search) | ~1ms (diccionario) |
| Precisión códigos | ~70% (semántico) | 100% (exacto) |
| Tokens LLM | ~2000 (todo contexto) | ~500 (solo relevante) |
| Escalabilidad | O(n) por documento | O(1) por lookup |

---

## Componentes a Implementar

### Fase 1: SAP Knowledge Service
**Archivo:** `Services/SapKnowledgeService.cs`

```csharp
// Responsabilidades:
// 1. Cargar Excel SAP desde Azure Blob Storage
// 2. Parsear las 4 hojas en modelos estructurados
// 3. Mantener datos en memoria

public class SapKnowledgeService
{
    // Modelos de datos
    public class SapTransaction { ... }
    public class SapRole { ... }
    public class SapPosition { ... }
    public class SapPositionRoleMapping { ... }
    
    // Carga inicial
    Task LoadFromExcelAsync(string blobPath);
    
    // Acceso a datos
    List<SapTransaction> Transactions { get; }
    List<SapRole> Roles { get; }
    List<SapPosition> Positions { get; }
}
```

### Fase 2: SAP Lookup Service
**Archivo:** `Services/SapLookupService.cs`

```csharp
// Responsabilidades:
// 1. Diccionarios indexados para lookups O(1)
// 2. Búsquedas relacionales entre entidades

public class SapLookupService
{
    // Diccionarios (O(1) lookup)
    Dictionary<string, SapTransaction> _transactionsByCode;
    Dictionary<string, List<SapTransaction>> _transactionsByRole;
    Dictionary<string, SapRole> _rolesByCode;
    Dictionary<string, SapPosition> _positionsByCode;
    Dictionary<string, List<string>> _rolesByPosition;
    
    // Métodos de búsqueda
    SapTransaction? GetTransaction(string code);
    List<SapTransaction> GetTransactionsByRole(string roleId);
    List<SapTransaction> GetTransactionsByPosition(string positionId);
    SapRole? GetRole(string roleId);
    SapPosition? GetPosition(string positionId);
    List<string> GetRolesForPosition(string positionId);
    
    // Búsqueda fuzzy (para cuando no es exacto)
    List<SapTransaction> SearchTransactions(string query);
    List<SapRole> SearchRoles(string query);
}
```

### Fase 3: SAP Agent Service
**Archivo:** `Services/SapAgentService.cs`

```csharp
// Responsabilidades:
// 1. Procesar queries SAP
// 2. Construir contexto optimizado
// 3. Usar prompt especializado

public class SapAgentService
{
    // Prompt especializado SAP
    const string SapSystemPrompt = @"
        Eres un experto en SAP de Grupo Antolin.
        Respondes sobre transacciones, roles y autorizaciones.
        Formato de respuesta: tablas cuando sea apropiado.
        ...
    ";
    
    // Método principal
    Task<AgentResponse> AskSapAsync(string question);
    
    // Detección de intención SAP
    SapQueryType DetectSapQueryType(string query);
    
    // Builder de contexto
    string BuildSapContext(string query, SapQueryType type);
}
```

### Fase 4: Agent Router Service
**Archivo:** `Services/AgentRouterService.cs`

```csharp
// Responsabilidades:
// 1. Detectar tipo de query
// 2. Enrutar al agente apropiado

public class AgentRouterService : IKnowledgeAgentService
{
    // Detectores
    bool IsSapQuery(string query);
    
    // Router principal
    Task<AgentResponse> AskAsync(string question)
    {
        if (IsSapQuery(question))
            return _sapAgent.AskSapAsync(question);
        else
            return _generalAgent.AskAsync(question);
    }
}
```

---

## Plan de Implementación

### Orden de Desarrollo

```
Fase 1: SapKnowledgeService (30 min)
    ├── Crear modelos de datos SAP
    ├── Implementar carga desde Excel
    └── Parsear las 4 hojas

Fase 2: SapLookupService (20 min)
    ├── Crear diccionarios indexados
    ├── Implementar métodos de lookup
    └── Añadir búsqueda fuzzy

Fase 3: SapAgentService (30 min)
    ├── Crear prompt especializado SAP
    ├── Implementar detección de query type
    └── Builder de contexto optimizado

Fase 4: AgentRouterService (20 min)
    ├── Implementar detección SAP
    ├── Integrar con agente general
    └── Actualizar DI y Program.cs

Fase 5: Testing & Deploy (15 min)
    ├── Probar queries SAP
    ├── Verificar que general sigue funcionando
    └── Deploy a Azure
```

### Archivos a Crear
```
RecipeSearchWeb/
├── Models/
│   └── SapModels.cs              # Modelos de datos SAP
├── Services/
│   ├── SapKnowledgeService.cs    # Carga y parseo Excel
│   ├── SapLookupService.cs       # Diccionarios y lookups
│   ├── SapAgentService.cs        # Agente especializado
│   └── AgentRouterService.cs     # Router de agentes
└── Extensions/
    └── DependencyInjection.cs    # Actualizar registro DI
```

### Archivos a Modificar
```
RecipeSearchWeb/
├── Program.cs                    # Registrar nuevos servicios
└── Interfaces/
    └── IKnowledgeAgentService.cs # (Sin cambios, router implementa)
```

---

## Tipos de Queries SAP

### Queries que Manejará el SAP Agent

| Tipo | Ejemplo | Lookup |
|------|---------|--------|
| **Transaction Info** | "¿Qué es la transacción SM35?" | `GetTransaction("SM35")` |
| **Role Transactions** | "¿Qué transacciones tiene SY01?" | `GetTransactionsByRole("SY01")` |
| **Position Access** | "¿Qué accesos necesita un Quality Manager?" | `GetTransactionsByPosition("INCA01")` |
| **Role Info** | "¿Qué hace el rol MM01?" | `GetRole("MM01")` |
| **Position Roles** | "¿Qué roles tiene INGM01?" | `GetRolesForPosition("INGM01")` |
| **Reverse Lookup** | "¿Qué rol necesito para MM01?" | `SearchRoles("MM01")` |
| **Compare** | "¿Diferencia entre INCA01 e INGM01?" | Multiple lookups |

### Keywords de Detección SAP
```csharp
var sapKeywords = new[] {
    // Español
    "transacción", "transacciones", "rol", "roles", "posición", "posiciones",
    "autorización", "autorizaciones", "acceso", "accesos", "sap",
    "t-code", "tcode", "permiso", "permisos",
    
    // English
    "transaction", "transactions", "role", "roles", "position", "positions",
    "authorization", "authorizations", "access", "permission", "permissions",
    
    // Códigos (regex pattern)
    // [A-Z]{2}[0-9]{2} - roles como SY01, MM01, QM01
    // [A-Z]{4}[0-9]{2} - posiciones como INCA01, INGM01
    // [A-Z]{2,4}[0-9]{0,2} - transacciones como SM35, FQUS
};
```

---

## Prompt Especializado SAP

```markdown
Eres un **Experto en SAP** del equipo de IT Operations de Grupo Antolin.

## Tu Rol
Ayudas a los empleados con consultas sobre:
- Transacciones SAP (T-codes)
- Roles y autorizaciones
- Posiciones y sus accesos
- Permisos necesarios para tareas

## Formato de Respuestas

### Para listados de transacciones, usa tablas:
| Transacción | Descripción |
|-------------|-------------|
| SM35 | Batch Input Monitoring |
| MM01 | Create / Modify Buying Request |

### Para información de roles:
**Rol:** SY01 - User System Operations Basic
**Descripción:** Operaciones básicas del sistema para usuarios
**Transacciones incluidas:** 45 transacciones

### Para comparaciones:
| Aspecto | INCA01 | INGM01 |
|---------|--------|--------|
| Nombre | Quality Manager | Materials & Logistic Manager |
| Roles | 3 | 5 |
| Transacciones | 120 | 85 |

## Reglas
1. Sé preciso con los códigos - son case-sensitive
2. Si no encuentras un código exacto, sugiere similares
3. Para crear accesos nuevos, dirige al ticket de SAP User Request
4. Responde en el mismo idioma que el usuario
```

---

## Resolución Dinámica de Tickets SAP

### Principio Fundamental (Actualizado Diciembre 2025)

> **Todos los tickets SAP deben venir de `Context_Jira_Forms.xlsx`**.
> El agente SAP NO debe inventar URLs de tickets.

### Implementación

El método `GetSapTicketsAsync()` busca tickets en el contexto:

```csharp
private async Task<List<ContextDocument>> GetSapTicketsAsync(string question)
{
    var results = new List<ContextDocument>();
    
    // Buscar en Context_Jira_Forms.xlsx
    await _contextService.InitializeAsync();
    var searchTerms = "SAP solicitud acceso transaccion usuario request";
    var contextResults = await _contextService.SearchAsync(searchTerms, topResults: 15);
    
    // Filtrar solo tickets de Jira ServiceDesk con contenido SAP
    var sapTickets = contextResults
        .Where(d => d.Link?.Contains("atlassian.net/servicedesk") == true)
        .Where(d => ContainsSapTerms(d))
        .ToList();
    
    // Solo usar fallback si NO hay nada en el contexto
    if (!sapTickets.Any())
    {
        results.Add(FallbackTicket); // URL genérica del portal
    }
    
    return results;
}
```

### Archivo Context_Jira_Forms.xlsx

| Name | Description | Keywords | Link |
|------|-------------|----------|------|
| SAP User Request | Solicitar accesos SAP | SAP, acceso, transaccion | https://antolin.atlassian.net/.../create/236 |

---

## Continuación del Desarrollo

Si pierdes el contexto de esta conversación, los pasos son:

1. **Leer este documento** para entender la arquitectura
2. **Leer [TIER3_MULTI_AGENT_SYSTEM.md](./TIER3_MULTI_AGENT_SYSTEM.md)** para el sistema completo
3. **Verificar estado actual** del código en `Services/`
4. **El archivo Excel SAP** debe estar en Azure Blob Storage en el container `agent-context`

### Comandos Útiles
```powershell
# Build
cd RecipeSearchWeb
dotnet build

# Publish
dotnet publish -c Release -o ./publish

# Deploy
Compress-Archive -Path ./publish/* -DestinationPath ./deploy.zip -Force
az webapp deploy --resource-group "rg-hq-helpdeskai-poc-001" --name "powershell-scripts-helpdesk" --src-path "./deploy.zip" --type zip
```

---

## Documentación Relacionada

- [TIER3_MULTI_AGENT_SYSTEM.md](./TIER3_MULTI_AGENT_SYSTEM.md) - Sistema Multi-Agente completo
- [CLEAN_ARCHITECTURE.md](./CLEAN_ARCHITECTURE.md) - Arquitectura del proyecto
- [PROJECT_DOCUMENTATION.md](./PROJECT_DOCUMENTATION.md) - Documentación general

---

*Documentación actualizada: Diciembre 2025*
*Tier 3 - SAP Specialist Agent*
*Versión: 2.0.0 - Tickets dinámicos desde contexto*
