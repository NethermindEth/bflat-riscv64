// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Runtime
{
    internal static class TypeCast
    {
        public static unsafe object CheckCastAny(MethodTable* pTargetType, object obj)
        {
            return null;
        }

        public static unsafe object CheckCastInterface(MethodTable* pTargetType, object obj)
        {
            return null;
        }

        public static unsafe object CheckCastClass(MethodTable* pTargetType, object obj)
        {
            return null;
        }

        internal static unsafe bool IsDerived(MethodTable* pDerivedType, MethodTable* pBaseType)
        {
            return false;
        }

        private static unsafe object CheckCastClassSpecial(MethodTable* pTargetType, object obj)
        {
            return null;
        }

        public static unsafe object? IsInstanceOfAny(MethodTable* pTargetType, object? obj)
        {
            return null;
        }

        public static unsafe object? IsInstanceOfInterface(MethodTable* pTargetType, object? obj)
        {
            return null;
        }

        public static unsafe object? IsInstanceOfClass(MethodTable* pTargetType, object? obj)
        {
            return null;
        }

        public static unsafe bool IsInstanceOfException(MethodTable* pTargetType, object? obj)
        {
            return false;
        }
    }
}