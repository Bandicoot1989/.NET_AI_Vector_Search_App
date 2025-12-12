# 🌐 Azure Deployment Information

## Operations One Centre - Información de Despliegue en Azure

**Documento creado:** 10 Diciembre 2025  
**Última actualización:** 12 Diciembre 2025

---

## 📋 Datos del Recurso en Azure

| Propiedad | Valor |
|-----------|-------|
| **Suscripción Azure** | Grupo Antolin ITHQ PoCs |
| **Grupo de Recursos** | `rg-hq-helpdeskai-poc-001` |
| **Nombre de Web App** | `powershell-scripts-helpdesk` |
| **URL de la aplicación** | https://powershell-scripts-helpdesk-f0h8h6ekcsb5amhn.germanywestcentral-01.azurewebsites.net |
| **Región** | Germany West Central |
| **Runtime** | .NET 10 |
| **Tipo de App Service** | Blazor Server (InteractiveServer) |

---

## 🚀 Despliegue con Azure CLI

### Prerrequisitos

1. Tener instalado [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
2. Tener permisos de despliegue en la suscripción "Grupo Antolin ITHQ PoCs"

### Pasos de Despliegue

#### 1. Iniciar sesión en Azure CLI

```powershell
# Iniciar sesión interactivo
az login

# Verificar suscripción activa
az account show --query "{Subscription:name, TenantId:tenantId}" --output table

# Si es necesario, cambiar a la suscripción correcta
az account set --subscription "Grupo Antolin ITHQ PoCs"
```

#### 2. Publicar la aplicación localmente

```powershell
# Navegar al proyecto
cd c:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App\RecipeSearchWeb

# Limpiar y publicar
dotnet clean
dotnet publish -c Release -o ../publish
```

#### 3. Desplegar desde la carpeta publish

> ⚠️ **IMPORTANTE: Problema de SSL con Proxy Corporativo (Zscaler)**
> 
> Si estás detrás de un proxy corporativo, puede que recibas el error:
> ```
> SSL: CERTIFICATE_VERIFY_FAILED - unable to get local issuer certificate
> ```
> 
> **✅ Solución (Bundle de certificados incluido en el proyecto):**
> ```powershell
> cd c:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App
> $env:REQUESTS_CA_BUNDLE = "$PWD\combined_ca_bundle.pem"
> ```
> 
> El bundle `combined_ca_bundle.pem` ya está en la raíz del repositorio e incluye los certificados CA de Python + Zscaler. Ver más opciones en la sección [🔐 Configurar Certificados SSL para Proxy](#-configurar-certificados-ssl-para-proxy).

```powershell
# Navegar al proyecto
cd c:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App

# Configurar certificado SSL para Zscaler (usar bundle combinado)
$env:REQUESTS_CA_BUNDLE = "$PWD\combined_ca_bundle.pem"

# Navegar a la carpeta publish
cd publish

# Comprimir el contenido para despliegue
Compress-Archive -Path .\* -DestinationPath ..\app.zip -Force

# Desplegar usando el nuevo comando (recomendado)
az webapp deploy `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --src-path ..\app.zip `
  --type zip
```

#### 4. Reiniciar la aplicación (opcional)

```powershell
az webapp restart `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk
```

---

## ⚙️ Configuración Importante

### WebSockets (Requerido para Blazor Server)

WebSockets **DEBE** estar habilitado para que Blazor Server funcione correctamente:

```powershell
# Verificar estado de WebSockets
az webapp config show `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --query "webSocketsEnabled"

# Habilitar WebSockets si está deshabilitado
az webapp config set `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --web-sockets-enabled true
```

### Variables de Configuración (App Settings)

Las siguientes variables de entorno deben estar configuradas en Azure App Service:

| Variable | Descripción |
|----------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Endpoint de Azure OpenAI |
| `AZURE_OPENAI_API_KEY` | API Key de Azure OpenAI |
| `AZURE_OPENAI_GPT_NAME` | Nombre del deployment GPT (gpt-4o-mini) |
| `AZURE_OPENAI_EMBEDDING_NAME` | Nombre del deployment de embeddings |
| `AzureStorage__ConnectionString` | Connection string de Azure Blob Storage |
| `Confluence__BaseUrl` | URL de Confluence (Atlassian) |
| `Confluence__Username` | Usuario de Confluence |
| `Confluence__ApiToken` | API Token de Confluence |
| `Jira__BaseUrl` | URL de Jira (antolin.atlassian.net) |
| `Jira__Email` | Email para autenticación Jira |
| `Jira__ApiToken` | API Token de Jira |
| `Jira__ProjectKeys` | Proyectos de Jira para monitoreo (MT, MTT) |

```powershell
# Ver configuración actual
az webapp config appsettings list `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --output table
```

---

## 📊 Monitorización y Logs

### Ver logs en tiempo real

```powershell
az webapp log tail `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk
```

### Habilitar logging detallado

```powershell
az webapp log config `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --application-logging filesystem `
  --detailed-error-messages true `
  --failed-request-tracing true `
  --web-server-logging filesystem
```

### Descargar logs

```powershell
az webapp log download `
  --resource-group rg-hq-helpdeskai-poc-001 `
  --name powershell-scripts-helpdesk `
  --log-file webapp_logs.zip
```

---

## 🔄 Script Completo de Despliegue

```powershell
# =========================================
# Script de Despliegue Completo
# Operations One Centre → Azure App Service
# =========================================

# 1. Variables
$resourceGroup = "rg-hq-helpdeskai-poc-001"
$appName = "powershell-scripts-helpdesk"
$projectPath = "c:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App"

# 2. Login en Azure
Write-Host "🔐 Iniciando sesión en Azure..." -ForegroundColor Cyan
az login

# 3. Verificar suscripción
Write-Host "📋 Verificando suscripción..." -ForegroundColor Cyan
az account show --query "{Subscription:name}" --output table

# 4. Compilar y publicar
Write-Host "🔨 Compilando aplicación..." -ForegroundColor Cyan
Set-Location "$projectPath\RecipeSearchWeb"
dotnet clean
dotnet publish -c Release -o "$projectPath\publish"

# 5. Crear ZIP
Write-Host "📦 Creando paquete de despliegue..." -ForegroundColor Cyan
Set-Location "$projectPath\publish"
if (Test-Path "$projectPath\app.zip") { Remove-Item "$projectPath\app.zip" -Force }
Compress-Archive -Path .\* -DestinationPath "$projectPath\app.zip" -Force

# 6. Desplegar
Write-Host "🚀 Desplegando a Azure..." -ForegroundColor Cyan
az webapp deployment source config-zip `
  --resource-group $resourceGroup `
  --name $appName `
  --src "$projectPath\app.zip"

# 7. Verificar WebSockets
Write-Host "⚙️ Verificando WebSockets..." -ForegroundColor Cyan
$webSockets = az webapp config show --resource-group $resourceGroup --name $appName --query "webSocketsEnabled" -o tsv
if ($webSockets -ne "True") {
    Write-Host "  → Habilitando WebSockets..." -ForegroundColor Yellow
    az webapp config set --resource-group $resourceGroup --name $appName --web-sockets-enabled true
}

# 8. Reiniciar
Write-Host "🔄 Reiniciando aplicación..." -ForegroundColor Cyan
az webapp restart --resource-group $resourceGroup --name $appName

Write-Host "✅ Despliegue completado!" -ForegroundColor Green
Write-Host "🌐 URL: https://powershell-scripts-helpdesk-f0h8h6ekcsb5amhn.germanywestcentral-01.azurewebsites.net" -ForegroundColor Cyan
```

---

## 🔐 Configurar Certificados SSL para Proxy

Si estás detrás de un proxy corporativo (como **Zscaler**), Azure CLI no puede verificar los certificados SSL porque el proxy intercepta el tráfico con su propio certificado.

> 📁 **Nota**: El proyecto incluye los siguientes archivos de certificado en la raíz del repositorio:
> - `zscale_root_CA.cer` - Certificado raíz de Zscaler (formato PEM)
> - `combined_ca_bundle.pem` - **Bundle combinado** (certificados CA de Python + Zscaler)

### Opción 1: Usar el Bundle Combinado (✅ Recomendado)

El bundle combinado incluye los certificados CA raíz de Python (`certifi`) junto con el certificado de Zscaler. Esta es la solución más robusta porque Azure CLI puede verificar tanto los certificados de Microsoft como los de Zscaler.

```powershell
# Navegar al proyecto
cd c:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App

# Configurar variable de entorno (temporal - sesión actual)
$env:REQUESTS_CA_BUNDLE = "$PWD\combined_ca_bundle.pem"

# Verificar que funciona (sin warnings de SSL)
az account show

# Ahora puedes ejecutar comandos de despliegue normalmente
az webapp deploy --resource-group rg-hq-helpdeskai-poc-001 --name powershell-scripts-helpdesk --src-path app.zip --type zip
```

> ⚠️ **¿Por qué no usar solo `zscale_root_CA.cer`?**  
> El certificado de Zscaler solo permite verificar conexiones interceptadas por el proxy, pero Azure CLI también necesita los certificados CA raíz estándar para verificar `management.azure.com` y otros servicios de Microsoft.

### Opción 2: Configuración Permanente (Ya configurada ✅)

La variable de entorno `REQUESTS_CA_BUNDLE` ya está configurada permanentemente para el usuario actual:

```powershell
# Verificar configuración actual
[Environment]::GetEnvironmentVariable("REQUESTS_CA_BUNDLE", "User")
# Resultado: C:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App\combined_ca_bundle.pem
```

Si necesitas reconfigurarla manualmente:

```powershell
# Configurar variable de entorno del usuario (permanente)
$bundlePath = "C:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App\combined_ca_bundle.pem"
[Environment]::SetEnvironmentVariable("REQUESTS_CA_BUNDLE", $bundlePath, "User")

# Reiniciar PowerShell/VS Code para que tome efecto
```

### Opción 3: Regenerar el Bundle Combinado

Si necesitas regenerar el bundle (por ejemplo, si `certifi` se actualiza o el certificado de Zscaler cambia):

```powershell
# Obtener ubicación del cacert.pem de Python
$cacertPath = python -c "import certifi; print(certifi.where())"

# Definir rutas
$zscalerPath = "C:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App\zscale_root_CA.cer"
$combinedPath = "C:\Users\osmany.fajardo\repos\.NET_AI_Vector_Search_App\combined_ca_bundle.pem"

# Combinar certificados
$cacert = Get-Content $cacertPath -Raw
$zscaler = Get-Content $zscalerPath -Raw
Set-Content -Path $combinedPath -Value ($cacert + "`n`n# Zscaler Root CA`n" + $zscaler) -NoNewline

Write-Host "Bundle combinado regenerado en: $combinedPath"
```

### Opción 4: Exportar Manualmente desde Windows (Si necesitas regenerar)

Si el certificado del proyecto no funciona o necesitas uno nuevo:

```powershell
# Abrir el administrador de certificados
certmgr.msc
```

1. Navega a: **Entidades de certificación raíz de confianza** → **Certificados**
2. Busca el certificado de **Zscaler** (puede llamarse "Zscaler Root CA" o similar)
3. Click derecho → **Todas las tareas** → **Exportar...**
4. Selecciona: **X.509 codificado en base 64 (.CER)**
5. Guarda como: `zscale_root_CA.cer` en la raíz del proyecto

### Opción 5: Deshabilitar Verificación SSL (⛔ NO RECOMENDADO)

> ⛔ **Ya no es necesario usar esta opción.** Con el bundle combinado configurado, Azure CLI funciona correctamente sin deshabilitar la verificación SSL.

```powershell
# Solo para la sesión actual de PowerShell (NO USAR si tienes el bundle configurado)
$env:AZURE_CLI_DISABLE_CONNECTION_VERIFICATION = "1"

# Ejecutar comandos de Azure CLI...
az webapp deploy ...

# Después de terminar, limpiar la variable (recomendado)
Remove-Item Env:AZURE_CLI_DISABLE_CONNECTION_VERIFICATION
```

> ⚠️ **Advertencia**: Deshabilitar la verificación SSL te hace vulnerable a ataques man-in-the-middle. Usa esta opción solo como último recurso y en redes de confianza.

### Verificar la Configuración

```powershell
# Verificar que Azure CLI funciona correctamente
az account show

# Si funciona sin warnings de SSL, la configuración es correcta
```

---

## 🆘 Troubleshooting

### El chatbot no responde a clicks
→ WebSockets está deshabilitado. Ver sección [WebSockets](#websockets-requerido-para-blazor-server)

### Error 500 al cargar la página
→ Revisar logs con `az webapp log tail`. Posibles causas:
- Variables de entorno faltantes
- Error en la compilación
- **Error de Dependency Injection** (ver siguiente sección)

### Error: "Unable to resolve service for type 'IXxxService'"

Este error ocurre cuando un nuevo servicio se añade al constructor de un componente pero no está registrado correctamente en el contenedor de DI.

**Síntoma en logs:**
```
System.InvalidOperationException: Unable to resolve service for type 'RecipeSearchWeb.Interfaces.ITicketLookupService' 
while attempting to activate 'RecipeSearchWeb.Services.KnowledgeAgentService'
```

**Causa:** En .NET Core DI, los parámetros nullable (`IService?`) **NO son opcionales automáticamente**. El contenedor DI intenta resolverlos de todas formas.

**Solución:** Usar una factory en el registro del servicio:

```csharp
// ❌ MAL - DI intenta resolver TODOS los parámetros
services.AddSingleton<MyService>();

// ✅ BIEN - Usar factory con GetService para opcionales
services.AddSingleton<MyService>(sp => new MyService(
    sp.GetRequiredService<IRequiredDependency>(),  // Obligatorio
    sp.GetService<IOptionalDependency>()           // Opcional (puede ser null)
));
```

**Archivo a modificar:** `Extensions/DependencyInjection.cs`

### La aplicación tarda en cargar
→ El primer request después de inactividad despierta el App Service (cold start). Esto es normal en planes gratuitos/básicos.

### SignalR connection failed
→ Verificar que WebSockets esté habilitado y que no haya un proxy/firewall bloqueando conexiones WebSocket.

---

## 📚 Documentación Relacionada

- [PROJECT_DOCUMENTATION.md](./PROJECT_DOCUMENTATION.md) - Documentación general del proyecto
- [TECHNICAL_REFERENCE.md](./TECHNICAL_REFERENCE.md) - Referencia técnica completa
- [TROUBLESHOOTING.md](./TROUBLESHOOTING.md) - Guía de resolución de problemas
- [AI_CONTEXT.md](./AI_CONTEXT.md) - Contexto para asistentes IA

---

## 📞 Contacto

Para problemas de despliegue o acceso a Azure, contactar al equipo de IT Operations.
