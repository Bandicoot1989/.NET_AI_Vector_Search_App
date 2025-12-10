# Plan de Implementación - Mejoras del Sistema Multi-Agente

**Fecha:** 4 de Diciembre 2025  
**Estado:** En progreso  
**Autor:** Análisis técnico externo + Evaluación interna

---

## 🎉 Cambios Implementados (4 Dic 2025)

### ✅ Tickets Dinámicos desde Contexto
**Estado: COMPLETADO**

Se eliminaron TODOS los URLs hardcodeados de tickets. Ahora los agentes solo sugieren tickets que existen en `Context_Jira_Forms.xlsx`.

#### SapAgentService
- Eliminado el diccionario `SapTicketMap` con URLs hardcodeadas
- Nueva función `GetSapTicketsAsync()` que busca SOLO en el contexto
- Scoring inteligente basado en la intención del usuario (usuario, acceso, problema)
- Si no encuentra ticket SAP en contexto → no sugiere ninguno

#### NetworkAgentService  
- Filtrado estricto: solo tickets con keywords de red (`zscaler`, `vpn`, `network`)
- Exclusión explícita de tickets de otros sistemas (`sap`, `bpc`, `consolidation`)
- Búsqueda de Confluence mejorada usando la pregunta del usuario
- Muestra enlaces a documentación con formato `📖 [Ver documentacion completa](url)`

---

## 📊 Resumen Ejecutivo

Basado en el análisis de la arquitectura actual (Tier 3 Multi-Agent, Clean Architecture, búsqueda híbrida), se han identificado mejoras para optimizar búsquedas y resultados sin incrementar costos significativamente.

---

## 🎯 Plan de Implementación por Prioridad

| Prioridad | Recomendación | Esfuerzo | Impacto | Estado |
|-----------|---------------|----------|---------|--------|
| 🥇 **1** | Feedback Loop (threshold <0.65) | 2h | Alto | ✅ Completado |
| 🥈 **2** | Caché Semántica | 2 días | Muy Alto | ✅ Completado |
| 🥉 **3** | Re-Ranking RRF | 1 día | Alto | ✅ Completado |
| 4 | Router LLM (fallback) | 0.5 días | Alto | ✅ Completado |
| 5 | Smart Chunking | 2-3 días | Muy Alto | ⏳ Pendiente |
| 6 | Jira Solution Harvester | 2 días | Alto | ✅ Completado |

---

## 1. Optimización del Retrieval 🔍

### A. Smart Chunking (Troceado Inteligente)

**Problema Actual:**  
El sistema almacena el contenido completo de artículos de Confluence en un solo campo para generar el embedding. Si un artículo es largo, el vector resultante es un "promedio" de todo el texto, diluyendo los detalles específicos.

**Solución Propuesta:**
- Dividir contenido en chunks de ~500 tokens con overlap de 100 tokens
- Nuevo modelo: `ChunkId`, `ParentArticleId`, `Text`, `Vector`
- Crear `ChunkingService` dedicado

**Beneficio:**  
Cuando el usuario pregunte por un detalle específico dentro de un manual largo, la búsqueda vectorial encontrará el párrafo exacto, no solo el documento general.

**Esfuerzo:** 2-3 días  
**Impacto:** ⭐⭐⭐⭐⭐ Muy Alto

---

### B. Re-Ranking con Reciprocal Rank Fusion (RRF)

**Problema Actual:**  
La similitud de coseno es rápida pero a veces trae resultados que "suenan" parecidos pero no son semánticamente relevantes.

**Solución Propuesta:**
```csharp
// Recuperar más resultados (20 en lugar de 5)
// Combinar rankings con RRF:
double rrfScore = (1.0 / (60 + keywordRank)) + (1.0 / (60 + semanticRank));
```

**Implementación en:** `ContextSearchService.cs`

**Esfuerzo:** 1 día  
**Impacto:** ⭐⭐⭐⭐ Alto

---

## 2. Modelos y Costos 💰

### Configuración Actual (MANTENER)
- **Chat:** `gpt-4o-mini` ✅ Óptimo
- **Embeddings:** `text-embedding-3-small` ✅ Suficiente

### Mejora Opcional (Solo si hay problemas de diferenciación)
- Cambiar a `text-embedding-3-large` con `dimensions: 1024`
- Mejor calidad semántica, costo similar

**Veredicto:** No cambiar por ahora. La combinación actual es la más eficiente en costo/beneficio.

---

## 3. Arquitectura de Agentes 🤖

### A. Router Híbrido Semántico-Ligero

**Problema Actual:**  
El `AgentRouterService` usa Regex/Keywords para enrutar. Puede fallar con lenguaje natural ambiguo.

**Ejemplo de fallo:**  
"No puedo entrar a la herramienta de finanzas" → Es SAP, pero no dice "SAP"

**Solución Propuesta:**
```
Paso 1 (Actual): Regex/Keywords (0 latencia)
Paso 2 (Nuevo Fallback): Clasificación con LLM
```

