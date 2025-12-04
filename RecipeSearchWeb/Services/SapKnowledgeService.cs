using Azure.Storage.Blobs;
using ClosedXML.Excel;
using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for loading and parsing SAP Excel data from Azure Blob Storage
/// Tier 3: SAP Specialist Agent - Data Layer
/// </summary>
public class SapKnowledgeService
{
    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<SapKnowledgeService> _logger;
    private bool _isInitialized = false;
    private bool _isAvailable = false;

    // Parsed data from Excel
    public List<SapPositionRoleMapping> Mappings { get; private set; } = new();
    public List<SapPosition> Positions { get; private set; } = new();
    public List<SapRole> Roles { get; private set; } = new();
    public List<SapBusinessRole> BusinessRoles { get; private set; } = new();
    public List<SapTransaction> Transactions { get; private set; } = new();

    public bool IsAvailable => _isAvailable && _isInitialized;

    public SapKnowledgeService(IConfiguration configuration, ILogger<SapKnowledgeService> logger)
    {
        _logger = logger;
        
        try
        {
            var connectionString = configuration["AzureStorage:ConnectionString"] 
                ?? configuration["AZURE_STORAGE_CONNECTION_STRING"];
            
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogWarning("Azure Storage not configured - SAP Knowledge Service disabled");
                return;
            }
            
            var blobServiceClient = new BlobServiceClient(connectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient("agent-context");
            _isAvailable = true;
            
            _logger.LogInformation("SapKnowledgeService initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SapKnowledgeService");
        }
    }

