# Structs in ink (OOP proposal)

Structs add lightweight OOP to ink: typed entities with fields, methods, inheritance, and (optional) references — mapped onto existing ink concepts (`VAR` / `temp`, function stitches, paths, save JSON).

This document is the design source of truth. Implementation may land in phases; the language surface described here is the full vision.

---

## Why structs?

Structs group state and behavior for story entities (characters, items, factions) without scattering parallel `VAR`s and free functions.

```ink
=== struct Character ===
VAR name = "anonymous"
= function ReactShocked() =
...
~ return
= function ReactFurious() =
The heck you are doing?
~ return

=== struct Oswald: Character ===
VAR name = "Flinn Oswald"
= function ReactShocked() =
Oh my goodness me!
~ return

=== story ===
~ Oswald.ReactShocked()
```

---

## Definition

Struct definitions reuse knot-shaped syntax. The keyword `struct` is reserved.

- **Fields** — declared with `VAR` or `REFVAR` inside the struct body.
- **Methods** — declared as function stitches (`= function Name(...) =`).
- **Externals** — `EXTERNAL` stubs are allowed the same way as for ordinary functions; a matching function stitch may provide a fallback body.

```ink
=== struct SomeStruct ===

VAR numberField = 1
VAR strField = "content"

= function Func1(arg1) =
{arg1}
~ return

EXTERNAL Func2(arg1)
= function Func2(arg1) =
~ return

```

Rules:

