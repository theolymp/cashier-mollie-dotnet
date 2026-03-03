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
}

public static class CashierModelBuilderExtensions
{
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
        });

        return modelBuilder;
    }
}
