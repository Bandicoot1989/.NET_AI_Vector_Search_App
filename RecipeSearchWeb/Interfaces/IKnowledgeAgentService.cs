using OpenAI.Chat;
using RecipeSearchWeb.Services;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Specialist type for routing
/// </summary>
public enum SpecialistType
{
    General,
    SAP,
    Network
}

/// <summary>
/// Interface for the AI Knowledge Agent (RAG-based Q&A)
/// </summary>
public interface IKnowledgeAgentService
{
    Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null);
    Task<AgentResponse> AskWithSpecialistAsync(string question, SpecialistType specialist, string? specialistContext = null, List<ChatMessage>? conversationHistory = null);
    IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null);
}
