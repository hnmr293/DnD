using System;
using System.Collections.Generic;
using System.Diagnostics;
using FuncEvalTest.Shadow;

// Fixture for func-eval defects that are triggered by the *kind* of locals living
// in the stopped frame, not by the expression being evaluated. RoslynEvaluator
// turns every variable of the frame into a parameter of a single generated
// wrapper method, so one unsupported local breaks every evaluate on that frame.
class Program
{
    static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "struct";
        switch (mode)
        {
            case "struct":
                StructLocal();
                break;
            case "enum":
                EnumLocal();
                break;
            case "generic-struct":
                GenericStructLocal();
                break;
            case "nested-type":
                NestedTypeLocal();
                break;
            case "nested-type-unique":
                UniqueNestedTypeLocal();
                break;
            default:
                throw new ArgumentException($"unknown mode: {mode}");
        }
    }

    /// <summary>User-defined struct local.</summary>
    static void StructLocal()
    {
        var number = 42;
        var point = new Point(3, 4);
        Debugger.Break();
        Console.WriteLine($"{number} {point.Sum}");
    }

    /// <summary>User-defined enum local.</summary>
    static void EnumLocal()
    {
        var number = 42;
        var color = Color.Green;
        Debugger.Break();
        Console.WriteLine($"{number} {color}");
    }

    /// <summary>BCL generic struct local.</summary>
    static void GenericStructLocal()
    {
        var number = 42;
        var pair = new KeyValuePair<int, string>(1, "one");
        Debugger.Break();
        Console.WriteLine($"{number} {pair.Key}");
    }

    /// <summary>
    /// Generic local whose type argument is a nested type (Outer.Inner) while a
    /// different type with the same simple name (Shadow.Inner) is imported by a
    /// using directive of this file.
    /// </summary>
    static void NestedTypeLocal()
    {
        var map = new Dictionary<string, Outer.Inner> { ["a"] = new Outer.Inner { Id = 7 } };
        var shadow = new Inner { Name = "shadow" };
        Debugger.Break();
        Console.WriteLine($"{map.Count} {shadow.Name}");
    }

    /// <summary>
    /// Generic local whose type argument is a nested type whose simple name is
    /// not visible anywhere else.
    /// </summary>
    static void UniqueNestedTypeLocal()
    {
        var map = new Dictionary<string, Outer.Leaf> { ["a"] = new Outer.Leaf { Id = 7 } };
        Debugger.Break();
        Console.WriteLine(map.Count);
    }
}

struct Point
{
    public int X;
    public int Y;
    public Point(int x, int y) { X = x; Y = y; }
    public int Sum => X + Y;
}

enum Color { Red, Green, Blue }

class Outer
{
    // Metadata records nested types with their simple name only ("Inner"),
    // without the declaring type.
    public class Inner
    {
        public int Id;
    }

    public class Leaf
    {
        public int Id;
    }
}

namespace FuncEvalTest.Shadow
{
    // Same simple name as Outer.Inner, brought into scope by the using directive.
    public class Inner
    {
        public string Name = "";
    }
}
