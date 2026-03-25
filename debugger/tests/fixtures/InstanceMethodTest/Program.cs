using System;
using System.Diagnostics;

class Calculator
{
    public int Value { get; }

    public Calculator(int value)
    {
        Value = value;
    }

    public int Multiply(int factor)
    {
        var result = Value * factor;
        Debugger.Break();
        return result;
    }
}

class Program
{
    static void Main(string[] args)
    {
        var calc = new Calculator(42);
        var result = calc.Multiply(3);
        Console.WriteLine(result);
    }
}
