using System;
using System.Collections.Generic;
using Ink;
using Ink.Runtime;

namespace Ink
{
    public class Compiler
    {
        public class Options
        {
            public string sourceFilename;
            public List<string> pluginDirectories;
            public HashSet<string> preprocessorSymbols;
            public bool countAllVisits;
            public Ink.ErrorHandler errorHandler;
            public Ink.IFileHandler fileHandler;
        }

        public Parsed.Story parsedStory {
            get {
                return _parsedStory;
            }
        }

        public Compiler (string inkSource, Options options = null)
        {
            _inputString = inkSource;
            _options = options ?? new Options();
            if( _options.pluginDirectories != null )
                _pluginManager = new PluginManager (_options.pluginDirectories);
        }

        public Parsed.Story Parse()
        {
            _parser = new InkParser(_inputString, _options.sourceFilename, OnParseError, _options.fileHandler, _options.preprocessorSymbols);
            _parsedStory = _parser.Parse();
            return _parsedStory;
        }

        public Runtime.Story Compile ()
        {
            if( _pluginManager != null )
                _inputString = _pluginManager.PreParse(_inputString);

            Parse();

            if( _pluginManager != null )
                _parsedStory = _pluginManager.PostParse(_parsedStory);

            if (_parsedStory != null && !_hadParseError) {

                _parsedStory.countAllVisits = _options.countAllVisits;

                _runtimeStory = _parsedStory.ExportRuntime (_options.errorHandler);

                if( _pluginManager != null )
                    _runtimeStory = _pluginManager.PostExport (_parsedStory, _runtimeStory);
            } else {
                _runtimeStory = null;
            }

            return _runtimeStory;
        }

        public class CommandLineInputResult {
            public bool requestsExit;
            public int choiceIdx = -1;
            public string divertedPath;
            public string output;
            /// <summary>JSON object body for an InspectVar response (no outer wrapper).</summary>
            public string inspectJson;
            public bool appliedBreakpoints;
            public bool isContinue;
            public bool isPlay;
        }
        public CommandLineInputResult HandleInput (CommandLineInput inputResult)
        {
            var result = new CommandLineInputResult ();

            // Request for debug source line number
            if (inputResult.debugSource != null) {
                var offset = (int)inputResult.debugSource;
                var dm = DebugMetadataForContentAtOffset (offset);
                if (dm != null)
                    result.output = "DebugSource: " + dm.ToString ();
                else
                    result.output = "DebugSource: Unknown source";
            }

            // Request for runtime path lookup (to line number)
            else if (inputResult.debugPathLookup != null) {
                var pathStr = inputResult.debugPathLookup;
                var contentResult = _runtimeStory.ContentAtPath (new Runtime.Path (pathStr));
                var dm = contentResult.obj.debugMetadata;
                if( dm != null )
                    result.output = "DebugSource: " + dm.ToString ();
                else
                    result.output = "DebugSource: Unknown source";
            }

            // Variable inspector dump for IDEs (Inky)
            else if (inputResult.inspectVariableName != null) {
                result.inspectJson = BuildInspectVariableJson (inputResult.inspectVariableName);
            }

            // User entered some ink
            else if (inputResult.userImmediateModeStatement != null) {
                var parsedObj = inputResult.userImmediateModeStatement as Parsed.Object;
                return ExecuteImmediateStatement(parsedObj);

            } else {
              return null;
            }

            return result;
        }

        string BuildInspectVariableJson (string name)
        {
            var writer = new SimpleJson.Writer ();
            writer.WriteObjectStart ();
            writer.WriteProperty ("name", name);

            var value = _runtimeStory.variablesState.GetVariableWithName (name);
            if (value == null) {
                writer.WriteProperty ("error", "Unknown variable");
                writer.WriteObjectEnd ();
                return writer.ToString ();
            }

            var structVal = value as StructValue;
            if (structVal != null && structVal.value != null) {
                WriteInspectStruct (writer, structVal.value);
            } else {
                writer.WriteProperty ("kind", "value");
                writer.WritePropertyStart ("value");
                WriteInspectValue (writer, value, nestingDepth: 0);
                writer.WritePropertyEnd ();
            }

            writer.WriteObjectEnd ();
            return writer.ToString ();
        }

