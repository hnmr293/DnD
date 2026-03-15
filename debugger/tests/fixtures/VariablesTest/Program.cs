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
        Debugger.Break(); // stops here
        Console.WriteLine($"{x} {name} {pi} {flag}");
    }
}
