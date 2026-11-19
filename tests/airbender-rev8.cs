using System.Buffers.Binary;
using System.Runtime.CompilerServices;

class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SwapWord(int value) => BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static short SwapHalf(short value) => BinaryPrimitives.ReverseEndianness(value);

    static int Main() =>
        SwapWord(0x01234589) == unchecked((int)0x89452301) &&
        SwapHalf(0x1280) == unchecked((short)0x8012) && SwapHalf(-256) == 255 ? 0 : 1;
}