        void WriteInspectStruct (SimpleJson.Writer writer, StructObject instance)
        {
            writer.WriteProperty ("kind", instance.isDynamic ? "dynamic" : "struct");
            writer.WriteProperty ("typeName", instance.typeName ?? "");

            writer.WritePropertyStart ("fields");
            writer.WriteObjectStart ();
            if (instance.storage != null) {
                foreach (var kv in instance.storage) {
                    writer.WritePropertyStart (kv.Key);
                    WriteInspectValue (writer, kv.Value, nestingDepth: 0);
                    writer.WritePropertyEnd ();
                }
            }
            writer.WriteObjectEnd ();
            writer.WritePropertyEnd ();

            writer.WritePropertyStart ("methods");
            writer.WriteObjectStart ();
            var typeDef = _runtimeStory.structDefinitions != null
                ? _runtimeStory.structDefinitions.GetDefinition (instance.typeName)
                : null;
            if (typeDef != null && typeDef.methods != null) {
                foreach (var kv in typeDef.methods)
                    writer.WriteProperty (kv.Key, kv.Value);
            }
            writer.WriteObjectEnd ();
            writer.WritePropertyEnd ();

            if (instance.isDynamic && instance.methods != null && instance.methods.Count > 0) {
                writer.WritePropertyStart ("dynamicMethods");
                writer.WriteObjectStart ();
                foreach (var kv in instance.methods) {
                    var divertTarget = kv.Value as DivertTargetValue;
                    writer.WriteProperty (kv.Key,
                        divertTarget != null && divertTarget.value != null
                            ? divertTarget.value.ToString ()
                            : (kv.Value != null ? kv.Value.ToString () : ""));
                }
                writer.WriteObjectEnd ();
                writer.WritePropertyEnd ();
            }
        }

        void WriteInspectValue (SimpleJson.Writer writer, Runtime.Object obj, int nestingDepth)
        {
            if (obj == null) {
                writer.WriteNull ();
                return;
            }

            var boolVal = obj as BoolValue;
            if (boolVal != null) {
                writer.Write (boolVal.value);
                return;
            }

            var intVal = obj as IntValue;
            if (intVal != null) {
                writer.Write (intVal.value);
                return;
            }

            var floatVal = obj as FloatValue;
            if (floatVal != null) {
                writer.Write (floatVal.value);
                return;
            }

            var strVal = obj as StringValue;
            if (strVal != null) {
                writer.Write (strVal.value ?? "");
                return;
            }

            var listVal = obj as ListValue;
            if (listVal != null) {
                writer.WriteArrayStart ();
                if (listVal.value != null) {
                    foreach (var kv in listVal.value) {
                        writer.WriteObjectStart ();
                        writer.WriteProperty ("item", kv.Key.fullName);
                        writer.WriteProperty ("value", kv.Value);
                        writer.WriteObjectEnd ();
                    }
                }
                writer.WriteArrayEnd ();
                return;
            }

            var structRefVal = obj as StructRefValue;
            if (structRefVal != null) {
                writer.WriteObjectStart ();
                writer.WriteProperty ("ref", structRefVal.targetName ?? "");
                writer.WriteObjectEnd ();
                return;
            }

            var divertTargetVal = obj as DivertTargetValue;
            if (divertTargetVal != null) {
                writer.Write (divertTargetVal.value != null ? divertTargetVal.value.ToString () : "");
                return;
            }

            var nestedStruct = obj as StructValue;
            if (nestedStruct != null) {
                if (nestedStruct.value == null) {
                    writer.WriteNull ();
                    return;
                }
                // One level of nested field expansion; deeper nests as summaries.
                if (nestingDepth < 1) {
                    writer.WriteObjectStart ();
                    writer.WriteProperty ("kind", nestedStruct.value.isDynamic ? "dynamic" : "struct");
                    writer.WriteProperty ("typeName", nestedStruct.value.typeName ?? "");
                    writer.WritePropertyStart ("fields");
                    writer.WriteObjectStart ();
                    if (nestedStruct.value.storage != null) {
                        foreach (var kv in nestedStruct.value.storage) {
                            writer.WritePropertyStart (kv.Key);
                            WriteInspectValue (writer, kv.Value, nestingDepth + 1);
                            writer.WritePropertyEnd ();
                        }
                    }
                    writer.WriteObjectEnd ();
                    writer.WritePropertyEnd ();
                    writer.WriteObjectEnd ();
                } else {
                    writer.Write ((nestedStruct.value.isDynamic ? "dynamic(" : "struct(")
                        + nestedStruct.value.typeName + ")");
                }
                return;
            }

            writer.Write (obj.ToString ());
        }

