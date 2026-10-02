using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

// Build with --libc zisk -Ot and run under ziskemu with a step limit. These operand widths
// regress the compiler bugs fixed by NethermindEth/dotnet-riscv#9.
internal static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LeadingZeros(int value) => BitOperations.LeadingZeroCount((nuint)value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Multiply(int left, int right) => (long)left * right;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Equal(int left, long right) => (long)left == right;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Reverse(int value) => BinaryPrimitives.ReverseEndianness(value);

    private static unsafe void Main()
    {
        if (LeadingZeros(127) != 57 || LeadingZeros(-1) != 0 ||
            Multiply(65536, 65536) != 4294967296L || Equal(0, 4294967296L) ||
            Reverse(128) != int.MinValue)
        {
            // ziskemu does not propagate the guest's exit code; exhaust the step limit on failure.
            while (true) { }
        }

        // ZisK v1.3.0-alpha OUTPUT_ADDR: count followed by public output words.
        uint* output = (uint*)0xa0410000;
        output[0] = 1;
        output[1] = 0x50415353;
    }
}
