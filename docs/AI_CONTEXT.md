# 🤖 Contexto para IA - Operations One Centre

Este archivo contiene todo el contexto necesario para que una IA pueda trabajar en este proyecto, incluyendo errores resueltos, patrones establecidos y decisiones de diseño.

---

## 📋 Resumen del Proyecto

**Nombre**: Operations One Centre  
**Tecnología**: Blazor Server .NET 10  
**Hosting**: Azure App Service  
**Autenticación**: Azure Easy Auth (Microsoft Entra ID)  
**Almacenamiento**: Azure Blob Storage  
**AI**: Azure OpenAI (embeddings para búsqueda semántica)

### Módulos Principales
1. **Scripts** - Biblioteca de PowerShell scripts con búsqueda AI
2. **Knowledge Base** - Documentación técnica con soporte Word, PDF y screenshots
3. **Home** - Dashboard centralizado con acceso rápido a módulos

### Módulos Eliminados (Nov 2025)
- **News** - Eliminado por simplicidad
- **Weather** - Eliminado por no ser relevante

---

## 🏗️ Arquitectura Crítica

### Render Modes de Blazor
El proyecto usa **InteractiveServer** rendermode:
```csharp
// En App.razor
<Routes @rendermode="InteractiveServer" />
```

Esto significa:
- Primera carga: **Static Server Rendering (SSR)** - HttpContext disponible
- Después: **Interactive Server** - HttpContext NO disponible (SignalR)

### Patrón de Usuario Cascading
```
App.razor → CascadingUserState.razor → Routes.razor → Pages
```

---

## 🐛 Errores Resueltos y Soluciones

### Error #1: CascadingParameter de Usuario siempre null en componentes interactivos

**Síntoma**: `[CascadingParameter(Name = "CurrentUser")] User? CurrentUser` siempre era `null` en páginas como `ScriptEditor.razor` y `KnowledgeAdmin.razor`.

**Causa**: En modo InteractiveServer, `HttpContext` no está disponible porque la conexión es vía SignalR. El `AzureAuthService` no puede leer los headers de autenticación.

**Solución**: Patrón de 4 estrategias de fallback con `PersistentComponentState`:

```csharp
@code {
    [CascadingParameter(Name = "CurrentUser")]
    private User? CascadingUser { get; set; }

    [Inject] private AzureAuthService AuthService { get; set; } = default!;
    [Inject] private UserStateService UserState { get; set; } = default!;
    [Inject] private PersistentComponentState ApplicationState { get; set; } = default!;

    private User? currentUser;
    private PersistingComponentStateSubscription _subscription;

    protected override void OnInitialized()
    {
        _subscription = ApplicationState.RegisterOnPersisting(PersistUser);
        
        // Estrategia 1: Restaurar de estado persistido
        if (ApplicationState.TryTakeFromJson<User>("PageName_User", out var restored))
        {
            currentUser = restored;
        }
        // Estrategia 2: Obtener de AuthService (SSR con HttpContext)
        else if (AuthService.GetCurrentUser() is User authUser)
        {
            currentUser = authUser;
        }
        // Estrategia 3: Obtener de UserStateService (scoped)
        else if (UserState.CurrentUser is User stateUser)
        {
            currentUser = stateUser;
        }
        // Estrategia 4: Usar CascadingParameter
        else
        {
            currentUser = CascadingUser;
        }
        
        // Siempre sincronizar con UserStateService
        if (currentUser != null)
        {
            UserState.SetUser(currentUser);
        }
    }

    private Task PersistUser()
    {
        ApplicationState.PersistAsJson("PageName_User", currentUser);
        return Task.CompletedTask;
    }
    
    public void Dispose() => _subscription.Dispose();
}
```

**Archivos afectados**:
- `Components/CascadingUserState.razor` - Implementa el patrón base
- `Components/Pages/ScriptEditor.razor` - Implementa el patrón localmente
- `Components/Pages/KnowledgeAdmin.razor` - Implementa el patrón localmente

---

### Error #2: Artículos inactivos desaparecen del panel de administración

