using RecipeSearchWeb.Models;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for fast O(1) lookups of SAP data using indexed dictionaries
/// Tier 3: SAP Specialist Agent - Lookup Layer
/// </summary>
public class SapLookupService
{
    private readonly SapKnowledgeService _knowledgeService;
    private readonly ILogger<SapLookupService> _logger;
    
    // Indexed dictionaries for O(1) lookups
    private Dictionary<string, SapTransaction> _transactionsByCode = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<SapTransaction>> _transactionsByRole = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<SapTransaction>> _transactionsByPosition = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SapRole> _rolesByCode = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SapPosition> _positionsByCode = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _rolesByPosition = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SapBusinessRole> _businessRolesByCode = new(StringComparer.OrdinalIgnoreCase);
    
    private bool _isIndexed = false;

    public bool IsAvailable => _knowledgeService.IsAvailable && _isIndexed;

    public SapLookupService(SapKnowledgeService knowledgeService, ILogger<SapLookupService> logger)
    {
        _knowledgeService = knowledgeService;
        _logger = logger;
    }

    /// <summary>
    /// Build indexes from knowledge service data
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isIndexed) return;
        
        await _knowledgeService.InitializeAsync();
        
        if (!_knowledgeService.IsAvailable)
        {
            _logger.LogWarning("SAP Knowledge Service not available - Lookup Service disabled");
            return;
        }

        BuildIndexes();
        _isIndexed = true;
        
        _logger.LogInformation("SAP Lookup indexes built: {Trans} transactions, {Roles} roles, {Positions} positions",
            _transactionsByCode.Count, _rolesByCode.Count, _positionsByCode.Count);
    }

    /// <summary>
    /// Build all lookup indexes
    /// </summary>
    private void BuildIndexes()
    {
        // Index positions
        foreach (var pos in _knowledgeService.Positions)
        {
            _positionsByCode[pos.PositionId] = pos;
        }

        // Index technical roles
        foreach (var role in _knowledgeService.Roles)
        {
            _rolesByCode[role.RoleId] = role;
        }

        // Index business roles
        foreach (var bRole in _knowledgeService.BusinessRoles)
        {
            _businessRolesByCode[bRole.BRoleCode] = bRole;
        }

        // Index transactions and build relationship indexes
        foreach (var trans in _knowledgeService.Transactions)
        {
            // Transaction by code (deduplicate, keep first with description)
            if (!_transactionsByCode.ContainsKey(trans.Code) || 
                string.IsNullOrEmpty(_transactionsByCode[trans.Code].Description))
            {
                _transactionsByCode[trans.Code] = trans;
            }

            // Transactions by role
            if (!string.IsNullOrEmpty(trans.RoleId))
            {
                if (!_transactionsByRole.ContainsKey(trans.RoleId))
                    _transactionsByRole[trans.RoleId] = new List<SapTransaction>();
                
                if (!_transactionsByRole[trans.RoleId].Any(t => t.Code == trans.Code))
                    _transactionsByRole[trans.RoleId].Add(trans);
            }

            // Transactions by position
            if (!string.IsNullOrEmpty(trans.PositionId))
            {
                if (!_transactionsByPosition.ContainsKey(trans.PositionId))
                    _transactionsByPosition[trans.PositionId] = new List<SapTransaction>();
                
                if (!_transactionsByPosition[trans.PositionId].Any(t => t.Code == trans.Code))
                    _transactionsByPosition[trans.PositionId].Add(trans);
            }
        }

        // Build roles by position index from mappings
        foreach (var mapping in _knowledgeService.Mappings)
        {
            if (!string.IsNullOrEmpty(mapping.PositionId) && !string.IsNullOrEmpty(mapping.RoleId))
            {
                if (!_rolesByPosition.ContainsKey(mapping.PositionId))
                    _rolesByPosition[mapping.PositionId] = new List<string>();
                
                if (!_rolesByPosition[mapping.PositionId].Contains(mapping.RoleId))
                    _rolesByPosition[mapping.PositionId].Add(mapping.RoleId);
            }
        }

        // Enrich positions with names from mappings
        foreach (var mapping in _knowledgeService.Mappings.Where(m => !string.IsNullOrEmpty(m.BRoleName)))
        {
            if (_positionsByCode.TryGetValue(mapping.PositionId, out var pos) && 
                string.IsNullOrEmpty(pos.Name))
            {
                pos.Name = mapping.BRoleName;
            }
        }
    }

    #region Direct Lookups (O(1))

    /// <summary>
    /// Get a transaction by its code
    /// </summary>
    public SapTransaction? GetTransaction(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        _transactionsByCode.TryGetValue(code.Trim(), out var trans);
        return trans;
    }

    /// <summary>
    /// Get a technical role by its ID
    /// </summary>
    public SapRole? GetRole(string roleId)
    {
        if (string.IsNullOrEmpty(roleId)) return null;
        _rolesByCode.TryGetValue(roleId.Trim(), out var role);
        return role;
    }

    /// <summary>
    /// Get a business role by its code
    /// </summary>
    public SapBusinessRole? GetBusinessRole(string bRoleCode)
    {
        if (string.IsNullOrEmpty(bRoleCode)) return null;
        _businessRolesByCode.TryGetValue(bRoleCode.Trim(), out var bRole);
        return bRole;
    }

    /// <summary>
    /// Get a position by its ID
    /// </summary>
    public SapPosition? GetPosition(string positionId)
    {
        if (string.IsNullOrEmpty(positionId)) return null;
        _positionsByCode.TryGetValue(positionId.Trim(), out var pos);
        return pos;
    }

    #endregion

    #region Relational Lookups

    /// <summary>
    /// Get all transactions for a given role
    /// </summary>
    public List<SapTransaction> GetTransactionsByRole(string roleId)
    {
        if (string.IsNullOrEmpty(roleId)) return new List<SapTransaction>();
        _transactionsByRole.TryGetValue(roleId.Trim(), out var transactions);
        return transactions ?? new List<SapTransaction>();
    }

    /// <summary>
    /// Get all transactions for a given position
    /// </summary>
    public List<SapTransaction> GetTransactionsByPosition(string positionId)
    {
        if (string.IsNullOrEmpty(positionId)) return new List<SapTransaction>();
        _transactionsByPosition.TryGetValue(positionId.Trim(), out var transactions);
        return transactions ?? new List<SapTransaction>();
    }

    /// <summary>
    /// Get all role IDs for a given position
    /// </summary>
    public List<string> GetRolesForPosition(string positionId)
    {
        if (string.IsNullOrEmpty(positionId)) return new List<string>();
        _rolesByPosition.TryGetValue(positionId.Trim(), out var roles);
        return roles ?? new List<string>();
    }

    /// <summary>
    /// Reverse lookup: find which roles contain a specific transaction
    /// </summary>
    public List<SapRole> GetRolesWithTransaction(string transactionCode)
    {
        if (string.IsNullOrEmpty(transactionCode)) return new List<SapRole>();
        
        var roles = new List<SapRole>();
        foreach (var kvp in _transactionsByRole)
        {
            if (kvp.Value.Any(t => t.Code.Equals(transactionCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (_rolesByCode.TryGetValue(kvp.Key, out var role))
                    roles.Add(role);
            }
        }
        return roles;
    }

    /// <summary>
    /// Reverse lookup: find which positions have access to a specific transaction
    /// </summary>
    public List<SapPosition> GetPositionsWithTransaction(string transactionCode)
    {
        if (string.IsNullOrEmpty(transactionCode)) return new List<SapPosition>();
        
        var positions = new List<SapPosition>();
        foreach (var kvp in _transactionsByPosition)
        {
            if (kvp.Value.Any(t => t.Code.Equals(transactionCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (_positionsByCode.TryGetValue(kvp.Key, out var pos))
                    positions.Add(pos);
            }
        }
        return positions;
    }

    #endregion

    #region Search/Fuzzy Lookups

    /// <summary>
    /// Search transactions by code or description (fuzzy)
    /// </summary>
    public List<SapTransaction> SearchTransactions(string query, int maxResults = 20)
    {
        if (string.IsNullOrEmpty(query)) return new List<SapTransaction>();
        
        var lower = query.ToLowerInvariant();
        
        return _transactionsByCode.Values
            .Where(t => 
                t.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Code.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.Code.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Search roles by ID, name, or description (fuzzy)
    /// </summary>
    public List<SapRole> SearchRoles(string query, int maxResults = 20)
    {
        if (string.IsNullOrEmpty(query)) return new List<SapRole>();
        
        return _rolesByCode.Values
            .Where(r => 
                r.RoleId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.RoleId.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.RoleId.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Search positions by ID or name (fuzzy)
    /// </summary>
    public List<SapPosition> SearchPositions(string query, int maxResults = 20)
    {
        if (string.IsNullOrEmpty(query)) return new List<SapPosition>();
        
        return _positionsByCode.Values
            .Where(p => 
                p.PositionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.PositionId.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.PositionId.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    #endregion

    #region Comprehensive Lookup

    /// <summary>
    /// Perform a comprehensive lookup based on detected query type
    /// </summary>
    public SapLookupResult PerformLookup(string query, SapQueryType queryType)
    {
        var result = new SapLookupResult { QueryType = queryType };
        
        // Extract potential codes from query
        var codes = ExtractCodes(query);
        
        switch (queryType)
        {
            case SapQueryType.TransactionInfo:
                foreach (var code in codes)
                {
                    var trans = GetTransaction(code);
                    if (trans != null)
                    {
                        result.Found = true;
                        result.TransactionCode = trans.Code;
                        result.TransactionDescription = trans.Description;
                        result.Transactions.Add(trans);
                        
                        // Also get roles that have this transaction
                        result.Roles.AddRange(GetRolesWithTransaction(code));
                    }
                }
                // Fallback to search
                if (!result.Found)
                {
                    result.Transactions = SearchTransactions(query, 10);
                    result.Found = result.Transactions.Any();
                }
                break;

            case SapQueryType.RoleTransactions:
                foreach (var code in codes)
                {
                    var role = GetRole(code);
                    if (role != null)
                    {
                        result.Found = true;
                        result.RoleId = role.RoleId;
                        result.RoleName = role.Description;
                        result.Roles.Add(role);
                        result.Transactions = GetTransactionsByRole(code);
                    }
                }
                if (!result.Found)
                {
                    result.Roles = SearchRoles(query, 10);
                    result.Found = result.Roles.Any();
                    if (result.Found && result.Roles.Count == 1)
                    {
                        result.Transactions = GetTransactionsByRole(result.Roles[0].RoleId);
                    }
                }
                break;

            case SapQueryType.PositionAccess:
                foreach (var code in codes)
                {
                    var pos = GetPosition(code);
                    if (pos != null)
                    {
                        result.Found = true;
                        result.PositionId = pos.PositionId;
                        result.PositionName = pos.Name;
                        result.Positions.Add(pos);
                        result.Transactions = GetTransactionsByPosition(code);
                        
                        // Also get roles for this position
                        var roleIds = GetRolesForPosition(code);
                        foreach (var roleId in roleIds)
                        {
                            var role = GetRole(roleId);
                            if (role != null) result.Roles.Add(role);
                        }
                    }
                }
                if (!result.Found)
                {
                    result.Positions = SearchPositions(query, 10);
                    result.Found = result.Positions.Any();
                }
                break;

            case SapQueryType.RoleInfo:
                foreach (var code in codes)
                {
                    var role = GetRole(code);
                    if (role != null)
                    {
                        result.Found = true;
                        result.RoleId = role.RoleId;
                        result.RoleName = role.Description;
                        result.Roles.Add(role);
                    }
                }
                if (!result.Found)
                {
                    result.Roles = SearchRoles(query, 10);
                    result.Found = result.Roles.Any();
                }
                break;

            case SapQueryType.PositionInfo:
                foreach (var code in codes)
                {
                    var pos = GetPosition(code);
                    if (pos != null)
                    {
                        result.Found = true;
                        result.PositionId = pos.PositionId;
                        result.PositionName = pos.Name;
                        result.Positions.Add(pos);
                    }
                }
                if (!result.Found)
                {
                    result.Positions = SearchPositions(query, 10);
                    result.Found = result.Positions.Any();
                }
                break;

            case SapQueryType.ReverseLookup:
                foreach (var code in codes)
                {
                    var trans = GetTransaction(code);
                    if (trans != null)
                    {
                        result.Found = true;
                        result.TransactionCode = trans.Code;
                        result.Roles = GetRolesWithTransaction(code);
                        result.Positions = GetPositionsWithTransaction(code);
                    }
                }
                break;

            case SapQueryType.Compare:
                // Get info for all detected codes
                foreach (var code in codes)
                {
                    var pos = GetPosition(code);
                    if (pos != null)
                    {
                        result.Positions.Add(pos);
                        result.Found = true;
                    }
                    var role = GetRole(code);
                    if (role != null)
                    {
                        result.Roles.Add(role);
                        result.Found = true;
                    }
                }
                break;

            default:
                // General: try all types
                result.Transactions = SearchTransactions(query, 5);
                result.Roles = SearchRoles(query, 5);
                result.Positions = SearchPositions(query, 5);
                result.Found = result.Transactions.Any() || result.Roles.Any() || result.Positions.Any();
                break;
        }

        // Build summary
        result.Summary = BuildSummary(result);
        
        return result;
    }

    /// <summary>
    /// Extract potential SAP codes from query
    /// </summary>
    private List<string> ExtractCodes(string query)
    {
        var codes = new List<string>();
        var words = query.Split(new[] { ' ', ',', '?', '¿', '!', '¡', '.', ':', ';', '"', '\'' }, 
            StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            var clean = word.Trim().ToUpperInvariant();
            
            // Check if it's a known code
            if (_transactionsByCode.ContainsKey(clean) ||
                _rolesByCode.ContainsKey(clean) ||
                _positionsByCode.ContainsKey(clean))
            {
                codes.Add(clean);
            }
            // Pattern matching for potential codes
            else if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Z]{2,4}\d{0,2}[A-Z]?$") ||
                     System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Z]{2}\d{2}$"))
            {
                codes.Add(clean);
            }
        }
        
        return codes.Distinct().ToList();
    }

    /// <summary>
    /// Build a summary string from lookup result
    /// </summary>
    private string BuildSummary(SapLookupResult result)
    {
        var parts = new List<string>();
        
        if (result.Transactions.Any())
            parts.Add($"{result.Transactions.Count} transacción(es)");
        if (result.Roles.Any())
            parts.Add($"{result.Roles.Count} rol(es)");
        if (result.Positions.Any())
            parts.Add($"{result.Positions.Count} posición(es)");
        
        return parts.Any() ? $"Encontrado: {string.Join(", ", parts)}" : "No se encontraron resultados";
    }

    #endregion
}
