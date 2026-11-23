using System.Reflection;
using Melomg.Application;
using Melomg.Cli.Commands;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Infrastructure;
using Xunit;

namespace Melomg.ArchitectureTests;

public sealed class CleanCliDependencyRuleTests
{
    [Fact]
    public void Domain_DependsOnNoOtherLayerOrThirdPartyPackages()
    {
        var domainAssembly = typeof(Result).Assembly;
        var referencedNames = domainAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referencedNames, n =>
            n.StartsWith("Melomg.Application", StringComparison.Ordinal) ||
            n.StartsWith("Melomg.Infrastructure", StringComparison.Ordinal) ||
            n.StartsWith("melomg", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Windows.SDK", StringComparison.Ordinal) ||
            n.StartsWith("WinRT.Runtime", StringComparison.Ordinal));

        AssertNoConsoleUsage(domainAssembly);
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrCliOrSqliteOrConsole()
    {
        var appAssembly = typeof(Melomg.Application.DependencyInjection).Assembly;
        var referencedNames = appAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referencedNames, n =>
            n.StartsWith("Melomg.Infrastructure", StringComparison.Ordinal) ||
            n.StartsWith("melomg", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Windows.SDK", StringComparison.Ordinal) ||
            n.StartsWith("WinRT.Runtime", StringComparison.Ordinal));

        AssertNoConsoleUsage(appAssembly);
    }

    [Fact]
    public void CliCommands_DoNotDependOnInfrastructureOrSqliteOrSystemConsoleDirectly()
    {
        var cliAssembly = typeof(IngestCliCommand).Assembly;
        var commandTypes = cliAssembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, "Melomg.Cli.Commands", StringComparison.Ordinal) ||
                        (t.DeclaringType?.Namespace == "Melomg.Cli.Commands"))
            .ToList();

        Assert.NotEmpty(commandTypes);

        foreach (var type in commandTypes)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    Assert.False(IsForbiddenInfraType(param.ParameterType),
                        $"CLI Command '{type.FullName}' injects forbidden type '{param.ParameterType.FullName}'.");
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.False(IsForbiddenInfraType(field.FieldType),
                    $"CLI Command '{type.FullName}' holds forbidden field '{field.FieldType.FullName}'.");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.False(IsForbiddenInfraType(method.ReturnType),
                    $"CLI Command '{type.FullName}.{method.Name}' returns forbidden type.");
            }
        }
    }

    [Fact]
    public void DomainEntities_HaveNoPublicConstructorsAndNoPublicPropertySetters()
    {
        var domainAssembly = typeof(Result).Assembly;
        var entityTypes = domainAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSealed &&
                        (t.Namespace?.StartsWith("Melomg.Domain.Documents", StringComparison.Ordinal) == true ||
                         t.Namespace?.StartsWith("Melomg.Domain.Models", StringComparison.Ordinal) == true))
            .Where(t => !t.IsNested && !typeof(Delegate).IsAssignableFrom(t))
            .Where(t => !typeof(IEquatable<>).IsAssignableFrom(t)) // exclude records (BoundingBox, PdfPageInfo)
            .Where(t => t.Name is "PdfDocument" or "DocumentFigure" or "OnnxModelProfile")
            .ToList();

        Assert.Equal(3, entityTypes.Count);

        foreach (var entity in entityTypes)
        {
            var publicCtors = entity.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.True(publicCtors.Length == 0, $"Entity '{entity.FullName}' must not expose public constructors.");

            foreach (var prop in entity.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.SetMethod is not null)
                {
                    Assert.False(prop.SetMethod.IsPublic,
                        $"Entity property '{entity.FullName}.{prop.Name}' must not have a public setter.");
                }
            }
        }
    }

    private static bool IsForbiddenInfraType(Type type) =>
        type.Assembly == typeof(MelomgStorageOptions).Assembly ||
        type.FullName?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true ||
        type.FullName?.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal) == true ||
        type == typeof(Console);

    private static void AssertNoConsoleUsage(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Assert.NotEqual(typeof(Console), field.FieldType);

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Assert.NotEqual(typeof(Console), prop.PropertyType);
        }
    }

    [Fact]
    public void Domain_OnlySupportsVisualImageAndDiagramCategories()
    {
        var names = Enum.GetNames<FigureCategory>();
        Assert.Equal(["Figure", "Diagram", "Chart"], names);
        Assert.DoesNotContain("Table", names);
        Assert.DoesNotContain("Formula", names);
        Assert.DoesNotContain("Equation", names);
        Assert.DoesNotContain("Box", names);
    }

    [Fact]
    public void AllMelomgAssemblies_ShareUnifiedProjectVersion()
    {
        var domainAsm = typeof(Result).Assembly;
        var appAsm = typeof(Melomg.Application.DependencyInjection).Assembly;
        var infraAsm = typeof(MelomgStorageOptions).Assembly;
        var cliAsm = typeof(IngestCliCommand).Assembly;

        string expected = MelomgVersion.Resolve(domainAsm);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal("2.1.0", expected);
        Assert.Equal(expected, MelomgVersion.Resolve(appAsm));
        Assert.Equal(expected, MelomgVersion.Resolve(infraAsm));
        Assert.Equal(expected, MelomgVersion.Resolve(cliAsm));
        Assert.Equal(expected, VersionCliCommand.GetCliVersion());

        var assemblyVersion = domainAsm.GetName().Version;
        Assert.NotNull(assemblyVersion);
        Assert.Equal(assemblyVersion, appAsm.GetName().Version);
        Assert.Equal(assemblyVersion, infraAsm.GetName().Version);
        Assert.Equal(assemblyVersion, cliAsm.GetName().Version);
    }
}
