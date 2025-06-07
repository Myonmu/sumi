# Sumi ~ an ink fork with extra features

**Sumi** is an ink fork that adds advanced features to ink. These features include:

1. Ability to define function stitches. The original ink forbids using stitches as functions. In Sumi, you could call a stitch function via `knot.stitchFunction(...)`, similar to how member functions are called in other languages. (note: there's no such thing as stitch in a stitch, so the maximum depth you can have is 1)

2. Use preprocessor directives (conditional compilation).

3. Struct definitions (WIP)

Usually these features are not required to make a game. We suggest that you get familiar with vanilla ink first then see if Sumi has features that seem interesting to you.

## The Sumi Toolchain

- [Sumy](https://github.com/Myonmu/sumy) : Inky but sumi compatible 

## Preprocessor Directives

The syntax is similar to C# preprocessors:

```ink
#IF INKY
This will only be shown in Inky.
// you may use logical expressions as well
#ELIF ( UNITY && STEAM_BUILD ) || UNREAL
This will only be shown in Unity Stem Build or in Unreal.
#ELSE
This will be shown if not in Inky nor Unity.
#ENDIF
```
This is helpful if you are including libraries that might have different paths when editing with Inky and in runtime, or debug content.
```ink
#IF INKY
INCLUDE ../InkLibs/Library.ink
#ELSE
INCLUDE Library.ink
#ENDIF
```
`InkPreprocessor` will resolve these directives after comment removal, and before compiling the main content. To keep line numbers intact, the preprocessor *replaces* masked out branches with line breaks, so you would end up with more empty line than you might expect. Hence, always check if a line has printable content before showing it to the player. 

> Caveat: The vanilla ink toolchain does not support preprocessor directives, so you will need to use a custom toolchain built from Sumi.

To specify preprocessor defines passed to the compiler, you could pass them via the constructor of the compiler:

```csharp
var compiler = new Compiler (inkSource, new Compiler.Options {
    preprocessorSymbols = new()
    {
        "SOME_DEFINE", "SOME_OTHER_DEFINE"
    }
});

var story = compiler.Compile ();
```

Currently, if you make a mistake in preprocessor directives and result in a parse failure, the whole block will not be evaluated and all the directives will be treated as regular ink content (which will become tags).
