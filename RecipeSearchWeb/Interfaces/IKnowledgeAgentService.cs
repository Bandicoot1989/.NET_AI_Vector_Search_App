using OpenAI.Chat;
using RecipeSearchWeb.Services;

namespace RecipeSearchWeb.Interfaces;

/// <summary>
/// Interface for the AI Knowledge Agent (RAG-based Q&A)
/// </summary>
public interface IKnowledgeAgentService
{
    Task<AgentResponse> AskAsync(string question, List<ChatMessage>? conversationHistory = null);
    IAsyncEnumerable<string> AskStreamingAsync(string question, List<ChatMessage>? conversationHistory = null);
}
