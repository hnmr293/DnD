using System;

class Program
{
    static int Add(int a, int b)
    {
        return a + b;  // BP target line
    }

    static void Main(string[] args)
    {
        var result = Add(1, 2);
        Console.WriteLine(result);
    }
}
