using System.Runtime.CompilerServices;

// Compiled per zkVM libc by run-unaligned-access.sh, which checks that the read below is a single
// wide load on targets that execute misaligned accesses and a byte-wise expansion on SP1.
internal static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ReadWord(ref byte source) => Unsafe.ReadUnaligned<ulong>(ref source);

    private static int Main()
    {
        byte[] buffer = new byte[16];
        return (int)ReadWord(ref buffer[3]);
    }
}
