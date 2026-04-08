using System.Collections.Concurrent;

namespace DynamoCopilot.Server.Services;

/// <summary>
/// In-memory nonce store for OAuth CSRF state parameters.
/// Maps a random GUID (sent to the OAuth provider) back to the client's local callback port.
/// Singleton lifetime — state must survive across requests.
/// </summary>
public sealed class OAuthStateService
{
    private readonly ConcurrentDictionary<string, (int Port, DateTime Expiry)> _pending = new();

    /// <summary>Creates a nonce for the given port and returns the state string.</summary>
    public string CreateState(int port)
    {
        // Opportunistically prune to avoid unbounded growth from abandoned flows.
        foreach (var key in _pending.Keys.ToArray())
            if (_pending.TryGetValue(key, out var v) && v.Expiry < DateTime.UtcNow)
                _pending.TryRemove(key, out _);

        var state = Guid.NewGuid().ToString("N");
        _pending[state] = (port, DateTime.UtcNow.AddMinutes(10));
        return state;
    }

    /// <summary>
    /// Consumes the nonce (one-time use) and returns the associated port.
    /// Returns null if the state is unknown or expired.
    /// </summary>
    public int? ConsumeState(string state)
    {
        if (_pending.TryRemove(state, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Port;
        return null;
    }
}
