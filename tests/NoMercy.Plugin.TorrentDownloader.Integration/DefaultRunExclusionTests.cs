using System.Reflection;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Integration;

public class DefaultRunExclusionTests
{
    /// <summary>
    /// Everything in this assembly talks to the real internet, and is kept out
    /// of the ordinary run by <c>--filter "FullyQualifiedName!~Integration"</c>.
    /// </summary>
    /// <remarks>
    /// That filter matches on the fully qualified name — namespace, class,
    /// method — and not on the project. A test dropped in here under a
    /// namespace without the word runs on every build, so the gate starts
    /// dialling seventeen sites and fails for whichever of them is down.
    /// </remarks>
    [Fact]
    public void EveryTestInThisAssemblyIsExcludedFromTheDefaultRun()
    {
        string[] included = typeof(DefaultRunExclusionTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            // TheoryAttribute derives from FactAttribute, so this is both of them.
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .Where(name => !name.Contains("Integration", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(included);
    }
}
