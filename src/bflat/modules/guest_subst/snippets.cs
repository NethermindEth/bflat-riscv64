extern alias corelib;
// Body-substitution snippets for the zkVM guests (rv64im, soft-float).
//
// Each method here replaces the body of a CoreLib method that would otherwise
// reach a libm function the nofp module traps (sqrt, modf, ...). Soft-float
// arithmetic itself runs, so plain FP is not a reason for a snippet. Writing
// the replacements in plain C# lets Roslyn - not hand-emitted IL - guarantee a
// valid body.
//
// Conventions:
//   * A snippet is a STATIC method whose argument layout matches the method it
//     replaces: for an instance method the first parameter stands in for
//     `this`, the rest are the original parameters in order.
//   * This file is compiled by BuildCommand with accessibility checks bypassed
//     (BinderFlags.IgnoreAccessibility), so it may name private nested types and
//     touch private/internal members of the referenced CoreLib.
//   * ZkvmSubstitutions.BuildBodySubstitutions maps each snippet to its target.
//     A snippet without a mapping there is an error.
//
// This file is NOT part of bflat's own build (it references CoreLib internals
// that do not exist in bflat's compilation); the guest_subst module ships it
// and bflat compiles it at guest-build time (ZkvmSubstitutions.CompileSnippets).

namespace __BflatSnippets
{
    internal static class Snippets
    {
        // ---- System.Collections.HashHelpers.IsPrime -------------------------
        // The original bounds its trial division with (int)Math.Sqrt(candidate),
        // i.e. libm sqrt. divisor <= candidate / divisor is the same bound in integers: for a
        // positive divisor it holds exactly when divisor * divisor <= candidate,
        // and Math.Sqrt of an int is never off by enough for the cast to land
        // on the other side of a square. One donor serves every copy of the
        // shared HashHelpers source (CoreLib, System.Collections.Concurrent,
        // System.Collections.Immutable).
        public static bool HashHelpersIsPrime(int candidate)
        {
            if ((candidate & 1) != 0)
            {
                for (int divisor = 3; divisor <= candidate / divisor; divisor += 2)
                {
                    if ((candidate % divisor) == 0)
                        return false;
                }
                return true;
            }
            return candidate == 2;
        }

        // ---- System.TimeZoneInfo..cctor -------------------------------------
        // The stock cctor computes s_daylightRuleMarker via
        // DateTime.MinValue.AddMilliseconds(2), which calls Math.Truncate, i.e.
        // libm modf. Every guest that touches TimeZoneInfo (DateTime.Now
        // included) runs this cctor. This donor reproduces the WHOLE cctor (five field
        // initializations - the rest of the type's statics are consts) with the
        // marker built tick-exactly in integers: 2 ms = 20_000 ticks. Applied only
        // after ZkvmSubstitutions.BuildBodySubstitutions verifies the original cctor still
        // stores exactly these five fields, so a CoreLib change is refused loudly
        // instead of silently dropping a new initializer.

        // The donor is not the field-owning cctor, so C# refuses direct stores to
        // the `static readonly` fields (CS0198) and cannot name the unspeakable
        // `<Invariant>k__BackingField` at all. UnsafeAccessor (implemented by
        // ILC) reaches each by its metadata name with a writable ref.
        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "s_utcTimeZone")]
        private static extern ref corelib::System.TimeZoneInfo TzUtcTimeZoneField(corelib::System.TimeZoneInfo _);

        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "<Invariant>k__BackingField")]
        private static extern ref bool TzInvariantField(corelib::System.TimeZoneInfo _);

        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "s_daylightRuleMarker")]
        private static extern ref corelib::System.TimeZoneInfo.TransitionTime TzDaylightRuleMarkerField(corelib::System.TimeZoneInfo _);

        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "s_ZonesThatUseLocationName")]
        private static extern ref string[] TzZonesThatUseLocationNameField(corelib::System.TimeZoneInfo _);

        // .NET 10 only: GetUtcOffsetFromUtc's whole-day range checks. .NET 11
        // dropped both fields, so these accessors are named only by the .NET 10
        // donor below - an accessor that is never called is never resolved, so
        // referencing absent fields from the unused donor costs nothing.
        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "s_maxDateOnly")]
        private static extern ref global::System.DateTime TzMaxDateOnlyField(corelib::System.TimeZoneInfo _);

        [global::System.Runtime.CompilerServices.UnsafeAccessor(
            global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "s_minDateOnly")]
        private static extern ref global::System.DateTime TzMinDateOnlyField(corelib::System.TimeZoneInfo _);

        // Shared by both donors: everything the two runtimes have in common.
        // Field values mirror the CoreLib sources exactly -
        //   s_utcTimeZone            = CreateUtcTimeZone()
        //   s_cachedData             = new CachedData()
        //   Invariant                = AppContextConfigHelper.GetBooleanConfig(
        //                                "System.TimeZoneInfo.Invariant",
        //                                "DOTNET_SYSTEM_TIMEZONE_INVARIANT")   [default false]
        //   s_daylightRuleMarker     = TransitionTime.CreateFixedDateRule(
        //                                DateTime.MinValue.AddMilliseconds(2), 1, 1)
        //   s_ZonesThatUseLocationName = { Minsk, Moscow, Simferopol, Apia, Pitcairn }
        // - with the marker's 2 ms expressed as 20_000 ticks, which is the one
        // and only reason this donor exists.
        private static void TimeZoneInfoCctorCommon()
        {
            TzUtcTimeZoneField(null) = corelib::System.TimeZoneInfo.CreateUtcTimeZone();
            corelib::System.TimeZoneInfo.s_cachedData = new corelib::System.TimeZoneInfo.CachedData();
            TzInvariantField(null) = corelib::System.AppContextConfigHelper.GetBooleanConfig(
                "System.TimeZoneInfo.Invariant", "DOTNET_SYSTEM_TIMEZONE_INVARIANT", false);
            TzDaylightRuleMarkerField(null) =
                corelib::System.TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                    new global::System.DateTime(2 * global::System.TimeSpan.TicksPerMillisecond), 1, 1);
            TzZonesThatUseLocationNameField(null) = new[]
            {
                "Europe/Minsk", "Europe/Moscow", "Europe/Simferopol", "Pacific/Apia", "Pacific/Pitcairn",
            };
        }

        // .NET 11 field set: the five members above and nothing else.
        public static void TimeZoneInfoCctor()
        {
            TimeZoneInfoCctorCommon();
        }

        // .NET 10 field set: the same five plus the whole-day range bounds.
        // Values verbatim from TimeZoneInfo.cs - `new DateTime(9999, 12, 31)`
        // and `new DateTime(1, 1, 2)`; both are integer date math, no FP.
        // ZkvmSubstitutions.BuildBodySubstitutions picks this variant when the original
        // cctor is seen storing s_maxDateOnly / s_minDateOnly.
        public static void TimeZoneInfoCctorWithDateBounds()
        {
            TimeZoneInfoCctorCommon();
            TzMaxDateOnlyField(null) = new global::System.DateTime(9999, 12, 31);
            TzMinDateOnlyField(null) = new global::System.DateTime(1, 1, 2);
        }
    }
}
