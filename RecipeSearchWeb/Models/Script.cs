namespace RecipeSearchWeb.Models;

/// <summary>
/// Represents a PowerShell script with all its details
/// </summary>
public class Script
{
    public int Key { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Purpose { get; set; }
    public required string Complexity { get; set; }  // Beginner, Intermediate, Advanced
    public required string Category { get; set; }  // System Admin, File Management, Network, Security, etc.
    public required string Code { get; set; }  // The actual PowerShell code
    public string Parameters { get; set; } = string.Empty;  // Parameters explanation
    public ReadOnlyMemory<float> Vector { get; set; }
    
    // Usage statistics
    public int ViewCount { get; set; } = 0;
    public DateTime? LastViewed { get; set; }
    
    // Helper property for displaying emoji based on category
    public string CategoryEmoji => Category switch
    {
        "System Admin" => "⚙️",
        "File Management" => "📁",
        "Network" => "🌐",
        "Security" => "🔒",
        "Automation" => "🤖",
        "Azure" => "☁️",
        "Git" => "📦",
        "Development" => "💻",
        _ => "📜"
    };
}
