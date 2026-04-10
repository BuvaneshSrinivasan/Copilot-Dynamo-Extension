using DynamoCopilot.Server.Models;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// ILlmService — Contract that all AI provider implementations must follow
// =============================================================================
//
// Why define an interface instead of using GeminiService directly?
//
//   SWAPPABILITY
//   ChatEndpoints.cs depends on ILlmService, not GeminiService.
//   To add a new provider (OpenAI, Claude, etc.):
//     1. Create a new class implementing ILlmService
//     2. Change ONE line in Program.cs
//   ChatEndpoints.cs doesn't change at all.
//
//   TESTABILITY
//   In unit tests you can create a FakeLlmService implementing ILlmService
//   that returns predictable responses — no real API calls needed.
//
//   REGISTRATION IN DI
//   In Program.cs we write:
//     builder.Services.AddScoped<ILlmService, GeminiService>();
//   When ChatEndpoints asks for an ILlmService, it gets a GeminiService.
//   Changing the provider = changing that one line.
// =============================================================================

public interface ILlmService
{
    /// <summary>
    /// Streams the AI response token by token.
    ///
    /// IAsyncEnumerable&lt;string&gt; is C#'s type for an async sequence.
    /// The caller does: await foreach (var token in StreamAsync(...)) { ... }
    /// and receives each string token as soon as it arrives from the AI API.
    ///
    /// This is "pull-based" streaming — the consumer requests the next value;
    /// the producer (GeminiService) yields it only when it has received it
    /// from the upstream HTTP response stream.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken);
}
