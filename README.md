# Operations One Centre - AI Helpdesk Bot

> Sistema multi-agente inteligente para soporte IT con RAG, búsqueda semántica y especialistas SAP/Network

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-blue)](https://blazor.net/)
[![Azure](https://img.shields.io/badge/Azure-OpenAI-0078D4)](https://azure.microsoft.com/)
[![Architecture](https://img.shields.io/badge/Architecture-Multi--Agent-green)]()

## 🎯 Características

### Core Features
- **🤖 Multi-Agent System** - Router inteligente con especialistas SAP, Network y General
- **🔍 RAG Search** - Retrieval-Augmented Generation con Knowledge Base, Context y Confluence
- **📊 Vector Search** - Embeddings con Azure OpenAI (text-embedding-3-small)
- **💬 Chat Interface** - Bot conversacional con historial y streaming
- **🎫 Jira Solution Harvester** - BackgroundService que recolecta soluciones de tickets resueltos automáticamente

### Búsqueda Inteligente
- **Query Expansion** - Expansión automática de consultas con sinónimos
- **RRF Ranking** - Reciprocal Rank Fusion para combinar resultados
- **Semantic Cache** - Cache de respuestas exitosas (92% similitud)
- **Intent Detection** - Detección de intención (informativa vs procedural)

### Especialistas
- **SAP Expert** - Transacciones, roles, posiciones con lookup automático
- **Network Expert** - Conectividad, VPN, acceso remoto, herramientas de red
- **Knowledge Expert** - Documentación técnica, procedimientos, troubleshooting

### Gestión
- **📜 Scripts Repository** - Biblioteca de PowerShell scripts con búsqueda semántica
- **📚 Knowledge Base** - Documentación técnica con Word docs y screenshots
- **📝 Feedback System** - Sistema de feedback con auto-learning
- **🔐 Azure AD Auth** - Autenticación con Microsoft Entra ID

## 🏗️ Arquitectura

```
┌─────────────────────────────────────────────────────────────────┐
│                    BLAZOR SERVER UI                             │
│                  KnowledgeChat.razor                            │
└──────────────────────────┬──────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────┐
│                   AgentRouterService                            │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐ │
│  │ SAP Keywords│  │Network Keys │  │ DetermineAgentAsync()   │ │
│  │ transaccion │  │ vpn, remote │  │ → SAP / Network / Gen   │ │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘ │
└──────────────────────────┬──────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────┐
│               KnowledgeAgentService (Unified)                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ AskWithSpecialistAsync(question, type, context, history) │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐                │
│  │ KB Search  │  │  Context   │  │ Confluence │  ←─ Parallel   │
│  │ (Vector)   │  │  Search    │  │   Search   │     Search     │
│  └────────────┘  └────────────┘  └────────────┘                │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Query Expansion → RRF Ranking → Intent Detection       │   │
│  └─────────────────────────────────────────────────────────┘   │
└──────────────────────────┬──────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────┐
│                    Azure OpenAI                                 │
│            gpt-4o-mini (Chat) + text-embedding-3-small          │
└─────────────────────────────────────────────────────────────────┘
```

## 🚀 Quick Start

### Prerrequisitos

- .NET 10.0 SDK
- Azure Subscription con:
  - Azure OpenAI (modelos `gpt-4o-mini` y `text-embedding-3-small`)
  - Azure Storage Account
  - Azure App Service (opcional para deploy)

### Configuración

1. Clonar el repositorio:
```bash
git clone https://github.com/Bandicoot1989/.NET_AI_Vector_Search_App.git
cd .NET_AI_Vector_Search_App
```

2. Configurar `appsettings.json`:
```json
{
  "AZURE_OPENAI_ENDPOINT": "https://your-resource.openai.azure.com/",
  "AZURE_OPENAI_GPT_NAME": "gpt-4o-mini",
  "AZURE_OPENAI_EMBEDDING_NAME": "text-embedding-3-small",
  "AZURE_OPENAI_API_KEY": "your-key",
  "AzureStorage": {
    "ConnectionString": "your-connection-string"
  },
  "Confluence": {
    "BaseUrl": "https://your-wiki.atlassian.net",
    "Username": "user@company.com",
    "ApiToken": "your-token",
    "SpaceKey": "DOCS"
  },
  "Authorization": {
    "AdminEmails": ["admin@yourcompany.com"]
  }
}
```

3. Ejecutar:
```bash
cd RecipeSearchWeb
dotnet run
```

4. Abrir `https://localhost:5001`

## 📁 Estructura del Proyecto

```
RecipeSearchWeb/
├── Components/
│   ├── Pages/                    # Páginas Blazor
│   │   ├── Knowledge.razor       # Chat principal del bot
│   │   ├── FeedbackAdmin.razor   # Panel admin de feedback
│   │   ├── AgentContext.razor    # Gestión de contexto
│   │   └── KnowledgeAdmin.razor  # Admin de Knowledge Base
│   ├── KnowledgeChat.razor       # Componente del chat
│   └── Layout/                   # Layout y navegación
├── Services/                     # 23 servicios de negocio
│   ├── AgentRouterService.cs     # Router multi-agente
│   ├── KnowledgeAgentService.cs  # Agente principal RAG
│   ├── SapAgentService.cs        # Especialista SAP
│   ├── SapLookupService.cs       # Lookup de SAP (posiciones→roles→trans)
│   ├── NetworkAgentService.cs    # Especialista Network
│   ├── FeedbackService.cs        # Sistema de feedback
│   ├── ContextSearchService.cs   # Búsqueda en contexto
│   └── ...
├── Models/                       # Modelos de datos
│   ├── SapModels.cs              # Transaction, Role, Position
│   ├── ContextDocument.cs        # Documentos de contexto
│   └── ChatFeedback.cs           # Modelo de feedback
├── Interfaces/                   # Contratos de servicios
└── wwwroot/                      # Assets estáticos

docs/
├── TECHNICAL_REFERENCE.md        # 📘 Documentación técnica completa
├── PROJECT_DOCUMENTATION.md      # Arquitectura general
├── TIER3_MULTI_AGENT_SYSTEM.md   # Sistema multi-agente
├── TIER3_SAP_SPECIALIST_AGENT.md # Agente SAP
└── AI_CONTEXT.md                 # Contexto para IA
```

## 📖 Documentación

| Documento | Descripción |
|-----------|-------------|
| [Technical Reference](docs/TECHNICAL_REFERENCE.md) | **Documentación técnica completa** - Todas las funciones, clases, flujos |
| [Project Documentation](docs/PROJECT_DOCUMENTATION.md) | Arquitectura general y módulos |
| [Multi-Agent System](docs/TIER3_MULTI_AGENT_SYSTEM.md) | Sistema de múltiples agentes |
| [SAP Specialist](docs/TIER3_SAP_SPECIALIST_AGENT.md) | Agente especialista SAP |
| [Clean Architecture](docs/CLEAN_ARCHITECTURE.md) | Patrones de arquitectura |

## 🛠️ Tecnologías

| Paquete | Versión | Uso |
|---------|---------|-----|
| Azure.AI.OpenAI | 2.1.0 | Chat (gpt-4o-mini) y Embeddings |
| Azure.Storage.Blobs | 12.26.0 | Almacenamiento (KB, Context, Feedback) |
| Azure.Identity | 1.17.1 | Autenticación Azure AD |
| DocumentFormat.OpenXml | 3.3.0 | Conversión de Word docs |
| System.Numerics.Tensors | - | Cálculo de similitud coseno |

## 🔧 Servicios Principales

| Servicio | Responsabilidad |
|----------|-----------------|
| `AgentRouterService` | Enruta queries al especialista correcto |
| `KnowledgeAgentService` | RAG principal con búsqueda unificada |
| `SapLookupService` | Lookup: Posición → Roles → Transacciones |
| `FeedbackService` | Feedback, cache, auto-learning |
| `ContextSearchService` | Búsqueda en documentos de contexto |
| `ConfluenceKnowledgeService` | Integración con Confluence Wiki |

## 🔑 Roles

- **Tecnico**: Acceso de lectura a scripts y KB
- **Admin**: CRUD completo en scripts y KB

Los admins se configuran en `appsettings.json` → `Authorization.AdminEmails`

## 📦 Deploy

### Publicar
```bash
cd RecipeSearchWeb
dotnet publish -c Release -o ../publish
```

### Azure App Service
1. Crear App Service (.NET 10, Windows)
2. Configurar Authentication → Microsoft provider
3. Deploy vía VS Code, Azure CLI o GitHub Actions
4. Configurar Application Settings

## 📝 Changelog

- **v3.0** - Sistema Multi-Agente unificado, Feedback System, Auto-learning
- **v2.5** - Integración Confluence, Context Search, Query Expansion
- **v2.1** - Filtros en admin panel, fix artículos inactivos
- **v2.0** - KB Admin con Word upload e imágenes
- **v1.2** - Autenticación Azure Easy Auth
- **v1.1** - Knowledge Base básico
- **v1.0** - Scripts Repository inicial

## 📄 Licencia

MIT License - ver [LICENSE](LICENSE)

---

Desarrollado para el equipo de Operations IT 🚀
