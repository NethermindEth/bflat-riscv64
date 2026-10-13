using System.Runtime.CompilerServices;

// Build with --libc zisk -Ot and run under ziskemu with a step limit. HashHelpers.IsPrime has an
// integer body from zisk.snippets.cs; while it was a constant stub that ILC inlined, GetPrime above
// its primes table counted to int.MaxValue without testing anything. Expected values are upstream's.
internal static class Program
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "IsPrime")]
    private static extern bool IsPrime(
        [UnsafeAccessorType("System.Collections.HashHelpers, System.Private.CoreLib")] object helpers, int candidate);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetPrime")]
    private static extern int GetPrime(
        [UnsafeAccessorType("System.Collections.HashHelpers, System.Private.CoreLib")] object helpers, int min);

    private static unsafe void Main()
    {
        if (!IsPrime(null, 1) || IsPrime(null, 9) || IsPrime(null, 49) || !IsPrime(null, int.MaxValue) ||
            IsPrime(null, 2147483641) || GetPrime(null, 7199369) != 7199369 ||
            GetPrime(null, 7199370) != 7199371 || GetPrime(null, 11998942) != 11998949 ||
            GetPrime(null, 2000000000) != 2000000011)
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