**Síntoma**: Al desmarcar "Active" en un artículo KB, desaparecía de la lista del admin y no se podía reactivar.

**Causa**: `GetAllArticles()` filtraba por `IsActive == true`:
```csharp
// INCORRECTO
return _articles.Where(a => a.IsActive).ToList();
```

**Solución**: Crear método específico para admin que retorne TODOS los artículos:
```csharp
// En KnowledgeSearchService.cs
public List<KnowledgeArticle> GetAllArticlesIncludingInactive()
{
    return _articles.OrderByDescending(a => a.IsActive)
                    .ThenByDescending(a => a.LastUpdated)
                    .ToList();
}
```

Y en `KnowledgeAdmin.razor`:
```csharp
private async Task LoadArticles()
{
    articles = KnowledgeService.GetAllArticlesIncludingInactive();
    // ...
}
```

---

### Error #3: Falta de filtros en panel admin con muchos artículos

**Síntoma**: Con cientos de artículos, era difícil encontrar uno específico.

**Solución**: Implementar sistema de filtros:
```csharp
// Variables de estado
private List<KnowledgeArticle> filteredArticles = new();
private List<string> availableGroups = new();
private string searchFilter = "";
private string selectedGroupFilter = "";
private string selectedStatusFilter = "all";

// Método de filtrado
private void ApplyFilters()
{
    var query = articles.AsEnumerable();
    
    // Filtro de búsqueda (título, KB number, descripción)
    if (!string.IsNullOrWhiteSpace(searchFilter))
    {
        var search = searchFilter.ToLower();
        query = query.Where(a => 
            a.Title.ToLower().Contains(search) ||
            a.KBNumber.ToLower().Contains(search) ||
            a.ShortDescription.ToLower().Contains(search));
    }
    
    // Filtro por categoría
    if (!string.IsNullOrWhiteSpace(selectedGroupFilter))
    {
        query = query.Where(a => a.KBGroup == selectedGroupFilter);
    }
    
    // Filtro por estado
    if (selectedStatusFilter == "active")
        query = query.Where(a => a.IsActive);
    else if (selectedStatusFilter == "inactive")
        query = query.Where(a => !a.IsActive);
    
    filteredArticles = query.ToList();
}
```

---

### Error #4: Word document upload falla silenciosamente

**Síntoma**: Al subir un documento Word, no se mostraba error pero tampoco se creaba el artículo.

**Causa**: El servicio `WordDocumentService` no manejaba correctamente documentos sin la estructura de tabla GA KB esperada.

**Solución**: Agregar fallbacks y mejor logging:
```csharp
public async Task<KnowledgeArticle> ProcessDocumentAsync(Stream stream, string fileName, string author)
{
    try
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        
        if (body == null)
            throw new InvalidOperationException("Document body not found");
        
        var article = new KnowledgeArticle
        {
            Author = author,
            SourceDocument = fileName,
            CreatedDate = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        };
        
        // Intentar extraer metadata de tabla
        ExtractMetadata(body, article);
        
        // Si no se encontró título, usar nombre del archivo
        if (string.IsNullOrEmpty(article.Title))
        {
            article.Title = Path.GetFileNameWithoutExtension(fileName);
        }
        
        // Extraer contenido
        ExtractContent(body, article);
        
        return article;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"Failed to process Word document: {ex.Message}", ex);
    }
}
```

---

### Error #5: Imágenes no se cargan en producción

**Síntoma**: Las imágenes subidas al KB mostraban URL correcta pero no cargaban.

**Causa**: El contenedor de Azure Blob no tenía acceso público configurado.

**Solución**: Configurar acceso público a nivel de blob:
```csharp
// En KnowledgeImageService
await _containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
```

O configurar en Azure Portal:
1. Storage Account → Containers → knowledge
2. Change access level → Blob (anonymous read for blobs only)

---

### Error #6: Botón Admin no aparece en Knowledge.razor

**Síntoma**: El botón de administración (⚙️) no se mostraba aunque el usuario fuera admin.

