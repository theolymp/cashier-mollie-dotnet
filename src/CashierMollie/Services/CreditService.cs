using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>Manages owner credit balances per currency.</summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class CreditService<TKey> : ICreditService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CashierMollieOptions _options;

    /// <summary>Creates a new CreditService.</summary>
    /// <param name="db">The Cashier database context.</param>
    /// <param name="dispatcher">The event dispatcher.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    public CreditService(CashierDbContext<TKey> db, ICashierEventDispatcher dispatcher,
        IOptions<CashierMollieOptions> options)
    {
        _db = db;
        _dispatcher = dispatcher;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<decimal> GetBalanceAsync(IBillable<TKey> owner, string? currency = null,
        CancellationToken ct = default)
    {
        currency ??= _options.Currency;
        var credit = await _db.Credits
            .FirstOrDefaultAsync(c => c.OwnerId.Equals(owner.Id) && c.Currency == currency, ct);
        return credit?.Balance ?? 0m;
    }

    /// <inheritdoc />
    public async Task AddCreditAsync(IBillable<TKey> owner, decimal amount, string? currency = null,
        string? description = null, CancellationToken ct = default)
    {
        currency ??= _options.Currency;
        var credit = await _db.Credits
            .FirstOrDefaultAsync(c => c.OwnerId.Equals(owner.Id) && c.Currency == currency, ct);

        if (credit == null)
        {
            credit = new Credit<TKey> { OwnerId = owner.Id, Currency = currency, Balance = amount };
            _db.Credits.Add(credit);
        }
        else
        {
            credit.Balance += amount;
            credit.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await _dispatcher.DispatchAsync(new CreditAdded<TKey>(amount, currency, description, owner.Id), ct);
    }

    /// <inheritdoc />
    public async Task<decimal> ApplyCreditAsync(IBillable<TKey> owner, decimal maxAmount,
        string? currency = null, CancellationToken ct = default)
    {
        currency ??= _options.Currency;
        var credit = await _db.Credits
            .FirstOrDefaultAsync(c => c.OwnerId.Equals(owner.Id) && c.Currency == currency, ct);

        if (credit == null || credit.Balance <= 0)
            return 0m;

        var used = Math.Min(credit.Balance, maxAmount);
        credit.Balance -= used;
        credit.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(new CreditApplied<TKey>(used, currency, owner.Id), ct);
        return used;
    }

    /// <inheritdoc />
    public async Task<bool> HasCreditAsync(IBillable<TKey> owner, string? currency = null,
        CancellationToken ct = default)
    {
        currency ??= _options.Currency;
        return await _db.Credits
            .AnyAsync(c => c.OwnerId.Equals(owner.Id) && c.Currency == currency && c.Balance > 0, ct);
    }
}
