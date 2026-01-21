using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    internal static partial class SpanHelpers // .ByteMemOps
    {
        [Intrinsic] // Unrolled for small constant lengths
        internal static void Memmove(ref byte dest, ref byte src, nuint len)
        {
        }

        public static void ClearWithoutReferences(ref byte dest, nuint len)
        {
        }

        internal static void Fill(ref byte dest, byte value, nuint len)
        {
        }
    }
}
