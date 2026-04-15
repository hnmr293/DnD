namespace DnD.Core.Tests;

using DnD.Core.Inspection;

public class SourceFileHelperTests
{
    [Fact]
    public void ExtractUsings_NullPath_ReturnsDefaults()
    {
        var usings = SourceFileHelper.ExtractUsings(null);

        Assert.Contains("using System;", usings);
        Assert.Contains("using System.Collections.Generic;", usings);
        Assert.Contains("using System.Linq;", usings);
        Assert.Contains("using System.Threading.Tasks;", usings);
    }

    [Fact]
    public void ExtractUsings_EmptyPath_ReturnsDefaults()
    {
        var usings = SourceFileHelper.ExtractUsings("");

        Assert.Equal(4, usings.Count);
    }

    [Fact]
    public void ExtractUsings_NonExistentFile_ReturnsDefaults()
    {
        var usings = SourceFileHelper.ExtractUsings(@"C:\nonexistent\file.cs");

        Assert.Equal(4, usings.Count);
        Assert.Contains("using System;", usings);
    }

    [Fact]
    public void ExtractUsings_FileWithUsings_ReturnsParsedUsings()
    {
        var tempFile = Path.GetTempFileName() + ".cs";
        try
        {
            File.WriteAllText(tempFile, """
                using System;
                using System.IO;
                using Foo.Bar;

                class MyClass { }
                """);

            var usings = SourceFileHelper.ExtractUsings(tempFile);

            Assert.Contains("using System;", usings);
            Assert.Contains("using System.IO;", usings);
            Assert.Contains("using Foo.Bar;", usings);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractUsings_FileWithNoUsings_ReturnsDefaults()
    {
        var tempFile = Path.GetTempFileName() + ".cs";
        try
        {
            File.WriteAllText(tempFile, """
                class MyClass
                {
                    void Method() { }
                }
                """);

            var usings = SourceFileHelper.ExtractUsings(tempFile);

            // Falls back to defaults when no usings found
            Assert.Equal(4, usings.Count);
            Assert.Contains("using System;", usings);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractUsings_FileWithoutSystemUsing_InsertsSystem()
    {
        var tempFile = Path.GetTempFileName() + ".cs";
        try
        {
            File.WriteAllText(tempFile, """
                using System.IO;
                using Foo.Bar;

                class MyClass { }
                """);

            var usings = SourceFileHelper.ExtractUsings(tempFile);

            // System should be inserted at position 0
            Assert.Equal("using System;", usings[0]);
            Assert.Contains("using System.IO;", usings);
            Assert.Contains("using Foo.Bar;", usings);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