**Causa**: Mismo problema que Error #1 - el `CascadingParameter` era null.

**Solución**: Aplicar el mismo patrón de 4 estrategias en `Knowledge.razor`:
```csharp
// Verificar si es admin usando cualquiera de las fuentes
private bool IsCurrentUserAdmin => 
    currentUser?.IsAdmin == true || 
    CascadingUser?.IsAdmin == true || 
    UserState.IsAdmin;
```

---

### Error #7: Modal no se cierra después de guardar

**Síntoma**: Al guardar un artículo/script, el modal permanecía abierto.

**Causa**: Faltaba `StateHasChanged()` después de cerrar el modal.

**Solución**:
```csharp
private async Task SaveArticle()
{
    // ... guardar lógica ...
    
    showEditModal = false;
    await LoadArticles();
    StateHasChanged();  // ← Importante!
}
```

---

### Error #8: Embedding vector no se genera para nuevos artículos

**Síntoma**: Artículos nuevos no aparecían en búsqueda semántica.

**Causa**: Después de crear/editar, no se regeneraba el embedding.

**Solución**: Llamar a `ReloadArticlesAsync()` que regenera todos los embeddings:
```csharp
private async Task SaveArticle()
{
    await StorageService.SaveArticleAsync(editingArticle);
    await KnowledgeService.ReloadArticlesAsync();  // ← Regenera embeddings
    // ...
}
```

---

### Error #9: No existe opción de eliminar artículos KB permanentemente

**Síntoma**: Solo se podía desactivar artículos, no eliminarlos. Artículos de prueba o erróneos permanecían en el storage.

**Solución**: Implementar eliminación permanente con confirmación:

1. **Nuevo método en KnowledgeSearchService**:
```csharp
public async Task DeleteArticleAsync(string kbNumber)
{
    var article = _articles.FirstOrDefault(a => 
        a.KBNumber.Equals(kbNumber, StringComparison.OrdinalIgnoreCase));
    if (article != null)
    {
        _articles.Remove(article);
        await _storageService.SaveArticlesAsync(_articles);
    }
}
```

2. **Modal de confirmación en KnowledgeAdmin.razor**:
```csharp
// Variables de estado
private bool showDeleteConfirmModal = false;
private KnowledgeArticle? articleToDelete;

// Métodos
private void ConfirmDeleteArticle(KnowledgeArticle article)
{
    articleToDelete = article;
    showDeleteConfirmModal = true;
}

private async Task DeleteArticlePermanently()
{
    if (articleToDelete == null) return;
    
    // Eliminar imágenes asociadas
    foreach (var image in articleToDelete.Images)
    {
        await ImageService.DeleteImageAsync(image.BlobUrl);
    }
    
    // Eliminar artículo
    await KnowledgeService.DeleteArticleAsync(articleToDelete.KBNumber);
    
    articleToDelete = null;
    showDeleteConfirmModal = false;
    await LoadArticles();
}
```

3. **Botón en tabla**:
```html
<button class="btn-icon btn-danger" @onclick="() => ConfirmDeleteArticle(article)">🗑️</button>
```

---

### Error #10: PDF no extrae imágenes

**Síntoma**: Al subir PDF, solo se extraía texto, las imágenes no aparecían.

**Causa**: `PdfDocumentService` solo contaba imágenes pero no las extraía.

**Solución**: Implementar extracción real de imágenes con PdfPig:
```csharp
private async Task<List<KBImage>> ExtractAndUploadImagesAsync(PdfDocument document, string kbNumber)
{
    var images = new List<KBImage>();
    
    foreach (var page in document.GetPages())
    {
        foreach (var image in page.GetImages())
        {
            byte[]? imageBytes = null;
            
            if (image.TryGetPng(out var pngBytes))
            {
                imageBytes = pngBytes;
            }
            else if (image.RawBytes.Length > 0)
            {
                imageBytes = image.RawBytes.ToArray();
            }
            
            if (imageBytes != null && imageBytes.Length > 100)
            {
                using var stream = new MemoryStream(imageBytes);
                var uploaded = await _imageService.UploadImageAsync(
                    kbNumber, stream, $"pdf_image_{index}.png", "image/png");
                if (uploaded != null) images.Add(uploaded);
            }
        }
    }
    return images;
}
```

