using CashierMollie.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CashierMollie.Testing;

/// <summary>Registration of <see cref="FakeMollieClient"/>.</summary>
public static class FakeMollieServiceCollectionExtensions
{
    /// <summary>
    /// Replaces CashierMollie's Mollie client with one <see cref="FakeMollieClient"/> for the
    /// container, registered as itself too so a test can resolve it and drive it. Call it after
    /// <c>AddCashierMollie</c>, for example in <c>ConfigureTestServices</c>.
    /// </summary>
    public static FakeMollieClient AddFakeMollie(this IServiceCollection services, FakeMollieClient? fake = null)
    {
        fake ??= new FakeMollieClient();
        services.RemoveAll<IMollieClientService>();
        services.AddSingleton(fake);
        services.AddSingleton<IMollieClientService>(fake);
        return fake;
    }
}
