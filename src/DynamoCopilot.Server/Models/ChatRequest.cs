namespace DynamoCopilot.Server.Models;

// Records are immutable data types introduced in C# 9.
// Compared to a class, a record gives you for free:
//   - A constructor generated from its properties
//   - Value-based equality  (two ChatRequests with the same data are "equal")
//   - A readable ToString()
// They're the ideal type for request/response data (DTOs) that you just read, never mutate.

/// <summary>
/// The JSON body the client sends to POST /api/chat/stream.
///
/// Example request:
/// {
///   "messages": [
///     { "role": "user",      "content": "Write a Python function to sum a list" },
///     { "role": "assistant", "content": "Here is the code..." },
///     { "role": "user",      "content": "Can you add error handling?" }
///   ]
/// }
///
/// The client sends the FULL conversation history on every request.
/// The server (GeminiService) passes all messages to Gemini so it has context.
/// </summary>
public record ChatRequest(List<ChatMessage> Messages);

/// <summary>
/// A single turn in the conversation.
/// Role values:
///   "user"      — the user's message
///   "assistant" — a previous AI response (for context in multi-turn conversations)
/// Note: "system" role is handled separately via the system prompt in GeminiService.
/// </summary>
public record ChatMessage(string Role, string Content);
