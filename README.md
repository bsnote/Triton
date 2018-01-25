# Triton [![Build Status](https://travis-ci.org/kevzhao2/Triton.svg?branch=master)](https://travis-ci.org/kevzhao2/Triton) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Triton provides an easy and efficient way to embed Lua 5.3 into your .NET application!

## Usage

To get started, you should create a new `Lua` instance, which will create a new Lua environment:
```csharp
using (var lua = new Lua()) {
    lua.DoString("print('Hello, world!')");
}
```

The `DoString` method will execute Lua code within the context of that environment. To access the globals from .NET, you can index into the `Lua` instance as follows:
```csharp
lua.DoString("x = 'test'");
var x = (string)lua["x"];
Assert.Equal("test", x);
```

If you're going to execute a certain string many times, you can also use the `CreateFunction` method, which will return a `LuaFunction` that you can then call multiple times:
```csharp
var function = lua.CreateFunction("print('Hello!')");
for (var i = 0; i < 10000; ++i) {
    function.Call();
}
```

The `CreateFunction` method can also be used to create a function from a .NET delegate:
```csharp
var function = lua.CreateFunction(new Func<int, int>(x => x * x * x));
Assert.Equal(216L, function.Call(6)[0]);
```

### Passing .NET objects

To pass .NET objects over to the Lua environment, all you have to do is pass the object directly, e.g., using it as a function call argument or setting a global. Any Lua code will then be able to make use of the .NET object:
```csharp
var obj = new List<int>();
lua["obj"] = obj;
lua.DoString("obj:Add(2018)");
lua.DoString("count = obj.Count");

Assert.Single(obj);
Assert.Equal(2018, obj[0]);
Assert.Equal(1L, lua["count"]);
```

#### Generic Methods

You can access a generic method by "calling" it first with its type arguments, and then calling the method. For increased performance, consider caching the function generated using the type arguments.
```csharp
class Test {
    public void Generic<T>(T t);
}

lua["Int32"] = typeof(int);
lua["obj"] = new Test();
lua.DoString("obj:Generic(Int32)(5)");
```

#### Indexed Properties

You must access indexed properties using a wrapper object, as follows:
```csharp
lua["obj"] = new List<int> { 55 };
lua.DoString("obj.Item:Set(obj.Item:Get(0) + 1, 0)");
```

The arguments to `Get` are the indices, and the first argument to `Set` is the value, with the rest of the arguments being the indices.

#### Events

You must access events using a wrapper object, as follows:
```csharp
class Test {
    public event EventHandler Event;
}

lua["obj"] = new Test();
lua.DoString("callback = function(obj, args) print(obj) end)");
lua.DoString("obj.Event:Add(callback)");
// ...
lua.DoString("obj.Event:Remove(callback)");
```

Using events is **highly unrecommended** in the first place, since you have no control over when the event is called. It could be called on a different thread, which is a problem because Lua is not thread-safe.

### Passing .NET types

.NET types can be passed in using `ImportType`, and from the Lua side, .NET types can be imported using the `using` function. These types can then be used to access static members and create objects.
```csharp
lua.ImportType(typeof(int));
lua.DoString("using 'System.Collections.Generic'");
lua.DoString("list = List(Int32)()");
lua.DoString("list:Add(2018)");
```

## Comparison with NLua

### Advantages

* Triton works with an unmodified Lua library, and targets Lua 5.3, which has native support for integer types among other things.
* Triton supports .NET callbacks from coroutines.
* Triton supports `LuaThread` manipulation.
* Triton supports generic method invocation and generic type instantiation.
* Triton supports generalized indexed properties (including those declared in VB.NET or F# with names other than `Item`) with a variable number of indices.
* Triton supports `dynamic` usage of `Lua`, `LuaFunction`, and `LuaTable`.
* Triton will always correctly call the 'correct' overloads for methods.
* Triton reuses `LuaReference` objects and will clean up unused references, saving as much memory in a transparent way as possible.
* Triton is safer in its Lua -> .NET context switches, since it doesn't use `luaL_error` which uses `longjmp`, which can lead to issues when P/Invoked.

### Disadvantages
* Triton only supports event handler types that are "compatible" with the signature `void (object, EventArgs)`.
* Triton does not support calling extension methods on objects as instance methods.
* Triton does not currently have any debugging facilities.
* Triton is marginally slower for Lua -> .NET context switches. This is not a problem because it's *highly* unlikely that this would be a bottleneck in your application, and if it somehow is, then you shouldn't even be using an embedded scripting language for your purposes.

### Roadmap
* Improve general performance.