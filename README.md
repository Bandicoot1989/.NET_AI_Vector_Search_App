# Operations One Centre

> Portal centralizado de herramientas IT con búsqueda inteligente por IA

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-blue)](https://blazor.net/)
[![Azure](https://img.shields.io/badge/Azure-App%20Service-0078D4)](https://azure.microsoft.com/)

## 🎯 Características

- **📜 Scripts Repository** - Biblioteca de PowerShell scripts con búsqueda semántica AI
- **📚 Knowledge Base** - Documentación técnica con soporte para Word docs y screenshots
- **🔐 Autenticación** - Azure Easy Auth con Microsoft Entra ID
- **🔍 Búsqueda AI** - Embeddings de Azure OpenAI para búsqueda semántica
- **☁️ Cloud Native** - Azure Blob Storage para persistencia

## 🚀 Quick Start

### Prerrequisitos

- .NET 10.0 SDK
- Azure Subscription con:
  - Azure OpenAI (modelo `text-embedding-3-small`)
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
  "AZURE_OPENAI_GPT_NAME": "text-embedding-3-small",
  "AZURE_OPENAI_API_KEY": "your-key",
  "AzureBlobStorage": {
    "ConnectionString": "your-connection-string"
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
│   ├── Pages/           # Páginas Blazor
│   │   ├── Scripts.razor
│   │   ├── Knowledge.razor
│   │   └── KnowledgeAdmin.razor
│   └── Layout/          # Layout y navegación
├── Models/              # Modelos de datos
├── Services/            # Servicios de negocio
└── wwwroot/            # Assets estáticos

docs/
├── PROJECT_DOCUMENTATION.md  # Documentación completa
└── AI_CONTEXT.md            # Contexto para IA (errores resueltos)
```

## 📖 Documentación

- [Documentación del Proyecto](docs/PROJECT_DOCUMENTATION.md) - Arquitectura, módulos, configuración
- [Contexto para IA](docs/AI_CONTEXT.md) - Errores resueltos y patrones establecidos

## 🛠️ Tecnologías

| Paquete | Versión | Uso |
|---------|---------|-----|
| Azure.AI.OpenAI | 2.1.0 | Embeddings para búsqueda semántica |
| Azure.Storage.Blobs | 12.26.0 | Almacenamiento de datos |
| Azure.Identity | 1.17.1 | Autenticación Azure |
| DocumentFormat.OpenXml | 3.3.0 | Conversión de Word docs |

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

- **v2.1** - Filtros en admin panel, fix artículos inactivos
- **v2.0** - KB Admin con Word upload e imágenes
- **v1.2** - Autenticación Azure Easy Auth
- **v1.1** - Knowledge Base básico
- **v1.0** - Scripts Repository inicial

## 📄 Licencia

MIT License - ver [LICENSE](LICENSE)

---

Desarrollado para el equipo de Operations IT 🚀
