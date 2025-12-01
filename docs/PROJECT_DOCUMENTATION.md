# Operations One Centre - Documentación del Proyecto

## 📋 Índice

1. [Descripción General](#descripción-general)
2. [Arquitectura](#arquitectura)
3. [Tecnologías](#tecnologías)
4. [Estructura del Proyecto](#estructura-del-proyecto)
5. [Módulos](#módulos)
6. [Modelos de Datos](#modelos-de-datos)
7. [Servicios](#servicios)
8. [Autenticación](#autenticación)
9. [Almacenamiento Azure](#almacenamiento-azure)
10. [Configuración](#configuración)
11. [Despliegue](#despliegue)

---

## Descripción General

**Operations One Centre** es una aplicación web empresarial desarrollada en Blazor .NET 10 que centraliza herramientas para el equipo de operaciones IT. Incluye:

- **Scripts Repository**: Biblioteca de scripts PowerShell con búsqueda semántica por IA
- **Knowledge Base (KB)**: Base de conocimientos con artículos técnicos, soporte para Word docs, PDFs y screenshots

La aplicación está desplegada en **Azure App Service** con autenticación **Azure Easy Auth** (Microsoft Entra ID).

---

## Arquitectura

```
┌─────────────────────────────────────────────────────────────────┐
│                    Azure App Service                             │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                 Blazor Server (.NET 10)                    │  │
│  │  ┌─────────────┐  ┌─────────────┐                       │  │
│  │  │   Scripts   │  │ Knowledge   │                       │  │
│  │  │   Module    │  │ Base Module │                       │  │
│  │  └──────┬──────┘  └──────┬──────┘                       │  │
│  │         │                │                               │  │
│  │  ┌──────┴────────────────┴──────────────────────────┐   │  │
│  │  │              Services Layer                       │    │  │
│  │  │  ScriptSearchService | KnowledgeSearchService    │    │  │
│  │  │  ScriptStorageService | KnowledgeStorageService  │    │  │
│  │  │  KnowledgeImageService | WordDocumentService     │    │  │
│  │  │  PdfDocumentService | AzureAuthService           │    │  │
│  │  │  UserStateService                                │    │  │
│  │  └──────────────────────────────────────────────────┘    │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  Azure OpenAI   │  │  Azure Blob     │  │   Azure Easy    │
│  (Embeddings)   │  │  Storage        │  │   Auth (AAD)    │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

---

## Tecnologías

| Tecnología | Versión | Propósito |
|------------|---------|----------|
| .NET | 10.0 | Framework principal |
| Blazor Server | Interactive | UI con renderizado SSR + Interactivo |
| Azure.AI.OpenAI | 2.1.0 | Búsqueda semántica con embeddings |
| Azure.Storage.Blobs | 12.26.0 | Almacenamiento de scripts/KB/imágenes |
| Azure.Identity | 1.17.1 | Autenticación con Azure |
| DocumentFormat.OpenXml | 3.3.0 | Conversión de Word a Markdown |
| PdfPig | 0.1.12 | Extracción de texto e imágenes de PDFs |

---

## Estructura del Proyecto

```
RecipeSearchWeb/
├── Program.cs                    # Configuración y startup
├── RecipeSearchWeb.csproj        # Dependencias NuGet
├── appsettings.json              # Configuración (Azure keys, etc.)
│
├── Components/
│   ├── App.razor                 # Componente raíz
│   ├── Routes.razor              # Enrutamiento
│   ├── CascadingUserState.razor  # Proveedor de estado de usuario
│   │
│   ├── Layout/
│   │   ├── MainLayout.razor      # Layout principal
│   │   ├── NavMenu.razor         # Menú de navegación
│   │   └── ReconnectModal.razor  # Modal de reconexión SignalR
│   │
│   └── Pages/
│       ├── Home.razor            # Página de inicio
│       ├── Scripts.razor         # Biblioteca de scripts
│       ├── ScriptEditor.razor    # Editor de scripts (Admin)
│       ├── Knowledge.razor       # Knowledge Base (lectura)
│       └── KnowledgeAdmin.razor  # KB Admin (gestión)
│
├── Models/
│   ├── Script.cs                 # Modelo de script PowerShell
│   ├── KnowledgeArticle.cs       # Modelo de artículo KB + KBImage
│   ├── User.cs                   # Modelo de usuario + UserRole enum
│   └── Recipe.cs                 # Modelo legacy (recetas demo)
│
├── Services/
│   ├── AzureAuthService.cs       # Autenticación Azure Easy Auth
│   ├── UserStateService.cs       # Persistencia de estado de usuario
│   ├── ScriptSearchService.cs    # Búsqueda AI de scripts
│   ├── ScriptStorageService.cs   # Azure Blob para scripts
│   ├── KnowledgeSearchService.cs # Búsqueda AI de KB
│   ├── KnowledgeStorageService.cs# Azure Blob para KB
│   ├── KnowledgeImageService.cs  # Azure Blob para imágenes KB
│   ├── WordDocumentService.cs    # Conversión Word → KB
│   └── PdfDocumentService.cs     # Conversión PDF → KB (texto + imágenes)
│
└── wwwroot/
    ├── app.css                   # Estilos globales
    └── css/
        └── recipes.css           # Estilos de recetas
```

---

## Módulos

### 1. Scripts Repository (`/scripts`)

- **Vista**: Biblioteca de scripts PowerShell categorizados
- **Búsqueda**: Semántica con Azure OpenAI embeddings
- **Categorías**: System Admin, File Management, Network, Security, Automation, Azure, Git, Development
- **Admin Features**: Crear, editar, eliminar scripts (solo admin)

### 2. Knowledge Base (`/knowledge`)

- **Vista**: Artículos de documentación técnica con theme toggle (light/dark)
- **Búsqueda**: Por texto y categoría (KBGroup)
- **Contenido**: Markdown con imágenes inline (integradas en el contenido)
- **Botón Admin**: Visible solo para admins, ubicado junto al subtítulo
- **Admin Features** (`/knowledge/admin`):
  - Subir documentos Word (.docx) o PDF (.pdf) con conversión automática
  - Extracción automática de imágenes de PDFs
  - Crear/editar artículos manualmente
  - Gestión de screenshots y imágenes
  - Activar/desactivar artículos
  - **Eliminar artículos permanentemente** (con confirmación)
  - Filtros por categoría y estado

### 3. Knowledge Admin (`/knowledge/admin`)

- **Acceso**: Solo usuarios Admin
- **Funciones**:
  - Lista de TODOS los artículos (activos e inactivos)
  - Búsqueda y filtros avanzados
  - Upload de Word docs
  - Editor de artículos completo
  - Gestor de imágenes con upload múltiple

---

## Modelos de Datos

### Script
```csharp
public class Script
{
    public int Key { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Purpose { get; set; }
    public string Complexity { get; set; }  // Beginner, Intermediate, Advanced
    public string Category { get; set; }    // System Admin, File Management, etc.
    public string Code { get; set; }        // PowerShell code
    public string Parameters { get; set; }
    public ReadOnlyMemory<float> Vector { get; set; }  // AI embedding
    public int ViewCount { get; set; }
    public DateTime? LastViewed { get; set; }
}
```

### KnowledgeArticle
```csharp
public class KnowledgeArticle
{
    public int Id { get; set; }
    public string KBNumber { get; set; }       // e.g., "KB0001"
    public string Title { get; set; }
    public string ShortDescription { get; set; }
    public string Purpose { get; set; }
    public string Context { get; set; }
    public string AppliesTo { get; set; }
    public string Content { get; set; }        // Markdown content
    public string KBGroup { get; set; }        // Category/Group
    public string KBOwner { get; set; }
    public string TargetReaders { get; set; }
    public string Language { get; set; }
    public List<string> Tags { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Author { get; set; }
    public List<KBImage> Images { get; set; }  // Screenshots
    public string? SourceDocument { get; set; } // Original Word file
}

public class KBImage
{
    public string Id { get; set; }
    public string FileName { get; set; }
    public string BlobUrl { get; set; }
    public string AltText { get; set; }
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
    public string Username { get; set; }      // Email from Azure AD
    public string FullName { get; set; }
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    public bool IsAdmin => Role == UserRole.Admin;
}
```

---

## Servicios

### AzureAuthService
Lee la identidad del usuario desde Azure Easy Auth headers:
- `X-MS-CLIENT-PRINCIPAL-NAME`: Email del usuario
- `X-MS-CLIENT-PRINCIPAL-ID`: ID único
- Lista de admins configurable en `appsettings.json`

### UserStateService
Servicio scoped que mantiene el estado del usuario durante la sesión interactiva.

### CascadingUserState.razor
Componente que:
1. Lee usuario de HttpContext (render estático)
2. Persiste con `PersistentComponentState`
3. Restaura en modo interactivo
4. Propaga vía `CascadingValue`

### ScriptSearchService / KnowledgeSearchService
- Búsqueda semántica con embeddings de Azure OpenAI
- Cálculo de similitud coseno
- Ranking de resultados

### StorageServices
- CRUD contra Azure Blob Storage
- Serialización JSON
- Estructura: `{container}/{tipo}/{archivo}.json`

### WordDocumentService
- Convierte `.docx` a `KnowledgeArticle`
- Extrae metadata de tablas GA KB
- Extrae contenido como Markdown
- Extrae imágenes embebidas

### KnowledgeImageService
- Upload de imágenes a Azure Blob
- Ruta: `knowledge/images/{kbNumber}/{id}_{filename}`
- Validación de tipos (JPEG, PNG, GIF, WebP, BMP)
- Límite: 5MB por imagen

---

## Autenticación

### Azure Easy Auth
- Configurado en Azure App Service
- Provider: Microsoft (Azure AD)
- Headers automáticos para usuario autenticado

### Flujo de Autenticación en Blazor Server
```
1. Usuario accede → Azure Easy Auth verifica → Redirect si no autenticado
2. Request llega con headers X-MS-CLIENT-PRINCIPAL-*
3. AzureAuthService lee headers (render estático)
4. CascadingUserState persiste usuario
5. Modo interactivo restaura de PersistentComponentState
6. Componentes acceden via UserStateService o CascadingParameter
```

### Patrón Robusto para Componentes Interactivos
```csharp
// 4 estrategias de fallback:
1. PersistentComponentState (restauración)
2. AzureAuthService.GetCurrentUser() (HttpContext)
3. UserStateService.CurrentUser (sesión scoped)
4. CascadingParameter (fallback)
```

---

## Almacenamiento Azure

### Blob Containers

| Container | Contenido | Estructura |
|-----------|-----------|------------|
| `scripts` | Scripts PowerShell | `scripts/all-scripts.json` |
| `knowledge` | Artículos KB | `knowledge/articles.json` |
| `knowledge` | Imágenes KB | `knowledge/images/{kbNumber}/{file}` |

### Connection String
Configurado en `appsettings.json`:
```json
{
  "AzureBlobStorage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;..."
  }
}
```

---

## Configuración

### appsettings.json
```json
{
  "AZURE_OPENAI_ENDPOINT": "https://xxx.openai.azure.com/",
  "AZURE_OPENAI_GPT_NAME": "text-embedding-3-small",
  "AZURE_OPENAI_API_KEY": "xxx",
  "AzureBlobStorage": {
    "ConnectionString": "xxx"
  },
  "Authorization": {
    "AdminEmails": [
      "admin1@company.com",
      "admin2@company.com"
    ]
  }
}
```

---

## Despliegue

### Build & Publish
```powershell
cd RecipeSearchWeb
dotnet build
dotnet publish -c Release -o ..\publish
```

### Azure App Service
1. Crear App Service (Windows, .NET 10)
2. Configurar Authentication → Microsoft provider
3. Subir contenido de `/publish`
4. Configurar Application Settings con los valores de appsettings

### Comandos Útiles
```powershell
# Ejecutar localmente
dotnet run --urls "http://localhost:5000"

# Ver logs Azure
az webapp log tail --name <app-name> --resource-group <rg>

# Deploy via Azure CLI
az webapp deploy --name <app> --src-path publish.zip
```

---

## Changelog

| Fecha | Versión | Cambios |
|-------|---------|--------|
| Nov 2024 | 1.0 | Scripts Repository inicial |
| Nov 2024 | 1.1 | Knowledge Base básico |
| Nov 2024 | 1.2 | Autenticación Azure Easy Auth |
| Nov 2024 | 2.0 | KB Admin con Word upload e imágenes |
| Nov 2024 | 2.1 | Fix: Artículos inactivos en admin + filtros |
| Nov 28, 2025 | 2.2 | Logo Antolin en sidebar, PDF support con extracción de imágenes |
| Nov 28, 2025 | 2.3 | Light/dark mode toggle en KB viewer, imágenes inline en contenido |
| Nov 28, 2025 | 2.4 | Eliminación News/Weather modules, botón Admin reubicado |
| Nov 28, 2025 | 2.5 | Eliminación permanente de artículos KB con confirmación |

---

*Última actualización: 28 Noviembre 2025*
