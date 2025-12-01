using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Embeddings;
using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service that handles PowerShell script searching using AI embeddings
/// </summary>
public class ScriptSearchService : IScriptService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly ScriptStorageService _storageService;
    private List<Script> _scripts = new();
    private bool _isInitialized = false;

    public ScriptSearchService(EmbeddingClient embeddingClient, ScriptStorageService storageService)
    {
        _embeddingClient = embeddingClient;
        _storageService = storageService;
    }

    /// <summary>
    /// Initialize the service by loading scripts and generating embeddings
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Initialize storage
        await _storageService.InitializeAsync();

        // Load built-in scripts
        _scripts = GetAllScripts();

        // Load custom scripts from storage
        var customScripts = await _storageService.LoadScriptsAsync();
        _scripts.AddRange(customScripts);

        // Generate embeddings for all scripts
        foreach (var script in _scripts)
        {
            if (script.Vector.Length == 0)
            {
                var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { script.Description + " " + script.Purpose });
                script.Vector = embeddingResponse.Value[0].ToFloats();
            }
        }

        _isInitialized = true;
    }

    /// <summary>
    /// Search for scripts based on a natural language query
    /// </summary>
    public async Task<List<Script>> SearchScriptsAsync(string query, int topResults = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _scripts.Take(topResults).ToList();

        var queryEmbeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { query });
        var queryVector = queryEmbeddingResponse.Value[0].ToFloats().ToArray();

        var results = _scripts
            .Select(script => new
            {
                Script = script,
                Score = CosineSimilarity(queryVector, script.Vector.ToArray())
            })
            .OrderByDescending(x => x.Score)
            .Take(topResults)
            .Select(x => x.Script)
            .ToList();

        return results;
    }

    private double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        var dotProduct = vectorA.Zip(vectorB, (a, b) => a * b).Sum();
        var magnitudeA = Math.Sqrt(vectorA.Sum(a => a * a));
        var magnitudeB = Math.Sqrt(vectorB.Sum(b => b * b));
        return dotProduct / (magnitudeA * magnitudeB);
    }

    /// <summary>
    /// Add a custom script to the collection and generate its embedding
    /// </summary>
    public async Task AddCustomScriptAsync(Script script)
    {
        // Generate embedding for the new script
        var embeddingText = script.Description + " " + script.Purpose;
        var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
        script.Vector = embeddingResponse.Value[0].ToFloats();

        // Add to collection
        _scripts.Add(script);

        // Persist to Azure Blob Storage
        await _storageService.SaveScriptsAsync(_scripts);
    }

    /// <summary>
    /// Update an existing custom script
    /// </summary>
    public async Task UpdateCustomScriptAsync(Script script)
    {
        // Find and remove the old script
        var existingScript = _scripts.FirstOrDefault(s => s.Key == script.Key);
        if (existingScript != null)
        {
            _scripts.Remove(existingScript);
        }

        // Generate new embedding
        var embeddingText = script.Description + " " + script.Purpose;
        var embeddingResponse = await _embeddingClient.GenerateEmbeddingsAsync(new List<string> { embeddingText });
        script.Vector = embeddingResponse.Value[0].ToFloats();

        // Add updated script
        _scripts.Add(script);

        // Persist to Azure Blob Storage
        await _storageService.SaveScriptsAsync(_scripts);
    }

    /// <summary>
    /// Delete a custom script
    /// </summary>
    public async Task DeleteCustomScriptAsync(int scriptKey)
    {
        var script = _scripts.FirstOrDefault(s => s.Key == scriptKey);
        if (script != null && script.Key >= 1000) // Only allow deleting custom scripts
        {
            _scripts.Remove(script);
            await _storageService.SaveScriptsAsync(_scripts);
        }
    }

    /// <summary>
    /// Get the next available key for a new custom script
    /// Custom scripts start at Key 1000 to differentiate from built-in scripts
    /// </summary>
    public int GetNextAvailableKey()
    {
        // Custom scripts must have Key >= 1000
        var customScripts = _scripts.Where(s => s.Key >= 1000).ToList();
        return customScripts.Any() ? customScripts.Max(s => s.Key) + 1 : 1000;
    }

    /// <summary>
    /// Get all custom scripts (Key >= 1000)
    /// </summary>
    public List<Script> GetCustomScripts()
    {
        return _scripts.Where(s => s.Key >= 1000).ToList();
    }

    public List<Script> GetAllScripts()
    {
        return new List<Script>
        {
            // SYSTEM ADMIN
            new() {
                Key = 0,
                Name = "Get System Information",
                Category = "System Admin",
                Description = "Comprehensive system information including OS version, hardware specs, memory usage, disk space, and uptime. Perfect for quick system diagnostics.",
                Purpose = "Quickly gather complete system information for troubleshooting or documentation",
                Complexity = "Beginner",
                Code = @"# Get comprehensive system information
$OS = Get-CimInstance Win32_OperatingSystem
$CPU = Get-CimInstance Win32_Processor
$Disk = Get-CimInstance Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3}

[PSCustomObject]@{
    ComputerName = $env:COMPUTERNAME
    OS = $OS.Caption
    Version = $OS.Version
    Processor = $CPU.Name
    Cores = $CPU.NumberOfCores
    RAM_GB = [math]::Round($OS.TotalVisibleMemorySize / 1MB, 2)
    FreeRAM_GB = [math]::Round($OS.FreePhysicalMemory / 1MB, 2)
    Uptime_Days = (Get-Date) - $OS.LastBootUpTime | Select-Object -ExpandProperty Days
    Disks = $Disk | ForEach-Object {
        ""$($_.DeviceID) - $([math]::Round($_.Size/1GB,2))GB Total, $([math]::Round($_.FreeSpace/1GB,2))GB Free""
    }
} | Format-List",
                Parameters = "None required. Run as-is to see all system info."
            },
            
            new() {
                Key = 1,
                Name = "Monitor CPU and Memory",
                Category = "System Admin",
                Description = "Real-time monitoring of CPU and memory usage with alerts when thresholds are exceeded. Useful for performance troubleshooting and capacity planning.",
                Purpose = "Monitor system resources and get alerts when usage is too high",
                Complexity = "Intermediate",
                Code = @"# Monitor CPU and Memory usage
param(
    [int]$CPUThreshold = 80,
    [int]$MemoryThreshold = 90,
    [int]$IntervalSeconds = 5
)

Write-Host ""Monitoring system resources (Press Ctrl+C to stop)..."" -ForegroundColor Cyan

while ($true) {
    $CPU = Get-Counter '\Processor(_Total)\% Processor Time' | Select-Object -ExpandProperty CounterSamples | Select-Object -ExpandProperty CookedValue
    $Memory = Get-CimInstance Win32_OperatingSystem
    $MemoryUsedPercent = [math]::Round((($Memory.TotalVisibleMemorySize - $Memory.FreePhysicalMemory) / $Memory.TotalVisibleMemorySize) * 100, 2)
    
    $timestamp = Get-Date -Format ""yyyy-MM-dd HH:mm:ss""
    Write-Host ""[$timestamp] CPU: $([math]::Round($CPU, 2))% | Memory: $MemoryUsedPercent%"" -ForegroundColor $(
        if ($CPU -gt $CPUThreshold -or $MemoryUsedPercent -gt $MemoryThreshold) { 'Red' } else { 'Green' }
    )
    
    if ($CPU -gt $CPUThreshold) {
        Write-Warning ""CPU usage exceeded $CPUThreshold%!""
    }
    if ($MemoryUsedPercent -gt $MemoryThreshold) {
        Write-Warning ""Memory usage exceeded $MemoryThreshold%!""
    }
    
    Start-Sleep -Seconds $IntervalSeconds
}",
                Parameters = "-CPUThreshold (default: 80), -MemoryThreshold (default: 90), -IntervalSeconds (default: 5)"
            },

            // FILE MANAGEMENT
            new() {
                Key = 2,
                Name = "Find Large Files",
                Category = "File Management",
                Description = "Recursively scan directories to find the largest files. Great for identifying disk space hogs and cleaning up storage.",
                Purpose = "Identify large files consuming disk space to free up storage",
                Complexity = "Beginner",
                Code = @"# Find largest files in a directory
param(
    [string]$Path = ""C:\"",
    [int]$TopN = 20,
    [int]$MinSizeMB = 100
)

Write-Host ""Scanning $Path for large files (min $MinSizeMB MB)..."" -ForegroundColor Cyan

Get-ChildItem -Path $Path -File -Recurse -ErrorAction SilentlyContinue | 
    Where-Object { $_.Length -ge ($MinSizeMB * 1MB) } |
    Select-Object @{Name='Size_GB';Expression={[math]::Round($_.Length/1GB, 2)}}, FullName, LastWriteTime |
    Sort-Object Size_GB -Descending |
    Select-Object -First $TopN |
    Format-Table -AutoSize",
                Parameters = "-Path (default: C:\\), -TopN (default: 20), -MinSizeMB (default: 100)"
            },

            new() {
                Key = 3,
                Name = "Bulk File Renamer",
                Category = "File Management",
                Description = "Rename multiple files at once with prefix, suffix, or pattern replacement. Supports preview mode to check changes before applying.",
                Purpose = "Batch rename files quickly and safely with preview option",
                Complexity = "Intermediate",
                Code = @"# Bulk rename files with preview
param(
    [string]$Path = ""."",
    [string]$Filter = ""*.*"",
    [string]$Prefix = """",
    [string]$Suffix = """",
    [string]$Replace = """",
    [string]$ReplaceWith = """",
    [switch]$Preview
)

$files = Get-ChildItem -Path $Path -Filter $Filter -File

Write-Host ""Found $($files.Count) files"" -ForegroundColor Cyan

foreach ($file in $files) {
    $newName = $file.BaseName
    
    if ($Replace) { $newName = $newName -replace $Replace, $ReplaceWith }
    $newName = $Prefix + $newName + $Suffix + $file.Extension
    
    if ($Preview) {
        Write-Host ""$($file.Name) -> $newName"" -ForegroundColor Yellow
    } else {
        Rename-Item -Path $file.FullName -NewName $newName
        Write-Host ""Renamed: $newName"" -ForegroundColor Green
    }
}

if ($Preview) {
    Write-Host ""`nPreview mode. Remove -Preview to apply changes."" -ForegroundColor Magenta
}",
                Parameters = "-Path, -Filter (*.txt), -Prefix, -Suffix, -Replace, -ReplaceWith, -Preview (safe mode)"
            },

            new() {
                Key = 4,
                Name = "Organize Files by Extension",
                Category = "File Management",
                Description = "Automatically organize messy folders by moving files into subdirectories based on their file extensions.",
                Purpose = "Clean up cluttered folders by sorting files into extension-based folders",
                Complexity = "Beginner",
                Code = @"# Organize files by extension
param(
    [string]$SourcePath = ""$env:USERPROFILE\\Downloads""
)

Write-Host ""Organizing files in $SourcePath..."" -ForegroundColor Cyan

$files = Get-ChildItem -Path $SourcePath -File

foreach ($file in $files) {
    $ext = $file.Extension.TrimStart('.').ToUpper()
    if ([string]::IsNullOrEmpty($ext)) { $ext = ""NO_EXTENSION"" }
    
    $destFolder = Join-Path $SourcePath $ext
    
    if (-not (Test-Path $destFolder)) {
        New-Item -ItemType Directory -Path $destFolder | Out-Null
        Write-Host ""Created folder: $ext"" -ForegroundColor Yellow
    }
    
    Move-Item -Path $file.FullName -Destination $destFolder
    Write-Host ""Moved: $($file.Name) -> $ext"" -ForegroundColor Green
}

Write-Host ""Organization complete!"" -ForegroundColor Cyan",
                Parameters = "-SourcePath (default: Downloads folder)"
            },

            // NETWORK
            new() {
                Key = 5,
                Name = "Test Network Connectivity",
                Category = "Network",
                Description = "Comprehensive network testing including ping, DNS resolution, port checks, and traceroute. Perfect for troubleshooting connectivity issues.",
                Purpose = "Diagnose network connectivity problems with multiple tests",
                Complexity = "Intermediate",
                Code = @"# Comprehensive network diagnostics
param(
    [string]$Target = ""google.com"",
    [int[]]$Ports = @(80, 443, 3389)
)

Write-Host ""Network Diagnostics for $Target"" -ForegroundColor Cyan
Write-Host (""="" * 50)

# DNS Resolution
Write-Host ""`n[1] DNS Resolution:"" -ForegroundColor Yellow
try {
    $dns = [System.Net.Dns]::GetHostAddresses($Target)
    $dns | ForEach-Object { Write-Host ""   $_"" -ForegroundColor Green }
} catch {
    Write-Host ""   Failed: $($_.Exception.Message)"" -ForegroundColor Red
}

# Ping Test
Write-Host ""`n[2] Ping Test:"" -ForegroundColor Yellow
$ping = Test-Connection -ComputerName $Target -Count 4 -ErrorAction SilentlyContinue
if ($ping) {
    $avg = ($ping | Measure-Object -Property ResponseTime -Average).Average
    Write-Host ""   Success! Average: $([math]::Round($avg, 2))ms"" -ForegroundColor Green
} else {
    Write-Host ""   Failed"" -ForegroundColor Red
}

# Port Tests
Write-Host ""`n[3] Port Tests:"" -ForegroundColor Yellow
foreach ($port in $Ports) {
    $result = Test-NetConnection -ComputerName $Target -Port $port -WarningAction SilentlyContinue
    $status = if ($result.TcpTestSucceeded) { ""Open"" } else { ""Closed"" }
    $color = if ($result.TcpTestSucceeded) { ""Green"" } else { ""Red"" }
    Write-Host ""   Port $port : $status"" -ForegroundColor $color
}",
                Parameters = "-Target (default: google.com), -Ports (default: 80, 443, 3389)"
            },

            new() {
                Key = 6,
                Name = "Get Network Adapters Info",
                Category = "Network",
                Description = "Display detailed information about all network adapters including IP addresses, MAC addresses, DNS servers, and connection status.",
                Purpose = "View complete network adapter configuration for troubleshooting",
                Complexity = "Beginner",
                Code = @"# Get network adapter information
Get-NetAdapter | Where-Object {$_.Status -eq 'Up'} | ForEach-Object {
    $adapter = $_
    $ipConfig = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue
    $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue
    
    [PSCustomObject]@{
        Name = $adapter.Name
        Status = $adapter.Status
        Speed = $adapter.LinkSpeed
        MAC = $adapter.MacAddress
        IPv4 = ($ipConfig | Where-Object {$_.AddressFamily -eq 'IPv4'}).IPAddress
        IPv6 = ($ipConfig | Where-Object {$_.AddressFamily -eq 'IPv6'}).IPAddress
        DNS = ($dns | Where-Object {$_.AddressFamily -eq 2}).ServerAddresses -join ', '
    }
} | Format-List",
                Parameters = "None required"
            },

            // SECURITY
            new() {
                Key = 7,
                Name = "Check Failed Login Attempts",
                Category = "Security",
                Description = "Scan Windows Event Logs for failed login attempts to detect potential security threats or brute force attacks.",
                Purpose = "Monitor failed login attempts for security threats",
                Complexity = "Intermediate",
                Code = @"# Check for failed login attempts
param(
    [int]$Hours = 24
)

$startTime = (Get-Date).AddHours(-$Hours)

Write-Host ""Checking failed login attempts in the last $Hours hours..."" -ForegroundColor Cyan

$failedLogins = Get-WinEvent -FilterHashtable @{
    LogName = 'Security'
    ID = 4625
    StartTime = $startTime
} -ErrorAction SilentlyContinue

if ($failedLogins) {
    $grouped = $failedLogins | ForEach-Object {
        [PSCustomObject]@{
            Time = $_.TimeCreated
            Account = $_.Properties[5].Value
            Source = $_.Properties[19].Value
        }
    } | Group-Object Account | Sort-Object Count -Descending
    
    Write-Host ""`nFailed Login Summary:"" -ForegroundColor Yellow
    $grouped | Select-Object @{Name='Account';Expression={$_.Name}}, Count | Format-Table -AutoSize
    
    Write-Host ""`nTotal Failed Attempts: $($failedLogins.Count)"" -ForegroundColor Red
} else {
    Write-Host ""No failed login attempts found."" -ForegroundColor Green
}",
                Parameters = "-Hours (default: 24)"
            },

            new() {
                Key = 8,
                Name = "Scan Open Ports",
                Category = "Security",
                Description = "Scan localhost or remote systems for open ports to identify potential security vulnerabilities or services running.",
                Purpose = "Identify open ports and services for security auditing",
                Complexity = "Advanced",
                Code = @"# Scan for open ports
param(
    [string]$ComputerName = ""localhost"",
    [int[]]$Ports = 1..1024
)

Write-Host ""Scanning $ComputerName for open ports..."" -ForegroundColor Cyan

$openPorts = @()

foreach ($port in $Ports) {
    $connection = New-Object System.Net.Sockets.TcpClient
    try {
        $connection.Connect($ComputerName, $port)
        $openPorts += $port
        Write-Host ""Port $port : OPEN"" -ForegroundColor Green
        $connection.Close()
    } catch {
        # Port closed, silently continue
    }
}

Write-Host ""`nScan Complete. Open Ports: $($openPorts.Count)"" -ForegroundColor Cyan
if ($openPorts) {
    Write-Host ""Open Ports: $($openPorts -join ', ')"" -ForegroundColor Yellow
}",
                Parameters = "-ComputerName (default: localhost), -Ports (default: 1-1024)"
            },

            // AUTOMATION
            new() {
                Key = 9,
                Name = "Scheduled Backup Script",
                Category = "Automation",
                Description = "Automated backup solution with compression, rotation, and email notifications. Set up scheduled tasks to run daily/weekly.",
                Purpose = "Automatically backup important folders with rotation and cleanup",
                Complexity = "Advanced",
                Code = @"# Automated backup with rotation
param(
    [string]$SourcePath = ""C:\\Important"",
    [string]$BackupPath = ""D:\\Backups"",
    [int]$RetentionDays = 30
)

$timestamp = Get-Date -Format ""yyyyMMdd_HHmmss""
$backupName = ""Backup_$timestamp.zip""
$backupFile = Join-Path $BackupPath $backupName

Write-Host ""Starting backup of $SourcePath..."" -ForegroundColor Cyan

# Create backup folder if doesn't exist
if (-not (Test-Path $BackupPath)) {
    New-Item -ItemType Directory -Path $BackupPath | Out-Null
}

# Compress and backup
Compress-Archive -Path $SourcePath -DestinationPath $backupFile -CompressionLevel Optimal

Write-Host ""Backup created: $backupFile"" -ForegroundColor Green
Write-Host ""Size: $([math]::Round((Get-Item $backupFile).Length / 1MB, 2)) MB"" -ForegroundColor Yellow

# Clean old backups
$cutoffDate = (Get-Date).AddDays(-$RetentionDays)
$oldBackups = Get-ChildItem -Path $BackupPath -Filter ""Backup_*.zip"" | Where-Object { $_.LastWriteTime -lt $cutoffDate }

if ($oldBackups) {
    Write-Host ""`nRemoving old backups (older than $RetentionDays days)..."" -ForegroundColor Yellow
    $oldBackups | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host ""Removed: $($_.Name)"" -ForegroundColor Gray
    }
}

Write-Host ""`nBackup complete!"" -ForegroundColor Cyan",
                Parameters = "-SourcePath, -BackupPath, -RetentionDays (default: 30)"
            },

            new() {
                Key = 10,
                Name = "Clean Temp Files",
                Category = "Automation",
                Description = "Safely remove temporary files from Windows temp folders, browser caches, and other locations to free up disk space.",
                Purpose = "Free up disk space by cleaning temporary and cache files",
                Complexity = "Beginner",
                Code = @"# Clean temporary files
Write-Host ""Cleaning temporary files..."" -ForegroundColor Cyan

$tempPaths = @(
    ""$env:TEMP"",
    ""$env:LOCALAPPDATA\\Microsoft\\Windows\\INetCache"",
    ""$env:LOCALAPPDATA\\Microsoft\\Windows\\Temporary Internet Files"",
    ""C:\\Windows\\Temp""
)

$totalSize = 0

foreach ($path in $tempPaths) {
    if (Test-Path $path) {
        Write-Host ""`nCleaning: $path"" -ForegroundColor Yellow
        
        $files = Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue
        $size = ($files | Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum / 1MB
        
        $files | ForEach-Object {
            try {
                Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            } catch {
                # Skip locked files
            }
        }
        
        Write-Host ""Cleaned: $([math]::Round($size, 2)) MB"" -ForegroundColor Green
        $totalSize += $size
    }
}

