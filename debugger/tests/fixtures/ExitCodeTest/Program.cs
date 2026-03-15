using System;

class Program
{
    static int Main(string[] args)
    {
        var code = args.Length > 0 ? int.Parse(args[0]) : 0;
        return code;
    }
}