        CommandLineInputResult ExecuteImmediateStatement(Parsed.Object parsedObj) {
            var result = new CommandLineInputResult ();

           // Variable assignment: create in Parsed.Story as well as the Runtime.Story
           // so that we don't get an error message during reference resolution
           if (parsedObj is Parsed.VariableAssignment) {
               var varAssign = (Parsed.VariableAssignment)parsedObj;
               if (varAssign.isNewTemporaryDeclaration) {
                   _parsedStory.TryAddNewVariableDeclaration (varAssign);
               }
           }

           parsedObj.parent = _parsedStory;
           var runtimeObj = parsedObj.runtimeObject;

           parsedObj.ResolveReferences (_parsedStory);

           if (!_parsedStory.hadError) {

               // Divert
               if (parsedObj is Parsed.Divert) {
                   var parsedDivert = parsedObj as Parsed.Divert;
                   result.divertedPath = parsedDivert.runtimeDivert.targetPath.ToString();
               }

               // Expression or variable assignment
               else if (parsedObj is Parsed.Expression || parsedObj is Parsed.VariableAssignment) {
                   var evalResult = _runtimeStory.EvaluateExpression ((Runtime.Container)runtimeObj);
                   if (evalResult != null) {
                       result.output = evalResult.ToString ();
                   }
               }
           } else {
               _parsedStory.ResetError ();
           }

          return result;
        }

        public void RetrieveDebugSourceForLatestContent ()
        {
            foreach (var outputObj in _runtimeStory.state.outputStream) {
                var textContent = outputObj as Runtime.StringValue;
                if (textContent != null) {
                    var range = new DebugSourceRange ();
                    range.length = textContent.value.Length;
                    range.debugMetadata = textContent.debugMetadata;
                    range.text = textContent.value;
                    _debugSourceRanges.Add (range);
                }
            }
        }

        Runtime.DebugMetadata DebugMetadataForContentAtOffset (int offset)
        {
            int currOffset = 0;

            Runtime.DebugMetadata lastValidMetadata = null;
            foreach (var range in _debugSourceRanges) {
                if (range.debugMetadata != null)
                    lastValidMetadata = range.debugMetadata;

                if (offset >= currOffset && offset < currOffset + range.length)
                    return lastValidMetadata;

                currOffset += range.length;
            }

            return null;
        }

        public struct DebugSourceRange
        {
            public int length;
            public Runtime.DebugMetadata debugMetadata;
            public string text;
        }

        // Need to wrap the error handler so that we know
        // when there was a critical error between parse and codegen stages
        void OnParseError (string message, ErrorType errorType)
        {
            if( errorType == ErrorType.Error )
                _hadParseError = true;
            
            if (_options.errorHandler != null)
                _options.errorHandler (message, errorType);
            else
                throw new System.Exception(message);
        }

        string _inputString;
        Options _options;


        InkParser _parser;
        Parsed.Story _parsedStory;
        Runtime.Story _runtimeStory;

        PluginManager _pluginManager;

        bool _hadParseError;

        List<DebugSourceRange> _debugSourceRanges = new List<DebugSourceRange> ();
    }
}
