namespace RecipeSearchWeb.Models;

/// <summary>
/// SAP Transaction (T-code) information
/// </summary>
public class SapTransaction
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string BRole { get; set; } = string.Empty;
    public string PositionId { get; set; } = string.Empty;
    
    public override string ToString() => $"{Code}: {Description}";
}

/// <summary>
/// SAP Technical Role (SY01, MM01, QM01, etc.)
/// </summary>
public class SapRole
{
    public string RoleId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    public override string ToString() => $"{RoleId}: {Description}";
}

/// <summary>
/// SAP Business Role (AF01, PT40, etc.)
/// </summary>
public class SapBusinessRole
{
    public string BRoleCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    public override string ToString() => $"{BRoleCode}: {Description}";
}

/// <summary>
/// SAP Position (INCA01, INGM01, etc.)
/// </summary>
public class SapPosition
{
    public string PositionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    
    public override string ToString() => $"{PositionId}: {Name}";
}

/// <summary>
/// Complete mapping: Position → Business Role → Technical Role → Transaction
/// </summary>
public class SapPositionRoleMapping
{
    public string PositionId { get; set; } = string.Empty;
    public string PositionName { get; set; } = string.Empty;
    public string BRole { get; set; } = string.Empty;
    public string BRoleName { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string Transaction { get; set; } = string.Empty;
    public string TransactionDescription { get; set; } = string.Empty;
}

/// <summary>
/// SAP Query type for routing
/// </summary>
public enum SapQueryType
{
    /// <summary>Query about a specific transaction</summary>
    TransactionInfo,
    
    /// <summary>Query about transactions in a role</summary>
    RoleTransactions,
    
    /// <summary>Query about what access a position needs</summary>
    PositionAccess,
    
    /// <summary>Query about role details</summary>
    RoleInfo,
    
    /// <summary>Query about position details</summary>
    PositionInfo,
    
    /// <summary>Query comparing positions or roles</summary>
    Compare,
    
    /// <summary>Reverse lookup - what role has this transaction</summary>
    ReverseLookup,
    
    /// <summary>General SAP question</summary>
    General
}

/// <summary>
/// Result from SAP lookup operations
/// </summary>
public class SapLookupResult
{
    public bool Found { get; set; }
    public SapQueryType QueryType { get; set; }
    public string? PositionId { get; set; }
    public string? PositionName { get; set; }
    public string? RoleId { get; set; }
    public string? RoleName { get; set; }
    public string? TransactionCode { get; set; }
    public string? TransactionDescription { get; set; }
    public List<SapTransaction> Transactions { get; set; } = new();
    public List<SapRole> Roles { get; set; } = new();
    public List<SapPosition> Positions { get; set; } = new();
    public List<SapPositionRoleMapping> Mappings { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}
