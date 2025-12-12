# Real-Time Ticket Lookup Feature

## Overview

The Operations One Centre Bot now supports real-time Jira ticket lookups. Users can ask questions about specific tickets (e.g., "Ayúdame con el ticket MT-799225") and the bot will:

1. **Fetch real-time ticket data** directly from Jira
2. **Find similar solved tickets** for reference solutions
3. **Search related documentation** from KB and Confluence
4. **Generate a comprehensive response** with actionable suggestions

## Architecture

### Components

```
┌─────────────────────────────────────────────────────────────────┐
│                    KnowledgeAgentService                        │
│                         (Orchestrator)                          │
├─────────────────────────────────────────────────────────────────┤
│  DetectIntent() → TicketLookup                                  │
│  ↓                                                              │
│  ExtractTicketIds(question) → ["MT-799225"]                     │
│  ↓                                                              │
│  LookupTicketsAsync() → TicketInfo + Similar Solutions          │
│  ↓                                                              │
│  SearchArticlesAsync() + SearchConfluenceAsync()                │
│  ↓                                                              │
│  Build Context + Specialized System Prompt                      │
│  ↓                                                              │
│  OpenAI Chat Completion → Response                              │
└─────────────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────────────┐
│                   ITicketLookupService                          │
│                    (TicketLookupService)                        │
├─────────────────────────────────────────────────────────────────┤
│  • ContainsTicketReference(query)                               │
│  • ExtractTicketIds(query)                                      │
│  • LookupTicketAsync(ticketId)                                  │
│  • LookupTicketsAsync(ticketIds)                                │
└─────────────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────────────┐
│                       JiraClient                                │
│                    (HTTP to Jira API)                           │
└─────────────────────────────────────────────────────────────────┘
```

## Supported Ticket Patterns

The following ticket ID prefixes are recognized:
- **MT-** (Manufacturing Technology)
- **MTT-** (Manufacturing Technology Test)
- **IT-** (IT General)
- **HELP-** (Help Desk)
- **SD-** (Service Desk)
- **INC-** (Incidents)
- **REQ-** (Requests)
- **SR-** (Service Requests)

Pattern: `(MT|MTT|IT|HELP|SD|INC|REQ|SR)-\d+`

## Security Features

### Input Validation
- **ReDoS Protection**: Regex timeout of 100ms to prevent denial of service
- **Input Sanitization**: Control characters removed from responses
- **Rate Limiting Awareness**: Maximum 3 tickets per query
- **Content Length Limits**: 
  - Summary: 500 chars
  - Description: 2000 chars
  - Comments: 1000 chars each

### Data Handling
- Ticket data is fetched directly from Jira (no caching of sensitive data)
- User must have appropriate Jira permissions to access tickets
- Only ticket metadata and content are exposed (no internal Jira fields)

## Usage Examples

### Example 1: Simple Ticket Query
**User**: "Ayúdame con el ticket MT-799225"

**Bot Response**:
```
## 🎫 Ticket MT-799225

**Estado**: En progreso
**Prioridad**: Alta
**Asignado a**: John Doe
**Sistema detectado**: SAP

### Resumen del problema
El usuario reporta que no puede acceder al módulo SS2 en SAP...

### Sugerencia basada en tickets similares
Según el ticket MT-745123 que fue resuelto anteriormente, este problema 
suele deberse a permisos faltantes. Se recomienda:
1. Verificar los roles asignados en SU01
2. Comprobar el perfil de autorización...

📎 [Ver ticket en Jira](https://antolin.atlassian.net/browse/MT-799225)
```

### Example 2: Multiple Tickets
**User**: "Compara los tickets MT-100 y MT-200"

The bot will fetch both tickets and provide a comparison.

### Example 3: Ticket Not Found
**User**: "Ayuda con ticket XYZ-999"

**Bot Response**:
```
No pude encontrar el ticket XYZ-999 en Jira. Esto puede deberse a:
- El número de ticket no es correcto
- El ticket fue eliminado o archivado
- No tengo permisos para acceder a ese proyecto

¿Podrías verificar el número de ticket o proporcionar más detalles?
```

## Configuration

No additional configuration is required. The service uses the existing Jira credentials from:
- `appsettings.json` → `Jira:BaseUrl`, `Jira:Username`, `Jira:ApiToken`

## Files Modified/Created

### New Files
- `Interfaces/ITicketLookupService.cs` - Service interface and DTOs
- `Services/TicketLookupService.cs` - Implementation with security features

### Modified Files
- `Extensions/DependencyInjection.cs` - Service registration
- `Services/KnowledgeAgentService.cs` - Integration with agent flow

## Testing

### Manual Testing

1. **Start the application**:
   ```powershell
   cd RecipeSearchWeb
   dotnet run
   ```

2. **Test ticket lookup**:
   - In the chat, type: "Ayúdame con el ticket MT-XXXXX" (use a real ticket ID)
   - Verify the bot returns ticket details

3. **Test invalid ticket**:
   - Type: "Ayuda con ticket FAKE-12345"
   - Verify graceful error handling

### Unit Test Ideas

```csharp
[Fact]
public void ContainsTicketReference_WithValidTicket_ReturnsTrue()
{
    var service = new TicketLookupService(...);
    Assert.True(service.ContainsTicketReference("Ayuda con MT-12345"));
}

[Fact]
public void ExtractTicketIds_WithMultipleTickets_ReturnsAll()
{
    var service = new TicketLookupService(...);
    var ids = service.ExtractTicketIds("Compare MT-100 and MTT-200");
    Assert.Equal(2, ids.Count);
}
```

## Logging

The feature logs the following events:
- `Information`: Ticket lookup intent detected
- `Information`: Response generated with timing
- `Warning`: Ticket not found or lookup failed

Example log output:
```
[INF] Ticket lookup detected - fetching ticket information directly from Jira
[INF] Ticket MT-799225 fetched successfully
[INF] Found 3 similar solved tickets
[INF] Ticket lookup response generated in 1234ms for tickets: MT-799225
```

## Future Improvements

1. **Ticket Actions**: Allow bot to perform actions (assign, comment, transition)
2. **Ticket Creation**: Create new tickets from chat conversations
3. **Ticket Analytics**: Show ticket history patterns and trends
4. **Bulk Operations**: Support operations on multiple tickets
5. **Webhook Integration**: Real-time notifications when ticket status changes
