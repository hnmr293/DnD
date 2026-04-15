namespace DnD.Core.Inspection;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Extracts using directives from C# source files.
/// </summary>
public static class SourceFileHelper
{
    private static readonly string[] DefaultUsings =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.Linq;",
        "using System.Threading.Tasks;"
    ];

    /// <summary>
    /// Extracts using directives from a source file.
    /// Returns default usings if the file cannot be read.
    /// </summary>
    public static List<string> ExtractUsings(string? sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
            return new List<string>(DefaultUsings);

        try
        {
            var sourceText = File.ReadAllText(sourceFilePath);
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var usings = new List<string>();

            foreach (var directive in root.Usings)
            {
                usings.Add(directive.ToFullString().Trim());
            }

            // Add defaults if none found
            if (usings.Count == 0)
                return new List<string>(DefaultUsings);

            // Ensure System is present
            if (!usings.Any(u => u == "using System;" || u.StartsWith("global using") && u.Contains("System;")))
                usings.Insert(0, "using System;");

            return usings;
        }
        catch
        {
            return new List<string>(DefaultUsings);
        }
    }
}