```csharp
// Si keywords no matchean:
var prompt = @"Clasifica la siguiente consulta técnica en una categoría JSON: 
{""category"": ""SAP"" | ""NETWORK"" | ""GENERAL""}. 
Query: [UserQuery]";

// Costo: ~$0.0001 por clasificación (10 tokens max)
```

**Esfuerzo:** 0.5 días  
**Impacto:** ⭐⭐⭐⭐ Alto

---

### B. Caché Semántica (Mejora del Tier 2)

**Problema Actual:**  
La caché actual es por string exacto normalizado (lowercase, sin puntuación).

**Solución Propuesta:**
- Generar embedding de cada pregunta
- Buscar en caché por similitud vectorial (>0.95)
- Cache hit si preguntas son semánticamente iguales

**Ejemplo:**
- "¿Cómo configuro la VPN?"
- "¿Pasos para poner la VPN?"
- "Configuración VPN por favor"

→ Todas darían cache hit

```csharp
// En lugar de: _cache[normalizedQuestion] = response
var cachedEmbedding = await FindSimilarCachedQuestion(questionEmbedding, threshold: 0.95);
if (cachedEmbedding != null) return cachedResponse;
```

**Esfuerzo:** 2 días  
**Impacto:** ⭐⭐⭐⭐⭐ Muy Alto (reduce costos LLM)

---

## 4. Mejora de Datos 📊

### A. Jira Solution Harvester (Integración Jira)

**Estado:** 🔄 **EN PROGRESO** - Fase 2 Completada

**Objetivo:**
Extraer automáticamente soluciones de tickets resueltos en Jira para enriquecer la base de conocimiento.

#### ✅ Fase 1: Diseño y Modelos (Completado - 9 Dic 2025)
- Modelos: `JiraTicket`, `JiraComment`, `HarvestedSolution`
- Servicios definidos: `IJiraClient`, `IJiraSolutionHarvester`
- Documentación en `docs/JIRA_SOLUTION_HARVESTER.md`

#### ✅ Fase 2: Cliente Jira (Completado - 10 Dic 2025)
- `JiraClient.cs` conecta con Jira Cloud REST API v3
- Autenticación Basic Auth (email + API token)
- Migración a POST `/rest/api/3/search/jql` (API 2024)
- Campos extraídos: Summary, Description, Status, Resolution, Comments, Assignee
- Endpoints de prueba en `JiraTestController`
- **Documentación:** `docs/JIRA_INTEGRATION_TROUBLESHOOTING.md`

#### ⏳ Fase 3: Harvesting Automático (Pendiente)
- Timer/WebJob cada 24h para escanear tickets resueltos
- Filtrado por palabras clave (solución, resuelto, fix)
- Almacenamiento en Azure Blob Storage
- Integración con sistema de búsqueda existente

#### Configuración Requerida
```json
{
  "Jira": {
    "BaseUrl": "https://antolin.atlassian.net",
    "Email": "user@company.com",
    "ApiToken": "API_TOKEN_FROM_ATLASSIAN"
  }
}
```

**Esfuerzo Total:** 3-4 días  
**Impacto:** ⭐⭐⭐⭐ Alto (auto-enriquece KB con soluciones reales)

---

### B. Feedback Loop Negativo (Threshold de Confianza)

**Problema:**  
El bot puede "alucinar" si intenta responder sin información suficiente.

**Solución:**
```csharp
if (bestSearchScore < 0.65)
{
    return "No encuentro información exacta sobre esto en mi base de conocimiento. " +
           "¿Te gustaría abrir un ticket general de soporte para que un humano te ayude?";
    // Mostrar FallbackTicketLink inmediatamente
}
```

**Esfuerzo:** 2 horas  
**Impacto:** ⭐⭐⭐⭐ Alto (previene alucinaciones)

---

## 📅 Roadmap Sugerido

### Semana 1 (Inmediato) - ✅ COMPLETADO
- [x] Feedback Loop (threshold <0.65)
- [x] Re-Ranking RRF
- [x] Caché Semántica
- [x] Router LLM fallback

### Semana 2 (Diciembre 2025) - 🔄 EN PROGRESO
- [x] Jira Solution Harvester - Fase 1: Diseño
- [x] Jira Solution Harvester - Fase 2: Cliente Jira API
- [ ] Jira Solution Harvester - Fase 3: Harvesting Automático

### Semana 3-4
- [ ] Smart Chunking (requiere re-indexar contenido)

### Backlog
- [ ] Panel Admin para sincronización manual

---

## 📝 Notas Técnicas

### Archivos a Modificar

| Mejora | Archivos |
|--------|----------|
| Feedback Loop | `KnowledgeAgentService.cs` |
| Re-Ranking RRF | `ContextSearchService.cs` |
| Caché Semántica | `CacheService.cs` (nuevo), `KnowledgeAgentService.cs` |
| Router LLM | `AgentRouterService.cs` |
| Smart Chunking | `ConfluenceService.cs`, `KnowledgeArticle.cs`, nuevo `ChunkingService.cs` |

