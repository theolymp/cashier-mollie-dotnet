namespace CashierMollie.Services;

/// <summary>
/// The single message both proration guards raise. It lives here rather than at the two throw
/// sites so the wording cannot drift between them.
/// </summary>
internal static class ProrationSupport
{
    internal const string NotImplementedMessage =
        "Proration is not implemented. Up to and including 0.4.0 this flag was accepted and never " +
        "read by either billing engine, so a caller received no credit and no error. It now fails " +
        "instead of silently doing nothing, because a money flag that is quietly inert is " +
        "indistinguishable from a working one. Until proration is built, do not set it and compute " +
        "any credit yourself.";
}
