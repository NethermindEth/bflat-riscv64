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

        public static void TimeZoneInfoCctor()
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
    }
}
