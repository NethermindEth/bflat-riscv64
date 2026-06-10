// Routes a managed `throw` into a C# handler under the Zisk zkVM, prints the
// exception, then exits cleanly.
//
//   throw -> CORINFO_HELP_THROW -> RhpThrowEx (exception object in a0)
//         -> --wrap=RhpThrowEx  -> __wrap_RhpThrowEx (rhp/module.c)
//         -> ZkvmThrow (this method)
//
// The C wrapper forwards the exception object (a0) to a weak symbol named
// `ZkvmThrow`. A program that exports one (via [UnmanagedCallersOnly]) takes
// over the throw; a program that does not falls back to a plain fail-fast.
//
// Two zkVM-specific details make this work end to end:
//
//  * Print with sys_write, NOT Console.WriteLine. Under --libc zisk the .NET
//    console path (SystemNative_Write / __stdio_write) is wrapped to a no-op,
//    so its output is invisible. sys_write (from libziskos, the same call
//    behind Nethermind.Zkvm.Abstractions IO.PrintLine) reaches the real zkVM
//    stdout. It is only available with --extlib bflat-libziskos.
//
//  * Terminate with the native exit() (a direct Zisk `a7=93; ecall`), NOT
//    Environment.Exit. Environment.Exit runs the managed runtime shutdown
//    which, while an exception is in flight, re-enters the throw path and
//    recurses through this handler forever.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    // Statically-linked libc exit; --wrap=exit -> pal __wrap_exit -> a7=93 ecall.
    [DllImport("*", EntryPoint = "exit")]
    static extern void NativeExit(int code);

    // zkVM raw stdout write (from libziskos, linked via --extlib).
    [DllImport("*", EntryPoint = "sys_write")]
    static extern unsafe void sys_write(uint fd, byte* ptr, nuint nbytes);

    static unsafe void Print(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\n");
        fixed (byte* p = bytes)
            sys_write(1u, p, (nuint)bytes.Length);
    }

    [UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]
    static void ZkvmThrow(IntPtr exceptionObj)
    {
        // The a0 pointer value IS the managed object reference; reinterpret it.
        Exception ex = Unsafe.As<IntPtr, Exception>(ref exceptionObj);

        Print("[ZkvmThrow] " + ex.GetType().ToString() + ": " + ex.Message);

        // The handler owns the termination decision (here: clean exit).
        NativeExit(0);
    }

    static int Main()
    {
        Print("before throw");
        throw new InvalidOperationException("boom from managed code");
    }
}
