# Jira Solution Harvester

## Descripción General

El **Jira Solution Harvester** es un sistema automatizado que extrae soluciones de tickets resueltos en Jira Service Management para enriquecer la base de conocimiento del asistente de Helpdesk.

## Estado del Proyecto

| Fase | Descripción | Estado | Fecha |
|------|-------------|--------|-------|
| **Fase 1** | Diseño y Modelos | ✅ Completado | 9 Dic 2025 |
| **Fase 2** | Cliente Jira API | ✅ Completado | 10 Dic 2025 |
| **Fase 3** | Harvesting Automático | ✅ Completado | 10 Dic 2025 |
| **Fase 4** | Integración Búsqueda | ⏳ Pendiente | - |

---

## Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│                    JIRA CLOUD                                │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  Tickets Resueltos (MT-*, MTT-*)                    │    │
│  │  - Summary, Description, Comments                    │    │
│  │  - Resolution, Status, Assignee                      │    │
│  └─────────────────────────────────────────────────────┘    │
└──────────────────────────┬──────────────────────────────────┘
                           │ REST API v3 (POST /search/jql)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                    JIRA CLIENT                               │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  JiraClient.cs                                       │    │
│  │  - Basic Auth (Email + API Token)                    │    │
│  │  - GetResolvedTicketsAsync()                         │    │
│  │  - GetTicketCommentsAsync()                          │    │
│  └─────────────────────────────────────────────────────┘    │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│               SOLUTION HARVESTER (Fase 3)                   │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  JiraSolutionHarvester.cs                            │    │
│  │  - Timer/WebJob cada 24h                             │    │
│  │  - Filtrado por keywords (solución, fix, resolved)   │    │
│  │  - Extracción de pasos de resolución                 │    │
│  └─────────────────────────────────────────────────────┘    │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                 KNOWLEDGE BASE                               │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  Azure Blob Storage                                  │    │
│  │  - harvested-solutions.json                          │    │
│  │  - Embeddings generados automáticamente              │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

---

## Fase 2: Cliente Jira API (Completado)

### Archivos Implementados

| Archivo | Descripción |
|---------|-------------|
| `Services/JiraClient.cs` | Cliente HTTP para Jira Cloud REST API |
| `Controllers/JiraTestController.cs` | Endpoints de prueba |
| `Models/JiraTicket.cs` | Modelo de ticket |
| `docs/JIRA_INTEGRATION_TROUBLESHOOTING.md` | Guía de troubleshooting |

### Endpoints de Prueba

```
GET /api/jiratest/config              # Verificar configuración
GET /api/jiratest/connection          # Probar conexión a Jira
GET /api/jiratest/tickets?days=7      # Obtener tickets resueltos
GET /api/jiratest/ticket/{key}        # Obtener ticket específico
GET /api/jiratest/raw                 # Respuesta cruda (debug)
GET /api/jiratest/deserialize-test    # Verificar deserialización
```

### Configuración

**Azure App Settings:**
```
Jira__BaseUrl = https://antolin.atlassian.net
Jira__Email = user@company.com
Jira__ApiToken = <token_from_atlassian>
```

**appsettings.json (local):**
```json
{
  "Jira": {
    "BaseUrl": "https://antolin.atlassian.net",
    "Email": "user@company.com",
    "ApiToken": "your_api_token"
  }
}
```

### Campos Extraídos

| Campo | Descripción | Uso |
|-------|-------------|-----|
| `Key` | ID del ticket (MT-12345) | Referencia única |
| `Summary` | Título del ticket | Búsqueda semántica |
| `Description` | Descripción completa (ADF→texto) | Contexto del problema |
| `Status` | Estado actual | Filtrado |
| `Resolution` | Tipo de resolución | Categorización |
| `Comments` | Lista de comentarios | **Clave para soluciones** |
| `Assignee` | Técnico asignado | Estadísticas |
| `Resolved` | Fecha de resolución | Filtrado temporal |

### Problemas Resueltos

1. **API Deprecada**: Migrado de GET `/search` a POST `/search/jql`
2. **Deserialización**: Nuevas clases `JiraSearchResponsePublic`
3. **SDK .NET 10**: Eliminado manifest conflictivo, creado `global.json`
4. **ADF Format**: Implementado extractor de texto recursivo

Ver detalles en: [JIRA_INTEGRATION_TROUBLESHOOTING.md](./JIRA_INTEGRATION_TROUBLESHOOTING.md)

---

## Fase 3: Harvesting Automático (Completado)

### Objetivo
Servicio BackgroundService que automáticamente:
1. Escanea tickets resueltos periódicamente (configurable, default 1h)
2. Extrae soluciones de los comentarios usando LLM
3. Evita duplicados con persistencia en Azure Blob Storage
4. Almacena soluciones en contenedor `harvested-solutions`

