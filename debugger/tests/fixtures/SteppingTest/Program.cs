using System;

class Program
{
    static void Inner()
    {
        Console.WriteLine("inner");  // stepIn enters here
    }

    static void Outer()
    {
        Inner();                      // stepOver skips this
        Console.WriteLine("outer");
    }

    static void Main(string[] args)
    {
        Outer();
    }
}
