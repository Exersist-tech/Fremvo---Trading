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
        }
    }
}