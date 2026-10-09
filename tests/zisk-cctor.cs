using System;
using System.Runtime.CompilerServices;

// Build with --libc zisk -Ot and run under ziskemu with a step limit. Checks the
// class-constructor rules the zkVM runner keeps: a cctor runs once, a cctor cycle
// sees the in-progress type's defaults instead of re-entering it, and a cctor that
// throws keeps throwing the same TypeInitializationException.
internal static class Program
{
    private static class Counted
    {
        public static int Runs;
        public static readonly int Value;

        static Counted()
        {
            Runs++;
            Value = 42;
        }
    }

    private static class CycleA
    {
        public static readonly int SeenFromB;
        public static readonly int Value;

        static CycleA()
        {
            Value = 1;
            SeenFromB = CycleB.SeenFromA;
        }
    }

    private static class CycleB
    {
        public static readonly int SeenFromA;

        static CycleB()
        {
            // CycleA's cctor is running further up the stack: it is not re-entered,
            // so this sees the value it has stored so far.
            SeenFromA = CycleA.Value + 10;
        }
    }

    private static class ThrowsRuns
    {
        public static int Count;
    }

    private static class Throws
    {
        public static readonly int Value;

        static Throws()
        {
            ThrowsRuns.Count++;
            if (ThrowsRuns.Count > 0)
                throw new InvalidOperationException("cctor");
            Value = 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadCounted() => Counted.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadThrows() => Throws.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception TouchThrows()
    {
        try
        {
            ReadThrows();
            return null;
        }
        catch (TypeInitializationException e)
        {
            return e;
        }
    }

    private static unsafe void Main()
    {
        bool ok = ReadCounted() == 42 && ReadCounted() == 42 && Counted.Runs == 1;
        ok &= CycleA.Value == 1 && CycleA.SeenFromB == 11 && CycleB.SeenFromA == 11;

        Exception first = TouchThrows();
        Exception second = TouchThrows();
        ok &= first is not null && ReferenceEquals(first, second) &&
              first.InnerException is InvalidOperationException && ThrowsRuns.Count == 1;

        if (!ok)
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
