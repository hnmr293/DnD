using System;

class Program
{
    static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "null-ref";
        switch (mode)
        {
            case "null-ref":
                string s = null;
                Console.WriteLine(s.Length);
                break;
            case "custom":
                throw new InvalidOperationException("Something went wrong");
            case "no-message":
                throw new ApplicationException();
            case "nested":
                try { throw new InvalidOperationException("inner"); }
                catch (Exception ex) { throw new AggregateException("outer", ex); }
            case "argument":
                throw new ArgumentException("bad param", "myParam");
            case "custom-type":
                throw new CustomArgumentException("custom fail", "theParam");
        }
    }
}

/// <summary>
/// Custom exception that extends Exception directly (not System.ArgumentException).
/// Simulates third-party exceptions that define their own ParamName property with a different inheritance chain.
/// </summary>
class CustomArgumentException : Exception
{
    public string ParamName { get; }
    public CustomArgumentException(string message, string paramName) : base(message)
    {
        ParamName = paramName;
    }
}
