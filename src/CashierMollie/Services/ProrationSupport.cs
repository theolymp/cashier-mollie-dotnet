namespace CashierMollie.Services;

/// <summary>
/// The single message every proration guard raises, and the single throw. Both live here rather
/// than at the call sites so neither the wording nor the condition can drift between them.
/// </summary>
internal static class ProrationSupport
{
    internal const string NotImplementedMessage =
        "Proration is not implemented. Up to and including 0.4.0 this flag was accepted and never " +
        "read by either billing engine, so a caller received no credit and no error. It now fails " +
        "instead of silently doing nothing, because a money flag that is quietly inert is " +
        "indistinguishable from a working one. Until proration is built, do not set it and compute " +
        "any credit yourself.";

    /// <summary>
    /// Fails if proration was requested. Called at every entry point that carries the flag,
    /// including both billing engines: <c>IBillingEngine</c> is public and registered in DI, so
    /// guarding only the builder and <c>ICashierService</c> left a caller who reaches an engine
    /// directly with the old silence. Create is guarded as well as swap, for the same reason
    /// <c>WithProration()</c> throws on the create path: a caller who sets the flag believes
    /// proration is enabled for this subscription, and it is not.
    /// </summary>
    internal static void ThrowIfRequested(bool prorate)
    {
        if (prorate)
        {
            throw new NotSupportedException(NotImplementedMessage);
        }
    }
}
