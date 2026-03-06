namespace CashierMollie.Models;

/// <summary>
/// Marker interface for entities that have CreatedAt/UpdatedAt timestamps.
/// UpdatedAt is automatically set by <see cref="Data.CashierDbContext{TKey}.SaveChangesAsync"/>
/// on every modification.
/// </summary>
public interface IHasTimestamps
{
    /// <summary>When this record was created.</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this record was last updated.</summary>
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Marker interface for entities that use optimistic concurrency via a RowVersion counter.
/// RowVersion is automatically incremented by <see cref="Data.CashierDbContext{TKey}.SaveChangesAsync"/>
/// on every modification.
/// </summary>
public interface IHasConcurrencyToken
{
    /// <summary>Optimistic concurrency token, incremented on each update.</summary>
    uint RowVersion { get; set; }
}
