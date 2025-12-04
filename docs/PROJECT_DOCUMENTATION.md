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
- **Knowledge Chat Bot**: Asistente IA tipo burbuja 🤖 con RAG (Retrieval Augmented Generation)
- **Integración Confluence**: Sincronización con páginas de Confluence como fuente adicional de KB
- **Context Documents**: Importación de tickets Jira desde Excel para guiar usuarios

La aplicación está desplegada en **Azure App Service** con autenticación **Azure Easy Auth** (Microsoft Entra ID).

---

## Arquitectura

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Azure App Service                              │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │                  Blazor Server (.NET 10)                     │    │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │    │
│  │  │   Scripts   │  │ Knowledge   │  │ Knowledge   │         │    │
│  │  │   Module    │  │ Base Module │  │ Chat Bot 🤖 │         │    │
│  │  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘         │    │
│  │         │                │                │                 │    │
│  │  ┌──────┴────────────────┴────────────────┴────────────┐   │    │
│  │  │                   Services Layer                     │   │    │
│  │  │  ScriptSearchService    | KnowledgeSearchService     │   │    │
│  │  │  ScriptStorageService   | KnowledgeStorageService    │   │    │
│  │  │  KnowledgeImageService  | WordDocumentService        │   │    │
│  │  │  PdfDocumentService     | AzureAuthService           │   │    │
│  │  │  UserStateService       | MarkdownRenderService      │   │    │
│  │  │  ─────────── RAG Services ───────────                │   │    │
│  │  │  KnowledgeAgentService  | ConfluenceKnowledgeService │   │    │
│  │  │  ContextSearchService   | ContextStorageService      │   │    │
│  │  └──────────────────────────────────────────────────────┘   │    │
│  └─────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
         │                    │                    │              │
         ▼                    ▼                    ▼              ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────┐  ┌──────────────┐
│  Azure OpenAI   │  │  Azure Blob     │  │ Azure Easy  │  │  Confluence  │
│  Embeddings +   │  │  Storage        │  │ Auth (AAD)  │  │  REST API    │
│  Chat (GPT-4o)  │  └─────────────────┘  └─────────────┘  └──────────────┘
└─────────────────┘
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
  - **Confluence Sync Panel**: 
    - Vista de spaces configurados con conteo de páginas
    - Botón "🔄 Sync All Spaces" para sincronizar todos
    - Botones individuales por space para sincronización selectiva
    - Progress visual durante sincronización
    - Mensajes de éxito/error

### 4. Knowledge Chat Bot (Burbuja 🤖)

- **Componente**: `KnowledgeChat.razor` - Flotante en esquina inferior derecha
- **Características**:
  - Interfaz tipo chat con animaciones
  - Sugerencias de preguntas frecuentes
  - Referencias a artículos KB clickeables
  - Links a tickets Jira formateados correctamente
  - Indicador de "pensando" mientras procesa
  - Histórico de conversación en sesión

#### Arquitectura Multi-Agente (Tier 3)

El Chat Bot utiliza un sistema de **agentes especializados** que enrutan las consultas según su dominio:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         AgentRouterService                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  1. ¿Es query de red/Zscaler?  ──yes──► NetworkAgentService             │
│     • zscaler, vpn, remoto, red              • Documentación Zscaler    │
│                                               • Tickets de red           │
│                                                                          │
│  2. ¿Es query de SAP?          ──yes──► SapAgentService                 │
│     • transacción, rol, posición             • Lookups O(1) en memoria  │
│     • códigos SAP (MM01, SY01...)            • SAP_Dictionary.xlsx      │
│                                               • Tickets SAP              │
│                                                                          │
│  3. Default                    ──────► KnowledgeAgentService            │
│                                               • KB Local                 │
│                                               • Confluence               │
│                                               • Context Documents        │
│                                               • Tickets generales        │
└─────────────────────────────────────────────────────────────────────────┘
```

#### Flujo RAG del Chat Bot
```
1. Usuario hace pregunta
2. AgentRouterService detecta tipo de query
3. Agente especializado procesa:
   
   NetworkAgent:
   - Busca documentación Confluence sobre Zscaler/VPN
   - Obtiene tickets de red desde Context_Jira_Forms.xlsx
   - Genera respuesta con Azure OpenAI
   
   SapAgent:
   - Detecta tipo de query SAP (transacción, rol, posición)
   - SapLookupService hace búsqueda O(1) en memoria
   - Obtiene tickets SAP desde Context_Jira_Forms.xlsx
   - Genera respuesta tabular con Azure OpenAI
   
   KnowledgeAgent (General):
   - Expande query con sinónimos
   - Búsqueda paralela en KB, Confluence, Context
   - Obtiene tickets desde Context_Jira_Forms.xlsx
   - Genera respuesta con Azure OpenAI