---

## 📁 Archivos Clave

### Program.cs - Registro de Servicios
```csharp
// Servicios de autenticación
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AzureAuthService>();
builder.Services.AddScoped<UserStateService>();

// Servicios de almacenamiento
builder.Services.AddSingleton<ScriptStorageService>();
builder.Services.AddSingleton<KnowledgeStorageService>();
builder.Services.AddSingleton<KnowledgeImageService>();

// Servicios de búsqueda (con AI)
builder.Services.AddSingleton<ScriptSearchService>();
builder.Services.AddSingleton<KnowledgeSearchService>();

// Servicio de conversión Word
builder.Services.AddSingleton<WordDocumentService>();
```

### Estructura de Blob Storage
```
scripts/
  └── all-scripts.json           # Array de Script[]

knowledge/
  ├── articles.json              # Array de KnowledgeArticle[]
  └── images/
      └── {kbNumber}/            # e.g., "KB0001/"
          └── {id}_{filename}    # e.g., "a1b2c3d4_screenshot.png"
```

### CSS Classes Importantes
```css
/* Filas inactivas en tablas admin */
.inactive-row {
    opacity: 0.6;
    background: rgba(255, 68, 68, 0.05);
}

/* Filtros de admin */
.admin-filters {
    display: flex;
    gap: 1rem;
    flex-wrap: wrap;
}

/* Imagen en galería */
.kb-image-item {
    position: relative;
    border-radius: 8px;
    overflow: hidden;
}
```

---

## 🎯 Patrones Establecidos

### 1. Patrón de Autenticación en Páginas Admin
Siempre usar las 4 estrategias de fallback para obtener el usuario.

### 2. Patrón de Carga de Datos
```csharp
protected override async Task OnInitializedAsync()
{
    isLoading = true;
    try
    {
        await LoadData();
    }
    catch (Exception ex)
    {
        errorMessage = ex.Message;
    }
    finally
    {
        isLoading = false;
    }
}
```

### 3. Patrón de Modales
```csharp
// Variables
private bool showModal = false;
private ModelType? editingItem;

// Abrir
private void OpenModal(ModelType? item = null)
{
    editingItem = item ?? new ModelType();
    showModal = true;
}

// Cerrar
private void CloseModal()
{
    showModal = false;
    editingItem = null;
    StateHasChanged();
}

// Guardar
private async Task SaveItem()
{
    await Service.SaveAsync(editingItem);
    await LoadData();
    CloseModal();
}
```

### 4. Patrón de Filtros
```csharp
private void ApplyFilters()
{
    var query = allItems.AsEnumerable();
    
    if (!string.IsNullOrWhiteSpace(searchTerm))
        query = query.Where(FilterBySearch);
    
    if (!string.IsNullOrWhiteSpace(categoryFilter))
        query = query.Where(x => x.Category == categoryFilter);
    
    filteredItems = query.ToList();
}
```

---

## ⚠️ Gotchas y Cuidados

1. **Nunca usar `HttpContext` directamente en componentes interactivos** - usar el patrón de persistencia

2. **Siempre llamar `StateHasChanged()` después de cambios de UI** - especialmente después de cerrar modales

3. **Regenerar embeddings después de CRUD** - llamar a `ReloadArticlesAsync()`

4. **Validar archivos antes de upload** - tipo, tamaño, etc.

5. **Usar `@bind:event="oninput"` para búsqueda en tiempo real** - no `onchange`

6. **Dispose de subscripciones** - implementar `IDisposable` para `PersistingComponentStateSubscription`

7. **Acceso público a blobs de imágenes** - configurar en Azure

---

## 📝 Comandos Útiles

```powershell
# Build
dotnet build

# Publish
dotnet publish -c Release -o ..\publish

# Run locally
dotnet run --urls "http://localhost:5000"

# Ver estructura de blob
az storage blob list --container-name knowledge --connection-string "..."
```

