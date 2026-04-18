using System;
using System.Collections.Generic;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var service = new MyService("test-instance");
        var result = await service.ProcessAsync(42, "hello");
        Console.WriteLine(result);
    }
}

class MyService
{
    private string _name;

    public MyService(string name) { _name = name; }

    public async Task<string> ProcessAsync(int count, string message)
    {
        var prefix = $"[{_name}]";
        var items = new List<int> { 1, 2, 3 };
        await Task.Delay(1);
        var result = $"{prefix} {message} x{count} items={items.Count}";
        Console.WriteLine(result);
        return result;
    }
}