- A struct name must not collide with a knot, list, or global variable of the same name.
- Member names must be unique within the struct’s flattened member set (after inheritance).
- The identifier `static` is reserved and must not be used as a field or method name.
- The identifier `base` is reserved inside method bodies (see [Calling base implementations](#calling-base-implementations)).
- Structs are not narrative entry points: you cannot `-> SomeStruct` as a knot divert. Methods are functions, not stitches you weave into.

---

## Default (static) instance

Each struct type has an implicit **default instance** (Unreal-style default object).

```ink
=== struct Character ===
VAR field1 = 1
VAR field2 = 2
= function ReactFurious() =
The heck you are doing?
~ return
```

Conceptual equivalent:

```csharp
class Character {
    public int field1 = 1;
    public int field2 = 2;
    public static Character @static = new Character(); // default instance
    public virtual void ReactFurious() { /* prints line */ }
}
```

Outside struct definitions, an unqualified type name used as a value refers to that type’s default instance:

- `Character` → the default instance of `Character`
- `Character.ReactFurious()` → call on the default instance
- `Character.static.field1` → same default instance, explicit path form (see [Paths](#paths-to-fields-and-methods))

Field initializers in the struct body define the default instance’s initial values. Subtypes that redeclare a field override the default value for *their* default instance and for new instances of that subtype.

---

## Instantiation

Instances are either **global** (path-addressable, persisted) or **local/temporary** (scoped, not globally path-addressable).

### Typed declaration syntax

```ink
// Global: copy defaults from Character.static
VAR Oswald: Character

// Global: copy from an existing instance (deep copy of embedded fields; see value/ref rules)
VAR Oswald: Character = Character
VAR Clone: Character = Oswald

// Local / temp: same forms, not saved, not addressable as Type.InstanceName
~ temp guest: Character
~ temp guest: Character = Oswald
```

Notes:

- The type annotation (`: Character`) is required for struct-typed variables.
- The initializer is optional; when omitted, the instance is a deep copy of the type’s default instance.
- The variable’s **name** is the instance name used in global paths (`Character.Oswald`).
- Assigning a struct value (`~ Oswald = Clone`) deep-copies embedded fields and copies `REFVAR` slots by reference identity (see [Value vs reference](#value-vs-reference-fields)).

### Function parameters

```ink
// By value: callee receives a deep copy
=== function Greet(who: Character) ===
{who.name} waves.
~ return

// By reference: callee mutates the caller’s instance (same as ink `ref` today)
=== function Rename(ref who: Character, newName) ===
~ who.name = newName
~ return
```

`ref` on a struct parameter passes the instance by reference (no copy). It may refer to a global or a local instance still in scope.

### What may be instantiated

- Any concrete struct type may be instantiated.
- There is no separate `abstract` keyword in v1; a struct used only as a mixin/interface is a normal struct (often with methods and few fields).

---

## Polymorphism and inheritance

```ink
=== struct JohnPaul: Character ===
VAR field1 = 2
= function ReactFurious() =
Very well then, I will take that as a personal threat.
~ return
```

### Single inheritance

- Redeclared fields override the inherited **default value** (and occupy the same field slot).
- Fields not redeclared keep the closest ancestor’s default value.
- Redeclared methods override the inherited implementation in the vtable.
- Methods not redeclared inherit the closest ancestor’s implementation.

### Multiple inheritance

```ink
=== struct ISuspect ===
= function IsSuspect() =
~ return true

=== struct JohnPaul: Character, ISuspect ===
VAR field1 = 2
= function ReactFurious() =
Very well then, I will take that as a personal threat.
~ return
```

**Linearization / vtable construction** (left-to-right, declaration order):

1. Start with an empty member map.
2. For each base in order `Base1, Base2, ...`, import that base’s flattened members; **skip** any slot already filled (first base wins on conflicts between bases).
3. Apply the child’s own declarations, which **always override** inherited slots.

Diamond inheritance is covered by the same rule: the first base in declaration order that contributes a given field/method wins unless the child overrides it.

Cycles in the base graph are a compile error. Unknown base names are a compile error.

### Calling base implementations

Inside a method body, `base` resolves to the inherited implementation of the **current method** according to the child’s linearization (the slot that would have been used before the child’s override).

```ink
=== struct JohnPaul: Character ===
VAR field1 = 2
= function ReactFurious() =
~ base.ReactFurious()
Very well then, I will take that as a personal threat.
~ return
```

Rules:

- `base.MethodName(...)` is only valid inside a method that overrides `MethodName`.
- `base` is not a first-class value; it cannot be stored or passed.
- If multiple bases contributed overloads of different methods, `base` still means “the inherited slot for this method,” not “a particular parent type.”
- Disambiguating a specific parent (`base<Character>.Foo`) is **out of scope** for v1; authors should override carefully or use composition if they need that.

---

## Paths to fields and methods

### Global instances

Globally declared struct variables are addressable with a unique path:

```text
[TypeName].[InstanceName].[member]
[TypeName].static.[member]
```

Examples:

| Path | Meaning |
|------|---------|
| `Character.static.field1` | `field1` on `Character`’s default instance |
| `Character.field1` | Same as `Character.static.field1` when `Character` denotes the default instance |
| `Character.Oswald.field1` | `field1` on global `VAR Oswald: Character` (or subtype) |
| `Character.Oswald.ReactFurious()` | Virtual call of `ReactFurious` on Oswald |
| `Oswald.field1` | Also valid: the variable name alone refers to the instance |

The compiler forbids naming a field or method `static`.

When the static type of a variable is a base type but the instance was created as a subtype, **method** lookup uses the instance’s runtime type vtable. **Field** slots are fixed by the declared struct layout of the runtime type (subtype fields exist on the instance even if viewed through a base-typed `REFVAR`).

### Local / temporary instances

Locals are **not** registered under `TypeName.InstanceName`. They are only reachable through the local binding:

```ink
~ temp guest: Character = Oswald
~ guest.ReactFurious()
~ guest.name = "Visitor"
```

Attempting to form `Character.guest` for a temp is a compile error (no such global instance).

---

## VTables and type layout

Each struct type has a compile-time **type descriptor** with two parallel tables built by the same linearization pass:

| Table | Purpose | Used by |
|-------|---------|---------|
| **Field layout** | Ordered map of field name → storage slot metadata (`kind`: `var` / `refvar`, optional nested type) | Field get/set, copy, serialize |
| **Method vtable** | Map of method name → function container path | Virtual calls only |

Fields never go through the method vtable. Methods never go through the field layout (except that the callee body reads/writes fields via `self`).

### Type descriptor (compile / story JSON)

Conceptual runtime shape (mirrors `structDefs` in JSON):

```text
TypeDesc {
  name: string
  bases: TypeDesc[]                    // declaration order
  fields: OrderedMap<string, FieldSlot> // flattened layout
  vtable: Map<string, DivertPath>       // flattened methods
  // optional: methodSlotIndex for dense int dispatch
}

FieldSlot {
  name: string
  kind: Var | RefVar
  type: TypeDesc | Scalar              // struct type if nested / ref target
  default: Value                       // after inheritance defaults applied
}
```

Dense optional optimization: assign each method name a stable **slot index** shared across a hierarchy (see [Slot indices](#slot-indices-optional-optimization)). Name-keyed maps are the normative model; indices are an implementation detail.

### Building layout + vtable (inheritance)

Same left-to-right algorithm as [Multiple inheritance](#multiple-inheritance), applied to **both** tables:

```text
function BuildType(child, bases[]):
  fields = {}
  vtable = {}

  for base in bases:                    // declaration order
    for (name, slot) in base.fields:
      if name not in fields:
        fields[name] = copy(slot)       // first base wins
    for (name, path) in base.vtable:
      if name not in vtable:
        vtable[name] = path             // first base wins

  for fieldDecl in child.ownFields:
    fields[fieldDecl.name] = fieldDecl  // child always overrides
  for methodDecl in child.ownMethods:
    vtable[methodDecl.name] = path(methodDecl)  // child always overrides

  return TypeDesc(child.name, bases, fields, vtable)
```

Consequences:

- A child that **redeclares a field** replaces default value / kind / nested type in the layout slot; storage identity of that name is still “one slot per name” on the instance.
- A child that **omits a field** keeps the inherited default in the layout; every instance of the child still **allocates** that slot.
- A child that **overrides a method** replaces only the vtable path; field layout is unchanged.
- A child that **adds** fields/methods appends new names; base instances do not grow those slots.
- Diamond / multi-base conflicts: first base in `bases` that contributes a name wins unless the child overrides.

Example:

```ink
=== struct Character ===
VAR name = "anonymous"
VAR nerves = 1
= function ReactFurious() =
The heck you are doing?
~ return

=== struct ISuspect ===
= function IsSuspect() =
~ return true

=== struct Oswald: Character, ISuspect ===
VAR name = "Flinn Oswald"
= function ReactFurious() =
~ base.ReactFurious()
...
~ return
```

After build:

```text
Character.fields  = { name: Var(str,"anonymous"), nerves: Var(int,1) }
Character.vtable  = { ReactFurious → Character.static.ReactFurious }

ISuspect.fields   = { }
ISuspect.vtable   = { IsSuspect → ISuspect.static.IsSuspect }

Oswald.fields     = { name: Var(str,"Flinn Oswald"), nerves: Var(int,1) }   // nerves from Character
Oswald.vtable     = {
                      ReactFurious → Oswald.static.ReactFurious,           // override
                      IsSuspect    → ISuspect.static.IsSuspect             // inherited
                    }
```

`base.ReactFurious` inside Oswald’s override is **not** stored in Oswald’s vtable. The compiler records a side table on the override:

```text
Oswald.baseCall["ReactFurious"] → Character.static.ReactFurious
```

used only when lowering `base.ReactFurious(...)`.

### Instance storage

Every live instance (global, default/`static`, temp, embedded value) carries:

```text
StructObject {
  type: TypeDesc          // concrete runtime type (vtable + layout owner)
  storage: Map<string, Value>   // or array parallel to type.fields order
}
```

- `type` is set at construction from the declared instantiation type (`VAR x: Oswald` → `Oswald`), never from a base-typed `REFVAR` view.
- `storage` has **exactly** the keys in `type.fields` (flattened). No per-base subobjects unless a field is itself an embedded struct value.
- Default instance `Type.static` is a normal `StructObject` with `type = Type` and storage seeded from `Type.fields[*].default`.

Embedded struct fields store a nested `StructObject` value. `REFVAR` fields store a target id / global name / `none` — not a nested object.

### Field get / set (no vtable)

Field operations use **concrete layout** of `instance.type`, keyed by field name (or layout index).

```text
function GetField(instance, fieldName):
  slot = instance.type.fields[fieldName]
  if slot == null: error "no such field on " + instance.type.name
  return instance.storage[fieldName]

function SetField(instance, fieldName, value):
  slot = instance.type.fields[fieldName]
  if slot == null: error
  if slot.kind == RefVar:
    instance.storage[fieldName] = asReference(value)   // rebind
  else if slot.type is struct:
    instance.storage[fieldName] = deepCopy(value)      // embed
  else:
    instance.storage[fieldName] = value
```

Important interactions:

| Situation | Behavior |
|-----------|----------|
| Get/set `name` on `Oswald` instance | Uses `Oswald.fields` / `Oswald` storage |
| `REFVAR scout: Character = Oswald` then `scout.name` | Resolve ref → `Oswald` instance; get/set on **Oswald’s** storage via **Oswald’s** layout. `Character`’s layout is used only for **compile-time** checks that `name` exists on the static type `Character`. |
| Static type lacks a subtype field | `scout.oswaldOnlyField` is a **compile error** even if runtime target is `Oswald`. To access subtype fields, the static type must be the subtype (or a temp/`VAR` of that type). |
| View through base does not strip storage | Subtype-only fields remain in storage; they are just invisible through a base-typed expression. |
| Copy / assign instance | Deep-copy all `Var` slots per source concrete layout; copy `RefVar` identities |

Compile-time name resolution for `expr.field`:

1. Let `T` = static type of `expr` (after ref dereference for `REFVAR`).
2. Require `field` ∈ flattened `T.fields` (or inherited — same table).
3. Emit get/set against the **runtime** instance pointer; do not bake a base-only offset that would miss subtype storage. Practical approach: always access by **field name** (or by a layout index taken from the **concrete** type at runtime after a name→index lookup on `instance.type`).

Recommended runtime implementation: **name-keyed storage** (simple, safe across MI). Optional: dense arrays where each `TypeDesc` maps field name → index into that type’s array; get/set does `index = instance.type.fieldIndex[name]; instance.storage[index]`.

Fields are **non-virtual**: there is no “field vtable.” Overriding a field in a child only changes the child’s default and layout metadata for that name; it does not redirect get/set through a parent thunk.

### Method calling (uses vtable)

Virtual call lowering for `recv.Method(args...)`:

```text
function VirtualCall(recvLValue, methodName, args):
  instance = resolveLValue(recvLValue)          // follow REFVAR if needed
  if instance == none: runtime error

  path = instance.type.vtable[methodName]       // concrete type only
  if path == null: error                        // should be impossible if typechecked

  pushVariablePointer(recvLValueStorage)        // ref self → concrete instance storage
  pushEach(args)
  divertFunction(path)                          // see Calling convention
```

Compile-time check uses the **static** type `S` of `recv`: `methodName` must exist in `S.vtable` (flattened). Codegen still emits a **runtime** vtable lookup on `instance.type`, so a `Character`-typed `REFVAR` to an `Oswald` calls `Oswald.vtable["ReactFurious"]`, not `Character`’s.

```text
                    static type check
                    ─────────────────
scout: REFVAR Character = Oswald
scout.ReactFurious()
       │
       │  compile: Character.vtable has ReactFurious? yes
       ▼
resolve scout → Oswald instance
       │
       │  runtime dispatch
       ▼
Oswald.vtable["ReactFurious"] → Oswald.static.ReactFurious
```

`base.Method` bypasses the instance vtable:

```text
function BaseCall(selfPtr, ownerType, methodName, args):
  path = ownerType.baseCall[methodName]   // compile-time constant in the callee body
  pushVariablePointer(selfPtr)
  pushEach(args)
  divertFunction(path)
```

Even if `self`’s concrete type is a further subtype, `base` inside `Oswald.ReactFurious` always jumps to `Character.static.ReactFurious`, never back into `Oswald`’s vtable slot.

### How the three interact

```text
┌─────────────────────────────────────────────────────────┐
│                     TypeDesc (per type)                 │
│  fields[] ──────────────────────┐                       │
│  vtable{} ──────────────┐       │                       │
└─────────────────────────┼───────┼───────────────────────┘
                          │       │
          method call     │       │  field get/set
                          ▼       ▼
                   divert path   storage[name]
                          │       │
                          ▼       ▼
                     function    StructObject
                     body uses   (concrete type id
                     self.* ─────► same storage)
```

| Operation | Consults vtable? | Consults field layout? | Type used |
|-----------|------------------|------------------------|-----------|
| `x.foo` get/set | No | Yes | Concrete `x.type` at runtime; membership checked with static type at compile time |
| `x.Method()` | Yes | No (until body runs) | Static type for existence check; **concrete** `x.type.vtable` for dispatch |
| `base.Method()` | No (side `baseCall` map) | No | Override’s owning type (compile-time) |
| `~ x = y` copy | No | Yes (both concrete layouts) | Source concrete type drives which slots are copied |
| Inheritance build | Builds vtable | Builds layout | Shared linearization order |
| Save / load | Persist/restore type name; rebind vtable by `^t` | Persist `storage` | Concrete type |

### Slot indices (optional optimization)

Normative semantics are name-keyed. An implementation may additionally assign:

- `methodIndex["ReactFurious"] = 0` shared so every type’s vtable is a dense array of divert targets.
- For MI, the global method-name → index space is story-wide (or per connected hierarchy) so `instance.type.vtableArray[i]` is O(1).

Field indices are **per-type** (different types may pack fields differently) because MI field sets are not a single linear C++-style subobject layout. Do not assume `Character`’s index for `nerves` equals `Oswald`’s without looking up through `instance.type`.

### Default instances

`Type.static` uses `Type`’s descriptor like any other instance: same field layout, same vtable. Calls on the bare type name (`~ Character.ReactFurious()`) are virtual calls on that default instance.

---

## Value vs reference fields

### Embedded fields (`VAR` of struct type)

A field declared as a struct-typed `VAR` is **embedded** (value semantics):

```ink
=== struct Inventory ===
VAR weapon: Item = Item
```

- Storage is inline in the outer instance.
- Assignment **deep-copies** embedded fields.
- Nested embeds copy recursively.
- `REFVAR` slots inside the copied value are copied as reference identities (the ref still points at the same target), not deep-cloned targets.

### Reference fields (`REFVAR`)

`REFVAR` stores a reference to an existing instance instead of embedding a copy:

```ink
=== struct Party ===
VAR leader: Character = Character      // embedded copy of defaults
REFVAR scout: Character = Oswald       // alias of global Oswald
REFVAR backup: Character = none        // empty reference
```

Rules:

- `REFVAR` targets must be struct-typed.
- A `REFVAR` may be initialized to:
  - a global instance (including a type’s default instance),
  - `none` (empty reference),
  - or, in a local initializer context, another in-scope instance (global or local).
- Reading members through a `REFVAR` that is `none` is a runtime error.
- Assigning to a `REFVAR` (`~ party.scout = Flinn`) rebinds the reference; it does not copy fields.
- Assigning through a `REFVAR` (`~ party.scout.name = "x"`) mutates the target instance.
- `REFVAR` is only for **fields**. Ordinary variables use `VAR` / `temp` plus optional `ref` on parameters; there is no `REFVAR` at global/temp declaration sites.

### Mixing with ink `ref`

| Mechanism | Role |
|-----------|------|
| `ref` parameter | Existing ink: pass variable storage by reference for the call |
| Struct-typed `VAR` / `temp` | Owns an instance value (deep copy on assign) |
| `REFVAR` field | Stored alias inside a struct instance |

---

## Methods as functions (content and control flow)

Member functions are ordinary ink **functions**:

- They may print text, evaluate logic, and `~ return` / `~ return value`.
- They are invoked like functions: `~ instance.Method(args)` or inline `{instance.Method()}` where a value is expected.
- They participate in the evaluation stack and call stack the same way as `=== function ... ===`.
- They are **not** knots: `-> instance.Method` is invalid. Use a call, not a divert.
- Choices and nested stitches inside methods follow the same restrictions as today’s functions (no gathering points / choices that escape function semantics — same compiler rules as `=== function`).

Calling convention details (receiver, stack order, `base`, externals) are specified in [Calling convention](#calling-convention).

---

## Calling convention

Struct method calls reuse ink’s existing function-call machinery (evaluation stack + call-stack push/pop). The only additions are an implicit receiver parameter and virtual dispatch.

### Author-facing signature vs runtime signature

```ink
= function ReactFurious(intensity) =
{self.name} scowls ({intensity}).
~ return
```

| Layer | Parameters |
|-------|------------|
| Author writes | `(intensity)` — receiver is not listed |
| Runtime / codegen | `(ref self, intensity)` — `self` is prepended |

- `self` is a reserved name inside method bodies and must not appear in the author parameter list.
- Assigning to `self` itself (`~ self = other`) is a compile error; mutate fields (`~ self.name = "..."`) instead.
- **Python-style qualification:** bare names always resolve in the normal ink outer scope (globals, temps, parameters, knots/functions). Struct fields and methods are only reached through an explicit receiver — `self.field`, `self.Method(...)`, or another instance path. There is no unqualified `name` → `self.name` or `Foo()` → `self.Foo()` sugar.

### Receiver (`self`): always by reference

The receiver expression must be an **lvalue** that denotes instance storage:

- a struct-typed global / temp variable: `Oswald`, `guest`
- a path to an embedded field: `party.leader`
- a path through a `REFVAR` (binds to the **target** instance, not the ref slot): `party.scout`

The compiler emits a **variable pointer** (same mechanism as ink `ref` arguments) for `self`. Mutations through `self` affect the caller’s instance.

Illegal receivers (compile error):

- rvalue expressions with no storage (e.g. a hypothetical constructor expression used only as a temporary without binding)
- `none`
- non-struct values

### Evaluation stack layout

Same push/pop discipline as ordinary ink functions:

1. Enter expression / function-call evaluation context (as today).
2. Push arguments **left to right**:
   1. receiver pointer → becomes `self`
   2. each explicit argument in source order (`ref` args push a variable pointer; value args push an evaluated value; struct-typed value args are **deep-copied** before the push)
3. Resolve the callee:
   - **Virtual call** `recv.Method(...)`: read concrete type id from the receiver instance; index that type’s vtable slot for `Method`; divert to that function container.
   - **Base call** `base.Method(...)`: divert to the **compile-time** inherited function path for this override (no vtable lookup; see below).
4. Push call stack (`PushPopType.Function`), divert into the method container.
5. On entry, pop parameters **right to left** into temps (identical to `FlowBase.GenerateArgumentVariableAssignments`): last explicit arg first, then …, then `self`.
6. On `~ return` / `~ return expr`:
   - with a value: push it on the evaluation stack (struct returns are deep copies unless the return expression is already a pointer — v1: return-by-value only for structs).
   - without a value: push `void` as today.
7. Pop call stack; caller keeps or discards the return value (`~ method()` pops; `{method()}` consumes as expression).

Example — `~ party.scout.ReactFurious(2)` where `ReactFurious(intensity)` is declared on `Character` and overridden on `Oswald`:

```text
eval stack (bottom → top) before divert:
  [ ptr(Oswald), 2 ]

vtable(Oswald)["ReactFurious"] → container Oswald.static.ReactFurious

on entry, after pops:
  self      = ptr(Oswald)   // ref
  intensity = 2
```

### `base.Method(...)`

```ink
= function ReactShocked() =
~ base.ReactShocked()
Oh my goodness me!
~ return
```

- Valid only inside a method that **overrides** `Method`.
- Callee path is fixed at compile time (the inherited slot from linearization), **not** looked up on the runtime vtable (avoids infinite recursion into the override).
- Stack layout is identical to a normal method call: push `self` (same pointer), then explicit args, then divert to the base container.
- `base` is not a value; `base` alone or storing `base` is illegal.

### Passing struct instances as explicit arguments

| Parameter form | Call-site behavior |
|----------------|--------------------|
| `who: Character` | Deep-copy the argument instance onto the stack (by value). |
| `ref who: Character` | Push a variable pointer to the argument lvalue (by reference). |

This matches non-method functions with struct-typed parameters. The implicit `self` is always `ref`, never a copy.

### `EXTERNAL` methods

`EXTERNAL` declarations on structs do **not** auto-inject `self` into the game-facing callback. The external sees only the arguments written at the call site / in the `EXTERNAL` signature.

To expose the receiver to native code, pass it explicitly:

```ink
EXTERNAL NativeReact(who, intensity)
= function ReactFurious(intensity) =
~ NativeReact(self, intensity)
~ return
```

Game binder arity must match the `EXTERNAL` parameter list. Virtual dispatch still selects which ink fallback body runs when the external is unbound; the external name used for binding is the flattened method name from `structDefs` (see JSON).

### Calls from the game / runtime API

Recommended API shape (mirrors `EvaluateFunction`):

```csharp
// Virtual call: pass the instance (global name, StructObject, or variable pointer),
// then explicit args. Runtime pushes ref self + args and uses the instance's vtable.
object EvaluateMethod(object instance, string methodName, params object[] arguments);

// Optional: non-virtual call to a specific type's implementation (base-call analogue)
object EvaluateMethodNonVirtual(object instance, string typeName, string methodName, params object[] arguments);
```

`EvaluateFunction("Oswald.ReactShocked")` without a receiver is **invalid** for instance methods. Default-instance methods may be invoked as `EvaluateMethod(story.variablesState["Character"], "ReactFurious")` or a thin helper `EvaluateMethodOnType("Character", "ReactFurious", ...)`.

### Summary diagram

```text
ink:   ~ recv.Foo(a, b)
           │
           ▼
push:  ptr(recv), eval(a), eval(b)
           │
           ▼
dispatch: vtable[runtimeType(recv)]["Foo"]
           │
           ▼
enter: pop b → temp b; pop a → temp a; pop ptr → ref self
           │
           ▼
body:  uses self / fields / params; may print; ~ return [value]
           │
           ▼
leave: pop callstack; return value on eval stack (or void)
```

---

## Compiler checks (summary)

- `struct`, `REFVAR`, and in-method `self` / `base` are reserved as specified.
- Struct method names may collide with top-level `=== function` / knot names; `Foo()` is the global, `self.Foo()` is the method.
- No member named `static`.
- Inheritance acyclic; bases must exist.
- Typed struct variables require a known struct type.
- Global instance names unique among globals; path `Type.Instance` unique.
- `base.Method` only in overrides of `Method`.
- `REFVAR` initializers must type-check against the declared struct type (subtype allowed — reference is to a compatible instance).
- Forbidding divert-into-method and knot-named-like-struct collisions.

---

## JSON format

Structs extend the compiled story JSON and the save-state JSON.

### Compiled story (definitions)

Emitted alongside list defs / root containers. Illustrative shape:

```json
{
  "structDefs": {
    "Character": {
      "bases": [],
      "fields": [
        { "name": "name", "kind": "var", "default": "anonymous" },
        { "name": "field1", "kind": "var", "default": 1 }
      ],
      "methods": {
        "ReactShocked": "Character.static.ReactShocked",
        "ReactFurious": "Character.static.ReactFurious"
      }
    },
    "Oswald": {
      "bases": ["Character"],
      "fields": [
        { "name": "name", "kind": "var", "default": "Flinn Oswald" },
        { "name": "field1", "kind": "var", "default": 1 }
      ],
      "methods": {
        "ReactShocked": "Oswald.static.ReactShocked",
        "ReactFurious": "Character.static.ReactFurious"
      }
    },
    "Party": {
      "bases": [],
      "fields": [
        { "name": "leader", "kind": "var", "type": "Character" },
        { "name": "scout", "kind": "refvar", "type": "Character", "default": null }
      ],
      "methods": {}
    }
  }
}
```

Method values are paths to runtime containers (function bodies) after codegen. The vtable for a type is exactly the `methods` map after linearization.

Default instances are materialized as ordinary global instances named `static` under each type (`Character.static`, …) and also seeded into variables state at startup.

### Runtime instance encoding (variables / save state)

Global struct-typed variables serialize as typed objects, not as flat scalars:

```json
{
  "variables": {
    "Oswald": {
      "^t": "Oswald",
      "name": "Flinn Oswald",
      "field1": 1
    },
    "party": {
      "^t": "Party",
      "leader": {
        "^t": "Character",
        "name": "anonymous",
        "field1": 1
      },
      "scout": { "^r": "Oswald" }
    }
  }
}
```

Conventions:

| Key | Meaning |
|-----|---------|
| `^t` | Concrete runtime type name |
| `^r` | `REFVAR` payload: global variable name of the target, or `null` for `none` |
| other keys | Embedded field values (`VAR`), recursively encoded if struct-typed |

Save / load rules:

- **Global** instances (including each type’s `static` default if mutated) are persisted.
- **Local/temp** instances are not part of the save blob; they die with the call stack / tunnel, same as ordinary temps.
- A `REFVAR` may only be saved if it is `none` or points at a **global** instance. A `REFVAR` still pointing at a local at save time is a runtime error (or is coerced to `none` with a warning — compiler/runtime should prefer error).
- Loading reconstitutes instances, then patches `^r` links by global name.
- Subtype instances stored in base-typed globals keep their concrete `^t` so virtual calls survive save games.

### Codegen mapping (implementation sketch)

| Ink concept | Runtime mapping |
|-------------|-----------------|
| Struct type | `structDefs` entry + vtable; method bodies as named function containers |
| Default instance | Global instance `Type.static` / symbol `Type` |
| `VAR x: T` | Global variable holding a `StructObject` value |
| `temp x: T` | Call-stack variable holding a `StructObject` value |
| Embedded field | Nested value on `StructObject` |
| `REFVAR` field | Slot storing target id / global name / null |
| Method call | Push `ref self` then args (L→R); vtable divert; pop R→L into temps (see [Calling convention](#calling-convention)) |
| `base.Method` | Same stack as method call; divert to compile-time inherited path (no vtable) |

---

## Full example

```ink
=== struct Character ===
VAR name = "anonymous"
VAR nerves = 1
= function ReactShocked() =
{self.name} looks startled.
~ return
= function ReactFurious() =
The heck you are doing?
~ return

=== struct ISuspect ===
= function IsSuspect() =
~ return true

=== struct Oswald: Character, ISuspect ===
VAR name = "Flinn Oswald"
= function ReactShocked() =
~ base.ReactShocked()
Oh my goodness me!
~ return

=== struct Party ===
VAR leader: Character = Character
REFVAR scout: Character = none

VAR Oswald: Oswald
VAR party: Party = Party

=== start ===
~ party.scout = Oswald
~ party.scout.ReactFurious()
~ temp visitor: Character = Oswald
~ visitor.name = "Guest"
~ visitor.ReactShocked()
{party.scout.IsSuspect()}
-> END
```

---

## Non-goals (v1)

- Parent-disambiguated `base<Type>.Method`.
- Heap allocation of anonymous globals without a variable name.
- Structs as list elements (may be considered later).
- Reflection / runtime type creation from ink.
- Parallel “class” keyword — `struct` is the only OOP form.

---

## Open implementation notes

- Parser already accepts `=== struct Name: Base1, Base2 ===`; body retention, registration, codegen, runtime objects, `REFVAR`, and tests remain to be built.
- Prefer reusing function-call and variable machinery over inventing a second evaluation mode.
- Inkject / inklecate version bumps should accompany `structDefs` in the story JSON so older runtimes fail clearly.
