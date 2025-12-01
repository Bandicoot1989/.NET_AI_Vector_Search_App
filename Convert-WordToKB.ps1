<#
.SYNOPSIS
    Converts Word documents (.docx) in the KnowledgeBase folder to JSON format for the Knowledge Base system.

.DESCRIPTION
    This script uses Pandoc to convert Word documents to Markdown, then parses the content
    to extract metadata and creates a JSON file compatible with the Knowledge Base system.

.PARAMETER InputFolder
    Path to the folder containing Word documents. Default: .\KnowledgeBase

.PARAMETER OutputFile
    Path for the output JSON file. Default: .\KnowledgeBase\knowledge-articles.json

.PARAMETER Force
    If specified, will re-process all documents even if they already exist in the JSON.

.EXAMPLE
    .\Convert-WordToKB.ps1
    
.EXAMPLE
    .\Convert-WordToKB.ps1 -Force
    
.NOTES
    Requires Pandoc to be installed: winget install JohnMacFarlane.Pandoc
    
    Document Structure Expected:
    - PURPOSE section
    - CONTEXT section
    - Applies to section
    - Main content with ### headers
    - Metadata table at the end with: Short Description, KB Group, KB Owner, Meta, Target Readers, Language, KB Number
#>

param(
    [string]$InputFolder = ".\KnowledgeBase",
    [string]$OutputFile = ".\KnowledgeBase\knowledge-articles.json",
    [switch]$Force
)

# Refresh PATH to include newly installed programs
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")

