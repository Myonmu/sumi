using System;
using Ink;
using Ink.Runtime;

namespace StructDebug
{
    class Program
    {
        static void Main()
        {
            try
            {
                Console.WriteLine("1. Minimal struct");
                Run(@"
=== struct Character ===
VAR name = ""anonymous""

VAR x: Character

-> start
=== start ===
{x.name}
-> END
");
                Console.WriteLine("2. With method");
                Run(@"
=== struct Character ===
VAR name = ""anonymous""
= function Hello() =
Hi
~ return

VAR x: Character

-> start
=== start ===
~ x.Hello()
-> END
");
                Console.WriteLine("3. Full-ish");
                Run(@"
=== struct Character ===
VAR name = ""anonymous""
= function ReactFurious() =
angry
~ return

=== struct Oswald: Character ===
VAR name = ""Flinn Oswald""
= function ReactFurious() =
~ base.ReactFurious()
Oh my!
~ return

VAR Oswald: Oswald

-> start
=== start ===
{Oswald.name}
~ Oswald.ReactFurious()
-> END
");
                Console.WriteLine("All OK");
            }
            catch (Exception e)
            {
                Console.WriteLine("EX: " + e);
            }
        }

        static void Run(string str)
        {
            var parser = new InkParser(str, null, (msg, t) => Console.WriteLine(t + ": " + msg));
            var parsed = parser.Parse();
            if (parsed == null) { Console.WriteLine("parse null"); return; }
            var story = parsed.ExportRuntime((msg, t) => Console.WriteLine(t + ": " + msg));
            if (story == null) { Console.WriteLine("export null"); return; }
            Console.WriteLine("OUT: " + story.ContinueMaximally().Replace("\n", "\\n"));
        }
    }
}
