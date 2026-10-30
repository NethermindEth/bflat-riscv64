// The input through the non-determinism CSR, an accelerator of libairbender
// on the Keccak-f delegation, and the output in x10-x17.
using System.Runtime.InteropServices;

unsafe class Program
{
    [DllImport("__Internal")]
    private static extern void read_input(byte** buffer, nuint* size);

    [DllImport("__Internal")]
    private static extern int zkvm_keccak256(byte* data, nuint length, byte* output);

    [DllImport("__Internal")]
    private static extern void write_output(byte* output, nuint size);

    static int Main()
    {
        byte* input;
        nuint size;
        read_input(&input, &size);
        byte* hash = stackalloc byte[32];
        if (zkvm_keccak256(input, size, hash) != 0)
            return 1;
        write_output(hash, 32);
        return 0;
    }
}