---

## 🔧 Configuración Requerida (appsettings.json)

```json
{
  "AZURE_OPENAI_ENDPOINT": "https://xxx.openai.azure.com/",
  "AZURE_OPENAI_GPT_NAME": "text-embedding-3-small",
  "AZURE_OPENAI_API_KEY": "xxx",
  "AzureBlobStorage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=xxx;AccountKey=xxx;EndpointSuffix=core.windows.net"
  },
  "Authorization": {
    "AdminEmails": ["admin@company.com"]
  }
}
```

---

## 📊 Modelos de Datos Completos

### KnowledgeArticle
```csharp
public class KnowledgeArticle
{
    public int Id { get; set; }
    public string KBNumber { get; set; } = "";        // "KB0001"
    public string Title { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Context { get; set; } = "";
    public string AppliesTo { get; set; } = "";
    public string Content { get; set; } = "";         // Markdown
    public string KBGroup { get; set; } = "";         // Category
    public string KBOwner { get; set; } = "";
    public string TargetReaders { get; set; } = "";
    public string Language { get; set; } = "English";
    public List<string> Tags { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Author { get; set; } = "";
    public List<KBImage> Images { get; set; } = new();
    public string? SourceDocument { get; set; }
    
    [JsonIgnore]
    public double SearchScore { get; set; }
}

public class KBImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FileName { get; set; } = "";
    public string BlobUrl { get; set; } = "";
    public string AltText { get; set; } = "";
    public string? Caption { get; set; }
    public int Order { get; set; }
    public long SizeBytes { get; set; }
}
```

### User
```csharp
public enum UserRole { Tecnico, Admin }

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";     // Email
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    
    [JsonIgnore]
    public bool IsAdmin => Role == UserRole.Admin;
}
```

---

*Última actualización: 3 Diciembre 2025*

---

## 🆕 Cambios Recientes (Nov 28, 2025)

### Logo de Antolin en Sidebar
- Logo corporativo añadido en `wwwroot/logo.png`
- Visible en `NavMenu.razor` con efecto hover cyan
- Nombre de app "Operations One" debajo del logo

### Soporte PDF Mejorado
- `PdfDocumentService.cs` ahora extrae imágenes de PDFs
- Usa `TryGetPng()` y `RawBytes` de PdfPig
- Imágenes se suben automáticamente a Azure Blob Storage
- Detección automática de formato (JPEG/PNG) por magic bytes

### Knowledge Base UI Updates
- **Theme Toggle**: Botón light/dark mode en el article viewer
- **Imágenes Inline**: Screenshots integradas en el contenido Markdown (no galería separada)
- **Botón Admin**: Reubicado junto al subtítulo para consistencia con Scripts

### Scripts UI Updates  
- **Botón Admin**: Reubicado debajo del título, alineado a la derecha del subtítulo
- Layout `.header-subtitle-row` con flexbox

### Eliminación Permanente de KB
- Nuevo botón 🗑️ en tabla de admin (rojo)
- Modal de confirmación con advertencia
- Elimina artículo Y todas las imágenes asociadas
- Método `DeleteArticleAsync(string kbNumber)` en `KnowledgeSearchService`

### Limpieza de Código
- Eliminados módulos News y Weather (servicios, páginas, CSS, nav links)
- Eliminado acceso directo a Script Editor desde NavMenu (ahora solo vía botón Admin)

---

## 🆕 Cambios Recientes (Dic 2-3, 2025)

### Knowledge Chat Bot (Burbuja Asistente)
- **Componente**: `KnowledgeChat.razor` - Chat flotante tipo burbuja 🤖
- **Servicio**: `KnowledgeAgentService.cs` - RAG-based Q&A con múltiples fuentes
- **Funcionalidades**:
  - Búsqueda en KB local, Confluence y Context Documents (Jira tickets)
  - Respuestas en el mismo idioma que la pregunta del usuario
  - Links clickeables a tickets Jira y documentación
  - Referencias a artículos KB con navegación directa
  - Sugerencias de preguntas frecuentes