4. FormatMessage() renderiza markdown → HTML
```

#### Principio de Tickets
> **Todos los tickets sugeridos vienen de `Context_Jira_Forms.xlsx`**.
> Los agentes NUNCA inventan URLs de tickets.

### 5. Agent Context (`/agentcontext`)

- **Vista**: Panel de debug para Context Documents
- **Funciones**:
  - Ver documentos importados
  - Importar Excel con tickets Jira
  - Probar búsquedas semánticas

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

### Servicios de Autenticación

#### AzureAuthService
Lee la identidad del usuario desde Azure Easy Auth headers:
- `X-MS-CLIENT-PRINCIPAL-NAME`: Email del usuario
- `X-MS-CLIENT-PRINCIPAL-ID`: ID único
- Lista de admins configurable en `appsettings.json`

#### UserStateService
Servicio scoped que mantiene el estado del usuario durante la sesión interactiva.

#### CascadingUserState.razor
Componente que:
1. Lee usuario de HttpContext (render estático)
2. Persiste con `PersistentComponentState`
3. Restaura en modo interactivo
4. Propaga vía `CascadingValue`

### Servicios de Búsqueda

#### ScriptSearchService / KnowledgeSearchService
- Búsqueda semántica con embeddings de Azure OpenAI
- Cálculo de similitud coseno
- Ranking de resultados

#### ContextSearchService
- Importación de Excel con categorías de tickets Jira
- Campos: Name, Description, Keywords, Link (URL)
- Búsqueda semántica para matching de problemas → tickets
- **Fuente principal para URLs de tickets en todos los agentes**

### Servicios de Agentes (Tier 3 Multi-Agent)

#### AgentRouterService
- **Implementa IKnowledgeAgentService** (inyectado en KnowledgeChat.razor)
- Detecta tipo de query y enruta al agente apropiado
- Orden de prioridad: Network → SAP → General

#### KnowledgeAgentService (Agente General)
- **RAG (Retrieval Augmented Generation)** para respuestas contextuales
- Busca en múltiples fuentes: KB local, Confluence, Context Documents
- Usa Azure OpenAI Chat (gpt-4o-mini) para generar respuestas
- System prompt con instrucciones específicas para formato de links
- Expansión de queries con sinónimos para mejor matching
- Tickets desde Context_Jira_Forms.xlsx

#### SapAgentService (Agente SAP)
- Especializado en consultas SAP (transacciones, roles, posiciones)
- Usa SapLookupService para búsquedas O(1) en memoria
- Detecta tipos de query: TransactionInfo, RoleTransactions, PositionAccess, etc.
- Prompt especializado para formato tabular
- Tickets SAP desde Context_Jira_Forms.xlsx

#### NetworkAgentService (Agente de Red)
- Especializado en Zscaler, VPN, conectividad
- Conocimiento embebido sobre trabajo remoto
- Integración con documentación Confluence
- Tickets de red desde Context_Jira_Forms.xlsx

### Servicios SAP

#### SapKnowledgeService
- Carga SAP_Dictionary.xlsx desde Azure Blob Storage
- Parsea 4 hojas: Dictionary_PL, Roles, Positions, BusinessRoles
- Mantiene datos en memoria como singleton

#### SapLookupService
- Diccionarios indexados para búsquedas O(1)
- Métodos: GetTransaction, GetTransactionsByRole, GetTransactionsByPosition
- Búsqueda fuzzy cuando no hay match exacto

### Servicios de Confluence

#### ConfluenceKnowledgeService
- Integración con Atlassian Confluence REST API
- Autenticación con API Token (soporte Base64)
- Cache de páginas en Azure Blob Storage
- Búsqueda semántica con embeddings

### Servicios de Documentos

#### WordDocumentService
- Convierte `.docx` a `KnowledgeArticle`
- Extrae metadata de tablas GA KB
- Extrae contenido como Markdown
- Extrae imágenes embebidas

#### PdfDocumentService
- Convierte `.pdf` a `KnowledgeArticle`
- Extracción de texto con PdfPig
- Extracción automática de imágenes embebidas
- Detección de formato por magic bytes

### Servicios de Storage

#### StorageServices
- CRUD contra Azure Blob Storage
- Serialización JSON
- Estructura: `{container}/{tipo}/{archivo}.json`

#### KnowledgeImageService
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
  "AZURE_OPENAI_CHAT_NAME": "gpt-4o-mini",
  "AZURE_OPENAI_API_KEY": "xxx",
  "AzureStorage": {
    "ConnectionString": "xxx",
    "ContainerName": "scripts",
    "KnowledgeContainerName": "knowledge",
    "ConfluenceCacheContainer": "confluence-cache"
  },
  "Authorization": {
    "AdminEmails": [
      "admin1@company.com",
      "admin2@company.com"
    ]
  },
  "Confluence": {
    "BaseUrl": "https://your-domain.atlassian.net",
    "Email": "your-email@company.com",
    "ApiTokenBase64": "BASE64_ENCODED_API_TOKEN",
    "SpaceKeys": "GAUKB,OPER,TECH,SDPA"
  }
}
```

