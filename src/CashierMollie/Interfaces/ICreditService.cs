namespace CashierMollie.Interfaces;

/// <summary>Manages owner credit balances.</summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface ICreditService<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Gets the credit balance for the owner.</summary>
    /// <param name="owner">The billable owner.</param>
    /// <param name="currency">ISO 4217 currency code. Defaults to <see cref="CashierMollieOptions.Currency"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current balance, or 0 if no credit record exists.</returns>
    Task<decimal> GetBalanceAsync(IBillable<TKey> owner, string? currency = null, CancellationToken ct = default);

    /// <summary>Adds credit to the owner's balance.</summary>
    /// <param name="owner">The billable owner.</param>
    /// <param name="amount">The amount to add (must be positive).</param>
    /// <param name="currency">ISO 4217 currency code. Defaults to <see cref="CashierMollieOptions.Currency"/>.</param>
    /// <param name="description">Optional description for audit purposes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddCreditAsync(IBillable<TKey> owner, decimal amount, string? currency = null,
        string? description = null, CancellationToken ct = default);

    /// <summary>Applies credit against an amount. Returns the amount actually deducted.</summary>
    /// <param name="owner">The billable owner.</param>
    /// <param name="maxAmount">The maximum amount to deduct from the balance.</param>
    /// <param name="currency">ISO 4217 currency code. Defaults to <see cref="CashierMollieOptions.Currency"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The amount actually deducted (capped at the current balance).</returns>
    Task<decimal> ApplyCreditAsync(IBillable<TKey> owner, decimal maxAmount,
        string? currency = null, CancellationToken ct = default);

    /// <summary>Checks if the owner has a positive credit balance.</summary>
    /// <param name="owner">The billable owner.</param>
    /// <param name="currency">ISO 4217 currency code. Defaults to <see cref="CashierMollieOptions.Currency"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the owner has credit greater than zero.</returns>
    Task<bool> HasCreditAsync(IBillable<TKey> owner, string? currency = null, CancellationToken ct = default);
}
