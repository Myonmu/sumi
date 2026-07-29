# Sumi ~ an ink fork with extra features

**Sumi** is an ink fork that adds advanced features to ink. These features include:

1. Ability to define function stitches. The original ink forbids using stitches as functions. In Sumi, you could call a stitch function via `knot.stitchFunction(...)`, similar to how member functions are called in other languages. (note: there's no such thing as stitch in a stitch, so the maximum depth you can have is 1)

2. Use preprocessor directives (conditional compilation).

3. [Structs and dynamics](#structs-and-dynamics) — typed entities with fields, methods, inheritance, and optional open slots.

Usually these features are not required to make a game. We suggest that you get familiar with vanilla ink first then see if Sumi has features that seem interesting to you.

## The Sumi Toolchain

- [Sumy](https://github.com/Myonmu/sumy) : Inky but sumi compatible
- [Sumi Unity Integration](https://github.com/Myonmu/sumi-unity-integration)

## Structs and Dynamics

**Structs** are *things* — characters, items, factions — that need several traits and shared behaviour in one place. Imagine in vanilla ink you want to track if you have met a certain character, you would end up with:

```ink
VAR met_claudia = false
VAR met_johnson = false
VAR met_estelle = false

Met Estelle ? {met_estelle}
```

With structs:

```ink
=== struct Character ===
VAR met = false
===
VAR Claudia : Character
VAR Johnson : Character
VAR Estelle : Character

Met Estelle ? {Estelle.met}
```

So structs are a nice way to organise your content — though they have much more potential than just a handy folder. If you are familiar with Lua tables, you may find them somewhat familiar.

This is only a crash course. Full rules, `REFVAR`, narrative stitches, inheritance details, and game-side access are in [*Writing with Ink* — Part 6: Structs](Documentation/WritingWithInk.md#part-6-structs).

### Defining and using a struct

Struct definition looks like a knot, but with the `struct` keyword. Put fields (`VAR` / `REFVAR`) first, then methods and stitches. Close the body with `===` (or another knot/struct title) before top-level globals — otherwise a following `VAR` is treated as another field.

```ink
=== struct Character ===
VAR name = "anonymous"
VAR nerves = 1
= function Introduce() =
My name is {self.name}.
~ return
===

VAR Oswald: Character
~ Oswald.name = "Flinn"
~ Oswald.Introduce()
```

What happens here: declaring the struct also creates a **default instance** (the bare name `Character` refers to it). `VAR Oswald: Character` makes a new instance by **deep-copying** those defaults. Inside methods, the instance arrives as `self` — bare names are still ordinary globals, so you write `self.name`, not just `name`.

A few other habits worth knowing early:

- Methods are call-only (`~ Oswald.Introduce()`). Narrative stitches (no `function` keyword) are divert/tunnel targets (`-> scene.intro ->`). You cannot `-> Character` like a knot.
- Subtypes use a colon: `=== struct Oswald: Character ===`. Overrides can call `base.Method()`, and calls are virtual when you hold a subtype in a base-typed variable.

### Dynamics (open slots)

Structs are closed boxes: every instance has the same fields and methods you authored. Sometimes you want a bag you can stuff new things into as the story unfolds. That is a **dynamic** — it looks like a struct, but the live instance can gain and lose slots at runtime.

```ink
=== dynamic Bag ===
VAR score = 0
= function Bonus() =
~ return self.score + 1
===

VAR bag: Bag
~ bag.extra = 5
{bag has extra}
~ bag.extra = []
{bag hasnt extra}
```

Assigning a name that was never in the definition *creates* that slot. `[]` removes it (empty `()` is still an empty list, not a delete). Missing reads/calls are runtime errors — ask with `has` / `hasnt` first. You can also start blank with `VAR scratch: dynamic` and grow it from nothing.

A dynamic may inherit a struct or another dynamic; a struct **cannot** inherit a dynamic. Kind checks are `{x is dynamic}` / `{x is struct}`.

For inheritance, references (`REFVAR`), stitches, evaluated path components, and everything else, see [Part 6 in Writing with Ink](Documentation/WritingWithInk.md#part-6-structs) (including [Dynamics](Documentation/WritingWithInk.md#6-dynamics-open-slots)).

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