### Variables de Configuración

| Variable | Descripción |
|----------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Endpoint de Azure OpenAI |
| `AZURE_OPENAI_GPT_NAME` | Modelo para embeddings (text-embedding-3-small) |
| `AZURE_OPENAI_CHAT_NAME` | Modelo para chat (gpt-4o-mini) |
| `AZURE_OPENAI_API_KEY` | API Key de Azure OpenAI |
| `AzureBlobStorage:ConnectionString` | Connection string de Azure Storage |
| `Authorization:AdminEmails` | Lista de emails con rol Admin |
| `Confluence:BaseUrl` | URL base de Confluence Cloud |
| `Confluence:Email` | Email para autenticación |
| `Confluence:ApiTokenBase64` | Token API en Base64 (soporta caracteres especiales) |
| `Confluence:SpaceKeys` | Espacios de Confluence a sincronizar |

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
| Dic 2, 2025 | 3.0 | **Knowledge Chat Bot** - Asistente IA con RAG |
| Dic 2, 2025 | 3.1 | Integración Confluence KB |
| Dic 2, 2025 | 3.2 | Context Documents (Jira tickets desde Excel) |
| Dic 3, 2025 | 3.3 | Fix: Markdown links en chat bot (preservar antes de HtmlEncode) |
| Dic 3, 2025 | 3.4 | **Confluence Multi-Space Sync** - Soporte para múltiples spaces (GAUKB, OPER, TECH, SDPA) |
| Dic 3, 2025 | 3.5 | **Botón Sync Confluence** en KB Admin - Sincronización con un click, progress visual |
| Dic 3, 2025 | 3.6 | System prompt mejorado - Prioriza documentación Confluence, incluye URLs de páginas |
| Dic 3, 2025 | 3.7 | Limpieza: Eliminado Teams Bot integration (no se implementará) |

---

*Última actualización: 3 Diciembre 2025*
