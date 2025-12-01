using System.Text.Json.Serialization;

namespace RecipeSearchWeb.Models;

/// <summary>
/// User roles in the system
/// </summary>
public enum UserRole
{
    Tecnico,
    Admin
}

/// <summary>
/// Represents a user in the system
/// </summary>
public class User
{
    public int Id { get; set; }
    
    [JsonInclude]
    public string Username { get; set; } = string.Empty;
    
    public string? PasswordHash { get; set; }  // Optional - not used with Azure Easy Auth
    
    [JsonInclude]
    public string FullName { get; set; } = string.Empty;
    
    [JsonInclude]
    public UserRole Role { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLogin { get; set; }

    /// <summary>
    /// Check if user is Admin
    /// </summary>
    [JsonIgnore]
    public bool IsAdmin => Role == UserRole.Admin;

    /// <summary>
    /// Check if user is Tecnico
    /// </summary>
    [JsonIgnore]
    public bool IsTecnico => Role == UserRole.Tecnico;
}
