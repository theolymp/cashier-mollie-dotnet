using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Data;

/// <summary>
/// EF Core DbContext for Cashier Mollie tables.
/// Use <see cref="CashierModelBuilderExtensions.ApplyCashierMollie"/> to integrate
/// into your application's DbContext instead of using this directly.
/// </summary>
public class CashierDbContext : DbContext
{
    public CashierDbContext(DbContextOptions<CashierDbContext> options)
        : base(options) { }

    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCashierMollie();
    }
}

public static class CashierModelBuilderExtensions
{
    /// <summary>
    /// Applies CashierMollie entity configurations to an existing ModelBuilder.
    /// Call this in your application's DbContext.OnModelCreating().
    /// </summary>
    public static ModelBuilder ApplyCashierMollie(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("cashier_subscriptions");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MollieSubscriptionId);
            entity.HasMany(e => e.OrderItems)
                .WithOne(e => e.Subscription)
                .HasForeignKey(e => e.SubscriptionId);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("cashier_order_items");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MolliePaymentId);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("cashier_payments");
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.MolliePaymentId).IsUnique();
            entity.HasOne(e => e.Subscription)
                .WithMany()
                .HasForeignKey(e => e.SubscriptionId)
                .IsRequired(false);
        });

        return modelBuilder;
    }
}
