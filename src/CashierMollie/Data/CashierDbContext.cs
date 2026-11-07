using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Data;

/// <summary>
/// EF Core DbContext for Cashier Mollie tables.
/// Use <see cref="CashierModelBuilderExtensions.ApplyCashierMollie{TKey}"/> to integrate
/// into your application's DbContext instead of using this directly.
/// </summary>
public class CashierDbContext<TKey> : DbContext where TKey : IEquatable<TKey>
{
    public CashierDbContext(DbContextOptions<CashierDbContext<TKey>> options)
        : base(options) { }

    public DbSet<Subscription<TKey>> Subscriptions => Set<Subscription<TKey>>();
    public DbSet<OrderItem<TKey>> OrderItems => Set<OrderItem<TKey>>();
    public DbSet<Payment<TKey>> Payments => Set<Payment<TKey>>();
    public DbSet<Order<TKey>> Orders => Set<Order<TKey>>();
    public DbSet<Credit<TKey>> Credits => Set<Credit<TKey>>();
    public DbSet<Refund<TKey>> Refunds => Set<Refund<TKey>>();
    public DbSet<RedeemedCoupon<TKey>> RedeemedCoupons => Set<RedeemedCoupon<TKey>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCashierMollie<TKey>();
    }

    /// <summary>
    /// Automatically updates UpdatedAt timestamps and increments RowVersion
    /// concurrency tokens on modified entities.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Modified)
            {
                if (entry.Entity is IHasTimestamps timestamped)
                    timestamped.UpdatedAt = now;
                if (entry.Entity is IHasConcurrencyToken versioned)
                    versioned.RowVersion++;
            }
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}

public static class CashierModelBuilderExtensions
{
    /// <summary>
    /// Total digits on every mapped <c>decimal</c> column. Public so a consumer writing a
    /// migration by hand does not have to read it off a generated schema.
    /// </summary>
    public const int MoneyPrecision = 18;

    /// <summary>
    /// Decimal places on every mapped <c>decimal</c> column: two for the amounts Mollie exchanges,
    /// two more beneath them for the intermediates of a calculation.
    /// </summary>
    public const int MoneyScale = 4;

    /// <summary>
    /// Applies CashierMollie entity configurations to an existing ModelBuilder.
    /// Call this in your application's DbContext.OnModelCreating().
    /// </summary>
    public static ModelBuilder ApplyCashierMollie<TKey>(this ModelBuilder modelBuilder)
        where TKey : IEquatable<TKey>
    {
        modelBuilder.Entity<Subscription<TKey>>(entity =>
        {
            entity.ToTable("cashier_subscriptions");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MollieSubscriptionId);
            entity.HasIndex(e => new { e.OwnerId, e.Name }).IsUnique();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.HasMany(e => e.OrderItems)
                .WithOne(e => e.Subscription)
                .HasForeignKey(e => e.SubscriptionId);
        });

        modelBuilder.Entity<OrderItem<TKey>>(entity =>
        {
            entity.ToTable("cashier_order_items");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MolliePaymentId);
            entity.HasIndex(e => e.ProcessAt);
        });

        modelBuilder.Entity<Payment<TKey>>(entity =>
        {
            entity.ToTable("cashier_payments");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MolliePaymentId).IsUnique();
            entity.HasOne(e => e.Subscription)
                .WithMany()
                .HasForeignKey(e => e.SubscriptionId)
                .IsRequired(false);
        });

        modelBuilder.Entity<Order<TKey>>(entity =>
        {
            entity.ToTable("cashier_orders");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MolliePaymentId);
            entity.HasMany(e => e.Items)
                .WithOne(e => e.Order)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Credit<TKey>>(entity =>
        {
            entity.ToTable("cashier_credits");
            entity.HasIndex(e => new { e.OwnerId, e.Currency }).IsUnique();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
        });

        // Money has an explicit shape, so the provider does not choose one.
        //
        // Without these lines each provider picked: unconstrained `numeric` on PostgreSQL, `TEXT`
        // on SQLite, `decimal(18,2)` on SQL Server. Measured against a real SQL Server before this
        // was set, 11.3862 came back as 11.39 while PostgreSQL returned it whole -- the same code,
        // the same value, two different amounts. That was tolerable only while the library did no
        // money arithmetic at all; it is not something to discover with proration half-written.
        //
        // (18, 4) uniformly, including the tax percentage, and the uniformity is the point: one
        // rule has no per-column judgement to get wrong, and an architecture test can then assert
        // every mapped decimal rather than a list that goes stale. Four places, not two, because
        // Mollie exchanges amounts with two and a computation needs room beneath them -- a
        // proration of 29.99 over 11 days is 11.3862 before it is rounded to a chargeable amount.
        // Widening relative to SQL Server's old default, so nothing stored there can be lost; on
        // PostgreSQL it narrows an unbounded column, which only touches values with more than four
        // decimal places, and nothing in this library has ever been able to produce one.
        //
        // Set per entity rather than by sweeping modelBuilder.Model: ApplyCashierMollie runs
        // against the CONSUMER's model, and a loop over every decimal property in it would
        // silently re-type their columns too.
        modelBuilder.Entity<OrderItem<TKey>>(entity =>
        {
            entity.Property(e => e.UnitPrice).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.TaxPercentage).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.Discount).HasPrecision(MoneyPrecision, MoneyScale);
        });

        modelBuilder.Entity<Order<TKey>>(entity =>
        {
            entity.Property(e => e.Subtotal).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.Tax).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.Total).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.TotalDue).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.CreditUsed).HasPrecision(MoneyPrecision, MoneyScale);
        });

        modelBuilder.Entity<Payment<TKey>>(entity =>
        {
            entity.Property(e => e.Amount).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.AmountRefunded).HasPrecision(MoneyPrecision, MoneyScale);
            entity.Property(e => e.AmountChargedBack).HasPrecision(MoneyPrecision, MoneyScale);
        });

        modelBuilder.Entity<Credit<TKey>>(entity =>
        {
            entity.Property(e => e.Balance).HasPrecision(MoneyPrecision, MoneyScale);
        });

        modelBuilder.Entity<Refund<TKey>>(entity =>
        {
            entity.Property(e => e.Amount).HasPrecision(MoneyPrecision, MoneyScale);
        });

        modelBuilder.Entity<Refund<TKey>>(entity =>
        {
            entity.ToTable("cashier_refunds");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MollieRefundId).IsUnique();
            entity.HasOne(e => e.Payment)
                .WithMany()
                .HasForeignKey(e => e.PaymentId);
        });

        modelBuilder.Entity<RedeemedCoupon<TKey>>(entity =>
        {
            entity.ToTable("cashier_redeemed_coupons");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => new { e.OwnerId, e.Code, e.SubscriptionName }).IsUnique();
        });

        return modelBuilder;
    }
}
