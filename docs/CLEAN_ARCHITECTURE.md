# Operations One Centre - Clean Architecture Documentation

## Resumen de la Arquitectura

Este proyecto implementa **Clean Architecture** (Arquitectura Limpia) en una aplicación Blazor Server para el centro de operaciones IT.

## Estructura de Carpetas

```
RecipeSearchWeb/
├── Domain/                    # Capa de Dominio (núcleo)
│   └── Common/
│       ├── Entity.cs          # Clase base para entidades
│       ├── AggregateRoot.cs   # Clase base para raíces de agregado
│       ├── ValueObject.cs     # Clase base para objetos de valor
│       └── Result.cs          # Patrón Result para manejo de errores
│
├── Interfaces/                # Capa de Aplicación (contratos)
│   ├── IScriptService.cs      # Búsqueda y gestión de scripts
│   ├── IKnowledgeService.cs   # Búsqueda de artículos KB
│   ├── IEmbeddingService.cs   # Generación de embeddings AI
│   ├── IAuthService.cs        # Autenticación Azure Easy Auth
│   ├── IScriptStorageService.cs
│   ├── IKnowledgeStorageService.cs
│   ├── IImageStorageService.cs
│   ├── IContextService.cs
│   ├── IKnowledgeAgentService.cs
│   └── IDocumentService.cs
│
├── Services/                  # Capa de Infraestructura (implementaciones)
│   ├── ScriptSearchService.cs          : IScriptService
│   ├── KnowledgeSearchService.cs       : IKnowledgeService
│   ├── AzureAuthService.cs             : IAuthService
│   ├── ScriptStorageService.cs         : IScriptStorageService
│   ├── KnowledgeStorageService.cs      : IKnowledgeStorageService
│   ├── KnowledgeImageService.cs        : IImageStorageService
│   ├── ContextSearchService.cs         : IContextService
│   ├── KnowledgeAgentService.cs        : IKnowledgeAgentService
│   ├── WordDocumentService.cs
│   ├── PdfDocumentService.cs
│   ├── MarkdownRenderService.cs
│   ├── ContextStorageService.cs
│   └── UserStateService.cs
│
├── Extensions/                # Extensiones DI
│   └── DependencyInjection.cs # Registro centralizado de servicios
│
├── Models/                    # DTOs y entidades de datos
│   ├── Script.cs
│   ├── KnowledgeArticle.cs
│   ├── User.cs
│   └── ContextDocument.cs
│
├── Components/                # Capa de Presentación (Blazor)
│   ├── Pages/
│   └── Layout/
│
└── Program.cs                 # Punto de entrada con DI limpia
```

## Interfaces Principales

### IScriptService
```csharp
public interface IScriptService
{
    Task InitializeAsync();
    Task<List<Script>> SearchScriptsAsync(string query, int topResults = 6);
    List<Script> GetAllScripts();
    List<Script> GetCustomScripts();
    Task AddCustomScriptAsync(Script script);
    Task UpdateCustomScriptAsync(Script script);
    Task DeleteCustomScriptAsync(int scriptKey);
    int GetNextAvailableKey();
}
```

### IKnowledgeService
```csharp
public interface IKnowledgeService
{
    Task InitializeAsync();
    Task<List<KnowledgeArticle>> SearchArticlesAsync(string query, int topResults = 10);
    List<KnowledgeArticle> GetAllArticles();
    List<KnowledgeArticle> GetAllArticlesIncludingInactive();
    List<KnowledgeArticle> GetArticlesByGroup(string group);
    KnowledgeArticle? GetArticleByKBNumber(string kbNumber);
    KnowledgeArticle? GetArticleById(int id);
    Dictionary<string, int> GetGroupsWithCounts();
    Task AddArticleAsync(KnowledgeArticle article);
    Task UpdateArticleAsync(KnowledgeArticle article);
    Task DeleteArticleAsync(int id);
}
```

