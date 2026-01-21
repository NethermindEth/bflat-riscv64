//
// Copyright (C) 2025-2026 Demerzel Solutions Limited (Nethermind)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.CompilerServices;

namespace System
{
    /// ZisK Virtual Machine I/O operations
    public static unsafe class ZisK
    {
        private const ulong INPUT_ADDR = 0x90000000;
        private const ulong OUTPUT_ADDR = 0xa0010000;
        private const ulong UART_ADDR = 0xa0000200;

        /// Read input data from ZisK VM
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ReadInput()
        {
            byte* sizePtr = (byte*)(INPUT_ADDR + 8);
            ulong size = 0;

            for (int i = 0; i < 8; i++)
                size |= ((ulong)sizePtr[i]) << (i * 8);

            if (size == 0) return new byte[0];

            byte[] result = new byte[size];
            byte* dataPtr = (byte*)(INPUT_ADDR + 16);

            for (ulong i = 0; i < size; i++)
                result[i] = dataPtr[i];

            return result;
        }

        /// Set output value
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetOutput(int id, uint value)
        {
            if (id < 0 || id >= 64) return;

            uint* baseAddr = (uint*)OUTPUT_ADDR;
            uint currentCount = *baseAddr;

            if (id + 1 > currentCount)
                *baseAddr = (uint)(id + 1);

            *(baseAddr + 1 + id) = value;
        }

        /// Write single character
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteChar(char c)
        {
            *(byte*)UART_ADDR = (byte)c;
        }

        /// Write string
        public static void WriteString(string s)
        {
            for (int i = 0; i < s.Length; i++)
                WriteChar(s[i]);
        }

        /// Write line (string + newline)
        public static void WriteLine(string s)
        {
            WriteString(s);
            WriteChar('\n');
        }

        /// Read 64-bit unsigned integer from byte array (little-endian)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64(byte[] data, int offset = 0)
        {
            if (data.Length < offset + 8) return 0;

            ulong result = 0;
            for (int i = 0; i < 8; i++)
                result |= ((ulong)data[offset + i]) << (i * 8);
            return result;
        }

        /// Read 32-bit unsigned integer from byte array (little-endian)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(byte[] data, int offset = 0)
        {
            if (data.Length < offset + 4) return 0;

            uint result = 0;
            for (int i = 0; i < 4; i++)
                result |= ((uint)data[offset + i]) << (i * 8);
            return result;
        }

        /// Output 64-bit value as two 32-bit outputs
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetOutput64(int baseId, ulong value)
        {
            SetOutput(baseId, (uint)(value & 0xFFFFFFFF));
            SetOutput(baseId + 1, (uint)(value >> 32));
        }
    }
}