    /// <summary>
    /// Initialize by loading SAP data from Excel files in blob storage
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized || !_isAvailable || _containerClient == null)
            return;

        try
        {
            // Find SAP Excel files in the container
            var sapFiles = new List<string>();
            await foreach (var blob in _containerClient.GetBlobsAsync())
            {
                var name = blob.Name.ToLowerInvariant();
                if (name.Contains("sap") && (name.EndsWith(".xlsx") || name.EndsWith(".xls")))
                {
                    sapFiles.Add(blob.Name);
                }
            }

            if (!sapFiles.Any())
            {
                _logger.LogWarning("No SAP Excel files found in agent-context container");
                return;
            }

            _logger.LogInformation("Found {Count} SAP Excel file(s): {Files}", 
                sapFiles.Count, string.Join(", ", sapFiles));

            // Load each SAP file
            foreach (var fileName in sapFiles)
            {
                await LoadSapExcelAsync(fileName);
            }

            _isInitialized = true;
            _logger.LogInformation("SAP Knowledge loaded: {Positions} positions, {Roles} roles, {Transactions} unique transactions, {Mappings} mappings",
                Positions.Count, Roles.Count, Transactions.GroupBy(t => t.Code).Count(), Mappings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SAP knowledge");
        }
    }

    /// <summary>
    /// Load a single SAP Excel file using ClosedXML
    /// </summary>
    private async Task LoadSapExcelAsync(string fileName)
    {
        try
        {
            var blobClient = _containerClient!.GetBlobClient(fileName);
            
            using var stream = new MemoryStream();
            await blobClient.DownloadToAsync(stream);
            stream.Position = 0;

            using var workbook = new XLWorkbook(stream);
            
            _logger.LogInformation("Loading SAP Excel: {FileName} with {SheetCount} sheets", 
                fileName, workbook.Worksheets.Count);

            foreach (var worksheet in workbook.Worksheets)
            {
                var sheetName = worksheet.Name.ToLowerInvariant();
                _logger.LogDebug("Processing sheet: {SheetName}", worksheet.Name);

                // Detect sheet type by name or content
                if (sheetName.Contains("position") && sheetName.Contains("name"))
                {
                    LoadPositionsSheet(worksheet);
                }
                else if (sheetName.Contains("roles") || sheetName.Contains("rol"))
                {
                    // Check if it's the technical roles sheet or business roles
                    var headers = GetHeaders(worksheet);
                    if (headers.Contains("rol full name") || headers.Contains("rol text"))
                    {
                        LoadTechnicalRolesSheet(worksheet);
                    }
                    else
                    {
                        LoadBusinessRolesSheet(worksheet);
                    }
                }
                else if (sheetName.Contains("dictionary") || sheetName.Contains("pl"))
                {
                    LoadDictionarySheet(worksheet);
                }
                else
                {
                    // Try to detect by headers
                    var headers = GetHeaders(worksheet);
                    if (headers.Contains("position id") && headers.Contains("transaction"))
                    {
                        LoadDictionarySheet(worksheet);
                    }
                    else if (headers.Contains("brole") && headers.Contains("transaction"))
                    {
                        LoadBusinessRolesSheet(worksheet);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load SAP Excel file: {FileName}", fileName);
        }
    }

    /// <summary>
    /// Get headers from first row (lowercase) using ClosedXML
    /// </summary>
    private List<string> GetHeaders(IXLWorksheet worksheet)
    {
        var headers = new List<string>();
        var firstRow = worksheet.FirstRowUsed();
        if (firstRow == null) return headers;

        foreach (var cell in firstRow.CellsUsed())
        {
            var value = cell.GetString()?.ToLowerInvariant() ?? "";
            headers.Add(value);
        }
        
        return headers;
    }

    /// <summary>
    /// Load Positions name sheet (Position ID → Position name)
    /// </summary>
    private void LoadPositionsSheet(IXLWorksheet worksheet)
    {
        var headers = GetHeaders(worksheet);
        
        var posIdCol = headers.IndexOf("position id") + 1;
        var posNameCol = headers.IndexOf("position name") + 1;
        
        if (posIdCol == 0) posIdCol = 1;
        if (posNameCol == 0) posNameCol = 2;

        var lastRowUsed = worksheet.LastRowUsed();
        if (lastRowUsed == null) return;
        var rowCount = lastRowUsed.RowNumber();

        for (int row = 2; row <= rowCount; row++)
        {
            var posId = worksheet.Cell(row, posIdCol).GetString()?.Trim();
            var posName = worksheet.Cell(row, posNameCol).GetString()?.Trim();
            
            if (!string.IsNullOrEmpty(posId))
            {
                Positions.Add(new SapPosition
                {
                    PositionId = posId,
                    Name = posName ?? ""
                });
            }
        }
        
        // Deduplicate
        Positions = Positions.GroupBy(p => p.PositionId).Select(g => g.First()).ToList();
        _logger.LogInformation("Loaded {Count} positions from sheet {Sheet}", 
            Positions.Count, worksheet.Name);
    }

    /// <summary>
    /// Load Technical Roles sheet (Rol ID → Rol full name → Rol Text)
    /// </summary>
    private void LoadTechnicalRolesSheet(IXLWorksheet worksheet)
    {
        var headers = GetHeaders(worksheet);
        
        var roleIdCol = headers.IndexOf("rol id") + 1;
        if (roleIdCol == 0) roleIdCol = headers.IndexOf("role id") + 1;
        if (roleIdCol == 0) roleIdCol = 1;
        
        var fullNameCol = headers.IndexOf("rol full name") + 1;
        if (fullNameCol == 0) fullNameCol = headers.IndexOf("role full name") + 1;
        if (fullNameCol == 0) fullNameCol = 2;
        
        var textCol = headers.IndexOf("rol text") + 1;
        if (textCol == 0) textCol = headers.IndexOf("role text") + 1;
        if (textCol == 0) textCol = 3;

        var lastRowUsed = worksheet.LastRowUsed();
        if (lastRowUsed == null) return;
        var rowCount = lastRowUsed.RowNumber();

        for (int row = 2; row <= rowCount; row++)
        {
            var roleId = worksheet.Cell(row, roleIdCol).GetString()?.Trim();
            var fullName = worksheet.Cell(row, fullNameCol).GetString()?.Trim();
            var text = worksheet.Cell(row, textCol).GetString()?.Trim();
            
            if (!string.IsNullOrEmpty(roleId))
            {
                Roles.Add(new SapRole
                {
                    RoleId = roleId,
                    FullName = fullName ?? "",
                    Description = text ?? ""
                });
            }
        }
        
        // Deduplicate
        Roles = Roles.GroupBy(r => r.RoleId).Select(g => g.First()).ToList();
        _logger.LogInformation("Loaded {Count} technical roles from sheet {Sheet}", 
            Roles.Count, worksheet.Name);
    }

    /// <summary>
    /// Load Business Roles sheet (BRole → Name BR → Desc. BR → Transactions)
    /// </summary>
    private void LoadBusinessRolesSheet(IXLWorksheet worksheet)
    {
        var headers = GetHeaders(worksheet);
        
        var bRoleCol = headers.IndexOf("brole") + 1;
        if (bRoleCol == 0) bRoleCol = 1;
        
        var nameCol = headers.IndexOf("name br") + 1;
        if (nameCol == 0) nameCol = 2;
        
        var descCol = headers.IndexOf("desc. br") + 1;
        if (descCol == 0) descCol = headers.IndexOf("desc br") + 1;
        if (descCol == 0) descCol = 3;
        
        var roleIdCol = headers.IndexOf("rol id") + 1;
        if (roleIdCol == 0) roleIdCol = headers.IndexOf("role id") + 1;
        if (roleIdCol == 0) roleIdCol = 4;
        
        var transCol = headers.IndexOf("transaction") + 1;
        if (transCol == 0) transCol = 5;
        
        var transDescCol = headers.IndexOf("transaction description") + 1;
        if (transDescCol == 0) transDescCol = 6;

        var lastRowUsed = worksheet.LastRowUsed();
        if (lastRowUsed == null) return;
        var rowCount = lastRowUsed.RowNumber();

        for (int row = 2; row <= rowCount; row++)
        {
            var bRole = worksheet.Cell(row, bRoleCol).GetString()?.Trim();
            var name = worksheet.Cell(row, nameCol).GetString()?.Trim();
            var desc = worksheet.Cell(row, descCol).GetString()?.Trim();
            var roleId = worksheet.Cell(row, roleIdCol).GetString()?.Trim();
            var trans = worksheet.Cell(row, transCol).GetString()?.Trim();
            var transDesc = worksheet.Cell(row, transDescCol).GetString()?.Trim();
            
            if (!string.IsNullOrEmpty(bRole))
            {
                BusinessRoles.Add(new SapBusinessRole
                {
                    BRoleCode = bRole,
                    Name = name ?? "",
                    Description = desc ?? ""
                });
                
                if (!string.IsNullOrEmpty(trans))
                {
                    Transactions.Add(new SapTransaction
                    {
                        Code = trans,
                        Description = transDesc ?? "",
                        RoleId = roleId ?? "",
                        BRole = bRole
                    });
                }
            }
        }
        
        // Deduplicate business roles
        BusinessRoles = BusinessRoles.GroupBy(b => b.BRoleCode).Select(g => g.First()).ToList();
        _logger.LogInformation("Loaded {BRoleCount} business roles, {TransCount} transactions from sheet {Sheet}", 
            BusinessRoles.Count, Transactions.Count, worksheet.Name);
    }

    /// <summary>
    /// Load Dictionary sheet (Position ID → BRole → Role ID → Transaction)
    /// </summary>
    private void LoadDictionarySheet(IXLWorksheet worksheet)
    {
        var headers = GetHeaders(worksheet);
        
        var posIdCol = headers.IndexOf("position id") + 1;
        if (posIdCol == 0) posIdCol = 1;
        
        var bRoleCol = headers.IndexOf("brole") + 1;
        if (bRoleCol == 0) bRoleCol = 2;
        
        var bRoleNameCol = headers.IndexOf("brole name") + 1;
        if (bRoleNameCol == 0) bRoleNameCol = 3;
        
        var roleIdCol = headers.IndexOf("role id") + 1;
        if (roleIdCol == 0) roleIdCol = headers.IndexOf("rol id") + 1;
        if (roleIdCol == 0) roleIdCol = 4;
        
        var transCol = headers.IndexOf("transaction") + 1;
        if (transCol == 0) transCol = 5;
        
        var transDescCol = headers.IndexOf("transaction description") + 1;
        if (transDescCol == 0) transDescCol = 6;

        var lastRowUsed = worksheet.LastRowUsed();
        if (lastRowUsed == null) return;
        var rowCount = lastRowUsed.RowNumber();

        for (int row = 2; row <= rowCount; row++)
        {
            var posId = worksheet.Cell(row, posIdCol).GetString()?.Trim();
            var bRole = worksheet.Cell(row, bRoleCol).GetString()?.Trim();
            var bRoleName = worksheet.Cell(row, bRoleNameCol).GetString()?.Trim();
            var roleId = worksheet.Cell(row, roleIdCol).GetString()?.Trim();
            var trans = worksheet.Cell(row, transCol).GetString()?.Trim();
            var transDesc = worksheet.Cell(row, transDescCol).GetString()?.Trim();
            
            if (!string.IsNullOrEmpty(posId) && !string.IsNullOrEmpty(trans))
            {
                Mappings.Add(new SapPositionRoleMapping
                {
                    PositionId = posId,
                    BRole = bRole ?? "",
                    BRoleName = bRoleName ?? "",
                    RoleId = roleId ?? "",
                    Transaction = trans,
                    TransactionDescription = transDesc ?? ""
                });
                
                // Also add to transactions list
                Transactions.Add(new SapTransaction
                {
                    Code = trans,
                    Description = transDesc ?? "",
                    RoleId = roleId ?? "",
                    BRole = bRole ?? "",
                    PositionId = posId
                });
            }
        }
        
        _logger.LogInformation("Loaded {Count} position-role-transaction mappings from sheet {Sheet}", 
            Mappings.Count, worksheet.Name);
    }

    /// <summary>
    /// Get statistics about loaded SAP data
    /// </summary>
    public SapDataStatistics GetStatistics()
    {
        return new SapDataStatistics
        {
            TotalPositions = Positions.Count,
            TotalRoles = Roles.Count,
            TotalBusinessRoles = BusinessRoles.Count,
            TotalTransactions = Transactions.GroupBy(t => t.Code).Count(),
            TotalMappings = Mappings.Count,
            IsInitialized = _isInitialized
        };
    }
}

/// <summary>
/// Statistics about loaded SAP data
/// </summary>
public class SapDataStatistics
{
    public int TotalPositions { get; set; }
    public int TotalRoles { get; set; }
    public int TotalBusinessRoles { get; set; }
    public int TotalTransactions { get; set; }
    public int TotalMappings { get; set; }
    public bool IsInitialized { get; set; }
}