### IKnowledgeAgentService
```csharp
public interface IKnowledgeAgentService
{
    Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null);
    IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null);
}
```

### IAuthService
```csharp
public interface IAuthService
{
    User? GetCurrentUser();
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
    string GetLogoutUrl();
    string GetLoginUrl();
}
```

## Registro de Servicios (Program.cs)

```csharp
// Add all Operations One Centre services with clean architecture pattern
builder.Services.AddStorageServices();     // Azure Blob Storage services
builder.Services.AddSearchServices();       // Vector search with embeddings
builder.Services.AddAgentServices();        // AI RAG Agent
builder.Services.AddAuthServices();         // Azure Easy Auth
builder.Services.AddDocumentServices();     // Word/PDF processing

// Initialize all services
await app.Services.InitializeServicesWithLoggingAsync(app.Logger);
```

## Extensiones de DI Disponibles

| Método | Servicios Registrados |
|--------|----------------------|
| `AddStorageServices()` | ScriptStorageService, KnowledgeStorageService, KnowledgeImageService, ContextStorageService |
| `AddSearchServices()` | ScriptSearchService, KnowledgeSearchService, ContextSearchService |
| `AddAgentServices()` | KnowledgeAgentService |
| `AddAuthServices()` | HttpContextAccessor, AzureAuthService, UserStateService |
| `AddDocumentServices()` | WordDocumentService, PdfDocumentService, MarkdownRenderService |

## Clases Base de Dominio

### Entity<TId>
Clase base para todas las entidades con ID tipado. Implementa igualdad por identidad.

### AggregateRoot<TId>
Extiende Entity para raíces de agregado. Soporta eventos de dominio.

### ValueObject
Clase base para objetos de valor. Implementa igualdad estructural.

### Result<T>
Patrón Result para operaciones que pueden fallar. Evita excepciones para flujos de control.

```csharp
// Ejemplo de uso
public async Task<Result<KnowledgeArticle>> GetArticleAsync(string kbNumber)
{
    var article = await _repository.FindByKBNumberAsync(kbNumber);
    return article is not null 
        ? Result<KnowledgeArticle>.Success(article)
        : Result<KnowledgeArticle>.Failure("Article not found", "KB_NOT_FOUND");
}

// Uso
var result = await service.GetArticleAsync("KB0001234");
result
    .OnSuccess(article => Console.WriteLine(article.Title))
    .OnFailure(error => Console.WriteLine($"Error: {error}"));
```

## Flujo de Dependencias

```
┌─────────────────────────────────────────────────┐
│              Presentation Layer                  │
│         (Blazor Components, Pages)              │
└─────────────────────┬───────────────────────────┘
                      │ depends on
                      ▼
┌─────────────────────────────────────────────────┐
│             Application Layer                    │
│              (Interfaces/)                       │
│   IScriptService, IKnowledgeService, etc.       │
└─────────────────────┬───────────────────────────┘
                      │ depends on
                      ▼
┌─────────────────────────────────────────────────┐
│               Domain Layer                       │
│           (Domain/Common/)                       │
│     Entity, ValueObject, Result, etc.           │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│           Infrastructure Layer                   │
│              (Services/)                         │
│   ScriptSearchService, KnowledgeSearchService   │
│   AzureAuthService, KnowledgeAgentService       │
│         ↓ implements ↓                          │
│      Application Layer Interfaces               │
└─────────────────────────────────────────────────┘
```

## Tecnologías Utilizadas

- **.NET 10** - Framework
- **Blazor Server** - Presentación interactiva
- **Azure OpenAI** - Embeddings y Chat
- **Azure Blob Storage** - Persistencia
- **Azure Easy Auth** - Autenticación

## Próximos Pasos Recomendados

1. **Agregar tests unitarios** usando las interfaces para mocking
2. **Implementar CQRS** si la complejidad crece
3. **Agregar validación** usando FluentValidation
4. **Implementar caching** para embeddings frecuentes
5. **Agregar logging estructurado** con Serilog
