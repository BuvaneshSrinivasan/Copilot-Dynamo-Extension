using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Converts text into a fixed-dimension float vector.
    /// The implementation lives in the Extension project (ONNX) so Core has
    /// no native-binary dependency.
    /// </summary>
    public interface IEmbeddingService
    {
        /// <summary>Dimension of every vector this service produces.</summary>
        int Dimension { get; }

        Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    }
}
