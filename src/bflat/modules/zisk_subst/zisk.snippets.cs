extern alias corelib;
// Body-substitution snippets for the zisk/zisk_sim (rv64ima, no-FPU) targets.
//
// Each method here replaces the body of a CoreLib method that would otherwise
// emit hardware floating point. Writing the replacements in plain C# lets Roslyn
// - not hand-emitted IL - guarantee a valid body, which is far less error-prone
// than byte-level IL rewriting (see BuildCommand.SubstituteBodyMethodIL).
//
// Conventions:
//   * A snippet is a STATIC method whose argument layout matches the instance
//     method it replaces: the first parameter stands in for `this`, the rest are
//     the original parameters in order. The compiled body transplants directly
//     (arg0 = this = self, arg1 = ..., ...).
//   * `this` is typed as the real type when that type is accessible (ValueType,
//     Hashtable), or as `object` and cast inside when it is a private nested type
//     (System.Random.CompatSeedImpl/CompatDerivedImpl).
//   * This file is compiled by BuildCommand with accessibility checks bypassed
//     (BinderFlags.IgnoreAccessibility), so it may name private nested types and
//     touch private/internal members of the referenced CoreLib.
//
// This file is NOT part of bflat's own build (it references CoreLib internals
// that do not exist in bflat's compilation); it is shipped as an embedded
// resource and compiled at guest-build time. See bflat.csproj (Compile Remove +
// EmbeddedResource) and BuildCommand.TryCompileZiskSnippets.

namespace __ZiskSnippets
{
    internal static class Snippets
    {
        // ---- System.Random legacy compat PRNG -------------------------------
        // Next(int[,int]) does `(int)(Sample() * range)` in double; scale the raw
        // integer sample to the requested range with integer math instead. `self`
        // is the private impl type (not nameable in a signature), so it is typed
        // as object and cast inside; at run time it is always the impl.

        public static int RandomSeedNext1(object self, int maxValue)
        {
            var s = (global::System.Random.CompatSeedImpl)self;
            return (int)((long)s._prng.InternalSample() * maxValue / int.MaxValue);
        }

        public static int RandomSeedNext2(object self, int minValue, int maxValue)
        {
            var s = (global::System.Random.CompatSeedImpl)self;
            long range = (long)maxValue - minValue;
            return (int)((long)s._prng.InternalSample() * range / int.MaxValue) + minValue;
        }

        public static int RandomDerivedNext1(object self, int maxValue)
        {
            var s = (global::System.Random.CompatDerivedImpl)self;
            s._prng.EnsureInitialized(s._seed);
            return (int)((long)s._prng.InternalSample() * maxValue / int.MaxValue);
        }

        public static int RandomDerivedNext2(object self, int minValue, int maxValue)
        {
            var s = (global::System.Random.CompatDerivedImpl)self;
            s._prng.EnsureInitialized(s._seed);
            long range = (long)maxValue - minValue;
            return (int)((long)s._prng.InternalSample() * range / int.MaxValue) + minValue;
        }

        // ---- System.Collections.Hashtable -----------------------------------
        // Hashtable threads its load factor through a `float _loadFactor` field and
        // a `float loadFactor` ctor parameter that every overload funnels through -
        // the last FP in programs touching the legacy Hashtable. Every ctor is
        // replaced with a self-contained integer init (so no overload delegates and
        // materializes the 1.0f default), and rehash's `(int)(_loadFactor*n)` is
        // rewritten to integer `n*72/100`. 72/100 == the runtime's default load
        // factor 0.72f. Custom load factors are ignored: that only shifts the
        // resize threshold; collisions still chain. The zero-initialized object
        // leaves _loadFactor/_isWriterInProgress at their defaults.

        // Shared integer sizing: mirrors the (int,float) worker ctor with
        // _loadFactor fixed at 0.72 (rawsize = capacity/0.72 = capacity*100/72;
        // hashsize = rawsize>InitialSize ? GetPrime(rawsize) : InitialSize;
        // _loadsize = (int)(0.72*hashsize) = hashsize*72/100). InitialSize == 3.
        private static void HashtableInit(global::System.Collections.Hashtable self, int capacity, global::System.Collections.IEqualityComparer comparer)
        {
            int rawsize = capacity * 100 / 72;
            int hashsize = rawsize > 3 ? corelib::System.Collections.HashHelpers.GetPrime(rawsize) : 3;
            self._buckets = new global::System.Collections.Hashtable.Bucket[hashsize];
            self._loadsize = hashsize * 72 / 100;
            self._keycomparer = comparer;
        }

