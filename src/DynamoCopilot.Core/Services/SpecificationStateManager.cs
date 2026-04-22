using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Thread-safe holder for the pending spec between the "spec card shown" phase
    /// and the user's confirm/cancel action.
    /// </summary>
    public sealed class SpecificationStateManager
    {
        private volatile CodeSpecification? _pending;

        public bool HasPendingSpec => _pending != null;

        public void SetPending(CodeSpecification spec) => _pending = spec;
        public CodeSpecification? GetPending()         => _pending;
        public void Clear()                            => _pending = null;
    }
}
