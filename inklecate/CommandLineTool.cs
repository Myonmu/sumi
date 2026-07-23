using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

namespace Ink
{
	class CommandLineTool
	{
		class Options {
            public bool verbose;
			public bool playMode;
            public bool stats;
            public bool jsonOutput;
			public string inputFile;
            public string outputFile;
            public bool countAllVisits;
            public bool keepOpenAfterStoryFinish;
            /// <summary>Comma-separated file:line breakpoints, e.g. story.ink:12,other.ink:3</summary>
            public string breakpoints;
		}

		public static int ExitCodeError = 1;

		public static void Main (string[] args)
		{
			new CommandLineTool(args);
		}

        void ExitWithUsageInstructions()
        {
            Console.WriteLine (
                "Usage: inklecate <options> <ink file>\n"+
                "   -o <filename>:   Output file name\n"+
                "   -c:              Count all visits to knots, stitches and weave points, not\n" +
                "                    just those referenced by TURNS_SINCE and read counts.\n" +
                "   -p:              Play mode\n"+
                "   -j:              Output in JSON format (for communication with tools like Inky)\n"+
                "   -s:              Print stats about story including word count in JSON format\n" +
                "   -v:              Verbose mode - print compilation timings\n"+
                "   -k:              Keep inklecate running in play mode even after story is complete\n" +
                "   -x <directory>:              Import plugins for the compiler.\n" +
                "   -d <define>:     Adds a preprocessor symbol (you can have multiple -d)\n"+
                "   -b <breakpoints>: Comma-separated file:line breakpoints for play mode\n"
                );
            Environment.Exit (ExitCodeError);
        }

