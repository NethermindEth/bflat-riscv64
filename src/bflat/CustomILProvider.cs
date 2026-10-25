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

// The ILProvider that hands ILC a replaced body for two methods: the
// default message of OutOfMemoryException, and CorInfoImpl.getAsyncInfo,
// a part of ILCompiler, which the layout build compiles with bflat.

using System;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.TypeSystem;

class CustomILProvider : ILProvider
{
    private ILProvider inner;
    public TypeSystemContext TypeContext;

    public CustomILProvider(ILProvider innerProvider, TypeSystemContext typeContext)
    {
        inner = innerProvider;
        TypeContext = typeContext;
    }

    public override MethodIL GetMethodIL(MethodDesc method)
    {
        if (method.OwningType is MetadataType owningType &&
            owningType.Namespace.StringEquals("System") &&
            owningType.Name.StringEquals("OutOfMemoryException") &&
            method.Name.StringEquals("GetDefaultMessage"))
        {
            var stringType = TypeContext.GetWellKnownType(WellKnownType.String);
            FieldDesc emptyField = null;

            foreach (var field in stringType.GetFields())
            {
                if (field.Name.StringEquals("Empty") && field.IsStatic)
                {
                    emptyField = field;
                    break;
                }
            }

            if (emptyField == null)
            {
                throw new Exception("No Empty field found for OutOfMemoryException");
            }

            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ldsfld, 0x01, 0x00, 0x00, 0x00,
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                new object[] { emptyField }
            );
        }

        if (method.OwningType is MetadataType owningType2 &&
            owningType2.Namespace.StringEquals("Internal.JitInterface") &&
            owningType2.Name.StringEquals("CorInfoImpl") &&
            method.Name.StringEquals("getAsyncInfo"))
        {
            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                Array.Empty<object>()
            );
        }

        return inner.GetMethodIL(method);
    }
}
