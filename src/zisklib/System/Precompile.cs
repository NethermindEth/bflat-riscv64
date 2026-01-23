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
//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

//
// This is precompile support for the ZisK-flavor of bflat.
// ZisK syscalls are implemented in the assembly. The direct P/Invoke is implemented here, in the managed code,
// and then bflat has a special P/Invoke policy to link these functions without using the dynamic loader.
//
// Example of using precompiles in the client code:
//
//  using System;
//
//  public static unsafe void Example_Arith256()
//  {
//      ulong* a = stackalloc ulong[4];
//      ulong* b = stackalloc ulong[4];
//      ulong* c = stackalloc ulong[4];
//      ulong* dl = stackalloc ulong[4];
//      ulong* dh = stackalloc ulong[4];
//
//      a[0] = 3; a[1] = a[2] = a[3] = 0;
//      b[0] = 7; b[1] = b[2] = b[3] = 0;
//      c[0] = 5; c[1] = c[2] = c[3] = 0;
//
//      var p = new System.ZisKPrecompile.SyscallArith256Params
//      {
//          a = a,
//          b = b,
//          c = c,
//          dl = dl,
//          dh = dh
//      };
//
//      System.ZisKPrecompile.Arith256(ref p);
//
//      bool isCorrect =
//          dh[0] == 0 && dh[1] == 0 && dh[2] == 0 && dh[3] == 0 &&
//          dl[0] == 26 && dl[1] == 0 && dl[2] == 0 && dl[3] == 0;
//
//      if (!isCorrect)
//      {
//          throw new System.Exception("Arith256 result is incorrect.");
//      }
//  }
//
namespace System
{
    /// Implementation of ZisK precompiles
    public static unsafe partial class ZisKPrecompile
    {
        // The precompile stubs are expected to be linked into the main image, other variants
        // will never work in NativeAOT for Nethermind binaries.
        private const string NativeLibraryName = "__Internal";

