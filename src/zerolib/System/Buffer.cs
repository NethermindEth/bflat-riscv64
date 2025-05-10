// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public static partial class Buffer
    {
        public static void BlockCopy(Array src, int srcOffset, Array dst, int dstOffset, int count)
        {
        }

        public static int ByteLength(Array array)
        {
            return 0;
        }

        public static byte GetByte(Array array, int index)
        {
            return 0;
        }

        public static void SetByte(Array array, int index, byte value)
        {
        }

        public static unsafe void MemoryCopy(void* source, void* destination, long destinationSizeInBytes, long sourceBytesToCopy)
        {
        }

        public static unsafe void MemoryCopy(void* source, void* destination, ulong destinationSizeInBytes, ulong sourceBytesToCopy)
        {
        }

        internal static unsafe void _Memmove(ref byte dest, ref byte src, nuint len)
        {
        }

        internal static unsafe void _ZeroMemory(ref byte b, nuint byteLength)
        {
        }

        internal static void BulkMoveWithWriteBarrier(ref byte destination, ref byte source, nuint byteCount)
        {
        }
    }
}