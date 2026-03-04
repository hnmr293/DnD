static void Inner()
{
    Console.WriteLine("inner");  // stepIn enters here
}

static void Outer()
{
    Inner();                      // stepOver skips this
    Console.WriteLine("outer");
}

Outer();
