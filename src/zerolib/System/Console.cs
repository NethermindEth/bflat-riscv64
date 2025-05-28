// bflat minimal runtime library
// Copyright (C) 2021-2022 Michal Strehovsky
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

namespace System
{
    public enum ConsoleColor
    {
        Black, DarkBlue, DarkGreen, DarkCyan, DarkRed, DarkMagenta, DarkYellow,
        Gray, DarkGray, Blue, Green, Cyan, Red, Magenta, Yellow, White
    }

    public enum ConsoleKey
    {
        Escape = 27,
        LeftArrow = 37,
        UpArrow = 38,
        RightArrow = 39,
        DownArrow = 40,
    }

    public readonly struct ConsoleKeyInfo
    {
        public ConsoleKeyInfo(char keyChar, ConsoleKey key, bool shift, bool alt, bool control)
        {
            Key = key;
        }

        public readonly ConsoleKey Key;
    }

    public static unsafe partial class Console
    {
#if ZISK
        // Exported from start_zisk.S
        [System.Runtime.InteropServices.DllImport("*", EntryPoint = "__zisk_write_byte")]
        private static extern void ZiskWriteByte(byte b);

        [System.Runtime.InteropServices.DllImport("*", EntryPoint = "set_output")]
        public static extern void SetPublicOutput(int id, uint value);

        /// <summary>
        /// ZisK-specific character output. Only included when ZISK is defined.
        /// </summary>
        public static void Write(char c)
        {
            ZiskWriteByte((byte)c);
        }
#endif

        public static void WriteLine(string s)
        {
            for (int i = 0; i < s.Length; i++)
                Write(s[i]);
#if WINDOWS || UEFI
            Write('\r');
#endif
            Write('\n');
        }

        public static void WriteLine(int i)
        {
            const int BufferSize = 16;
            char* pBuffer = stackalloc char[BufferSize];
            int value = i;
            if (value < 0)
            {
                Write('-');
                value = -value;
            }

            char* pEnd = &pBuffer[BufferSize - 1];
            char* pCur = pEnd;
            do
            {
                *(pCur--) = (char)((value % 10) + '0');
                value /= 10;
            } while (value != 0);

            while (pCur <= pEnd)
                Write(*(pCur++));

#if WINDOWS || UEFI
            Write('\r');
#endif
            Write('\n');
        }
    }
}