### Archivos Implementados

| Archivo | Descripción |
|---------|-------------|
| `Services/JiraSolutionHarvesterService.cs` | BackgroundService para harvesting automático |
| `Services/HarvestedSolution.cs` | Modelo de solución extraída |
| `Extensions/DependencyInjection.cs` | Método `AddJiraSolutionServices()` |

### Flujo de Ejecución

```
┌───────────────────────────────────────────────────────┐
│           JiraSolutionHarvesterService                │
│                (BackgroundService)                     │
└───────────────────────┬───────────────────────────────┘
                        │
         ┌──────────────┼──────────────┐
         │              │              │
         ▼              ▼              ▼
   LoadProcessed   GetTickets   SaveProcessed
   (Blob Storage)  (Jira API)   (Blob Storage)
         │              │              │
         └──────────────┼──────────────┘
                        │
                        ▼
              HarvestSolutionsAsync()
                        │
                        ▼
         ┌──────────────────────────────┐
         │  Por cada ticket nuevo:      │
         │  1. Verificar si procesado   │
         │  2. Extraer solución (LLM)   │
         │  3. Guardar en Blob Storage  │
         │  4. Marcar como procesado    │
         └──────────────────────────────┘
```

### Deduplicación

El servicio mantiene un archivo `harvested-tickets.json` en Azure Blob Storage con las claves de tickets ya procesados:

```json
["MT-12345", "MT-12346", "MTT-7890", ...]
```

Esto evita reprocesar tickets y optimiza el uso de la API de Jira y recursos de LLM.

### Configuración

**appsettings.json:**
```json
{
  "AzureStorage": {
    "ConnectionString": "...",
    "HarvestedSolutionsContainer": "harvested-solutions"
  },
  "Jira": {
    "HarvestIntervalMinutes": 60,
    "HarvestDaysBack": 30
  }
}
```

### Lógica de Extracción de Soluciones

```csharp
// Identificar comentarios con solución
var solutionKeywords = new[] { 
    "solución", "solucion", "resuelto", "fixed", "resolved",
    "pasos:", "steps:", "to fix:", "para resolver:"
};

foreach (var comment in ticket.Comments.OrderByDescending(c => c.Created))
{
    if (solutionKeywords.Any(k => comment.Body.ToLower().Contains(k)))
    {
        // Este comentario probablemente contiene la solución
        yield return new HarvestedSolution
        {
            TicketKey = ticket.Key,
            Problem = ticket.Summary,
            Solution = comment.Body,
            Source = $"Jira {ticket.Key}",
            ExtractedAt = DateTime.UtcNow
        };
    }
}
```

### Modelo de Solución

```csharp
public class HarvestedSolution
{
    public string Id { get; set; }
    public string TicketKey { get; set; }
    public string Problem { get; set; }        // Summary del ticket
    public string Context { get; set; }        // Description del ticket
    public string Solution { get; set; }       // Comentario con solución
    public string Category { get; set; }       // SAP, Network, General
    public string[] Tags { get; set; }         // Keywords extraídas
    public float[] Embedding { get; set; }     // Vector para búsqueda
    public DateTime ExtractedAt { get; set; }
    public string SourceUrl { get; set; }      // Link al ticket
}
```

---

## Métricas de Éxito

| Métrica | Objetivo |
|---------|----------|
| Tickets procesados/día | >50 |
| Soluciones extraídas | >30% de tickets |
| Tiempo de procesamiento | <5 min/batch |
| Precisión de extracción | >85% |

---

## Próximos Pasos

1. **~~Implementar `JiraSolutionHarvester`~~** ✅
   - ~~Timer trigger cada 24h~~
   - ~~Lógica de extracción de soluciones~~
   - ~~Deduplicación (no re-procesar tickets ya vistos)~~

2. **Integrar con búsqueda existente (Fase 4)**
   - Añadir soluciones harvested al índice de búsqueda
   - Combinar con Confluence KB
   - Generar embeddings para búsqueda semántica

3. **Panel de administración (futuro)**
   - Ver soluciones extraídas
   - Editar/aprobar antes de publicar
   - Métricas de harvesting

---

## Referencias

- [Jira REST API v3](https://developer.atlassian.com/cloud/jira/platform/rest/v3/)
- [Atlassian Document Format (ADF)](https://developer.atlassian.com/cloud/jira/platform/apis/document/structure/)
- [Troubleshooting Guide](./JIRA_INTEGRATION_TROUBLESHOOTING.md)

---

**Última actualización:** 10 Diciembre 2025