Write-Host ""`nTotal space freed: $([math]::Round($totalSize, 2)) MB"" -ForegroundColor Cyan",
                Parameters = "None required. Run with admin rights for best results"
            },

            // AZURE
            new() {
                Key = 11,
                Name = "Azure VM Status Check",
                Category = "Azure",
                Description = "Check status of all Azure VMs across subscriptions including power state, size, location, and resource group.",
                Purpose = "Monitor Azure VM status and get cost insights",
                Complexity = "Intermediate",
                Code = @"# Check Azure VM status
# Requires: Install-Module Az

Import-Module Az

# Connect to Azure
Connect-AzAccount

Write-Host ""Fetching Azure VM status..."" -ForegroundColor Cyan

$vms = Get-AzVM -Status

$vms | Select-Object @{
    Name='Name'
    Expression={$_.Name}
}, @{
    Name='ResourceGroup'
    Expression={$_.ResourceGroupName}
}, @{
    Name='Location'
    Expression={$_.Location}
}, @{
    Name='Size'
    Expression={$_.HardwareProfile.VmSize}
}, @{
    Name='Status'
    Expression={
        $status = $_.Statuses | Where-Object {$_.Code -like 'PowerState/*'}
        $status.DisplayStatus
    }
} | Format-Table -AutoSize

Write-Host ""`nTotal VMs: $($vms.Count)"" -ForegroundColor Cyan",
                Parameters = "None required. Must be logged into Azure (Connect-AzAccount)"
            },

            new() {
                Key = 12,
                Name = "Azure Cost Report",
                Category = "Azure",
                Description = "Generate a cost report for Azure resources showing spending by resource group and service type.",
                Purpose = "Track and analyze Azure spending to optimize costs",
                Complexity = "Advanced",
                Code = @"# Azure cost analysis
# Requires: Install-Module Az.Billing

Import-Module Az.Billing

$endDate = Get-Date
$startDate = $endDate.AddMonths(-1)

Write-Host ""Generating cost report from $($startDate.ToString('yyyy-MM-dd')) to $($endDate.ToString('yyyy-MM-dd'))..."" -ForegroundColor Cyan

$costs = Get-AzConsumptionUsageDetail -StartDate $startDate -EndDate $endDate

$summary = $costs | Group-Object ResourceGroup | Select-Object @{
    Name='ResourceGroup'
    Expression={$_.Name}
}, @{
    Name='TotalCost'
    Expression={[math]::Round(($_.Group | Measure-Object -Property PretaxCost -Sum).Sum, 2)}
} | Sort-Object TotalCost -Descending

$summary | Format-Table -AutoSize

$total = ($costs | Measure-Object -Property PretaxCost -Sum).Sum
Write-Host ""`nTotal Cost: `$$([math]::Round($total, 2))"" -ForegroundColor Yellow",
                Parameters = "None required. Shows last month by default"
            },

            // GIT
            new() {
                Key = 13,
                Name = "Git Commit Summary",
                Category = "Git",
                Description = "Generate a summary of Git commits with author statistics, file changes, and contribution breakdown.",
                Purpose = "Analyze Git repository activity and contributor statistics",
                Complexity = "Intermediate",
                Code = @"# Git commit analysis
param(
    [string]$Path = ""."",
    [int]$Days = 30
)

Push-Location $Path

Write-Host ""Git Commit Analysis (Last $Days days)"" -ForegroundColor Cyan
Write-Host (""="" * 50)

$since = (Get-Date).AddDays(-$Days).ToString(""yyyy-MM-dd"")

# Commit count by author
Write-Host ""`nCommits by Author:"" -ForegroundColor Yellow
git log --since=$since --pretty=format:""%an"" | Group-Object | Sort-Object Count -Descending | Select-Object @{Name='Author';Expression={$_.Name}}, Count | Format-Table -AutoSize

# Recent commits
Write-Host ""`nRecent Commits:"" -ForegroundColor Yellow
git log --since=$since --pretty=format:""%h - %an, %ar : %s"" -10

# File changes
Write-Host ""`n`nMost Changed Files:"" -ForegroundColor Yellow
git log --since=$since --name-only --pretty=format: | Where-Object {$_} | Group-Object | Sort-Object Count -Descending | Select-Object -First 10 | Select-Object @{Name='File';Expression={$_.Name}}, Count | Format-Table -AutoSize

Pop-Location",
                Parameters = "-Path (default: current directory), -Days (default: 30)"
            },

            new() {
                Key = 14,
                Name = "Git Branch Cleanup",
                Category = "Git",
                Description = "Clean up merged and stale Git branches both locally and remotely. Keeps your repository tidy.",
                Purpose = "Remove old merged branches to keep repository clean",
                Complexity = "Intermediate",
                Code = @"# Clean up merged Git branches
param(
    [string]$Path = ""."",
    [switch]$DryRun
)

Push-Location $Path

Write-Host ""Git Branch Cleanup"" -ForegroundColor Cyan

# Get current branch
$currentBranch = git rev-parse --abbrev-ref HEAD

# Find merged branches
$mergedBranches = git branch --merged | Where-Object {
    $_ -notmatch ""^\*"" -and 
    $_ -notmatch ""main"" -and 
    $_ -notmatch ""master"" -and 
    $_ -notmatch ""develop""
} | ForEach-Object { $_.Trim() }

if ($mergedBranches) {
    Write-Host ""`nMerged branches to delete:"" -ForegroundColor Yellow
    $mergedBranches | ForEach-Object { Write-Host ""  $_"" -ForegroundColor Gray }
    
    if (-not $DryRun) {
        Write-Host ""`nDeleting branches..."" -ForegroundColor Red
        $mergedBranches | ForEach-Object {
            git branch -d $_
            Write-Host ""Deleted: $_"" -ForegroundColor Green
        }
    } else {
        Write-Host ""`nDry run mode. Add -DryRun:`$false to delete."" -ForegroundColor Magenta
    }
} else {
    Write-Host ""`nNo merged branches to clean up."" -ForegroundColor Green
}

Pop-Location",
                Parameters = "-Path (default: .), -DryRun (preview mode)"
            },

            // DEVELOPMENT
            new() {
                Key = 15,
                Name = "Project Directory Setup",
                Category = "Development",
                Description = "Quickly scaffold a new project directory structure with common folders like src, tests, docs, and initialize Git.",
                Purpose = "Create standard project structure for new development projects",
                Complexity = "Beginner",
                Code = @"# Create project directory structure
param(
    [string]$ProjectName = $(Read-Host ""Enter project name""),
    [string]$ProjectType = ""standard""  # standard, web, api, cli
)

$projectPath = Join-Path (Get-Location) $ProjectName

Write-Host ""Creating project: $ProjectName"" -ForegroundColor Cyan

# Create base folders
$folders = @(""src"", ""tests"", ""docs"", ""scripts"", "".vscode"")

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

foreach ($folder in $folders) {
    $path = Join-Path $projectPath $folder
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    Write-Host ""Created: $folder"" -ForegroundColor Green
}

# Create README
$readme = @""
# $ProjectName

## Description
Add your project description here.

## Setup
1. Install dependencies
2. Configure settings
3. Run project

## Usage
Instructions here

## License
MIT
""@

Set-Content -Path (Join-Path $projectPath ""README.md"") -Value $readme

# Initialize Git
Push-Location $projectPath
git init
git add .
git commit -m ""Initial commit""
Pop-Location

Write-Host ""`nProject created successfully at: $projectPath"" -ForegroundColor Cyan",
                Parameters = "-ProjectName (required), -ProjectType (optional)"
            },

            new() {
                Key = 16,
                Name = "Find TODO Comments",
                Category = "Development",
                Description = "Search codebase for TODO, FIXME, HACK, and BUG comments to track technical debt and pending work.",
                Purpose = "Track TODO items and technical debt in source code",
                Complexity = "Beginner",
                Code = @"# Find TODO comments in code
param(
    [string]$Path = ""."",
    [string[]]$Extensions = @(""*.cs"", ""*.js"", ""*.ts"", ""*.py"", ""*.java"")
)

Write-Host ""Searching for TODO comments in $Path..."" -ForegroundColor Cyan

$patterns = @(""TODO"", ""FIXME"", ""HACK"", ""BUG"", ""XXX"", ""NOTE"")
$results = @()

foreach ($ext in $Extensions) {
    $files = Get-ChildItem -Path $Path -Filter $ext -Recurse -File -ErrorAction SilentlyContinue
    
    foreach ($file in $files) {
        $lineNumber = 0
        Get-Content $file.FullName | ForEach-Object {
            $lineNumber++
            $line = $_
            foreach ($pattern in $patterns) {
                if ($line -match $pattern) {
                    $results += [PSCustomObject]@{
                        Type = $pattern
                        File = $file.Name
                        Line = $lineNumber
                        Content = $line.Trim()
                    }
                }
            }
        }
    }
}

$grouped = $results | Group-Object Type | Sort-Object Count -Descending

Write-Host ""`nSummary:"" -ForegroundColor Yellow
$grouped | Select-Object @{Name='Type';Expression={$_.Name}}, Count | Format-Table -AutoSize

Write-Host ""`nDetails:"" -ForegroundColor Yellow
$results | Format-Table -AutoSize",
                Parameters = "-Path (default: .), -Extensions (default: common code files)"
            },

            new() {
                Key = 17,
                Name = "NuGet Package Updater",
                Category = "Development",
                Description = "Check for outdated NuGet packages in .NET projects and optionally update them to latest versions.",
                Purpose = "Keep NuGet packages up to date in .NET projects",
                Complexity = "Intermediate",
                Code = @"# Check and update NuGet packages
param(
    [string]$ProjectPath = ""."",
    [switch]$Update
)

Write-Host ""Checking NuGet packages..."" -ForegroundColor Cyan

Push-Location $ProjectPath

# List outdated packages
$outdated = dotnet list package --outdated --format json | ConvertFrom-Json

if ($outdated.projects) {
    foreach ($project in $outdated.projects) {
        Write-Host ""`nProject: $($project.path)"" -ForegroundColor Yellow
        
        if ($project.frameworks) {
            foreach ($framework in $project.frameworks) {
                foreach ($package in $framework.topLevelPackages) {
                    Write-Host ""  $($package.id): $($package.resolvedVersion) -> $($package.latestVersion)"" -ForegroundColor $(
                        if ($Update) { 'Green' } else { 'Gray' }
                    )
                    
                    if ($Update) {
                        dotnet add package $package.id
                    }
                }
            }
        }
    }
    
    if (-not $Update) {
        Write-Host ""`nRun with -Update to update packages"" -ForegroundColor Magenta
    }
} else {
    Write-Host ""All packages are up to date!"" -ForegroundColor Green
}

Pop-Location",
                Parameters = "-ProjectPath (default: .), -Update (apply updates)"
            },

            new() {
                Key = 18,
                Name = "Code Metrics Calculator",
                Category = "Development",
                Description = "Calculate code metrics including lines of code, comment ratio, file counts, and language distribution.",
                Purpose = "Analyze codebase size and composition for reporting",
                Complexity = "Intermediate",
                Code = @"# Calculate code metrics
param(
    [string]$Path = ""."",
    [string[]]$Extensions = @(""*.cs"", ""*.js"", ""*.ts"", ""*.py"", ""*.java"", ""*.cpp"", ""*.h"")
)

Write-Host ""Calculating code metrics for $Path..."" -ForegroundColor Cyan

$totalLines = 0
$totalFiles = 0
$codeLines = 0
$commentLines = 0
$blankLines = 0

$byExtension = @{}

foreach ($ext in $Extensions) {
    $files = Get-ChildItem -Path $Path -Filter $ext -Recurse -File -ErrorAction SilentlyContinue
    
    foreach ($file in $files) {
        $totalFiles++
        $content = Get-Content $file.FullName
        $totalLines += $content.Count
        
        $extension = $file.Extension
        if (-not $byExtension.ContainsKey($extension)) {
            $byExtension[$extension] = @{Files=0; Lines=0}
        }
        $byExtension[$extension].Files++
        $byExtension[$extension].Lines += $content.Count
        
        foreach ($line in $content) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                $blankLines++
            } elseif ($line.Trim().StartsWith(""/"") -or $line.Trim().StartsWith(""#"") -or $line.Trim().StartsWith(""*"")) {
                $commentLines++
            } else {
                $codeLines++
            }
        }
    }
}