        // Common low-level value types matching `syscalls/complex.rs` and `syscalls/point.rs`

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallComplex256
        {
            public fixed ulong x[4];
            public fixed ulong y[4];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallComplex384
        {
            public fixed ulong x[6];
            public fixed ulong y[6];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallPoint256
        {
            public fixed ulong x[4];
            public fixed ulong y[4];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallPoint384
        {
            public fixed ulong x[6];
            public fixed ulong y[6];
        }

        // Params structs matching `syscalls/*.rs`

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallArith256Params
        {
            public ulong* a;  // &[u64; 4]
            public ulong* b;  // &[u64; 4]
            public ulong* c;  // &[u64; 4]
            public ulong* dl; // &mut [u64; 4]
            public ulong* dh; // &mut [u64; 4]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallArith256ModParams
        {
            public ulong* a;      // &[u64; 4]
            public ulong* b;      // &[u64; 4]
            public ulong* c;      // &[u64; 4]
            public ulong* module; // &[u64; 4]
            public ulong* d;      // &mut [u64; 4]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallArith384ModParams
        {
            public ulong* a;      // &[u64; 6]
            public ulong* b;      // &[u64; 6]
            public ulong* c;      // &[u64; 6]
            public ulong* module; // &[u64; 6]
            public ulong* d;      // &mut [u64; 6]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallSha256Params
        {
            public ulong* state; // &mut [u64; 4]
            public ulong* input; // &[u64; 8]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallAdd256Params
        {
            public ulong* a;   // &[u64; 4]
            public ulong* b;   // &[u64; 4]
            public ulong cin;  // u64
            public ulong* c;   // &mut [u64; 4]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallSecp256k1AddParams
        {
            public SyscallPoint256* p1; // &mut SyscallPoint256
            public SyscallPoint256* p2; // &SyscallPoint256
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBn254CurveAddParams
        {
            public SyscallPoint256* p1; // &mut SyscallPoint256
            public SyscallPoint256* p2; // &SyscallPoint256
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBn254ComplexAddParams
        {
            public SyscallComplex256* f1; // &mut SyscallComplex256
            public SyscallComplex256* f2; // &SyscallComplex256
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBn254ComplexSubParams
        {
            public SyscallComplex256* f1; // &mut SyscallComplex256
            public SyscallComplex256* f2; // &SyscallComplex256
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBn254ComplexMulParams
        {
            public SyscallComplex256* f1; // &mut SyscallComplex256
            public SyscallComplex256* f2; // &SyscallComplex256
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBls12_381CurveAddParams
        {
            public SyscallPoint384* p1; // &mut SyscallPoint384
            public SyscallPoint384* p2; // &SyscallPoint384
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBls12_381ComplexAddParams
        {
            public SyscallComplex384* f1; // &mut SyscallComplex384
            public SyscallComplex384* f2; // &SyscallComplex384
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBls12_381ComplexSubParams
        {
            public SyscallComplex384* f1; // &mut SyscallComplex384
            public SyscallComplex384* f2; // &SyscallComplex384
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SyscallBls12_381ComplexMulParams
        {
            public SyscallComplex384* f1; // &mut SyscallComplex384
            public SyscallComplex384* f2; // &SyscallComplex384
        }

        // Native imports: must match symbol names from zkvm_zisk_precomp/module.S
        // Using LibraryImport for NativeAOT source-generated (direct) P/Invoke.

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_keccakf")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_keccakf(ulong* state25_u64);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_arith256")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_arith256(SyscallArith256Params* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_arith256_mod")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_arith256_mod(SyscallArith256ModParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_secp256k1_add")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_secp256k1_add(SyscallSecp256k1AddParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_secp256k1_dbl")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_secp256k1_dbl(SyscallPoint256* p1);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_sha256f")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_sha256f(SyscallSha256Params* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bn254_curve_add")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bn254_curve_add(SyscallBn254CurveAddParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bn254_curve_dbl")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bn254_curve_dbl(SyscallPoint256* p1);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_add")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bn254_complex_add(SyscallBn254ComplexAddParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_sub")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bn254_complex_sub(SyscallBn254ComplexSubParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_mul")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bn254_complex_mul(SyscallBn254ComplexMulParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_arith384_mod")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_arith384_mod(SyscallArith384ModParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_curve_add")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bls12_381_curve_add(SyscallBls12_381CurveAddParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_curve_dbl")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bls12_381_curve_dbl(SyscallPoint384* p1);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_add")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bls12_381_complex_add(SyscallBls12_381ComplexAddParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_sub")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bls12_381_complex_sub(SyscallBls12_381ComplexSubParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_mul")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void zkvm_bls12_381_complex_mul(SyscallBls12_381ComplexMulParams* p);

        [LibraryImport(NativeLibraryName, EntryPoint = "zkvm_add256")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial ulong zkvm_add256(SyscallAdd256Params* p);

        // Public API (wrappers)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void KeccakF(Span<ulong> state25)
        {
            if (state25.Length < 25)
                throw new ArgumentException("KeccakF state must be 25 u64 words (1600 bits).", nameof(state25));

            fixed (ulong* p = state25)
                zkvm_keccakf(p);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Sha256F(ref SyscallSha256Params p)
        {
            fixed (SyscallSha256Params* pp = &p)
                zkvm_sha256f(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Arith256(ref SyscallArith256Params p)
        {
            fixed (SyscallArith256Params* pp = &p)
                zkvm_arith256(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Arith256Mod(ref SyscallArith256ModParams p)
        {
            fixed (SyscallArith256ModParams* pp = &p)
                zkvm_arith256_mod(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Arith384Mod(ref SyscallArith384ModParams p)
        {
            fixed (SyscallArith384ModParams* pp = &p)
                zkvm_arith384_mod(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Add256(ref SyscallAdd256Params p)
        {
            fixed (SyscallAdd256Params* pp = &p)
                return zkvm_add256(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Secp256k1Add(ref SyscallSecp256k1AddParams p)
        {
            fixed (SyscallSecp256k1AddParams* pp = &p)
                zkvm_secp256k1_add(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Secp256k1Dbl(ref SyscallPoint256 p1)
        {
            fixed (SyscallPoint256* pp1 = &p1)
                zkvm_secp256k1_dbl(pp1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bn254CurveAdd(ref SyscallBn254CurveAddParams p)
        {
            fixed (SyscallBn254CurveAddParams* pp = &p)
                zkvm_bn254_curve_add(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bn254CurveDbl(ref SyscallPoint256 p1)
        {
            fixed (SyscallPoint256* pp1 = &p1)
                zkvm_bn254_curve_dbl(pp1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bn254ComplexAdd(ref SyscallBn254ComplexAddParams p)
        {
            fixed (SyscallBn254ComplexAddParams* pp = &p)
                zkvm_bn254_complex_add(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bn254ComplexSub(ref SyscallBn254ComplexSubParams p)
        {
            fixed (SyscallBn254ComplexSubParams* pp = &p)
                zkvm_bn254_complex_sub(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bn254ComplexMul(ref SyscallBn254ComplexMulParams p)
        {
            fixed (SyscallBn254ComplexMulParams* pp = &p)
                zkvm_bn254_complex_mul(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bls12_381CurveAdd(ref SyscallBls12_381CurveAddParams p)
        {
            fixed (SyscallBls12_381CurveAddParams* pp = &p)
                zkvm_bls12_381_curve_add(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bls12_381CurveDbl(ref SyscallPoint384 p1)
        {
            fixed (SyscallPoint384* pp1 = &p1)
                zkvm_bls12_381_curve_dbl(pp1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bls12_381ComplexAdd(ref SyscallBls12_381ComplexAddParams p)
        {
            fixed (SyscallBls12_381ComplexAddParams* pp = &p)
                zkvm_bls12_381_complex_add(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bls12_381ComplexSub(ref SyscallBls12_381ComplexSubParams p)
        {
            fixed (SyscallBls12_381ComplexSubParams* pp = &p)
                zkvm_bls12_381_complex_sub(pp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bls12_381ComplexMul(ref SyscallBls12_381ComplexMulParams p)
        {
            fixed (SyscallBls12_381ComplexMulParams* pp = &p)
                zkvm_bls12_381_complex_mul(pp);
        }
    }
}
