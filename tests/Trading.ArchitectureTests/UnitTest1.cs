using System.Reflection;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests
{
    public class DomainArchitectureTests
    {
        [Fact]
        public void DomainMustNotReferenceForbiddenAssemblies()
        {
            var domainAssembly = typeof(User).Assembly;
            var forbiddenAssemblies = new[]
            {
                "Microsoft.EntityFrameworkCore",
                "Azure.Core",
                "Azure.Identity",
                "Azure.Security.KeyVault",
                "System.Net.Http",

                // Exchange SDKs. Listed by name so an accidental package
                // reference is caught, whichever venue it belongs to.
                "Kraken.Net",
                "Binance.Net"
            };

            var referencedAssemblies = domainAssembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var forbidden in forbiddenAssemblies)
            {
                Assert.False(
                    referencedAssemblies.Contains(forbidden),
                    $"Domain assembly should not reference '{forbidden}'.");
            }

            // No connector may be referenced from the domain, regardless of
            // which exchange it targets. Naming a single venue here would
            // stop guarding the rule as soon as another venue is added.
            Assert.DoesNotContain(
                referencedAssemblies,
                name => name is not null
                    && name.StartsWith("Trading.Exchanges.", StringComparison.OrdinalIgnoreCase));
        }
    }
}