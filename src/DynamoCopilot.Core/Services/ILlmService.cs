using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Abstraction over any LLM backend (OpenAI, Azure OpenAI, etc.).
    /// Implementations must be thread-safe.
    /// </summary>
    public interface ILlmService
    {
        /// <summary>
        /// Sends a full conversation to the LLM and streams back response tokens.
        /// The caller is responsible for providing the system message as the first message
        /// with Role = System.
        /// </summary>
        IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if the service is configured (API key present, etc.).
        /// Returns false and a human-readable reason if not usable.
        /// </summary>
        bool IsConfigured(out string reason);
    }
}
