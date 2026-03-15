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
        }
    }
}
