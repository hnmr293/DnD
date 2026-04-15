namespace DnD.Core.Tests;

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Verifies that the reflection approach for setting
/// TopLevelBinderFlags.IgnoreAccessibility on CSharpCompilationOptions
/// works against the shipped version of Microsoft.CodeAnalysis.CSharp.
/// </summary>
public class RoslynReflectionTests
{
    [Fact]
    public void ReflectionApproach_FindsBinderFlagsType_AndSetsIgnoreAccessibility()
    {
        // Step 1: Create options
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithMetadataImportOptions(MetadataImportOptions.All);

        // Step 2: Find Microsoft.CodeAnalysis.CSharp.BinderFlags type
        var csharpAssembly = typeof(CSharpCompilationOptions).Assembly;
        var binderFlagsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
        Assert.NotNull(binderFlagsType);

        // Step 3: Find IgnoreAccessibility field
        var ignoreAccessField = binderFlagsType.GetField(
            "IgnoreAccessibility",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ignoreAccessField);

        var ignoreAccessValue = ignoreAccessField.GetValue(null);
        Assert.NotNull(ignoreAccessValue);

        // Step 4: Find WithTopLevelBinderFlags method
        var withMethod = typeof(CSharpCompilationOptions).GetMethod(
            "WithTopLevelBinderFlags",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { binderFlagsType },
            null);
        Assert.NotNull(withMethod);

        // Step 5-6: Invoke the method and assert result is a valid CSharpCompilationOptions
        var result = withMethod.Invoke(options, new[] { ignoreAccessValue });
        Assert.NotNull(result);
        var newOptions = Assert.IsType<CSharpCompilationOptions>(result);

        // Step 7: Verify TopLevelBinderFlags property has IgnoreAccessibility set
        var topLevelBinderFlagsProp = typeof(CSharpCompilationOptions).GetProperty(
            "TopLevelBinderFlags",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(topLevelBinderFlagsProp);

        var flagsValue = topLevelBinderFlagsProp.GetValue(newOptions);
        Assert.NotNull(flagsValue);

        // BinderFlags is a struct wrapping a uint; verify the IgnoreAccessibility bit is set
        // by comparing it with the known IgnoreAccessibility value
        Assert.Equal(ignoreAccessValue.ToString(), flagsValue.ToString());
    }

    /// <summary>
    /// Compiles two assemblies: Assembly A has an internal class Foo, Assembly B tries to
    /// instantiate and access Foo's field. Without IgnoreAccessibility this produces CS0122;
    /// with the flag the compilation succeeds.
    /// </summary>
    [Fact]
    public void IgnoreAccessibility_AllowsCrossAssemblyInternalAccess()
    {
        var corlibRef = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        // --- Build Assembly A containing an internal class with a public field ---
        const string sourceA = """
            internal class Foo { public int X; }
            """;

        var compilationA = CSharpCompilation.Create(
            "AssemblyA",
            new[] { CSharpSyntaxTree.ParseText(sourceA) },
            new[] { corlibRef },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errorsA = compilationA.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errorsA);

        // Emit Assembly A so we can reference it from Assembly B
        using var msA = new MemoryStream();
        var emitResult = compilationA.Emit(msA);
        Assert.True(emitResult.Success, string.Join("; ", emitResult.Diagnostics));
        msA.Position = 0;
        var refA = MetadataReference.CreateFromStream(msA);

        // --- Build Assembly B that accesses Assembly A's internal class inside a method body ---
        // Foo is internal to Assembly A, so accessing it from Assembly B should produce
        // CS0122 ("inaccessible due to its protection level") under normal compilation.
        // Note: Foo must NOT appear in a public signature (that would be CS0051 which
        // IgnoreAccessibility does not suppress — it only suppresses access checks).
        const string sourceB = """
            public static class Bar
            {
                public static int Baz()
                {
                    var f = new Foo();
                    return f.X;
                }
            }
            """;

        var syntaxTreeB = CSharpSyntaxTree.ParseText(sourceB);

        // Without IgnoreAccessibility: internal Foo is not even visible in metadata
        // by default, so the type name is unresolved (CS0246).
        // With MetadataImportOptions.All but without IgnoreAccessibility: Foo is visible
        // but inaccessible (CS0122).
        var normalOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithMetadataImportOptions(MetadataImportOptions.All);
        var normalCompilation = CSharpCompilation.Create(
            "AssemblyB", new[] { syntaxTreeB }, new[] { corlibRef, refA }, normalOptions);
        var normalErrors = normalCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(normalErrors);
        Assert.Contains(normalErrors, d => d.Id == "CS0122");

        // With IgnoreAccessibility + MetadataImportOptions.All: should succeed
        var optionsWithAccess = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithMetadataImportOptions(MetadataImportOptions.All);
        optionsWithAccess = ApplyIgnoreAccessibility(optionsWithAccess);

        var accessCompilation = CSharpCompilation.Create(
            "AssemblyB", new[] { syntaxTreeB }, new[] { corlibRef, refA }, optionsWithAccess);
        var accessErrors = accessCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(accessErrors);
    }

    /// <summary>
    /// Applies IgnoreAccessibility via the same reflection approach used in RoslynEvaluator.
    /// </summary>
    private static CSharpCompilationOptions ApplyIgnoreAccessibility(CSharpCompilationOptions options)
    {
        var csharpAssembly = typeof(CSharpCompilationOptions).Assembly;
        var binderFlagsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags")!;
        var ignoreAccessField = binderFlagsType.GetField(
            "IgnoreAccessibility",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)!;
        var ignoreAccessValue = ignoreAccessField.GetValue(null)!;
        var withMethod = typeof(CSharpCompilationOptions).GetMethod(
            "WithTopLevelBinderFlags",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { binderFlagsType },
            null)!;
        return (CSharpCompilationOptions)withMethod.Invoke(options, new[] { ignoreAccessValue })!;
    }
}
