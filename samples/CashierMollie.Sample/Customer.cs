using CashierMollie.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Sample;

/// <summary>The application's own user. Implementing <see cref="IBillable{TKey}"/> is all CashierMollie needs.</summary>
public class Customer : IBillable<int>
{
    public int Id { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }

    /// <summary>Test-mode run only: which scenario this customer plays, and how far it got.</summary>
    public string? Scenario { get; set; }

    /// <summary>Test-mode run only: the scenario's current step.</summary>
    public int Step { get; set; }
}

/// <summary>Test-mode run only: one check of one scenario, kept as evidence.</summary>
public class Evidence
{
    public int Id { get; set; }
    public string Scenario { get; set; } = "";
    public string Check { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public bool Pass { get; set; }

    /// <summary>Set when the scenario was started again: this attempt no longer counts.</summary>
    public bool Superseded { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The application's database. The billing tables live in CashierMollie's own context; a real
/// application can instead call <c>modelBuilder.ApplyCashierMollie&lt;int&gt;()</c> here and keep one
/// database (see the README, "Database setup").
/// </summary>
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
}
