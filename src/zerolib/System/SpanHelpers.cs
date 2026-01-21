// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System
{
    internal static partial class SpanHelpers
    {
        public static unsafe void ClearWithReferences(ref IntPtr ip, nuint pointerSizeLength)
        {
        }

        public static void Reverse(ref int buf, nuint length)
        {
        }

        public static void Reverse(ref long buf, nuint length)
        {
        }

        public static unsafe void Reverse<T>(ref T elements, nuint length)
        {
        }

        private static void ReverseInner<T>(ref T elements, nuint length)
        {
        }
    }
}