### Fix: Markdown Links en Chat Bot
**Problema**: Los enlaces markdown `[texto](url)` no se renderizaban correctamente.
El `HtmlEncode` convertía `[` y `]` antes de que el regex pudiera detectarlos.

**Solución** en `FormatMessage()`:
```csharp
// PASO 1: Extraer markdown links ANTES del HTML encode usando placeholders
var linkPlaceholders = new Dictionary<string, string>();
var markdownLinkPattern = new Regex(@"\[([^\]]+)\]\((https?://[^\)]+)\)");

text = markdownLinkPattern.Replace(text, match => {
    var placeholder = $"__LINK_PLACEHOLDER_{linkIndex++}__";
    linkPlaceholders[placeholder] = $"<a href=\"{url}\">{linkText}</a>";
    return placeholder;
});

// PASO 2: HtmlEncode para seguridad XSS
text = WebUtility.HtmlEncode(text);

// PASO 3: Restaurar los links preservados
foreach (var kvp in linkPlaceholders)
    text = text.Replace(kvp.Key, kvp.Value);
```

### KnowledgeAgentService - System Prompt Mejorado
- Instrucciones específicas para formatear links: `[Texto descriptivo](url)` 
- Priorización de tickets Jira sobre documentación Confluence
- Manejo especial de preguntas sobre acceso remoto → Zscaler
- Expansión de queries con sinónimos para mejor matching de tickets

### Context Documents (Jira Tickets)
- **Servicio**: `ContextSearchService.cs`
- Importación de Excel con categorías de tickets
- Búsqueda semántica con embeddings
- Matching expandido con sinónimos (BMW, VW, Ford, SAP, etc.)

### Confluence Integration
- **Servicio**: `ConfluenceKnowledgeService.cs`
- Autenticación con API Token (soporte Base64 para tokens con caracteres especiales)
- Cache de páginas en Azure Blob Storage
- Búsqueda semántica con embeddings
- **Multi-space sync**: Soporte para múltiples espacios (GAUKB, OPER, TECH, SDPA)
- **Sync individual**: Método `SyncSingleSpaceAsync()` para evitar timeouts
- **URLs en contexto**: Las páginas incluyen su URL web para referencias

---

## 🆕 Cambios Recientes (Dic 3, 2025)

### Confluence Multi-Space Sync
- **Configuración**: `Confluence__SpaceKeys` acepta múltiples spaces separados por coma
- **Nuevo método**: `SyncSingleSpaceAsync(spaceKey)` para sincronizar un space individual
- **Nuevo método**: `GetConfiguredSpaceKeys()` para listar spaces configurados
- **Mejora**: Logging detallado durante sincronización

### Botón Sync Confluence en KB Admin
- **Ubicación**: Sección nueva en `/knowledge/admin` (visible solo si Confluence está configurado)
- **Características**:
  - Panel con estadísticas: total de páginas, desglose por space
  - Botón "🔄 Sync All Spaces" - sincroniza todos los spaces secuencialmente
  - Botones individuales por space para sincronización selectiva
  - Spinner y mensajes de progreso durante sync
  - Mensajes de éxito ✅ o error ❌ al finalizar

### System Prompt Mejorado para Chat Bot
- **Priorización**: Documentación Confluence ANTES de sugerir tickets
- **URLs de referencia**: Incluye link a la página de Confluence en respuestas
- **Formato**: `📖 [Título del documento](URL)` para referencias
- **Casos especiales**: B2B Portals (BMW, VW, Ford), SAP, Zscaler

### Limpieza de Código
- **Eliminado**: Teams Bot integration completo
  - Carpeta `Bot/` (AdapterWithErrorHandler, OperationsBot)
  - Carpeta `TeamsManifest/`
  - Paquete `Microsoft.Bot.Builder.Integration.AspNet.Core`
  - Endpoints `/api/messages`, `/api/bot-test`, `/api/bot-status`
  - Configuración `Bot:` en appsettings.json
  - Documentación `TEAMS_INTEGRATION.md`

---
