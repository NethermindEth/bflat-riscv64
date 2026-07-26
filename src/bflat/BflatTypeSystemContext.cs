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

using Internal.IL;
using Internal.TypeSystem;

using ILCompiler;

class BflatTypeSystemContext : CompilerTypeSystemContext
{
    // The compiled module is registered through InputFilePaths in BuildCommand, so
    // the context resolves it by simple name via the standard loader. This avoids
    // the previous in-memory CacheOpenModule hook, which required a runtime-side
    // patch to expose it.
    public BflatTypeSystemContext(TargetDetails details, SharedGenericsMode genericsMode, DelegateFeature delegateFeatures)
        : base(details, genericsMode, delegateFeatures)
    {
    }
}