		CommandLineTool(string[] args)
		{
            // Set console's output encoding to UTF-8
            try { 
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch {
                Debug.WriteLine("Unable to set console encoding to UTF8, using system default.");
            }

            if (ProcessArguments (args) == false) {
                ExitWithUsageInstructions ();
            }

            if (opts.inputFile == null) {
                ExitWithUsageInstructions ();
            }

            string inputString = null;
            string workingDirectory = Directory.GetCurrentDirectory();

            if (opts.outputFile == null)
                opts.outputFile = Path.ChangeExtension (opts.inputFile, ".ink.json");

            if( !Path.IsPathRooted(opts.outputFile) )
                opts.outputFile = Path.Combine (workingDirectory, opts.outputFile);

            try {
                string fullFilename = opts.inputFile;
                if(!Path.IsPathRooted(fullFilename)) {
                    fullFilename = Path.Combine(workingDirectory, fullFilename);
                }

                // Make the working directory the directory for the root ink file,
                // so that relative paths for INCLUDE files are correct.
                workingDirectory = Path.GetDirectoryName(fullFilename);
                Directory.SetCurrentDirectory(workingDirectory);

                // Now make the input file relative to the working directory,
                // but just getting the file's actual name.
                opts.inputFile = Path.GetFileName(fullFilename);

                inputString = File.ReadAllText(opts.inputFile);
            }
            catch {
                Console.WriteLine ("Could not open file '" + opts.inputFile+"'");
                Environment.Exit (ExitCodeError);
            }

            var inputIsJson = opts.inputFile.EndsWith (".json", StringComparison.InvariantCultureIgnoreCase);
            if( inputIsJson && opts.stats ) {
                Console.WriteLine ("Cannot show stats for .json, only for .ink");
                Environment.Exit (ExitCodeError);
            }

            Parsed.Story parsedStory = null;
            Runtime.Story story = null;
            Compiler compiler = null;

            // Loading a normal ink file (as opposed to an already compiled json file)
            if (!inputIsJson) {

                compiler = new Compiler (inputString, new Compiler.Options {
                    sourceFilename = opts.inputFile,
                    pluginDirectories = pluginDirectories,
                    preprocessorSymbols = preprocessorSymbols,
                    countAllVisits = opts.countAllVisits,
                    errorHandler = OnError
                });

                // Only want stats, don't need to code-gen
                if (opts.stats)
                {
                    parsedStory = compiler.Parse();

                    // Print any errors
                    PrintAllMessages();

                    // Generate stats, then print as JSON
                    var stats = Ink.Stats.Generate(compiler.parsedStory);

                    if( opts.jsonOutput ) {
                        var writer = new Runtime.SimpleJson.Writer();

                        writer.WriteObjectStart();
                        writer.WritePropertyStart("stats");

                        writer.WriteObjectStart();
                        writer.WriteProperty("words", stats.words);
                        writer.WriteProperty("knots", stats.knots);
                        writer.WriteProperty("stitches", stats.stitches);
                        writer.WriteProperty("functions", stats.functions);
                        writer.WriteProperty("choices", stats.choices);
                        writer.WriteProperty("gathers", stats.gathers);
                        writer.WriteProperty("diverts", stats.diverts);
                        writer.WriteObjectEnd();

                        writer.WritePropertyEnd();
                        writer.WriteObjectEnd();

                        Console.WriteLine(writer.ToString());
                    } else {
                        Console.WriteLine("Words: "+stats.words);
                        Console.WriteLine("Knots: "+stats.knots);
                        Console.WriteLine("Stitches: "+stats.stitches);
                        Console.WriteLine("Functions: "+stats.functions);
                        Console.WriteLine("Choices: "+stats.choices);
                        Console.WriteLine("Gathers: "+stats.gathers);
                        Console.WriteLine("Diverts: "+stats.diverts);
                    }

                    return;
                }

                // Full compile
                else
                    story = compiler.Compile();
            }

            // Opening up a compiled json file for playing
            else {
                story = new Runtime.Story (inputString);

                // No purpose for loading an already compiled file other than to play it
                opts.playMode = true;
            }

            var compileSuccess = !(story == null || _errors.Count > 0);
            if( opts.jsonOutput ) {
                if( compileSuccess )
                    EmitCompileSuccessJson (story, compiler);
                else
                    Console.WriteLine("{\"compile-success\": false}");
            }


            PrintAllMessages ();

            if (!compileSuccess)
				Environment.Exit (ExitCodeError);

			// Play mode
            if (opts.playMode) {

                _playing = true;

                // Always allow ink external fallbacks
                story.allowExternalFunctionFallbacks = true;

                var player = new CommandLinePlayer (story, false, compiler, opts.keepOpenAfterStoryFinish, opts.jsonOutput);

                if (!string.IsNullOrEmpty (opts.breakpoints)) {
                    player.ApplyBreakpoints (ParseBreakpointList (opts.breakpoints));
                }

                //Capture a CTRL+C key combo so we can restore the console's foreground color back to normal when exiting
                Console.CancelKeyPress += OnExit;

                try {
                    player.Begin ();
                } catch (Runtime.StoryException e) {
                    if (e.Message.Contains ("Missing function binding")) {
                        OnError (e.Message, ErrorType.Error);
                        PrintAllMessages ();
                    } else {
                        throw e;
                    }
                } catch (System.Exception e) {
                    string storyPath = "<END>";
                    var path = story.state.currentPathString;
                    if (path != null) {
                        storyPath = path.ToString ();
                    }
                    throw new System.Exception(e.Message + " (Internal story path: " + storyPath + ")", e);
                }
            }

            // Compile mode
            else {

                var jsonStr = story.ToJson ();

                try {
                    File.WriteAllText (opts.outputFile, jsonStr, new System.Text.UTF8Encoding(false));

                    if( opts.jsonOutput )
                        Console.WriteLine("{\"export-complete\": true}");

                } catch {
                    Console.WriteLine ("Could not write to output file '" + opts.outputFile+"'");
                    Environment.Exit (ExitCodeError);
                }
            }
        }

        /// <summary>
        /// Emit compile-success plus IDE symbol metadata (structDefs + typedGlobals)
        /// so tools like Inky can power accurate member autocomplete.
        /// </summary>
        void EmitCompileSuccessJson (Runtime.Story story, Compiler compiler)
        {
            var writer = new Runtime.SimpleJson.Writer ();
            writer.WriteObjectStart ();
            writer.WriteProperty ("compile-success", true);

            // Flattened struct type descriptors (fields + methods after inheritance)
            if (story != null && story.structDefinitions != null) {
                writer.WritePropertyStart ("structDefs");
                writer.WriteObjectStart ();
                foreach (var def in story.structDefinitions.structs) {
                    writer.WritePropertyStart (def.name);
                    Runtime.Json.WriteStructDefinition (writer, def);
                    writer.WritePropertyEnd ();
                }
                writer.WriteObjectEnd ();
                writer.WritePropertyEnd ();
            }

            // Global VAR/REFVAR → struct type name
            writer.WritePropertyStart ("typedGlobals");
            writer.WriteObjectStart ();
            if (compiler != null && compiler.parsedStory != null) {
                foreach (var kv in compiler.parsedStory.variableDeclarations) {
                    var decl = kv.Value;
                    if (decl != null && decl.isGlobalDeclaration && decl.structTypeName != null)
                        writer.WriteProperty (kv.Key, decl.structTypeName);
                }
            }
            writer.WriteObjectEnd ();
            writer.WritePropertyEnd ();

            writer.WriteObjectEnd ();
            Console.WriteLine (writer.ToString ());
        }

        static List<BreakpointSpec> ParseBreakpointList (string spec)
        {
            var list = new List<BreakpointSpec> ();
            if (string.IsNullOrEmpty (spec))
                return list;

            foreach (var token in spec.Split (new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                var trimmed = token.Trim ();
                int colon = trimmed.LastIndexOf (':');
                if (colon <= 0 || colon >= trimmed.Length - 1)
                    continue;
                int line;
                if (!int.TryParse (trimmed.Substring (colon + 1), out line) || line < 1)
                    continue;
                list.Add (new BreakpointSpec {
                    fileName = trimmed.Substring (0, colon),
                    lineNumber = line
                });
            }
            return list;
        }

        private void OnExit(object sender, ConsoleCancelEventArgs e)
        {
            Console.ResetColor();
        }

        void OnError(string message, ErrorType errorType)
        {
            switch (errorType) {
            case ErrorType.Author:
                _authorMessages.Add (message);
                break;

            case ErrorType.Warning:
                _warnings.Add (message);
                break;

            case ErrorType.Error:
                _errors.Add (message);
                break;
            }

            // If you get an error while playing, just print immediately
            if( _playing ) PrintAllMessages ();
        }

        void PrintIssues(List<string> messageList, ConsoleColor colour)
        {
            Console.ForegroundColor = colour;
            foreach (string msg in messageList) {
                Console.WriteLine (msg);
            }
            Console.ResetColor ();
        }

        void PrintAllMessages ()
        {
            // { "issues": ["ERROR: blah", "WARNING: blah"] }
            if( opts.jsonOutput ) {
                var writer = new Runtime.SimpleJson.Writer();

                writer.WriteObjectStart();
                writer.WritePropertyStart("issues");
                writer.WriteArrayStart();
                foreach (string msg in _authorMessages) {
                    writer.Write(msg);
                }
                foreach (string msg in _warnings) {
                    writer.Write(msg);
                }
                foreach (string msg in _errors) {
                    writer.Write(msg);
                }
                writer.WriteArrayEnd();
                writer.WritePropertyEnd();
                writer.WriteObjectEnd();
                Console.WriteLine (writer.ToString());
            }

            // Human consumption
            else {
                PrintIssues (_authorMessages, ConsoleColor.Green);
                PrintIssues (_warnings, ConsoleColor.Blue);
                PrintIssues (_errors, ConsoleColor.Red);
            }

            _authorMessages.Clear ();
            _warnings.Clear ();
            _errors.Clear ();
        }

        bool ProcessArguments(string[] args)
		{
            if (args.Length < 1) {
                opts = null;
                return false;
            }

			opts = new Options();
            pluginDirectories = new List<string> ();
            preprocessorSymbols = new HashSet<string> ();

            bool nextArgIsOutputFilename = false;
            bool nextArgIsPluginDirectory = false;
            bool nextArgIsPreprocessorSymbol = false;
            bool nextArgIsBreakpoints = false;

			// Process arguments
            int argIdx = 0;
			foreach (string arg in args) {

                if (nextArgIsOutputFilename) {
                    opts.outputFile = arg;
                    nextArgIsOutputFilename = false;
                } else if (nextArgIsPluginDirectory) {
                    pluginDirectories.Add (arg);
                    nextArgIsPluginDirectory = false;
                } else if (nextArgIsPreprocessorSymbol)
                {
                    preprocessorSymbols.Add(arg);
                    nextArgIsPreprocessorSymbol = false;
                } else if (nextArgIsBreakpoints) {
                    opts.breakpoints = arg;
                    nextArgIsBreakpoints = false;
                }

				// Options
				var firstChar = arg.Substring(0,1);
                if (firstChar == "-" && arg.Length > 1) {

                    for (int i = 1; i < arg.Length; ++i) {
                        char argChar = arg [i];

                        switch (argChar) {
                        case 'p':
                            opts.playMode = true;
                            break;
                        case 'j':
                            opts.jsonOutput = true;
                            break;
                        case 'v':
                            opts.verbose = true;
                            break;
                        case 's':
                            opts.stats = true;
                            break;
                        case 'o':
                            nextArgIsOutputFilename = true;
                            break;
                        case 'c':
                            opts.countAllVisits = true;
                            break;
                        case 'x':
                            nextArgIsPluginDirectory = true;
                            break;
                        case 'k':
                            opts.keepOpenAfterStoryFinish = true;
                            break;
                        case 'd':
                            nextArgIsPreprocessorSymbol = true;
                            break;
                        case 'b':
                            nextArgIsBreakpoints = true;
                            break;
                        default:
                            Console.WriteLine ("Unsupported argument type: '{0}'", argChar);
                            break;
                        }
                    }
                }

                // Last argument: input file
                else if( argIdx == args.Length-1 ) {
                    opts.inputFile = arg;
                }

                argIdx++;
			}

			return true;
		}

        Options opts;
        List<string> pluginDirectories;
        HashSet<string> preprocessorSymbols;

        List<string> _errors = new List<string>();
        List<string> _warnings = new List<string>();
        List<string> _authorMessages = new List<string>();

        bool _playing;
	}
}
