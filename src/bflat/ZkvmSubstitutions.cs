// bflat C# compiler
// Copyright (C) 2021-2022 Michal Strehovsky
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

using Internal.TypeSystem;

// Policy for the --error-on-float gate on the zkVM targets: the methods whose
// floating point the gate reports as a warning, because it sits in a block
// the target never runs. The IL scanner itself lives in ZkvmIL.cs.

class ZkvmSubstitutions
{
    // Methods whose IL genuinely contains an FP conversion, but only inside a
    // block the JIT proves unreachable on this target (a disabled-feature guard
    // reached through a local, which IL-level substitution cannot constant-fold),
    // so no FP instruction is ever emitted. Confirmed against the FP-free emulator.
    // Matched by owning type + name (signature-independent) and reported as a
    // warning by the --error-on-float gate rather than failing the build.
    private static readonly (string typeNamespace, string typeName, string method)[] s_knownDeadFloatMethods =
    {
        // Lock.TryEnterSlow: `double durationNs = (Stopwatch.GetTimestamp()-start)
        // * 1e9 / Stopwatch.Frequency` behind `if (areContentionEventsEnabled)`,
        // where areContentionEventsEnabled = NativeRuntimeEventSource.Log.IsEnabled
        // (...) - always false because EventPipe/EventSource is disabled here.
        ("System.Threading", "Lock", "TryEnterSlow"),
    };

    internal static bool IsKnownDeadFloatMethod(MethodDesc method)
    {
        MethodDesc typical = method.GetTypicalMethodDefinition();
        if (typical.OwningType is not MetadataType mt)
            return false;
        foreach (var (ns, name, m) in s_knownDeadFloatMethods)
            if (typical.Name.StringEquals(m) && mt.Name.StringEquals(name) && mt.Namespace.StringEquals(ns))
                return true;
        return false;
    }
}
