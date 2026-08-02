using EmotePurge.Core.Entities;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Turns the project's load-bearing layering rule into a red build instead of a review comment.
// EmotePurge.Core is allowed to reference the BCL and nothing else: its service contracts are
// interfaces, its messaging abstraction speaks plain strings, and its DTOs are plain records — all
// so that a persistence, cache or transport decision cannot leak into the domain by way of a using
// directive nobody noticed in review.
//
// Deliberately plain reflection rather than NetArchTest: one assertion, no extra package, and the
// failure message names the offending assembly directly.
public class CoreAssemblyReferenceTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
        "System.Net.Http",
    ];

    [Fact]
    public void CoreAssembly_ReferencesNoInfrastructureTechnology()
    {
        var coreAssembly = typeof(Channel).Assembly;

        var violations = coreAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"EmotePurge.Core darf keine Infrastruktur-Technologie referenzieren, verweist aber auf: {string.Join(", ", violations)}. "
            + "Die Schichtentreue-Tabelle in CLAUDE.md verlangt für Core ausschließlich BCL-Abhängigkeiten.");
    }

    [Fact]
    public void CoreAssembly_ReferencesNoOtherProjectOfThisSolution()
    {
        // The complement of the rule above: Core sits at the bottom, so a reference to any sibling
        // project would mean the dependency direction Api/Worker -> Infrastructure -> Core has been
        // inverted somewhere.
        var coreAssembly = typeof(Channel).Assembly;

        var violations = coreAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("EmotePurge.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"EmotePurge.Core darf kein anderes Projekt der Solution referenzieren, verweist aber auf: {string.Join(", ", violations)}.");
    }
}
