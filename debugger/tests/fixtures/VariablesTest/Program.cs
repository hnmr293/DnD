using System;
using System.Diagnostics;

class Program
{
    static void Main(string[] args)
    {
        int x = 42;
        string name = "hello";
        double pi = 3.14;
        bool flag = true;
        int[] arr = new[] { 1, 2, 3 };
        var obj = new { Name = "test", Value = 100 };
        var computed = new ComputedOnly();
        Debugger.Break(); // stops here
        Console.WriteLine($"{x} {name} {pi} {flag} {computed.Value}");
        x = 0; // keep process alive after Console.WriteLine for FILE_SHARE_READ verification
    }
}

/// <summary>
/// Class with computed properties only (no backing fields).
/// Used to test object expansion via getVariables — ExpandChildren
/// only enumerates fields, so this class returns empty on expansion.
/// </summary>
class ComputedOnly
{
    public int Value => 42;
    public string Label => "computed";
}
