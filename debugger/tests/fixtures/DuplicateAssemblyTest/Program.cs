using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;

class Program
{
    static void Main(string[] args)
    {
        // Load two separately-built assemblies sharing the simple name "DupShared"
        // (distinct MVIDs) into their own AssemblyLoadContexts, so multiple
        // assemblies with the same simple name coexist in the process
        // (plugin-host scenario of issue #34).
        foreach (var plugin in new[] { "PluginA", "PluginB" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, plugin, "DupShared.dll");
            var alc = new AssemblyLoadContext(plugin);
            alc.LoadFromAssemblyPath(path);
        }

        var number = 42;
        Debugger.Break();
        Console.WriteLine(number);
    }
}
