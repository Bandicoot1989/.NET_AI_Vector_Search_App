# 🤖 Operations One Centre Bot - Flujo de Búsqueda y Arquitectura

## Índice
1. [Visión General](#visión-general)
2. [Arquitectura del Sistema](#arquitectura-del-sistema)
3. [Flujo de Búsqueda Completo](#flujo-de-búsqueda-completo)
4. [Fuentes de Conocimiento](#fuentes-de-conocimiento)
5. [Servicios Principales](#servicios-principales)
6. [Algoritmos de Búsqueda](#algoritmos-de-búsqueda)
7. [Procesamiento de Contexto](#procesamiento-de-contexto)
8. [API de Azure OpenAI](#api-de-azure-openai)
9. [Interfaz de Usuario](#interfaz-de-usuario)
10. [Configuración](#configuración)

---

## Visión General

El **Operations One Centre Bot** (conocido como "burbuja" 🤖) es un asistente de IA que utiliza la arquitectura **RAG (Retrieval Augmented Generation)** para responder preguntas del equipo de IT Operations de Grupo Antolin.

### Características Principales
- **Búsqueda Híbrida**: Combina búsqueda semántica (embeddings) + búsqueda por palabras clave
- **Múltiples Fuentes**: Knowledge Base local, Confluence, y Documentos de Contexto (Excel)
- **Streaming de Respuestas**: Las respuestas se muestran en tiempo real
- **Multi-idioma**: Responde en el mismo idioma que el usuario (ES/EN)

---

## Arquitectura del Sistema

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           USUARIO (Blazor UI)                               │
│                         KnowledgeChat.razor (Burbuja 🤖)                    │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        KnowledgeAgentService                                │
│                    (Orquestador Principal - RAG)                            │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────┐  ┌──────────────┐  │
│  │ExpandQuery  │  │ BuildContext │  │ System Prompt   │  │ Chat Client  │  │
│  │WithSynonyms │  │              │  │                 │  │ (GPT-4o-mini)│  │
│  └─────────────┘  └──────────────┘  └─────────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
           │                    │                    │
           ▼                    ▼                    ▼
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────────────┐
│ KnowledgeSearch  │  │ ContextSearch    │  │ ConfluenceKnowledge          │
│ Service          │  │ Service          │  │ Service                      │
│ (KB Local)       │  │ (Excel Files)    │  │ (Atlassian Confluence)       │
└──────────────────┘  └──────────────────┘  └──────────────────────────────┘
           │                    │                    │
           ▼                    ▼                    ▼
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────────────┐
│ Azure Blob       │  │ Azure Blob       │  │ Azure Blob (Cache)           │
│ Storage (KB)     │  │ Storage (Context)│  │ + Confluence REST API        │
└──────────────────┘  └──────────────────┘  └──────────────────────────────┘
```

---

## Flujo de Búsqueda Completo

### Diagrama de Secuencia

```
Usuario          KnowledgeChat       KnowledgeAgentService      Servicios de Búsqueda     Azure OpenAI
   │                  │                      │                          │                      │
   │ 1. Pregunta      │                      │                          │                      │
   │─────────────────>│                      │                          │                      │
   │                  │ 2. SendMessage()     │                          │                      │
   │                  │─────────────────────>│                          │                      │
   │                  │                      │ 3. ExpandQueryWithSynonyms()                    │
   │                  │                      │────────────────────────┐ │                      │
   │                  │                      │<───────────────────────┘ │                      │
   │                  │                      │                          │                      │
   │                  │                      │ 4. SearchArticlesAsync() │                      │
   │                  │                      │─────────────────────────>│ (KB Local)           │
   │                  │                      │                          │                      │
   │                  │                      │ 5. SearchAsync()         │                      │
   │                  │                      │─────────────────────────>│ (Context Docs)       │
   │                  │                      │                          │                      │
   │                  │                      │ 6. SearchAsync()         │                      │
   │                  │                      │─────────────────────────>│ (Confluence)         │
   │                  │                      │                          │                      │
   │                  │                      │ 7. BuildContext()        │                      │
   │                  │                      │────────────────────────┐ │                      │
   │                  │                      │<───────────────────────┘ │                      │
   │                  │                      │                          │                      │
   │                  │                      │ 8. CompleteChatAsync()   │                      │
   │                  │                      │─────────────────────────────────────────────────>│
   │                  │                      │                          │                      │
   │                  │                      │<────────────────────────────────────────────────│
   │                  │                      │ 9. Respuesta IA          │                      │
   │                  │<─────────────────────│                          │                      │
   │ 10. Muestra      │                      │                          │                      │
   │<─────────────────│                      │                          │                      │
```

### Paso a Paso Detallado

#### 1️⃣ Usuario Hace una Pregunta
```csharp
// KnowledgeChat.razor - Línea 144
private async Task SendMessage()
{
    var question = currentMessage.Trim();
    messages.Add(new ChatMessage { Text = question, IsUser = true });
    
    var response = await AgentService.AskAsync(question);
    // ...
}
```

#### 2️⃣ Expansión de Query con Sinónimos
```csharp
// KnowledgeAgentService.cs - Método ExpandQueryWithSynonyms()
private string ExpandQueryWithSynonyms(string query)
{
    var lowerQuery = query.ToLowerInvariant();
    var expansions = new List<string> { query };
    
    // Ejemplo: Si pregunta sobre "casa" o "remoto"
    if (lowerQuery.Contains("casa") || lowerQuery.Contains("remoto"))
    {
        expansions.Add("remote access VPN Zscaler");
        expansions.Add("acceso remoto");
    }
    
    // Ejemplo: Si pregunta sobre un centro/planta
    if (lowerQuery.Contains("centro") || lowerQuery.Contains("planta"))
    {
        // Extrae códigos de planta como IGA, IBU, etc.
        expansions.Add("centre plant location");
    }
    
    return string.Join(" ", expansions);
}
```

**Sinónimos Soportados:**
| Tema | Palabras Clave | Expansión |
|------|----------------|-----------|
| Acceso Remoto | casa, home, remoto, remote | remote access VPN Zscaler |
| VPN/Red | vpn, red, network, internet | Zscaler remote access |
| Portales B2B | vw, volkswagen, bmw, ford | B2B Portals Customer Extranets |
| Email | correo, email, outlook | Email Outlook |
| SAP | sap | SAP transaction user |
| Centros/Plantas | centro, planta, factory | centre plant location |

#### 3️⃣ Búsqueda en Múltiples Fuentes (En Paralelo Conceptual)

**3.1 Knowledge Base Local:**
```csharp
var relevantArticles = await _knowledgeService.SearchArticlesAsync(question, topResults: 5);
```

**3.2 Documentos de Contexto (Excel):**
```csharp
var contextDocs = await _contextService.SearchAsync(expandedQuery, topResults: 8);
```

**3.3 Confluence (Doble búsqueda):**
```csharp
var results1 = await _confluenceService.SearchAsync(question, topResults: 5);
var results2 = await _confluenceService.SearchAsync(expandedQuery, topResults: 5);
confluencePages = results1.Concat(results2).GroupBy(p => p.Title).Select(g => g.First()).Take(6).ToList();
```

#### 4️⃣ Construcción del Contexto (BuildContext)
```csharp
private string BuildContext(List<KnowledgeArticle> articles, 
                           List<ContextDocument> contextDocs, 
                           List<ConfluencePage> confluencePages)
{
    var sb = new StringBuilder();
    
    // Separar documentos de contexto en categorías
    var jiraTickets = contextDocs.Where(d => d.Link.Contains("atlassian.net/servicedesk"));
    var referenceData = contextDocs.Where(d => !d.Link.Contains("atlassian.net/servicedesk"));
    
    // PRIORIDAD 0: Datos de referencia (Centros, Compañías)
    // PRIORIDAD 1: Tickets de Jira
    // PRIORIDAD 2: Páginas de Confluence
    // PRIORIDAD 3: Artículos del KB local
    
    return sb.ToString();
}
```

**Estructura del Contexto Generado:**
```
=== REFERENCE DATA (Centres, Companies, etc.) ===
Use this data to answer questions about company codes, plant names, locations, etc.

ENTRY: IGA
  Details: Planta de Iguala, México
  Centre code: IGA
  Country: Mexico
  Source: Centres.xlsx (Reference Data)

=== JIRA TICKET FORMS - USE THESE FOR SUPPORT REQUESTS ===
TICKET: Zscaler Access Request
  Use for: Request remote access to corporate network
  URL: https://antolin.atlassian.net/servicedesk/customer/portal/3/group/24/create/1985

=== CONFLUENCE DOCUMENTATION (How-To Guides & Procedures) ===
--- BMW B2B User Creation ---
Page URL: https://antolin.atlassian.net/wiki/spaces/OPER/pages/123456
Content: To create a new user in BMW B2B portal...

=== KNOWLEDGE BASE ARTICLES (Internal Procedures) ===
--- Article: KB0001 - VPN Troubleshooting ---
Summary: Steps to resolve common VPN issues...
```

#### 5️⃣ Envío a Azure OpenAI
```csharp
var messages = new List<ChatMessage>
{
    new SystemChatMessage(SystemPrompt),  // Instrucciones del bot
    new UserChatMessage(userMessage)       // Contexto + Pregunta
};

var response = await _chatClient.CompleteChatAsync(messages);
```

#### 6️⃣ Respuesta al Usuario
La respuesta incluye:
- Texto de la respuesta IA
- Referencias a artículos KB (si relevancia > 50%)
- Referencias a páginas de Confluence

---

## Fuentes de Conocimiento

### 1. Knowledge Base Local (KB)
- **Archivo**: `knowledge-articles.json` en Azure Blob Storage
- **Contenido**: Artículos de procedimientos internos
- **Campos**: KBNumber, Title, Content, Tags, Category

### 2. Documentos de Contexto (Excel)
- **Almacenamiento**: Azure Blob Storage (`agent-context` container)
- **Archivos Típicos**:
  - `Centres.xlsx` - Códigos de planta y ubicaciones
  - `Companies.xlsx` - Códigos de compañía
  - `Context_Jira_forms.xlsx` - URLs de formularios Jira

### 3. Confluence (Atlassian)
- **API**: REST API con Basic Auth
- **Spaces Configurados**: GAUKB, OPER, TECH, SDPA
- **Cache**: Embeddings cacheados en `confluence-kb-cache.json`

---

## Servicios Principales

### KnowledgeAgentService (Orquestador)
**Ubicación**: `Services/KnowledgeAgentService.cs`

**Responsabilidades:**
- Coordinar búsqueda en todas las fuentes
- Expandir queries con sinónimos
- Construir contexto para el modelo
- Gestionar conversación con Azure OpenAI

**Métodos Principales:**
| Método | Descripción |
|--------|-------------|
| `AskAsync(question)` | Procesa pregunta y devuelve respuesta completa |
| `AskStreamingAsync(question)` | Procesa con streaming para mejor UX |
| `ExpandQueryWithSynonyms(query)` | Añade términos relacionados |
| `BuildContext(...)` | Construye contexto de todas las fuentes |

### ContextSearchService (Búsqueda Híbrida)
**Ubicación**: `Services/ContextSearchService.cs`

**Responsabilidades:**
- Buscar en documentos de contexto (Excel)
- Implementar búsqueda híbrida (keyword + semántica)

**Algoritmo de Búsqueda Híbrida:**
```csharp
public async Task<List<ContextDocument>> SearchAsync(string query, int topResults = 5)
{
    // PASO 1: Búsqueda por palabra clave (exacta)
    var keywordMatches = SearchByKeyword(query);
    foreach (var doc in keywordMatches)
    {
        results.Add((doc, 1.0)); // Score alto para coincidencias exactas
    }

    // PASO 2: Búsqueda semántica (embeddings)
    var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query);
    var semanticResults = _documents
        .Where(doc => !keywordMatches.Contains(doc))
        .Select(doc => (doc, CosineSimilarity(queryVector, doc.Embedding)))
        .Where(x => x.Item2 > 0.2)
        .OrderByDescending(x => x.Item2);

    // Combinar y deduplicar
    return results.GroupBy(r => r.Doc.Id)
        .Select(g => g.OrderByDescending(r => r.Score).First())
        .OrderByDescending(r => r.Score)
        .Take(topResults);
}
```

**Búsqueda por Palabra Clave (con Stop Words):**
```csharp
private List<ContextDocument> SearchByKeyword(string query)
{
    // Stop words que se ignoran
    var stopWords = new HashSet<string> { 
        "que", "es", "el", "la", "los", "las", "un", "una", "de", "del", "en",
        "what", "is", "the", "a", "an", "of", "in", "for", "to", "how",
        "centro", "plant", "planta" // También ignoramos términos genéricos
    };
    
    var terms = query.Split(' ', '?', '¿', '!', '¡', ',', '.')
        .Where(t => t.Length >= 2 && !stopWords.Contains(t.ToLower()));

    // Buscar en Name, Description, Keywords y AdditionalData
    return _documents.Where(doc => 
        terms.Any(term => doc.GetFullText().Contains(term, StringComparison.OrdinalIgnoreCase))
    ).ToList();
}
```

### ConfluenceKnowledgeService
**Ubicación**: `Services/ConfluenceKnowledgeService.cs`

**Responsabilidades:**
- Sincronizar páginas de Confluence
- Buscar en páginas cacheadas
- Gestionar cache de embeddings

**Métodos Principales:**
| Método | Descripción |
|--------|-------------|
| `SearchAsync(query, topResults)` | Búsqueda semántica en páginas |
| `SyncSingleSpaceAsync(spaceKey)` | Sincroniza un space específico |
| `LoadCachedPagesAsync()` | Carga páginas desde blob cache |

### KnowledgeSearchService
**Ubicación**: `Services/KnowledgeSearchService.cs`

**Responsabilidades:**
- Buscar en artículos del KB local
- Gestionar CRUD de artículos

---

## Algoritmos de Búsqueda

### Similitud Coseno (Cosine Similarity)
Usado para comparar embeddings (vectores de 1536 dimensiones):

```csharp
private static double CosineSimilarity(float[] vectorA, ReadOnlyMemory<float> vectorB)
{
    var spanB = vectorB.Span;
    double dotProduct = 0;
    double magnitudeA = 0;
    double magnitudeB = 0;

    for (int i = 0; i < vectorA.Length; i++)
    {
        dotProduct += vectorA[i] * spanB[i];
        magnitudeA += vectorA[i] * vectorA[i];
        magnitudeB += spanB[i] * spanB[i];
    }

    magnitudeA = Math.Sqrt(magnitudeA);
    magnitudeB = Math.Sqrt(magnitudeB);

    if (magnitudeA == 0 || magnitudeB == 0) return 0;

    return dotProduct / (magnitudeA * magnitudeB);
}
```

### Umbrales de Relevancia
| Fuente | Umbral Mínimo | Descripción |
|--------|---------------|-------------|
| Context Docs (Keyword) | 1.0 | Coincidencia exacta |
| Context Docs (Semantic) | 0.2 | Similitud semántica |
| KB Articles | 0.5 | Solo muestra como fuente si > 50% |
| Confluence | 0.2 | Similitud semántica |

---

## Procesamiento de Contexto

### Prioridades en BuildContext

| Prioridad | Fuente | Cantidad Max | Uso |
|-----------|--------|--------------|-----|
| 0 (Más Alta) | Reference Data | 10 | Centros, Compañías, etc. |
| 1 | Jira Tickets | 5 | URLs de formularios |
| 2 | Confluence | 4 | Documentación, How-To |
| 3 | KB Articles | 3 | Procedimientos internos |

### Límites de Contenido
- **Confluence Content**: Máximo 2000 caracteres por página
- **KB Article Content**: Máximo 1500 caracteres por artículo

---

## API de Azure OpenAI

### Configuración
```json
{
  "AZURE_OPENAI_ENDPOINT": "https://xxx.openai.azure.com/",
  "AZURE_OPENAI_API_KEY": "xxx",
  "AZURE_OPENAI_CHAT_NAME": "gpt-4o-mini",
  "AZURE_OPENAI_GPT_NAME": "text-embedding-3-small"
}
```

### Modelos Utilizados
| Modelo | Uso | Dimensiones |
|--------|-----|-------------|
| `gpt-4o-mini` | Chat/Completions | N/A |
| `text-embedding-3-small` | Embeddings | 1536 |

### System Prompt
El System Prompt define el comportamiento del bot:
- Responder en el idioma del usuario
- Seguir orden de prioridad (documentación → ticket)
- Incluir URLs de Confluence como referencia
- NO inventar información

---

## Interfaz de Usuario

### KnowledgeChat.razor (Burbuja)
**Ubicación**: `Components/KnowledgeChat.razor`

**Características:**
- Botón flotante con animación de pulso
- Chat window expandible
- Indicador de "typing" durante procesamiento
- Sugerencias de preguntas
- Referencias a artículos clicables

**Estados:**
| Estado | Visual |
|--------|--------|
| Cerrado | Botón 🤖 con pulso |
| Abierto | Ventana de chat |
| Procesando | "Thinking..." + typing indicator |
| Error | Mensaje de error |

---

## Configuración

### Variables de Entorno (Azure App Service)
```
# Azure OpenAI
AZURE_OPENAI_ENDPOINT=https://xxx.openai.azure.com/
AZURE_OPENAI_API_KEY=xxx
AZURE_OPENAI_CHAT_NAME=gpt-4o-mini
AZURE_OPENAI_GPT_NAME=text-embedding-3-small

# Azure Storage
AzureStorage__ConnectionString=DefaultEndpointsProtocol=https;AccountName=xxx;...

# Confluence
Confluence__BaseUrl=https://antolin.atlassian.net
Confluence__Email=xxx@antolin.com
Confluence__ApiTokenBase64=xxx (base64 encoded)
Confluence__SpaceKeys=GAUKB,OPER,TECH,SDPA
```

### Contenedores de Azure Blob Storage
| Container | Contenido |
|-----------|-----------|
| `knowledge-base` | Artículos KB (JSON) |
| `agent-context` | Documentos de contexto (JSON) |
| `confluence-cache` | Cache de Confluence (JSON) |
| `kb-images` | Imágenes del KB |

---

## Troubleshooting

### El bot no responde sobre un tema específico
1. Verificar que el documento esté importado en "Agent Context"
2. Revisar logs: `SearchByKeyword` muestra términos filtrados
3. Verificar que el término no esté en la lista de stop words

### Confluence no sincroniza
1. Verificar credenciales en configuración
2. Revisar `ConfluenceKnowledgeService.IsConfigured`
3. Sincronizar por space individual para evitar timeouts

### Respuestas lentas
1. Verificar que embeddings estén cacheados
2. Reducir `topResults` si es necesario
3. Revisar logs de tiempo de respuesta de Azure OpenAI

---

## Métricas y Logging

### Logs Importantes
```csharp
// Búsqueda de contexto
_logger.LogInformation("SearchByKeyword: Query='{Query}', Filtered terms: [{Terms}], Total docs: {DocCount}");

// Construcción de contexto
_logger.LogInformation("BuildContext: {ArticleCount} articles, {ContextCount} context docs, {ConfluenceCount} confluence pages");

// Respuesta del agente
_logger.LogInformation("Agent answered question: {Question} using {ArticleCount} KB articles, {ConfluenceCount} Confluence pages");
```

### Application Insights (si configurado)
- Request duration
- Dependency calls (Azure OpenAI, Blob Storage)
- Exception tracking

---

## Arquitectura de Archivos

```
RecipeSearchWeb/
├── Components/
│   └── KnowledgeChat.razor          # UI del chat (burbuja)
├── Interfaces/
│   ├── IKnowledgeAgentService.cs    # Interface del agente
│   ├── IKnowledgeService.cs         # Interface KB
│   ├── IContextService.cs           # Interface contexto
│   └── IConfluenceService.cs        # Interface Confluence
├── Models/
│   ├── KnowledgeArticle.cs          # Modelo artículo KB
│   ├── ContextDocument.cs           # Modelo documento contexto
│   └── ConfluencePage.cs            # Modelo página Confluence
├── Services/
│   ├── KnowledgeAgentService.cs     # Orquestador principal (RAG)
│   ├── KnowledgeSearchService.cs    # Búsqueda KB local
│   ├── ContextSearchService.cs      # Búsqueda híbrida contexto
│   ├── ConfluenceKnowledgeService.cs# Integración Confluence
│   └── *StorageService.cs           # Servicios de persistencia
└── Extensions/
    └── DependencyInjection.cs       # Registro de servicios DI
```

---

*Documentación generada: Diciembre 2025*
*Versión: 2.0.0*
