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

namespace Triton {
    /// <summary>
    /// The exception that is thrown when a Lua error occurs.
    /// </summary>
#if NETFULL
    [Serializable]
#endif
    public class LuaException : Exception {
        /// <summary>
        /// Initializes a new instance of the <see cref="LuaException"/> class.
        /// </summary>
        public LuaException() {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LuaException"/> class with the specified message.
        /// </summary>
        /// <param name="message">The message.</param>
        public LuaException(string message) : base(message) {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LuaException"/> class with the specified message and inner exception.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public LuaException(string message, Exception innerException) : base(message, innerException) {
        }
    }
}