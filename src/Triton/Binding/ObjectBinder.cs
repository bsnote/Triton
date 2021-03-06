﻿// Copyright (c) 2018 Kevin Zhao
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Triton.Interop;

namespace Triton.Binding {
    /// <summary>
    /// Handles .NET object binding.
    /// </summary>
    internal sealed class ObjectBinder {
        private const string ObjectMetatable = "Triton$__object";
        private const string TypeMetatable = "Triton$__type";
        
        private readonly Lua _lua;
        private readonly LuaFunction _wrapFunction;
        private readonly Dictionary<string, LuaCFunction> _objectMetamethods;
        private readonly Dictionary<string, LuaCFunction> _typeMetamethods;
        private readonly LuaCFunction _proxyCallObjectDelegate;
        private readonly LuaCFunction _proxyCallTypeDelegate;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectBinder"/> class for the given <see cref="Lua"/> environment.
        /// </summary>
        /// <param name="lua">The <see cref="Lua"/> environment.</param>
        public ObjectBinder(Lua lua) {
            _lua = lua;
            
            // The __index metamethod can be cached in certain situations, such as methods and events. This can significantly improve
            // performance.
            var wrapIndexFunction = (LuaFunction)lua.DoString(@"
                local error = error
                local cache = setmetatable({}, { __mode = 'k' })
                return function(fn)
                    return function(obj, index)
                        local objcache = cache[obj]
                        if objcache == nil then
                            objcache = {}
                            cache[obj] = objcache
                        end
                        if objcache[index] ~= nil then
                            return objcache[index]
                        end

                        local success, iscached, result = fn(obj, index)
                        if not success then
                            error(iscached, 2)
                        end
                        if iscached then
                            objcache[index] = result
                        end
                        return result
                    end
                end")[0];

            // To raise Lua errors, we will do so directly in Lua. Doing so by P/Invoking luaL_error is problematic because luaL_error
            // performs a longjmp, which can destroy stack information.
            _wrapFunction = (LuaFunction)lua.DoString(@"
                local error = error
                local function helper(success, ...)
                    if not success then
                        error(..., 2)
                    end
                    return ...
                end
                return function(fn)
                    return function(...)
                        return helper(fn(...))
                    end
                end")[0];

            // Storing the LuaCFunction delegates prevents the .NET GC from garbage collecting them.
            _objectMetamethods = new Dictionary<string, LuaCFunction> {
                ["__call"] = CallObject,
                ["__index"] = IndexObject,
                ["__newindex"] = NewIndexObject,
                ["__add"] = AddObject,
                ["__sub"] = SubObject,
                ["__mul"] = MulObject,
                ["__div"] = DivObject,
                ["__mod"] = ModObject,
                ["__band"] = BandObject,
                ["__bor"] = BorObject,
                ["__bxor"] = BxorObject,
                ["__shr"] = ShrObject,
                ["__shl"] = ShlObject,
                ["__eq"] = EqObject,
                ["__lt"] = LtObject,
                ["__le"] = LeObject,
                ["__unm"] = UnmObject,
                ["__bnot"] = BnotObject,
                ["__gc"] = Gc,
                ["__tostring"] = ToString
            };
            _proxyCallObjectDelegate = ProxyCallObject;

            _typeMetamethods = new Dictionary<string, LuaCFunction> {
                ["__call"] = CallType,
                ["__index"] = IndexType,
                ["__newindex"] = NewIndexType,
                ["__gc"] = Gc,
                ["__tostring"] = ToString
            };
            _proxyCallTypeDelegate = ProxyCallType;

            NewMetatable(ObjectMetatable, _objectMetamethods);
            NewMetatable(TypeMetatable, _typeMetamethods);

            void NewMetatable(string name, Dictionary<string, LuaCFunction> metamethods) {
                LuaApi.NewMetatable(lua.MainState, name);

                foreach (var kvp in metamethods) {
                    LuaApi.PushString(lua.MainState, kvp.Key);
                    var isWrapped = kvp.Key != "__gc" && kvp.Key != "__tostring";
                    if (kvp.Key == "__index") {
                        wrapIndexFunction.PushOnto(lua.MainState);
                    } else if (isWrapped) {
                        _wrapFunction.PushOnto(lua.MainState);
                    }
                    LuaApi.PushCClosure(lua.MainState, kvp.Value, 0);
                    if (isWrapped) {
                        LuaApi.PCallK(lua.MainState, 1, 1);
                    }
                    LuaApi.SetTable(lua.MainState, -3);
                }

                // Setting __metatable to false will hide the metatable, protecting it from getmetatable and setmetatable.
                LuaApi.PushString(lua.MainState, "__metatable");
                LuaApi.PushBoolean(lua.MainState, false);
                LuaApi.SetTable(lua.MainState, -3);
                LuaApi.Pop(lua.MainState, 1);
            }
        }

        /// <summary>
        /// Pushes a .NET object onto the stack.
        /// </summary>
        /// <param name="state">The Lua state pointer.</param>
        /// <param name="obj">The object.</param>
        public static void PushNetObject(IntPtr state, object obj) => InternalPushNet(state, obj, ObjectMetatable);

        /// <summary>
        /// Pushes a .NET type onto the stack.
        /// </summary>
        /// <param name="state">The Lua state pointer.</param>
        /// <param name="type">The type.</param>
        public static void PushNetType(IntPtr state, Type type) => InternalPushNet(state, type, TypeMetatable);

        /// <summary>
        /// Tries to coerce the object into the given type.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <param name="type">The type.</param>
        /// <param name="result">The result.</param>
        /// <returns><c>true</c> if the object was successfully coerced; <c>false</c> otherwise.</returns>
        public static bool TryCoerce(object obj, Type type, out object result) {
            result = obj;
            if (result == null) {
                return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
            }

            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsByRef) {
                type = type.GetElementType();
            }

            if (result is long l) {
                var typeCode = Type.GetTypeCode(type);
                switch (typeCode) {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    try {
                        // We can pass null for provider because we're converting a long. This results in a marginal speed increase.
                        result = Convert.ChangeType(result, typeCode, null);
                        return true;
                    } catch (OverflowException) {
                        return false;
                    }

                case TypeCode.UInt64:
                    result = (ulong)l;
                    return true;
                }
            } else if (result is double d) {
                if (type == typeof(float)) {
                    result = (float)d;
                    return true;
                } else if (type == typeof(decimal)) {
                    result = (decimal)d;
                    return true;
                }
            } else if (result is string s) {
                if (type == typeof(char)) {
                    result = s.FirstOrDefault();
                    return s.Length == 1;
                }
            }

            return type.IsInstanceOfType(result);
        }

        /// <summary>
        /// Tries to coerce the given objects into the given parameter format.
        /// </summary>
        /// <param name="objs">The objects.</param>
        /// <param name="params">The parameters.</param>
        /// <param name="args">The resulting arguments.</param>
        /// <returns>A score of how well the objects fit the parameter format. int.MinValue signifies a failure.</returns>
        public static int TryCoerce(object[] objs, ParameterInfo[] @params, out object[] args) {
            args = new object[@params.Length];

            // The more parameters satisfied, the higher the score. The more implicit parameters, the lower the score.
            const int explicitBonus = 65536;
            const int implicitBonus = -1;
            var score = 0;

            var objIndex = 0;
            for (var i = 0; i < @params.Length; ++i) {
                var param = @params[i];
                if (param.IsOut) {
                    continue;
                }

                var type = param.ParameterType;
                if (i == @params.Length - 1 && type.IsArray && param.IsDefined(typeof(ParamArrayAttribute), false)) {
                    var elementType = type.GetElementType();
                    if (TryCoerceArray(elementType, out var array)) {
                        args[i] = array;
                        score += implicitBonus;
                        return score;
                    }
                }

                if (objIndex < objs.Length) {
                    if (!TryCoerce(objs[objIndex], type, out args[i])) {
                        return int.MinValue;
                    }

                    ++objIndex;
                    score += explicitBonus;
                } else {
                    if (!param.IsOptional) {
                        return int.MinValue;
                    }

                    args[i] = param.DefaultValue;
                    score += implicitBonus;
                }
            }

            return objIndex == objs.Length ? score : int.MinValue;

            bool TryCoerceArray(Type elementType, out Array array) {
                array = Array.CreateInstance(elementType, objs.Length - objIndex);

                for (var j = 0; j < array.Length; ++j) {
                    if (!TryCoerce(objs[objIndex + j], elementType, out var value)) {
                        return false;
                    }
                    array.SetValue(value, j);
                }

                return true;
            }
        }

        private static void InternalPushNet(IntPtr state, object obj, string metatable) {
            var handle = GCHandle.Alloc(obj, GCHandleType.Normal);
            LuaApi.PushHandle(state, handle);
            LuaApi.GetField(state, LuaApi.RegistryIndex, metatable);
            LuaApi.SetMetatable(state, -2);
        }

        private static T ResolveMethodCall<T>(object[] objs, IEnumerable<T> methods, out object[] args) where T : MethodBase {
            // To resolve overloads, we will utilize a scoring system to pick the best method out of the given methods.
            var bestScore = int.MinValue;
            T bestMethod = null;

            args = null;
            foreach (var method in methods) {
                var score = TryCoerce(objs, method.GetParameters(), out var methodArgs);
                if (score > bestScore) {
                    bestScore = score;
                    bestMethod = method;
                    args = methodArgs;
                }
            }

            return bestMethod;
        }

        private static int WrappedCall(IntPtr state, Func<int> function) {
            var oldTop = LuaApi.GetTop(state);
            try {
                LuaApi.PushBoolean(state, true);
                return function() + 1;
            } catch (Exception e) {
                // If an exception occurs, then we need to reset the top. We don't know what state the stack is in!
                LuaApi.SetTop(state, oldTop);
                LuaApi.PushBoolean(state, false);
                if (e is LuaException) {
                    LuaApi.PushString(state, e.Message);
                } else {
                    LuaApi.PushString(state, $"unhandled .NET exception:\n{e}");
                }
                return 2;
            }
        }

        private static int Gc(IntPtr state) {
            var handle = LuaApi.ToHandle(state, 1);
            handle.Free();
            return 0;
        }

        private static int ToString(IntPtr state) {
            var obj = LuaApi.ToHandle(state, 1).Target;
            LuaApi.PushString(state, obj.ToString() ?? "");
            return 1;
        }

        private int CallObject(IntPtr state) => WrappedCall(state, () => {
            var obj = LuaApi.ToHandle(state, 1).Target;
            if (!(obj is Delegate @delegate)) {
                throw new LuaException("attempt to call non-delegate");
            }

            var top = LuaApi.GetTop(state) - 1;
            var objs = _lua.ToObjects(2, top, state);

            var method = @delegate.Method;
            if (TryCoerce(objs, method.GetParameters(), out var args) == int.MinValue) {
                throw new LuaException("attempt to call delegate with invalid args");
            }

            object result;
            try {
                result = method.Invoke(@delegate.Target, args);
            } catch (TargetInvocationException e) {
                throw new LuaException($"attempt to call delegate threw:\n{e.InnerException}");
            }

            var numResults = 0;
            if (method.ReturnType != typeof(void)) {
                ++numResults;
                _lua.PushObject(result, state);
            }
            foreach (var param in method.GetParameters().Where(p => p.ParameterType.IsByRef)) {
                ++numResults;
                _lua.PushObject(args[param.Position], state);
            }
            return numResults;
        });

        private int CallType(IntPtr state) => WrappedCall(state, () => {
            var type = (Type)LuaApi.ToHandle(state, 1).Target;
            var top = LuaApi.GetTop(state) - 1;
            var objs = _lua.ToObjects(2, top, state);

            if (type.ContainsGenericParameters) {
                var typeArgs = type.GetGenericArguments();
                if (objs.Length != typeArgs.Length) {
                    throw new LuaException("attempt to construct generic type with incorrect number of type args");
                }

                for (var i = 0; i < typeArgs.Length; ++i) {
                    if (!(objs[i] is Type typeArg)) {
                        throw new LuaException("attempt to construct generic type with non-type arg");
                    }
                    if (typeArg.ContainsGenericParameters) {
                        throw new LuaException("attempt to construct generic type with generic type arg");
                    }

                    typeArgs[i] = typeArg;
                }

                Type result;
                try {
                    result = type.MakeGenericType(typeArgs);
                } catch (ArgumentException) {
                    throw new LuaException("attempt to construct generic type threw: type constraints");
                }
                PushNetType(state, result);
            } else {
                if (type.IsAbstract) {
                    throw new LuaException("attempt to instantiate abstract type");
                }

                var info = type.GetBindingInfo();
                var ctors = info.GetConstructors();
                if (ctors.Count == 0) {
                    throw new LuaException("attempt to instantiate type with no constructors");
                }

                var ctor = ResolveMethodCall(objs, ctors, out var args);
                if (ctor == null) {
                    throw new LuaException("attempt to instantiate type with invalid args");
                }

                object result;
                try {
                    result = ctor.Invoke(args);
                } catch (TargetInvocationException e) {
                    throw new LuaException($"attempt to instantiate type threw:\n{e.InnerException}");
                }
                _lua.PushObject(result, state);
            }
            return 1;
        });

        private int IndexObject(IntPtr state) => WrappedCall(state, () => {
            var obj = LuaApi.ToHandle(state, 1).Target;
            return IndexShared(state, obj, obj.GetType());
        });

        private int IndexType(IntPtr state) => WrappedCall(state, () => {
            var type = (Type)LuaApi.ToHandle(state, 1).Target;
            if (type.IsInterface) {
                throw new LuaException("attempt to index interface");
            }
            if (type.ContainsGenericParameters) {
                throw new LuaException("attempt to index generic type");
            }

            return IndexShared(state, null, type);
        });

        private int IndexShared(IntPtr state, object obj, Type type) {
            var keyType = LuaApi.Type(state, 2);
            if (keyType == LuaType.String) {
                var memberName = LuaApi.ToString(state, 2);
                var info = type.GetBindingInfo();
                var isStatic = obj == null;

                var member = info.GetMember(memberName, isStatic);
                if (member == null) {
                    throw new LuaException("attempt to index invalid member");
                }

                switch (member.MemberType) {
                case MemberTypes.Event:
                    var @event = (EventInfo)member;
                    LuaApi.PushBoolean(state, true);
                    PushNetObject(state, new EventWrapper(obj, @event));
                    return 2;

                case MemberTypes.Field:
                    var field = (FieldInfo)member;
                    LuaApi.PushBoolean(state, field.IsLiteral);
                    _lua.PushObject(field.GetValue(obj), state);
                    return 2;

                case MemberTypes.Method:
                    LuaApi.PushBoolean(state, true);
                    _wrapFunction.PushOnto(state);
                    LuaApi.PushValue(state, 1);
                    LuaApi.PushValue(state, 2);
                    LuaApi.PushInteger(state, 0);
                    LuaApi.PushCClosure(state, isStatic ? _proxyCallTypeDelegate : _proxyCallObjectDelegate, 3);
                    LuaApi.PCallK(state, 1, 1);
                    return 2;

                case MemberTypes.Property:
                    var property = (PropertyInfo)member;
                    if (property.GetIndexParameters().Length > 0) {
                        LuaApi.PushBoolean(state, true);
                        PushNetObject(state, new IndexedPropertyWrapper(obj, property));
                    } else {
                        if (property.GetGetMethod() == null) {
                            throw new LuaException("attempt to get property without getter");
                        }

                        try {
                            LuaApi.PushBoolean(state, false);
                            _lua.PushObject(property.GetValue(obj, null), state);
                        } catch (TargetInvocationException e) {
                            throw new LuaException($"attempt to get property threw:\n{e.InnerException}");
                        }
                    }
                    return 2;

                default:
                    LuaApi.PushBoolean(state, true);
                    PushNetType(state, (Type)member);
                    return 2;
                }
            }

            if (keyType == LuaType.Number && obj is Array array && LuaApi.IsInteger(state, 2)) {
                if (array.Rank != 1) {
                    throw new LuaException("attempt to index multi-dimensional array");
                }

                var index = (int)LuaApi.ToInteger(state, 2);
                if (index < 0 || index >= array.Length) {
                    throw new LuaException("attempt to index array with out-of-bounds index");
                }

                LuaApi.PushBoolean(state, false);
                _lua.PushObject(array.GetValue(index), state);
                return 2;
            }

            throw new LuaException("attempt to index with invalid key");
        }

        private int ProxyCallObject(IntPtr state) => WrappedCall(state, () => {
            var obj = LuaApi.ToHandle(state, LuaApi.UpvalueIndex(1)).Target;
            return ProxyCallShared(state, obj, obj.GetType());
        });

        private int ProxyCallType(IntPtr state) => WrappedCall(state, () => {
            var type = (Type)LuaApi.ToHandle(state, LuaApi.UpvalueIndex(1)).Target;
            return ProxyCallShared(state, null, type);
        });

        private int ProxyCallShared(IntPtr state, object obj, Type type) {
            var methodName = LuaApi.ToString(state, LuaApi.UpvalueIndex(2));
            var numTypeArgs = (int)LuaApi.ToInteger(state, LuaApi.UpvalueIndex(3));
            var top = LuaApi.GetTop(state) - 1;
            var info = type.GetBindingInfo();
            var isStatic = obj == null;

            // The arguments will start at 2 only if the call is an instance call and it's not a generic call. This is because the
            // obj:Method syntax will pass obj as the first argument, but a generic obj:Method call will not have obj as the first
            // argument because only the first "invocation" with the types will have obj as the first argument.
            var objs = _lua.ToObjects(isStatic || numTypeArgs > 0 ? 1 : 2, top, state);

            var methods = info.GetMethods(methodName, isStatic, numTypeArgs);
            if (numTypeArgs > 0) {
                var typeArgs = new Type[numTypeArgs];
                for (var i = 0; i < typeArgs.Length; ++i) {
                    if (!(_lua.ToObject(LuaApi.UpvalueIndex(4 + i), null, state) is Type typeArg)) {
                        throw new LuaException("attempt to construct generic method with non-type arg");
                    }
                    if (typeArg.ContainsGenericParameters) {
                        throw new LuaException("attempt to construct generic method with generic type arg");
                    }

                    typeArgs[i] = typeArg;
                }
                
                // "Resolve" all generic methods into non-generic methods, ignoring any type constraint issues unless there are no valid
                // resolved methods.
                var resolvedMethods = new List<MethodInfo>();
                foreach (var genericMethod in methods) {
                    try {
                        resolvedMethods.Add(genericMethod.MakeGenericMethod(typeArgs));
                    } catch (ArgumentException) {
                    }
                }
                if (resolvedMethods.Count == 0) {
                    throw new LuaException("attempt to construct generic method threw: type constraints");
                }
                methods = resolvedMethods;
            }

            var method = ResolveMethodCall(objs, methods, out var args);
            if (method == null) {
                if (numTypeArgs > 0) {
                    throw new LuaException("attempt to call generic method with invalid args");
                }

                // If the first attempt at resolving the method failed and there's at least one argument, then we're possibly dealing
                // with a generic method call, where the arguments are the types. We must return a function which handles the generic
                // overload resolution!
                var genericMethods = info.GetMethods(methodName, isStatic, objs.Length);
                if (objs.Length == 0 || !genericMethods.Any()) {
                    throw new LuaException("attempt to call method with invalid args");
                }

                _wrapFunction.PushOnto(state);
                LuaApi.PushValue(state, LuaApi.UpvalueIndex(1));
                LuaApi.PushValue(state, LuaApi.UpvalueIndex(2));
                LuaApi.PushInteger(state, objs.Length);
                for (var i = isStatic ? 1 : 2; i <= top; ++i) {
                    LuaApi.PushValue(state, i);
                }
                LuaApi.PushCClosure(state, isStatic ? _proxyCallTypeDelegate : _proxyCallObjectDelegate, objs.Length + 3);
                LuaApi.PCallK(state, 1, 1);
                return 1;
            }
            
            object result;
            try {
                result = method.Invoke(obj, args);
            } catch (TargetInvocationException e) {
                throw new LuaException($"attempt to call {(numTypeArgs > 0 ? "generic " : "")}method threw:\n{e.InnerException}");
            }

            var numResults = 0;
            if (method.ReturnType != typeof(void)) {
                ++numResults;
                _lua.PushObject(result, state);
            }
            foreach (var param in method.GetParameters().Where(p => p.ParameterType.IsByRef)) {
                ++numResults;
                _lua.PushObject(args[param.Position], state);
            }
            return numResults;
        }

        private int NewIndexObject(IntPtr state) => WrappedCall(state, () => {
            var obj = LuaApi.ToHandle(state, 1).Target;
            return NewIndexShared(state, obj, obj.GetType());
        });

        private int NewIndexType(IntPtr state) => WrappedCall(state, () => {
            var type = (Type)LuaApi.ToHandle(state, 1).Target;
            if (type.IsInterface) {
                throw new LuaException("attempt to index interface");
            }
            if (type.ContainsGenericParameters) {
                throw new LuaException("attempt to index generic type");
            }

            return NewIndexShared(state, null, type);
        });

        private int NewIndexShared(IntPtr state, object obj, Type type) {
            var value = _lua.ToObject(3, null, state);

            var keyType = LuaApi.Type(state, 2);
            if (keyType == LuaType.String) {
                var memberName = LuaApi.ToString(state, 2);
                var info = type.GetBindingInfo();
                var isStatic = obj == null;

                var member = info.GetMember(memberName, isStatic);
                if (member == null) {
                    throw new LuaException("attempt to set invalid member");
                }

                switch (member.MemberType) {
                case MemberTypes.Event:
                    throw new LuaException("attempt to set event");

                case MemberTypes.Field:
                    var field = (FieldInfo)member;
                    if (field.IsLiteral) {
                        throw new LuaException("attempt to set constant field");
                    }
                    if (!TryCoerce(value, field.FieldType, out value)) {
                        throw new LuaException("attempt to set field with invalid value");
                    }

                    field.SetValue(obj, value);
                    return 0;

                case MemberTypes.Method:
                    throw new LuaException("attempt to set method");

                case MemberTypes.Property:
                    var property = (PropertyInfo)member;
                    if (property.GetIndexParameters().Length > 0) {
                        throw new LuaException("attempt to set indexed property");
                    }
                    if (property.GetSetMethod() == null) {
                        throw new LuaException("attempt to set property without setter");
                    }
                    if (!TryCoerce(value, property.PropertyType, out value)) {
                        throw new LuaException("attempt to set property with invalid value");
                    }

                    try {
                        property.SetValue(obj, value, null);
                    } catch (TargetInvocationException e) {
                        throw new LuaException($"attempt to set property threw:\n{e.InnerException}");
                    }
                    return 0;

                default:
                    throw new LuaException("attempt to set nested type");
                }
            }
            
            if (keyType == LuaType.Number && obj is Array array && LuaApi.IsInteger(state, 2)) {
                if (array.Rank != 1) {
                    throw new LuaException("attempt to index multi-dimensional array");
                }

                var index = (int)LuaApi.ToInteger(state, 2);
                if (index < 0 || index >= array.Length) {
                    throw new LuaException("attempt to index array with out-of-bounds index");
                }
                if (!TryCoerce(value, type.GetElementType(), out value)) {
                    throw new LuaException("attempt to set array with invalid value");
                }

                array.SetValue(value, index);
                return 0;
            }

            throw new LuaException("attempt to index with invalid key");
        }

        private int AddObject(IntPtr state) => BinaryOpShared(state, "op_Addition", "perform arithmetic on");
        private int SubObject(IntPtr state) => BinaryOpShared(state, "op_Subtraction", "perform arithmetic on");
        private int MulObject(IntPtr state) => BinaryOpShared(state, "op_Multiply", "perform arithmetic on");
        private int DivObject(IntPtr state) => BinaryOpShared(state, "op_Division", "perform arithmetic on");
        private int ModObject(IntPtr state) => BinaryOpShared(state, "op_Modulus", "perform arithmetic on");
        private int BandObject(IntPtr state) => BinaryOpShared(state, "op_BitwiseAnd", "perform bitwise operation on");
        private int BorObject(IntPtr state) => BinaryOpShared(state, "op_BitwiseOr", "perform bitwise operation on");
        private int BxorObject(IntPtr state) => BinaryOpShared(state, "op_ExclusiveOr", "perform bitwise operation on");
        private int EqObject(IntPtr state) => BinaryOpShared(state, "op_Equality", "compare");
        private int LtObject(IntPtr state) => BinaryOpShared(state, "op_LessThan", "compare");
        private int LeObject(IntPtr state) => BinaryOpShared(state, "op_LessThanOrEqual", "compare");
        private int ShlObject(IntPtr state) => BinaryOpShared(state, "op_LeftShift", "perform bitwise operation on");
        private int ShrObject(IntPtr state) => BinaryOpShared(state, "op_RightShift", "perform bitwise operation on");

        private int BinaryOpShared(IntPtr state, string methodName, string errorText) => WrappedCall(state, () => {
            var operand1 = _lua.ToObject(1, null, state);
            var operand2 = _lua.ToObject(2, null, state);
            var info1 = operand1.GetType().GetBindingInfo();
            var info2 = operand2.GetType().GetBindingInfo();

            // Binary operators can be declared on either of the operands' types, so check both of them.
            var ops = info1.GetOperators(methodName);
            if (info2 != info1) {
                ops = ops.Concat(info2.GetOperators(methodName));
            }

            var op = ResolveMethodCall(new[] { operand1, operand2 }, ops, out var args);
            if (op == null) {
                throw new LuaException($"attempt to {errorText} two objects");
            }

            object result;
            try {
                result = op.Invoke(null, args);
            } catch (TargetInvocationException e) {
                throw new LuaException($"attempt to {errorText} two objects threw:\n{e.InnerException}");
            }
            _lua.PushObject(result, state);
            return 1;
        });

        private int UnmObject(IntPtr state) => UnaryOpShared(state, "op_UnaryNegation", "perform arithmetic on");
        private int BnotObject(IntPtr state) => UnaryOpShared(state, "op_OnesComplement", "perform bitwise operation on");

        private int UnaryOpShared(IntPtr state, string methodName, string errorText) => WrappedCall(state, () => {
            var operand = LuaApi.ToHandle(state, 1).Target;
            var info = operand.GetType().GetBindingInfo();

            var op = info.GetOperators(methodName).SingleOrDefault();
            if (op == null) {
                throw new LuaException($"attempt to {errorText} an object");
            }

            object result;
            try {
                result = op.Invoke(null, new[] { operand });
            } catch (TargetInvocationException e) {
                throw new LuaException($"attempt to {errorText} an object threw:\n{e.InnerException}");
            }
            _lua.PushObject(result, state);
            return 1;
        });
    }
}