        public static void HashtableCtor0(global::System.Collections.Hashtable self)
            => HashtableInit(self, 0, null);

        public static void HashtableCtorCap(global::System.Collections.Hashtable self, int capacity)
            => HashtableInit(self, capacity, null);

        public static void HashtableCtorCapLf(global::System.Collections.Hashtable self, int capacity, float loadFactor)
            => HashtableInit(self, capacity, null);

        public static void HashtableCtorCapLfCmp(global::System.Collections.Hashtable self, int capacity, float loadFactor, global::System.Collections.IEqualityComparer comparer)
            => HashtableInit(self, capacity, comparer);

        public static void HashtableCtorCmp(global::System.Collections.Hashtable self, global::System.Collections.IEqualityComparer comparer)
            => HashtableInit(self, 0, comparer);

        public static void HashtableCtorCapCmp(global::System.Collections.Hashtable self, int capacity, global::System.Collections.IEqualityComparer comparer)
            => HashtableInit(self, capacity, comparer);

        // Verbatim copy of Hashtable.rehash(int) with the single FP line
        // `_loadsize = (int)(_loadFactor * newsize)` replaced by `newsize*72/100`.
        public static void HashtableRehash(global::System.Collections.Hashtable self, int newsize)
        {
            self._occupancy = 0;
            var newBuckets = new global::System.Collections.Hashtable.Bucket[newsize];
            for (int nb = 0; nb < self._buckets.Length; nb++)
            {
                var oldb = self._buckets[nb];
                if ((oldb.key != null) && (oldb.key != self._buckets))
                {
                    int hashcode = oldb.hash_coll & 0x7FFFFFFF;
                    self.putEntry(newBuckets, oldb.key, oldb.val, hashcode);
                }
            }
            self._isWriterInProgress = true;
            self._buckets = newBuckets;
            self._loadsize = newsize * 72 / 100;
            self.UpdateVersion();
            self._isWriterInProgress = false;
        }

        // ---- System.ValueType.GetHashCode -----------------------------------
        // RegularGetValueTypeHashCode hashes Single/Double struct fields through
        // HashCode.Add<float/double>, passing the value BY VALUE so it travels in
        // an FP register (flw/fld here, fmv.x.w/d inside Add<T>) - the last FP in
        // the image for struct-keyed dictionaries. Verbatim copy of the CoreLib
        // method with the two FP branches hashing the raw bit pattern via
        // HashCode.Add<int/long> instead. Hashing the bit pattern matches
        // NativeAOT's byte-wise ValueType.Equals for blittable fields, so the
        // hash/equals contract stays consistent.
        public static unsafe void ValueTypeRegularHashCode(global::System.ValueType self, ref global::System.HashCode hashCode, ref byte data, int numFields)
        {
            // We only take the hashcode for the first non-null field. That's what the CLR does.
            for (int i = 0; i < numFields; i++)
            {
                int fieldOffset = self.__GetFieldHelper(i, out corelib::Internal.Runtime.MethodTable* fieldType);
                ref byte fieldData = ref global::System.Runtime.CompilerServices.Unsafe.Add(ref data, fieldOffset);

                if (fieldType->ElementType == corelib::Internal.Runtime.EETypeElementType.Single)
                {
                    // raw 32-bit pattern hashed as int (was: hashCode.Add((float)...))
                    hashCode.Add(global::System.Runtime.CompilerServices.Unsafe.As<byte, int>(ref fieldData));
                }
                else if (fieldType->ElementType == corelib::Internal.Runtime.EETypeElementType.Double)
                {
                    // raw 64-bit pattern hashed as long (was: hashCode.Add((double)...))
                    hashCode.Add(global::System.Runtime.CompilerServices.Unsafe.As<byte, long>(ref fieldData));
                }
                else if (fieldType->IsPrimitive)
                {
                    hashCode.AddBytes(global::System.ValueType.GetSpanForField(fieldType, ref fieldData));
                }
                else if (fieldType->IsValueType)
                {
                    var fieldValue = (global::System.ValueType)global::System.Runtime.RuntimeExports.RhBox(fieldType, ref fieldData);
                    if (fieldValue != null)
                        hashCode.Add(fieldValue);
                    else
                        continue; // nullable type with no value, try next
                }
                else
                {
                    object fieldValue = global::System.Runtime.CompilerServices.Unsafe.As<byte, object>(ref fieldData);
                    if (fieldValue != null)
                        hashCode.Add(fieldValue);
                    else
                        continue; // null object reference, try next
                }
                break;
            }
        }

