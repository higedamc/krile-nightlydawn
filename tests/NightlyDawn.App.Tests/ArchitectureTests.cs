using System.Text.Json;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>
/// B9: the App layer reaches the relay layer and the key store only through Core contracts (NightlyDawn.Nostr and NightlyDawn.Keys are both forbidden). Two checks, because they fail for
/// different mistakes: the assembly-metadata check catches App code that uses a NightlyDawn.Nostr type; the
/// dependency-graph check catches a ProjectReference that was added but not (yet) used, which the C# compiler
/// would otherwise drop from the metadata and hide.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] Forbidden = ["NightlyDawn.Nostr", "NightlyDawn.Keys"];

    [Fact]
    public void App_DoesNotUseTheNostrAssembly()
    {
        var referenced = typeof(App).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("NightlyDawn.Core", referenced);
        foreach (var forbidden in Forbidden)
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }

    [Fact]
    public void App_DoesNotDeclareTheNostrProjectAsADependency()
    {
        var depsPath = Path.Combine(AppContext.BaseDirectory, "NightlyDawn.App.Tests.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));

        var appEntries = deps.RootElement.GetProperty("targets")
            .EnumerateObject()
            .SelectMany(target => target.Value.EnumerateObject())
            .Where(library => library.Name.StartsWith("NightlyDawn.App/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(appEntries);

        foreach (var app in appEntries)
        {
            var dependencies = app.Value.TryGetProperty("dependencies", out var d)
                ? d.EnumerateObject().Select(p => p.Name).ToList()
                : [];

            Assert.Contains("NightlyDawn.Core", dependencies);
            foreach (var forbidden in Forbidden)
            {
                Assert.DoesNotContain(forbidden, dependencies);
            }
        }
    }
}