### Dependencias Actuales (No cambiar)
- Azure OpenAI: `gpt-4o-mini`, `text-embedding-3-small`
- Azure Blob Storage: `agent-context` container
- Confluence API: Para sincronización de KB

---

## 5. Arquitectura de Datos - Principios Clave 📁

> **Nota:** Estas recomendaciones definen cómo debe tratarse cada tipo de dato para maximizar eficiencia y minimizar costos.

### A. Clasificación por Tipo de Dato

| Archivo | Tipo de Dato | Estrategia de Búsqueda | ¿Usa IA? |
|---------|--------------|------------------------|----------|
| `SAP_Dictionary.xlsx` | Relacional/Estructurado | In-Memory Lookup O(1) | ❌ No |
| `Centres.xlsx` | Key-Value | In-Memory Dictionary | ❌ No |
| `Companies.xlsx` | Key-Value | In-Memory Dictionary | ❌ No |
| `Sharepoint Apps.xlsx` | Descriptivo/Semántico | Búsqueda Híbrida (Vector + Keyword) | ✅ Sí |
| `Context_Jira_Forms.xlsx` | Descriptivo/Semántico | Búsqueda Híbrida | ✅ Sí |

**Principio:** No usar embeddings para datos estructurados (códigos SAP, centros). Solo usar IA para búsquedas donde el usuario describe una necesidad sin saber el nombre exacto.

### B. Estado Actual de Implementación

| Componente | Estado | Implementación |
|------------|--------|----------------|
| SAP In-Memory Lookup | ✅ Implementado | `SapLookupService` con diccionarios O(1) |
| Mapas Inversos (Transaction→Roles) | ✅ Implementado | `_transactionsByRole`, `_rolesByPosition` |
| Embeddings Pre-calculados | ✅ Implementado | `context-documents.json` en Blob Storage |
| Búsqueda Híbrida | ✅ Implementado | `ContextSearchService` (keyword + cosine) |

### C. Organización de Blob Storage (Recomendado)

```
agent-context/
├── config-data/           # Datos estáticos - cargar en RAM (Singleton)
│   ├── SAP_Dictionary.xlsx
│   ├── centres.json
│   └── companies.json
│
└── vector-data/           # Datos con embeddings - búsqueda semántica
    ├── context-documents.json    # Apps + Jira + KB con vectores
    └── confluence-articles.json
```

**Beneficio:** Separación clara reduce consumo de tokens (no envías tablas al LLM) y mejora precisión en datos técnicos.

### D. Técnicas de "Entrenamiento" Manual del Bot

#### 1. Enriquecimiento de Keywords (Context_Jira_Forms)
Cuando el bot falle en encontrar un ticket:
1. Identificar las palabras exactas que usó el usuario
2. Agregar esas palabras a la columna `Keywords` del ticket correspondiente
3. Regenerar el JSON con embeddings

**Ejemplo:**
```
Usuario pregunta: "problema con el correo"
Ticket no encontrado: "Email configuration issues"
Solución: Agregar "correo, email, outlook, problema correo" a Keywords
```

#### 2. Concatenación de Campos para Vectores
Para mejorar búsqueda de apps, el `search_text` debe incluir:
```
search_text = Name + " " + Description + " " + Keywords + " " + Owner
```

El Owner puede ayudar: "La app de Juan de HR" → encuentra la app del equipo de HR.

---

## ✅ Decisiones Tomadas

1. **Modelos:** Mantener `gpt-4o-mini` + `text-embedding-3-small`
2. **Jira Solution Harvester:** ✅ Implementado (10 Diciembre 2025)
3. **Cross-Encoder:** No implementar (RRF es suficiente por ahora)

---

## 🎉 Jira Solution Harvester (Completado 10 Dic 2025)

### Descripción
BackgroundService que automáticamente recolecta tickets resueltos de Jira cada 6 horas, extrae soluciones y las almacena para enriquecer el conocimiento del bot.

### Componentes Implementados

| Componente | Archivo | Descripción |
|------------|---------|-------------|
| `JiraSolutionHarvesterService` | `Services/JiraSolutionHarvesterService.cs` | BackgroundService que ejecuta harvesting cada 6 horas |
| `BlobContainerClient (keyed)` | DI | Contenedor `harvested-solutions` para persistencia |
| `HarvestedSolution` | `Models/SapModels.cs` | Modelo para soluciones extraídas |

### Características
- ⏰ Ejecución automática cada 6 horas
- 🔄 Deduplicación: no reprocesa tickets ya cosechados
- 💾 Persistencia en Azure Blob Storage (`harvested-solutions` container)
- 📝 Extracción inteligente de soluciones desde descripción y comentarios
- 🔒 Registro de tickets procesados en `harvested-tickets.json`

### Flujo de Datos
```
Jira API → JiraSolutionHarvesterService → HarvestedSolution → Azure Blob Storage
                    ↓
            Deduplicación (HashSet + Blob JSON)
```

---

*Documento creado: 4 Diciembre 2025*  
*Última actualización: 10 Diciembre 2025*