        // ---- System.Double / System.Single Equals+CompareTo -----------------
        // These are the leaf IEquatable/IComparable primitives that ValueType.
        // Equals (field-by-field, since a double field forbids the byte-wise
        // fast path: -0.0 == +0.0), Comparer<T>.Default, Array.Sort, sorted
        // collections and LINQ OrderBy reach for any double/float key or any
        // struct carrying one. Those paths are normal, not rare, so the bodies
        // cannot be body="remove"d (that fail-fasts every such lookup) - but the
        // originals compare in FP registers (fld + feq.d/flt.d), which an FPU-
        // less zkVM rejects at ELF->ROM conversion whether or not the code ever
        // runs. These replacements do the identical IEEE-754 comparison on the
        // raw bit patterns with integer instructions only.
        //
        // Only the object overloads are replaced: both operands then come from
        // memory (the boxed payload and the `this` byref), so nothing ever needs
        // an FP register. The double/float overloads take their argument by
        // value, so eliminating FP there depends on the argument-passing ABI and
        // is a separate question; they are not compiled by any guest so far, and
        // the --error-on-float-binary gate is what would catch it if one were.

        private static bool DoubleBitsIsNaN(long bits)
            => (bits & 0x7FFF_FFFF_FFFF_FFFF) > 0x7FF0_0000_0000_0000;

        // The IEEE total-order key: non-negative patterns are already ordered,
        // negative ones are mirrored. long.MinValue - bits (rather than an XOR
        // of the magnitude bits) maps -0.0 to 0, the same key as +0.0, which is
        // what makes -0.0 compare EQUAL to +0.0 as IEEE requires.
        private static long DoubleBitsKey(long bits)
            => bits < 0 ? long.MinValue - bits : bits;

        // Original: `m_value == obj || (IsNaN(m_value) && IsNaN(obj))`, i.e. NaN
        // equals NaN here (unlike ==), and +0.0 equals -0.0.
        private static bool DoubleBitsEqual(long a, long b)
        {
            if (DoubleBitsIsNaN(a) || DoubleBitsIsNaN(b))
                return DoubleBitsIsNaN(a) && DoubleBitsIsNaN(b);
            return a == b || ((a | b) << 1) == 0; // the << 1 drops the sign: ±0
        }

        // Original: <, >, == in order, then NaN handling - NaN sorts below
        // everything and equals itself.
        private static int DoubleBitsCompare(long a, long b)
        {
            if (DoubleBitsIsNaN(a))
                return DoubleBitsIsNaN(b) ? 0 : -1;
            if (DoubleBitsIsNaN(b))
                return 1;
            long ka = DoubleBitsKey(a), kb = DoubleBitsKey(b);
            return ka < kb ? -1 : (ka > kb ? 1 : 0);
        }

        private static bool SingleBitsIsNaN(int bits)
            => (bits & 0x7FFF_FFFF) > 0x7F80_0000;

        private static int SingleBitsKey(int bits)
            => bits < 0 ? int.MinValue - bits : bits;

        private static bool SingleBitsEqual(int a, int b)
        {
            if (SingleBitsIsNaN(a) || SingleBitsIsNaN(b))
                return SingleBitsIsNaN(a) && SingleBitsIsNaN(b);
            return a == b || ((a | b) << 1) == 0;
        }

        private static int SingleBitsCompare(int a, int b)
        {
            if (SingleBitsIsNaN(a))
                return SingleBitsIsNaN(b) ? 0 : -1;
            if (SingleBitsIsNaN(b))
                return 1;
            int ka = SingleBitsKey(a), kb = SingleBitsKey(b);
            return ka < kb ? -1 : (ka > kb ? 1 : 0);
        }

