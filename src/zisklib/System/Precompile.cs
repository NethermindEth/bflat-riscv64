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

namespace System
{
    public static unsafe class ZisKPrecompile
    {
        private const string NativeLibraryName = "__Internal";

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

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_keccakf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_keccakf(ulong* state25_u64);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_arith256", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_arith256(SyscallArith256Params* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_arith256_mod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_arith256_mod(SyscallArith256ModParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_secp256k1_add", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_secp256k1_add(SyscallSecp256k1AddParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_secp256k1_dbl", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_secp256k1_dbl(SyscallPoint256* p1);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_sha256f", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_sha256f(SyscallSha256Params* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bn254_curve_add", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bn254_curve_add(SyscallBn254CurveAddParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bn254_curve_dbl", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bn254_curve_dbl(SyscallPoint256* p1);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_add", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bn254_complex_add(SyscallBn254ComplexAddParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_sub", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bn254_complex_sub(SyscallBn254ComplexSubParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bn254_complex_mul", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bn254_complex_mul(SyscallBn254ComplexMulParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_arith384_mod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_arith384_mod(SyscallArith384ModParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_curve_add", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bls12_381_curve_add(SyscallBls12_381CurveAddParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_curve_dbl", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bls12_381_curve_dbl(SyscallPoint384* p1);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_add", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bls12_381_complex_add(SyscallBls12_381ComplexAddParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_sub", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bls12_381_complex_sub(SyscallBls12_381ComplexSubParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_bls12_381_complex_mul", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void zkvm_bls12_381_complex_mul(SyscallBls12_381ComplexMulParams* p);

        [DllImport(NativeLibraryName, EntryPoint = "zkvm_add256", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern ulong zkvm_add256(SyscallAdd256Params* p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void KeccakF(Span<ulong> state25)
        {
            if (state25.Length < 25) throw new ArgumentException("KeccakF state must be 25 u64 words (1600 bits).", nameof(state25));
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
