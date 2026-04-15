using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

class Program
{
    static void Main(string[] args)
    {
        var greeting = "Hello, World!";
        var number = 42;
        var list = new List<int> { 1, 2, 3 };
        var obj = new TestClass("test", 100);
        Debugger.Break();
        Console.WriteLine($"{greeting} {number} {list.Count} {obj}");
    }
}

class TestClass
{
    public string Name { get; }
    public int Value { get; }
    public TestClass(string n, int v) { Name = n; Value = v; }
    public override string ToString() => $"TestClass({Name}, {Value})";
    public int Add(int x) => Value + x;
    public int Hang() { while (true) { } }
}