        // `this` on a struct instance method is a byref, so the stand-in first
        // parameter is `ref double`. Reading it (and the boxed payload reached
        // through Unsafe.Unbox, which yields a byref into the box rather than
        // loading the value) through Unsafe.As gives an integer load - a plain
        // `(double)obj` would be unbox.any and put the value in an FP register.
        // The type test stays a bare `is`, with no pattern variable, for the
        // same reason.

        public static bool DoubleEqualsObject(ref double self, object obj)
        {
            if (!(obj is double))
                return false;
            return DoubleBitsEqual(
                global::System.Runtime.CompilerServices.Unsafe.As<double, long>(ref self),
                global::System.Runtime.CompilerServices.Unsafe.As<double, long>(
                    ref global::System.Runtime.CompilerServices.Unsafe.Unbox<double>(obj)));
        }

        // The message is SR.Arg_MustBeDouble / SR.Arg_MustBeSingle spelled out.
        // Naming SR here instead would root the whole ResourceManager reader
        // (ResourceReader.LoadObjectV2 -> BinaryReader.ReadDouble/ReadSingle),
        // which carries FP of its own - the resource-string folding that turns
        // SR.get_Xxx into a literal for a CoreLib body does not reach a
        // transplanted one. The stock body ends up emitting exactly this
        // literal anyway.
        public static int DoubleCompareToObject(ref double self, object value)
        {
            if (value == null)
                return 1;
            if (!(value is double))
                throw new global::System.ArgumentException("Object must be of type Double.");
            return DoubleBitsCompare(
                global::System.Runtime.CompilerServices.Unsafe.As<double, long>(ref self),
                global::System.Runtime.CompilerServices.Unsafe.As<double, long>(
                    ref global::System.Runtime.CompilerServices.Unsafe.Unbox<double>(value)));
        }

        public static bool SingleEqualsObject(ref float self, object obj)
        {
            if (!(obj is float))
                return false;
            return SingleBitsEqual(
                global::System.Runtime.CompilerServices.Unsafe.As<float, int>(ref self),
                global::System.Runtime.CompilerServices.Unsafe.As<float, int>(
                    ref global::System.Runtime.CompilerServices.Unsafe.Unbox<float>(obj)));
        }

        public static int SingleCompareToObject(ref float self, object value)
        {
            if (value == null)
                return 1;
            if (!(value is float))
                throw new global::System.ArgumentException("Object must be of type Single.");
            return SingleBitsCompare(
                global::System.Runtime.CompilerServices.Unsafe.As<float, int>(ref self),
                global::System.Runtime.CompilerServices.Unsafe.As<float, int>(
                    ref global::System.Runtime.CompilerServices.Unsafe.Unbox<float>(value)));
        }

        // ---- System.Collections.Frozen.LengthBuckets ------------------------
        // CreateLengthBucketsArrayIfAppropriate rates a by-length string-lookup
        // optimization in double ratio math. Returning null = "use the fallback
        // frozen comparer", always a correct answer. Static method (no `this`), so
        // the parameter layout matches exactly.
        public static int[] LengthBucketsNone(string[] keys, global::System.Collections.Generic.IEqualityComparer<string> comparer, int minLength, int maxLength)
            => null;

        // ---- System.TimeZoneInfo..cctor -------------------------------------
        // The stock cctor computes s_daylightRuleMarker via
        // DateTime.MinValue.AddMilliseconds(2), whose inlined double scaling is
        // its only FPU code. This donor reproduces the WHOLE cctor (five field
        // initializations - the rest of the type's statics are consts) with the
        // marker built tick-exactly in integers: 2 ms = 20_000 ticks. Applied only
        // after BuildZiskBodySubstitutions verifies the original cctor still
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
        // BuildZiskBodySubstitutions picks this variant when the original
        // cctor is seen storing s_maxDateOnly / s_minDateOnly.
        public static void TimeZoneInfoCctorWithDateBounds()
        {
            TimeZoneInfoCctorCommon();
            TzMaxDateOnlyField(null) = new global::System.DateTime(9999, 12, 31);
            TzMinDateOnlyField(null) = new global::System.DateTime(1, 1, 2);
        }
    }
}
