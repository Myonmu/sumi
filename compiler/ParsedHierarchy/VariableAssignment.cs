using System.Collections.Generic;

namespace Ink.Parsed
{
    public class VariableAssignment : Parsed.Object
    {
        public string variableName
        {
            get { return variableIdentifier.name; }
        }
        public Identifier variableIdentifier { get; protected set; }
        public Expression expression { get; protected set; }
        public ListDefinition listDefinition { get; protected set; }

        public bool isGlobalDeclaration { get; set; }
        public bool isNewTemporaryDeclaration { get; set; }
        public bool isStructField { get; set; }
        public bool isRefVar { get; set; }
        /// <summary>When set, this variable/field holds a struct instance of this type.</summary>
        public string structTypeName { get; set; }

        public bool isDeclaration {
            get {
                return isGlobalDeclaration || isNewTemporaryDeclaration || isStructField;
            }
        }

        public VariableAssignment (Identifier identifier, Expression assignedExpression)
        {
            this.variableIdentifier = identifier;

            // Defensive programming in case parsing of assignedExpression failed
            if( assignedExpression )
                this.expression = AddContent(assignedExpression);
        }

        public VariableAssignment (Identifier identifier, ListDefinition listDef)
        {
            this.variableIdentifier = identifier;

            if (listDef) {
                this.listDefinition = AddContent (listDef);
                this.listDefinition.variableAssignment = this;
            }

            // List definitions are always global
            isGlobalDeclaration = true;
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            FlowBase newDeclScope = null;
            if (isGlobalDeclaration) {
                newDeclScope = story;
            } else if(isNewTemporaryDeclaration) {
                newDeclScope = ClosestFlowBase ();
            }
            // Struct fields are not registered as story/flow variables
            else if (isStructField) {
                return null;
            }

            if( newDeclScope )
                newDeclScope.TryAddNewVariableDeclaration (this);

            // Global declarations don't generate actual procedural
            // runtime objects, but instead add a global variable to the story itself.
            // The story then initialises them all in one go at the start of the game.
            // Struct fields are metadata only — defaults live on the type descriptor.
            if (isGlobalDeclaration || isStructField)
                return null;

            var container = new Runtime.Container ();

            // Reassignment to a global REFVAR: store reference identity, not a deep copy
            VariableAssignment existingDecl;
            if (!isNewTemporaryDeclaration
                && story != null
                && story.variableDeclarations.TryGetValue (variableName, out existingDecl)
                && existingDecl != null
                && existingDecl.isRefVar) {
                container.AddContent (Runtime.ControlCommand.EvalStart ());
                GenerateRefVarRhs (container);
                container.AddContent (Runtime.ControlCommand.EvalEnd ());
                _runtimeAssignment = new Runtime.VariableAssignment (variableName, false);
                container.AddContent (_runtimeAssignment);
                return container;
            }

            // Typed temp without initializer → default instance
            if (expression == null && structTypeName != null) {
                container.AddContent (new Runtime.StructCreateDefault (structTypeName));
            }
            // The expression's runtimeObject is actually another nested container
            else if( expression != null )
                container.AddContent (expression.runtimeObject);
            else if( listDefinition != null )
                container.AddContent (listDefinition.runtimeObject);

            _runtimeAssignment = new Runtime.VariableAssignment(variableName, isNewTemporaryDeclaration);
            container.AddContent (_runtimeAssignment);

            return container;
        }

        void GenerateRefVarRhs (Runtime.Container container)
        {
            var rhsVar = expression as VariableReference;
            if (rhsVar != null && rhsVar.path != null && rhsVar.path.Count == 1 && rhsVar.name != "none") {
                container.AddContent (new Runtime.StructRefValue (rhsVar.name));
            } else if (expression is NoneLiteral || (rhsVar != null && rhsVar.name == "none")) {
                container.AddContent (new Runtime.StructRefValue (null));
            } else if (expression != null) {
                expression.GenerateIntoContainer (container);
            } else {
                container.AddContent (new Runtime.StructRefValue (null));
            }
        }

        public override void ResolveReferences (Story context)
        {
            base.ResolveReferences (context);

            // Struct fields are members, not story variables
            if (isStructField)
                return;

            // List definitions are checked for conflicts separately
            if( this.isDeclaration && listDefinition == null )
                context.CheckForNamingCollisions (this, variableIdentifier, this.isGlobalDeclaration ? Story.SymbolType.Var : Story.SymbolType.Temp);

            // Initial VAR x = [intialValue] declaration, not re-assignment
            if (this.isGlobalDeclaration) {
                if (isRefVar && structTypeName == null)
                    Error ("REFVAR '" + variableName + "' requires a struct type, e.g. REFVAR " + variableName + ": TypeName = none");

                var variableReference = expression as VariableReference;
                if (variableReference && !variableReference.isConstantReference && !variableReference.isListItemReference && !variableReference.isStructReference) {
                    // Struct-typed init may refer to type default or another instance
                    if (structTypeName == null)
                        Error ("global variable assignments cannot refer to other variables, only literal values, constants and list items");
                }
            }

            if (!this.isNewTemporaryDeclaration) {
                var resolvedVarAssignment = context.ResolveVariableWithName(this.variableName, fromNode: this);
                if (!resolvedVarAssignment.found) {
                    if (story.constants.ContainsKey (variableName)) {
                        Error ("Can't re-assign to a constant (do you need to use VAR when declaring '" + this.variableName + "'?)", this);
                    } else {
                        Error ("Variable could not be found to assign to: '" + this.variableName + "'", this);
                    }
                }

                // A runtime assignment may not have been generated if it's the initial global declaration,
                // since these are hoisted out and handled specially in Story.ExportRuntime.
                if( _runtimeAssignment != null )
                    _runtimeAssignment.isGlobal = resolvedVarAssignment.isGlobal;
            }
        }


        public override string typeName {
            get {
                if (isNewTemporaryDeclaration) return "temp";
                else if (isGlobalDeclaration) return isRefVar ? "REFVAR" : "VAR";
                else if (isStructField) return isRefVar ? "REFVAR" : "VAR";
                else return "variable assignment";
            }
        }

        Runtime.VariableAssignment _runtimeAssignment;
    }
}

