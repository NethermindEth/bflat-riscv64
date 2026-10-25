// bflat C# compiler
// Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
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

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Bflat.UnitTests;

public class ManifestTests
{
    // A platform that requires the real modules of the repository, with
    // the parts of a platform manifest that these tests look at.
    private const string Platform = """
        {
          "schema": 2,
          "name": "testvm",
          "ilc": {"defines": ["ZKVM_TEST"]},
          "codegen": {
            "abi": "lp64",
            "isa": {"disable": ["c", "a", "f", "d"]},
            "options": [
              {"value": "JitObjectStackAllocationSize=2000", "when": {"optimize": true}},
              "EnableRiscV64Compressed=0"
            ]
          },
          "platform": {
            "aliases": {"libc": "testvm"},
            "base": {"os": "linux", "arch": "riscv64", "libc": "musl"},
            "requires": ["guest_subst", "nofp"],
            "linker_script": "testvm.ld",
            "entry": "testvm_entry.o"
          }
        }
        """;

    private static ManifestLink Link(TempDir dir, bool optimize)
    {
        BflatManifest platform = BflatManifest.Load(dir.File("testvm.bflat.manifest", Platform));
        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(Repo.Modules);
        var build = new ManifestTarget
        {
            Arch = "riscv64",
            Os = "linux",
            Libc = "testvm",
            Eh = "unwind",
            Stdlib = "dotnet",
            Optimize = optimize,
            Compiler = new HashSet<string>(StringComparer.Ordinal),
        };
        return ManifestLink.Create(platform, modules, ManifestLink.TargetFor(platform, build), Array.Empty<BflatManifest>());
    }

    [Fact]
    public void TheModulesOfTheRepositoryLoad()
    {
        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(Repo.Modules);

        Assert.Contains("guest_subst", modules.Keys);
        Assert.Contains("nofp", modules.Keys);
        Assert.All(modules.Values, m => Assert.Null(m.Platform));
    }

    [Fact]
    public void ACodegenOptionCanHaveACondition()
    {
        using var dir = new TempDir();

        Assert.Equal(new[] { "JitObjectStackAllocationSize=2000", "EnableRiscV64Compressed=0" },
            Link(dir, optimize: true).CodegenOptions.Where(o => !o.StartsWith("JitZk", StringComparison.Ordinal)));
        Assert.Equal(new[] { "EnableRiscV64Compressed=0" },
            Link(dir, optimize: false).CodegenOptions.Where(o => !o.StartsWith("JitZk", StringComparison.Ordinal)));
    }

    [Fact]
    public void ThePlatformGivesItsIsaAndDefines()
    {
        using var dir = new TempDir();
        ManifestLink link = Link(dir, optimize: true);

        Assert.Equal("-c,-a,-f,-d", link.Isa(_ => true));
        Assert.Equal("-f,-d", link.Isa(ext => ext is "f" or "d"));
        Assert.Equal("lp64", link.Abi);
        Assert.Equal(new[] { "ZKVM_TEST" }, link.Values(m => m.IlcDefines));
    }

    [Fact]
    public void GuestSubstGivesTheInvariantFeaturesAndTheDeadFloatList()
    {
        using var dir = new TempDir();
        ManifestLink link = Link(dir, optimize: true);

        Assert.True(link.Features["System.Globalization.Invariant"]);
        Assert.True(link.Features["System.TimeZoneInfo.Invariant"]);
        Assert.Equal(new[] { "System.Threading.Lock.TryEnterSlow" }, link.IlFloatAllowed);
        Assert.False(link.Checks.IlFloat);
    }

    [Theory]
    [InlineData("Lock.TryEnterSlow")]
    [InlineData("System..Lock.TryEnterSlow")]
    [InlineData("System.Threading.Lock.")]
    public void AnIlFloatAllowItemMustNameAMethod(string value)
    {
        using var dir = new TempDir();
        string path = dir.File("m/bflat-manifest.json", $$$"""
            {"schema": 2, "name": "m", "checks": {"il_float_allow": ["{{{value}}}"]}}
            """);

        var ex = Assert.Throws<ManifestException>(() => BflatManifest.Load(path));
        Assert.Contains("Namespace.Type.Method", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsAnError()
    {
        using var dir = new TempDir();
        string path = dir.File("m/bflat-manifest.json", """{"schema": 2, "name": "m", "checks": {"il_floats": true}}""");

        Assert.Throws<ManifestException>(() => BflatManifest.Load(path));
    }
}
