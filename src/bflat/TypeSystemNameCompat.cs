// bflat C# compiler
// Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
//
// Shims over the .NET 11 ILCompiler type-system rename of Name/Namespace
// (and name-based lookups) from System.String to Internal.Text.Utf8Span.
// Call sites use one shared form on both API generations:
//   - name comparisons go through StringEquals/StringStartsWith, which is
//     the native Utf8Span API on .NET 11 and an extension on string here
//     for .NET 10;
//   - string-based GetType/GetNestedType lookups keep their .NET 10 shape,
//     with .NET 11 getting extension overloads that encode to Utf8Span.

using System;
using System.Text;

using Internal.Text;
using Internal.TypeSystem;

internal static class TypeSystemNameCompat
{
#if NET11_0_OR_GREATER
    public static Utf8Span AsUtf8(this string s) => new Utf8Span(Encoding.UTF8.GetBytes(s));

    public static MetadataType GetType(this ModuleDesc module, string nameSpace, string name)
        => module.GetType(nameSpace.AsUtf8(), name.AsUtf8());

    public static object GetType(this ModuleDesc module, string nameSpace, string name, NotFoundBehavior notFoundBehavior)
        => module.GetType(nameSpace.AsUtf8(), name.AsUtf8(), notFoundBehavior);

    public static MetadataType GetNestedType(this MetadataType type, string name)
        => type.GetNestedType(name.AsUtf8());

    public static bool StringStartsWith(this Utf8Span span, string value)
        => span.StartsWith(value.AsUtf8());
#else
    public static bool StringEquals(this string self, string value)
        => string.Equals(self, value, StringComparison.Ordinal);

    public static bool StringStartsWith(this string self, string value)
        => self.StartsWith(value, StringComparison.Ordinal);
#endif
}
