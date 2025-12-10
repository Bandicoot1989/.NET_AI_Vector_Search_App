# Troubleshooting Guide - Operations One Centre

Este documento recopila problemas encontrados y sus soluciones durante el desarrollo y despliegue de la aplicación.

---

## 📋 Índice

1. [Chatbot No Responde a Clicks](#1-chatbot-no-responde-a-clicks)
2. [Error 500 al Usar @rendermode en MainLayout](#2-error-500-al-usar-rendermode-en-mainlayout)
3. [Error de Deserialización de Fechas Jira](#3-error-de-deserialización-de-fechas-jira)
4. [IJiraClient No Registrado en DI](#4-ijiraclient-no-registrado-en-di)
5. [Error BLAZOR106 - Archivo JS Huérfano](#5-error-blazor106---archivo-js-huérfano)

---

## 1. Chatbot No Responde a Clicks

### Síntomas
- El icono del chatbot es visible en la esquina inferior derecha
- Al hacer click no pasa nada
- No hay errores visibles en la consola del navegador

### Causa Raíz
**WebSockets estaba DESHABILITADO** en Azure App Service. Blazor Server requiere SignalR que usa WebSockets para la comunicación en tiempo real.

### Solución
```bash
# Habilitar WebSockets en Azure App Service
az webapp config set \
  --resource-group rg-hq-helpdeskai-poc-001 \
  --name powershell-scripts-helpdesk \
  --web-sockets-enabled true

# Reiniciar la aplicación
az webapp restart \
  --resource-group rg-hq-helpdeskai-poc-001 \
  --name powershell-scripts-helpdesk
```

### Verificación
```bash
# Comprobar que WebSockets está habilitado
az webapp config show \
  --resource-group rg-hq-helpdeskai-poc-001 \
  --name powershell-scripts-helpdesk \
  --query "webSocketsEnabled"
```

---

## 2. Error 500 al Usar @rendermode en MainLayout

### Síntomas
- Página muestra "Something went wrong"
- HTTP 500 Internal Server Error
- Request ID visible en la página de error

### Causa Raíz
En Blazor .NET 8+, **NO se puede usar `@rendermode` directamente en un `LayoutComponentBase`**. Los layouts son componentes especiales que no soportan esta directiva directamente.

### Código Incorrecto ❌
```razor
@inherits LayoutComponentBase
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@rendermode InteractiveServer  <!-- ESTO CAUSA 500 -->

<div class="md-app">
```

### Código Correcto ✅
```razor
@inherits LayoutComponentBase

<div class="md-app">
    <!-- Layout content -->
</div>

@* Componentes hijos SÍ pueden usar @rendermode *@
<KnowledgeChat @rendermode="InteractiveServer" />
```

### Solución
- Usar `@rendermode` solo en **componentes hijos**, no en el layout
- Los componentes interactivos como `KnowledgeChat` especifican su rendermode donde se instancian

---

## 3. Error de Deserialización de Fechas Jira

### Síntomas
```
System.Text.Json.JsonException: The JSON value could not be converted to System.DateTime.
```

### Causa Raíz
Jira devuelve fechas en formato ISO 8601 con timezone: `2024-01-15T10:30:00.000+0100`
`System.Text.Json` no parsea este formato automáticamente a `DateTime`.

### Solución
Cambiar los campos de `DateTime` a `string` y parsear manualmente:

```csharp
// En JiraClient.cs - Modelo de respuesta
public class JiraFieldsResponsePublic
{
    // ANTES (causaba error):
    // public DateTime Created { get; set; }
    
    // DESPUÉS (funciona):
    public string? Created { get; set; }
    public string? Resolutiondate { get; set; }
}

// Helper methods para parsear
private static DateTime ParseJiraDate(string? dateString)
{
    if (string.IsNullOrEmpty(dateString))
        return DateTime.MinValue;
    
    if (DateTime.TryParse(dateString, out var result))
        return result;
    
    return DateTime.MinValue;
}
```

---

## 4. IJiraClient No Registrado en DI

### Síntomas
```
System.InvalidOperationException: Unable to resolve service for type 
'RecipeSearchWeb.Interfaces.IJiraClient' while attempting to activate 
'RecipeSearchWeb.Services.JiraSolutionStorageService'.
```

### Causa Raíz
La interfaz `IJiraClient` no estaba registrada en el contenedor de inyección de dependencias.

### Solución
Consolidar todos los servicios de Jira en un método de extensión:

```csharp
// En DependencyInjection.cs
public static IServiceCollection AddJiraSolutionServices(
    this IServiceCollection services, 
    IConfiguration configuration)
{
    // Registrar el cliente HTTP de Jira
    services.AddHttpClient<JiraClient>();
    
    // Registrar la interfaz CON implementación
    services.AddScoped<IJiraClient, JiraClient>();
    
    // Registrar otros servicios
    services.AddScoped<IJiraSolutionStorageService, JiraSolutionStorageService>();
    services.AddScoped<IJiraSolutionSearchService, JiraSolutionSearchService>();
    services.AddHostedService<JiraSolutionHarvesterService>();
    
    return services;
}
```

---

## 5. Error BLAZOR106 - Archivo JS Huérfano

### Síntomas
```
error BLAZOR106: The JS module file '...\publish\wwwroot\Components\Layout\ReconnectModal.razor.js' 
was defined but no associated razor component or view was found for it.
```

### Causa Raíz
Archivo JavaScript de un componente Razor quedó en la carpeta `publish/` después de que el componente fue movido o renombrado.

### Solución
Limpiar la carpeta publish y reconstruir:

```powershell
# Eliminar carpeta publish
Remove-Item -Path publish -Recurse -Force

# Limpiar y reconstruir
dotnet clean
dotnet build
```

---

## 🔧 Comandos Útiles de Diagnóstico

### Ver logs de Azure en tiempo real
```bash
az webapp log tail \
  --resource-group rg-hq-helpdeskai-poc-001 \
  --name powershell-scripts-helpdesk
```

### Descargar logs de Azure
```bash
az webapp log download \
  --resource-group rg-hq-helpdeskai-poc-001 \
  --name powershell-scripts-helpdesk \
  --log-file webapp-logs.zip
```

### Verificar que la app responde
```powershell
(Invoke-WebRequest -Uri "https://powershell-scripts-helpdesk-f0h8h6ekcsb5amhn.germanywestcentral-01.azurewebsites.net/" -UseBasicParsing).StatusCode
```

### Verificar endpoint SignalR/Blazor
```powershell
$response = Invoke-WebRequest -Uri "https://powershell-scripts-helpdesk-f0h8h6ekcsb5amhn.germanywestcentral-01.azurewebsites.net/_blazor/negotiate?negotiateVersion=1" -Method POST -UseBasicParsing
$response.StatusCode  # Debe ser 200
```

---

## 📅 Historial de Actualizaciones

| Fecha | Problema | Solución |
|-------|----------|----------|
| 2025-12-10 | Chatbot no responde | Habilitar WebSockets en Azure |
| 2025-12-10 | Error 500 con @rendermode en Layout | Quitar @rendermode del MainLayout |
| 2025-12-10 | Error deserialización fechas Jira | Cambiar DateTime a string |
| 2025-12-10 | IJiraClient no registrado | Consolidar DI en AddJiraSolutionServices |

