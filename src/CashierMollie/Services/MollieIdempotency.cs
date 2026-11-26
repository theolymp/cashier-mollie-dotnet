namespace CashierMollie.Services;

/// <summary>
/// The Idempotency-Key for the Mollie call made inside a <see cref="Use"/> scope. Ambient rather
/// than a parameter so <see cref="Interfaces.IMollieClientService"/> keeps its shape: consumers
/// implement and mock it. Mollie answers a repeated key within one hour with the first response,
/// and keys belong to the API credential.
/// </summary>
/// <remarks>
/// Every Mollie call made inside the scope on this async flow sees the key, including calls a
/// decorator of <c>IMollieClientService</c> makes or tasks it starts there. Keep the scope around
/// exactly one charge.
/// </remarks>
internal static class MollieIdempotency
{
    private static readonly AsyncLocal<string?> Key = new();

    /// <summary>The key of the innermost open scope, or null.</summary>
    public static string? Current => Key.Value;

    /// <summary>Opens a scope; disposing it restores the previous key.</summary>
    public static IDisposable Use(string key)
    {
        var previous = Key.Value;
        Key.Value = key;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Key.Value = previous;
    }
}