# Check if Pandoc is available
$pandocPath = Get-Command pandoc -ErrorAction SilentlyContinue
if (-not $pandocPath) {
    Write-Error "Pandoc is not installed. Please run: winget install JohnMacFarlane.Pandoc"
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Knowledge Base Document Converter" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Load existing articles if the file exists
$existingArticles = @()
if (Test-Path $OutputFile) {
    try {
        $existingArticles = Get-Content $OutputFile -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Host "Loaded $($existingArticles.Count) existing articles from JSON" -ForegroundColor Green
    }
    catch {
        Write-Host "Could not parse existing JSON file, starting fresh" -ForegroundColor Yellow
        $existingArticles = @()
    }
}

# Get the next available ID
$nextId = 1
if ($existingArticles.Count -gt 0) {
    $nextId = ($existingArticles | Measure-Object -Property id -Maximum).Maximum + 1
}

# Get all Word documents
$wordFiles = Get-ChildItem -Path $InputFolder -Filter "*.docx" -File -ErrorAction SilentlyContinue

if (-not $wordFiles -or $wordFiles.Count -eq 0) {
    Write-Host "No Word documents found in $InputFolder" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($wordFiles.Count) Word document(s) to process" -ForegroundColor Green
Write-Host ""

$newArticles = @()
$skippedCount = 0
$processedCount = 0

foreach ($file in $wordFiles) {
    Write-Host "Processing: $($file.Name)" -ForegroundColor Yellow
    
    # Check if this file was already processed (by matching KB number or title)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    
    if (-not $Force) {
        $alreadyExists = $existingArticles | Where-Object { 
            $_.title -like "*$baseName*" -or 
            ($_.kbNumber -and $file.Name -match $_.kbNumber)
        }
        
        if ($alreadyExists) {
            Write-Host "  -> Already exists in KB (KB: $($alreadyExists.kbNumber)), skipping" -ForegroundColor DarkGray
            $skippedCount++
            continue
        }
    }
    
    # Convert to Markdown using Pandoc
    $tempMdFile = [System.IO.Path]::GetTempFileName() + ".md"
    
    try {
        $pandocOutput = & pandoc -f docx -t markdown --wrap=none $file.FullName -o $tempMdFile 2>&1
        
        if (-not (Test-Path $tempMdFile) -or (Get-Item $tempMdFile).Length -eq 0) {
            Write-Host "  -> Failed to convert with Pandoc: $pandocOutput" -ForegroundColor Red
            continue
        }
        
        $mdContent = Get-Content $tempMdFile -Raw -Encoding UTF8
        
        # Initialize metadata with defaults
        $metadata = @{
            ShortDescription = $baseName -replace "_", " " -replace "-", " "
            KBGroup = "My WorkPlace"
            KBOwner = "IT Support"
            Meta = ""
            TargetReaders = "Users"
            Language = "English"
            KBNumber = ""
        }
        
        # Extract metadata from table at the bottom (various formats)
        # Format 1: **Short Description** value
        if ($mdContent -match "\*\*Short Description\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.ShortDescription = $matches[1].Trim()
        }
        elseif ($mdContent -match "Short Description\s+(.+?)[\r\n]") {
            $metadata.ShortDescription = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*KB Group\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.KBGroup = $matches[1].Trim()
        }
        elseif ($mdContent -match "KB Group\s+(.+?)[\r\n]") {
            $metadata.KBGroup = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*KB Owner\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.KBOwner = $matches[1].Trim()
        }
        elseif ($mdContent -match "KB Owner\s+(.+?)[\r\n]") {
            $metadata.KBOwner = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*Meta\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.Meta = $matches[1].Trim()
        }
        elseif ($mdContent -match "Meta\s+(.+?)[\r\n]") {
            $metadata.Meta = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*Target Readers\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.TargetReaders = $matches[1].Trim()
        }
        elseif ($mdContent -match "Target Readers\s+(.+?)[\r\n]") {
            $metadata.TargetReaders = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*Language\*\*\s+(.+?)(?=[\r\n]|\*\*)" ) {
            $metadata.Language = $matches[1].Trim()
        }
        elseif ($mdContent -match "Language\s+(.+?)[\r\n]") {
            $metadata.Language = $matches[1].Trim()
        }
        
        if ($mdContent -match "\*\*KB Number\*\*\s+(KB\d+)" ) {
            $metadata.KBNumber = $matches[1].Trim()
        }
        elseif ($mdContent -match "KB Number\s+(KB\d+)") {
            $metadata.KBNumber = $matches[1].Trim()
        }
        
        # Extract PURPOSE section
        $purpose = ""
        if ($mdContent -match "(?:^|\n)#\s*PURPOSE\s*[\r\n]+(.+?)(?=[\r\n]+#|\n[A-Z]{2,}[\r\n]|$)") {
            $purpose = $matches[1].Trim()
        }
        elseif ($mdContent -match "PURPOSE[\r\n]+(.+?)(?=[\r\n]+CONTEXT|$)") {
            $purpose = $matches[1].Trim()
        }
        
        # Extract CONTEXT section
        $context = ""
        if ($mdContent -match "(?:^|\n)#\s*CONTEXT\s*[\r\n]+(.+?)(?=[\r\n]+#|Applies to|$)") {
            $context = $matches[1].Trim()
        }
        elseif ($mdContent -match "CONTEXT[\r\n]+(.+?)(?=[\r\n]+###|Applies to|$)") {
            $context = $matches[1].Trim()
        }
        
        # Extract Applies to section
        $appliesTo = ""
        if ($mdContent -match "Applies to[\r\n]+(.+?)(?=[\r\n]+###|[\r\n]+[A-Z][a-z]+\s+usage|$)") {
            $appliesTo = $matches[1].Trim()
        }
        
        # Extract main content - everything between "Applies to" section and the metadata table
        $mainContent = ""
        
        # Find the start of main content (after Applies to)
        $contentStart = $mdContent.IndexOf("### Equipment usage")
        if ($contentStart -eq -1) {
            $contentStart = $mdContent.IndexOf("### Laptops usage")
        }
        if ($contentStart -eq -1) {
            # Look for first ### after Applies to
            $appliesIndex = $mdContent.IndexOf("Applies to")
            if ($appliesIndex -gt -1) {
                $nextSection = $mdContent.IndexOf("###", $appliesIndex)
                if ($nextSection -gt -1) {
                    $contentStart = $nextSection
                }
            }
        }
        
        # Find the end of main content (before metadata table)
        $contentEnd = $mdContent.IndexOf("Short Description")
        if ($contentEnd -eq -1) {
            $contentEnd = $mdContent.IndexOf("**Short Description**")
        }
        
        if ($contentStart -gt -1 -and $contentEnd -gt $contentStart) {
            $mainContent = $mdContent.Substring($contentStart, $contentEnd - $contentStart).Trim()
            
            # Clean up the content - convert ### to ## and fix bullet points
            $mainContent = $mainContent -replace "^### ", "## " -replace "\n### ", "`n## "
            $mainContent = $mainContent -replace "┬À ", "- **" -replace "┬á", ""
            
            # Try to add bold to first phrase of each bullet
            $lines = $mainContent -split "`n"
            $cleanedLines = @()
            foreach ($line in $lines) {
                if ($line -match "^- \*\*") {
                    # Already has bold, check if it needs closing
                    if ($line -notmatch "\*\*.*\*\*") {
                        # Find first comma or period to close bold
                        $line = $line -replace "^(- \*\*[^,\.]+)([,\.])", '$1**$2'
                    }
                }
                $cleanedLines += $line
            }
            $mainContent = $cleanedLines -join "`n"
        }
        
        # Generate tags from Meta field
        $tags = @()
        if ($metadata.Meta) {
            $tags = $metadata.Meta -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch "^KB\d+" }
        }
        
        # If no tags from meta, generate from title
        if ($tags.Count -eq 0) {
            $tags = ($baseName -replace "_", " " -replace "-", " ") -split " " | Where-Object { $_.Length -gt 2 }
        }
        
        # Create the article object
        $generatedKbNumber = "KB" + $nextId.ToString().PadLeft(7, '0')
        $article = [PSCustomObject]@{
            id = $nextId
            kbNumber = if ($metadata.KBNumber) { $metadata.KBNumber } else { $generatedKbNumber }
            title = $metadata.ShortDescription
            shortDescription = $metadata.ShortDescription
            purpose = $purpose
            context = $context
            appliesTo = $appliesTo
            content = $mainContent
            kbGroup = $metadata.KBGroup
            kbOwner = $metadata.KBOwner
            targetReaders = $metadata.TargetReaders
            language = $metadata.Language
            tags = $tags
            isActive = $true
            createdDate = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
            lastUpdated = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
            author = $metadata.KBOwner
        }
        
        $newArticles += $article
        $nextId++
        $processedCount++
        
        Write-Host "  -> Converted successfully!" -ForegroundColor Green
        Write-Host "     Title: $($article.title)" -ForegroundColor DarkCyan
        Write-Host "     KB Number: $($article.kbNumber)" -ForegroundColor DarkCyan
        Write-Host "     Group: $($article.kbGroup)" -ForegroundColor DarkCyan
        Write-Host "     Tags: $($tags -join ', ')" -ForegroundColor DarkCyan
        Write-Host ""
    }
    catch {
        Write-Host "  -> Error processing: $_" -ForegroundColor Red
    }
    finally {
        if (Test-Path $tempMdFile) {
            Remove-Item $tempMdFile -Force -ErrorAction SilentlyContinue
        }
    }
}

# Merge new articles with existing ones
if ($Force) {
    # Replace all with new
    $allArticles = $newArticles
}
else {
    $allArticles = @($existingArticles) + $newArticles
}

# Save to JSON file
$allArticles | ConvertTo-Json -Depth 10 | Set-Content $OutputFile -Encoding UTF8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Conversion Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Total articles in KB: $($allArticles.Count)" -ForegroundColor White
Write-Host "New articles added: $processedCount" -ForegroundColor Green
Write-Host "Skipped (already exist): $skippedCount" -ForegroundColor Yellow
Write-Host "Output file: $OutputFile" -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Review the JSON file to verify content" -ForegroundColor White
Write-Host "2. Upload to Azure: az storage blob upload --account-name scriptlibrarystorage --container-name knowledge --name 'knowledge-articles.json' --file '$OutputFile' --auth-mode key" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