Write-Host ""`n========== CODE METRICS =========="" -ForegroundColor Cyan
Write-Host ""Total Files: $totalFiles"" -ForegroundColor Yellow
Write-Host ""Total Lines: $totalLines"" -ForegroundColor Yellow
Write-Host ""Code Lines: $codeLines ($([math]::Round(($codeLines/$totalLines)*100, 2))%)"" -ForegroundColor Green
Write-Host ""Comment Lines: $commentLines ($([math]::Round(($commentLines/$totalLines)*100, 2))%)"" -ForegroundColor Gray
Write-Host ""Blank Lines: $blankLines ($([math]::Round(($blankLines/$totalLines)*100, 2))%)"" -ForegroundColor DarkGray

Write-Host ""`n========== BY LANGUAGE =========="" -ForegroundColor Cyan
$byExtension.GetEnumerator() | Sort-Object {$_.Value.Lines} -Descending | ForEach-Object {
    Write-Host ""$($_.Key): $($_.Value.Files) files, $($_.Value.Lines) lines"" -ForegroundColor Yellow
}",
                Parameters = "-Path (default: .), -Extensions (default: common code files)"
            },

            // MORE SYSTEM ADMIN
            new() {
                Key = 19,
                Name = "Service Manager",
                Category = "System Admin",
                Description = "List, start, stop, and restart Windows services with filtering and bulk operations support.",
                Purpose = "Manage Windows services easily with bulk operations",
                Complexity = "Intermediate",
                Code = @"# Manage Windows services
param(
    [string]$ServiceName = """",
    [string]$Action = ""List"",  # List, Start, Stop, Restart
    [string]$Filter = """"
)

if ($Action -eq ""List"") {
    Write-Host ""Windows Services"" -ForegroundColor Cyan
    
    $services = Get-Service
    
    if ($Filter) {
        $services = $services | Where-Object { $_.DisplayName -like ""*$Filter*"" -or $_.Name -like ""*$Filter*"" }
    }
    
    $services | Select-Object Status, DisplayName, Name, StartType | 
        Sort-Object Status, DisplayName | 
        Format-Table -AutoSize
    
    Write-Host ""`nTotal: $($services.Count) services"" -ForegroundColor Yellow
} else {
    if (-not $ServiceName) {
        Write-Host ""Service name required for $Action action"" -ForegroundColor Red
        return
    }
    
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    
    if (-not $service) {
        Write-Host ""Service '$ServiceName' not found"" -ForegroundColor Red
        return
    }
    
    Write-Host ""$Action service: $($service.DisplayName)..."" -ForegroundColor Cyan
    
    switch ($Action) {
        ""Start"" { Start-Service -Name $ServiceName; Write-Host ""Started"" -ForegroundColor Green }
        ""Stop"" { Stop-Service -Name $ServiceName; Write-Host ""Stopped"" -ForegroundColor Yellow }
        ""Restart"" { Restart-Service -Name $ServiceName; Write-Host ""Restarted"" -ForegroundColor Green }
    }
}",
                Parameters = "-ServiceName, -Action (List/Start/Stop/Restart), -Filter"
            },

            new() {
                Key = 20,
                Name = "Event Log Analyzer",
                Category = "System Admin",
                Description = "Analyze Windows Event Logs for errors, warnings, and critical events with customizable time ranges and filtering.",
                Purpose = "Quickly find and analyze system errors and warnings",
                Complexity = "Intermediate",
                Code = @"# Analyze Windows Event Logs
param(
    [string]$LogName = ""System"",
    [int]$Hours = 24,
    [string]$Level = ""Error""  # Error, Warning, Critical
)

$startTime = (Get-Date).AddHours(-$Hours)

Write-Host ""Analyzing $LogName log for $Level events (last $Hours hours)..."" -ForegroundColor Cyan

$levelMap = @{
    'Error' = 2
    'Warning' = 3
    'Critical' = 1
}

$events = Get-WinEvent -FilterHashtable @{
    LogName = $LogName
    Level = $levelMap[$Level]
    StartTime = $startTime
} -ErrorAction SilentlyContinue

if ($events) {
    Write-Host ""`nFound $($events.Count) $Level events"" -ForegroundColor Yellow
    
    # Group by ProviderName
    $grouped = $events | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 10
    
    Write-Host ""`nTop Event Sources:"" -ForegroundColor Yellow
    $grouped | Select-Object @{Name='Source';Expression={$_.Name}}, Count | Format-Table -AutoSize
    
    # Show recent events
    Write-Host ""`nRecent Events:"" -ForegroundColor Yellow
    $events | Select-Object -First 10 | Select-Object TimeCreated, Id, ProviderName, Message | Format-Table -AutoSize -Wrap
} else {
    Write-Host ""No $Level events found"" -ForegroundColor Green
}",
                Parameters = "-LogName (default: System), -Hours (default: 24), -Level (Error/Warning/Critical)"
            },

            new() {
                Key = 21,
                Name = "Process Monitor",
                Category = "System Admin",
                Description = "Monitor running processes, sort by CPU or memory usage, and identify resource-intensive applications.",
                Purpose = "Identify and monitor resource-intensive processes",
                Complexity = "Beginner",
                Code = @"# Monitor running processes
param(
    [string]$SortBy = ""CPU"",  # CPU, Memory
    [int]$Top = 15
)

Write-Host ""Top $Top Processes by $SortBy Usage"" -ForegroundColor Cyan
Write-Host (""="" * 80)

if ($SortBy -eq ""CPU"") {
    Get-Process | Sort-Object CPU -Descending | Select-Object -First $Top |
        Select-Object @{Name='CPU(s)';Expression={[math]::Round($_.CPU, 2)}},
                      @{Name='Memory(MB)';Expression={[math]::Round($_.WorkingSet / 1MB, 2)}},
                      ProcessName,
                      Id,
                      @{Name='Threads';Expression={$_.Threads.Count}} |
        Format-Table -AutoSize
} else {
    Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First $Top |
        Select-Object @{Name='Memory(MB)';Expression={[math]::Round($_.WorkingSet / 1MB, 2)}},
                      @{Name='CPU(s)';Expression={[math]::Round($_.CPU, 2)}},
                      ProcessName,
                      Id,
                      @{Name='Threads';Expression={$_.Threads.Count}} |
        Format-Table -AutoSize
}

$totalMemory = (Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize / 1MB
$usedMemory = (Get-Process | Measure-Object -Property WorkingSet -Sum).Sum / 1MB
Write-Host ""`nTotal System Memory: $([math]::Round($totalMemory, 2)) MB"" -ForegroundColor Yellow
Write-Host ""Used Memory: $([math]::Round($usedMemory, 2)) MB ($([math]::Round(($usedMemory/$totalMemory)*100, 2))%)"" -ForegroundColor Yellow",
                Parameters = "-SortBy (CPU/Memory), -Top (default: 15)"
            },

            new() {
                Key = 22,
                Name = "Registry Backup",
                Category = "System Admin",
                Description = "Backup Windows Registry keys before making changes. Essential for safe system modifications.",
                Purpose = "Safely backup registry keys before modifications",
                Complexity = "Advanced",
                Code = @"# Backup Windows Registry
param(
    [string]$RegistryPath = ""HKLM:\\SOFTWARE"",
    [string]$BackupPath = ""$env:USERPROFILE\\RegistryBackups""
)

Write-Host ""Backing up registry key: $RegistryPath"" -ForegroundColor Cyan

# Create backup directory
if (-not (Test-Path $BackupPath)) {
    New-Item -ItemType Directory -Path $BackupPath | Out-Null
}

$timestamp = Get-Date -Format ""yyyyMMdd_HHmmss""
$backupFile = Join-Path $BackupPath ""Registry_Backup_$timestamp.reg""

# Convert PowerShell path to reg.exe format
$regPath = $RegistryPath -replace 'HKLM:\\', 'HKEY_LOCAL_MACHINE\\'
$regPath = $regPath -replace 'HKCU:\\', 'HKEY_CURRENT_USER\\'

# Export registry
try {
    Start-Process -FilePath ""reg.exe"" -ArgumentList ""export"", ""$regPath"", ""$backupFile"" -Wait -NoNewWindow
    
    if (Test-Path $backupFile) {
        Write-Host ""Backup created successfully!"" -ForegroundColor Green
        Write-Host ""Location: $backupFile"" -ForegroundColor Yellow
        Write-Host ""Size: $([math]::Round((Get-Item $backupFile).Length / 1KB, 2)) KB"" -ForegroundColor Yellow
    } else {
        Write-Host ""Backup failed"" -ForegroundColor Red
    }
} catch {
    Write-Host ""Error: $($_.Exception.Message)"" -ForegroundColor Red
}",
                Parameters = "-RegistryPath (default: HKLM:\\SOFTWARE), -BackupPath"
            },

            new() {
                Key = 23,
                Name = "Disk Space Report",
                Category = "System Admin",
                Description = "Generate comprehensive disk space report with usage percentages, warnings for low space, and folder size breakdown.",
                Purpose = "Monitor disk space usage and identify storage issues",
                Complexity = "Beginner",
                Code = @"# Disk space report
param(
    [int]$WarningThreshold = 85,
    [switch]$IncludeFolders
)

Write-Host ""Disk Space Report"" -ForegroundColor Cyan
Write-Host (""="" * 80)

$disks = Get-CimInstance Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3}

foreach ($disk in $disks) {
    $usedPercent = [math]::Round((($disk.Size - $disk.FreeSpace) / $disk.Size) * 100, 2)
    $color = if ($usedPercent -gt $WarningThreshold) { 'Red' } elseif ($usedPercent -gt 70) { 'Yellow' } else { 'Green' }
    
    Write-Host ""`nDrive: $($disk.DeviceID)"" -ForegroundColor White
    Write-Host ""Total: $([math]::Round($disk.Size / 1GB, 2)) GB"" -ForegroundColor Gray
    Write-Host ""Free: $([math]::Round($disk.FreeSpace / 1GB, 2)) GB"" -ForegroundColor Gray
    Write-Host ""Used: $usedPercent%"" -ForegroundColor $color
    
    if ($usedPercent -gt $WarningThreshold) {
        Write-Warning ""Low disk space on $($disk.DeviceID)!""
    }
    
    if ($IncludeFolders) {
        Write-Host ""`nLargest folders on $($disk.DeviceID):"" -ForegroundColor Yellow
        $rootFolders = Get-ChildItem -Path ""$($disk.DeviceID)\\"" -Directory -ErrorAction SilentlyContinue
        
        $folderSizes = foreach ($folder in $rootFolders) {
            try {
                $size = (Get-ChildItem -Path $folder.FullName -Recurse -File -ErrorAction SilentlyContinue | 
                        Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum / 1GB
                [PSCustomObject]@{
                    Folder = $folder.Name
                    'Size(GB)' = [math]::Round($size, 2)
                }
            } catch {
                # Skip inaccessible folders
            }
        }
        
        $folderSizes | Sort-Object 'Size(GB)' -Descending | Select-Object -First 10 | Format-Table -AutoSize
    }
}",
                Parameters = "-WarningThreshold (default: 85%), -IncludeFolders"
            },

            new() {
                Key = 24,
                Name = "User Account Report",
                Category = "Security",
                Description = "Generate report of local user accounts including status, last login, password age, and group memberships.",
                Purpose = "Audit local user accounts for security compliance",
                Complexity = "Intermediate",
                Code = @"# User account report
Write-Host ""Local User Account Report"" -ForegroundColor Cyan
Write-Host (""="" * 80)

$users = Get-LocalUser

foreach ($user in $users) {
    $groups = (Get-LocalGroup | Where-Object { 
        (Get-LocalGroupMember -Group $_.Name -ErrorAction SilentlyContinue).Name -contains ""$env:COMPUTERNAME\\$($user.Name)""
    }).Name -join ', '
    
    [PSCustomObject]@{
        Username = $user.Name
        Enabled = $user.Enabled
        PasswordRequired = $user.PasswordRequired
        PasswordExpires = $user.PasswordExpires
        LastLogon = $user.LastLogon
        Groups = $groups
    }
} | Format-Table -AutoSize -Wrap

# Summary
$totalUsers = $users.Count
$enabledUsers = ($users | Where-Object {$_.Enabled}).Count
$adminUsers = (Get-LocalGroupMember -Group ""Administrators"").Count

Write-Host ""`nSummary:"" -ForegroundColor Yellow
Write-Host ""Total Users: $totalUsers"" -ForegroundColor Gray
Write-Host ""Enabled Users: $enabledUsers"" -ForegroundColor Green
Write-Host ""Administrators: $adminUsers"" -ForegroundColor Red",
                Parameters = "None required. Requires admin privileges"
            }
        };
    }
}
