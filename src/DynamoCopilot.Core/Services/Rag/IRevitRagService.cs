using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.Core.Services.Rag
{
    public interface IRevitRagService
    {
        // Returns a formatted context string for injection into the system prompt,
        // or null/empty if the index is unavailable. Always fails open — never throws.
        Task<string?> FetchContextAsync(string userQuery, CancellationToken ct = default);
    }
}
