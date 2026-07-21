using System;
using Ink;
using Ink.Runtime;

class QuickStructTest
{
    static void Main()
    {
        var str = @"
=== struct Character ===
VAR name = ""anonymous""

VAR x: Character

=== start ===
{x.name}
-> END
";
        Console.WriteLine("Parsing...");
        var parser = new InkParser(str);
        var parsed = parser.Parse();
        Console.WriteLine("Exporting...");
        var story = parsed.ExportRuntime((msg, type) => Console.WriteLine(type + ": " + msg));
        if (story == null) { Console.WriteLine("Export failed"); return; }
        Console.WriteLine("Running...");
        Console.WriteLine(story.ContinueMaximally());
        Console.WriteLine("OK");
    }
}
