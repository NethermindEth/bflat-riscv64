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

    // A riscv32 platform without unwind tables, with the post-link steps of one.
    private const string Rv32Platform = """
        {
          "schema": 2,
          "name": "rv32vm",
          "postlink": [
            {"tool": "patch_elf", "args": ["--nop-fences", "--unsigned-muldiv"]},
            {"tool": "flat_image", "args": ["--suffix", ".bin"]}
          ],
          "platform": {
            "aliases": {"libc": "rv32vm"},
            "base": {"os": "linux", "arch": "riscv32", "libc": "musl"},
            "requires": ["nofp", "rhp_native", "ugc-zero"],
            "unwind": false,
            "linker_script": "rv32vm.ld",
            "entry": "rv32vm_entry.o"
          }
        }
        """;

    private static ManifestLink Rv32Link(TempDir dir)
    {
        BflatManifest platform = BflatManifest.Load(dir.File("rv32vm.bflat.manifest", Rv32Platform));
        var build = new ManifestTarget
        {
            Arch = "riscv32",
            Os = "linux",
            Libc = "rv32vm",
            Eh = "unwind",
            Stdlib = "dotnet",
            Optimize = true,
            Compiler = new HashSet<string>(StringComparer.Ordinal),
        };
        return ManifestLink.Create(platform, ManifestLink.LoadModules(Repo.Modules), ManifestLink.TargetFor(platform, build),
            Array.Empty<BflatManifest>());
    }

    [Fact]
    public void ARiscV32PlatformLinksTheRv32ObjectsOfTheModules()
    {
        using var dir = new TempDir();
        ManifestLink link = Rv32Link(dir);

        IEnumerable<string> objects = Enum.GetValues<ObjectGroup>().SelectMany(g => link.Objects(g)).Select(o => o.Path);
        Assert.Contains(objects, p => p.EndsWith("/nofp/module.rv32.o", StringComparison.Ordinal));
        Assert.Contains(objects, p => p.EndsWith("/ugc-zero/release-riscv32/uGCHeap.cpp.obj", StringComparison.Ordinal));
        Assert.DoesNotContain(objects, p => p.EndsWith("/module.o", StringComparison.Ordinal) || p.Contains("/release/", StringComparison.Ordinal));
        BflatManifest rhpNative = link.Manifests.Single(m => m.Name == "rhp_native");
        Assert.Contains("RhpAssignRefRiscV32", link.Wraps(rhpNative));
        Assert.DoesNotContain("RhpAssignRefRiscV64", link.Wraps(rhpNative));
    }

    [Fact]
    public void APlatformWithoutUnwindTablesBuildsAsWithRemoveEh()
    {
        using var dir = new TempDir();

        Assert.Equal("failfast", Rv32Link(dir).Target.Eh);
    }

    [Fact]
    public void ThePostlinkStepsOfARiscV32PlatformAreRead()
    {
        using var dir = new TempDir();

        Assert.Equal(new[] { ("patch_elf", "--nop-fences --unsigned-muldiv"), ("flat_image", "--suffix .bin") },
            Rv32Link(dir).PostlinkSteps().Select(s => (s.Tool, string.Join(" ", s.Args))));
    }

    [Fact]
    public void APostlinkToolMustBeBuiltIn()
    {
        using var dir = new TempDir();
        string path = dir.File("m/bflat-manifest.json", """{"schema": 2, "name": "m", "postlink": [{"tool": "objcopy"}]}""");

        var ex = Assert.Throws<ManifestException>(() => BflatManifest.Load(path));
        Assert.Contains("flat_image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExternalDownloadsOfUgcAreOnePerArchitecture()
    {
        BflatManifest ugc = ManifestLink.LoadModules(Repo.Modules)["ugc-zero"];

        string Tag(string arch) => ugc.Externals
            .Single(x => ManifestCondition.Applies(x.When, new ManifestTarget { Arch = arch, Os = "linux" })).Tag;
        Assert.Equal("v1.0.13", Tag("riscv64"));
        Assert.Equal("v1.0.15", Tag("riscv32"));
    }

    [Fact]
    public void AnExternalConditionTestsOnlyTheTarget()
    {
        using var dir = new TempDir();
        string path = dir.File("m/bflat-manifest.json", """
            {"schema": 2, "name": "m", "external": [{"repo": "https://example.org/r", "tag": "t", "release": "a.tar.gz", "dir": "d", "when": {"flag": "noos"}}]}
            """);

        Assert.Throws<ManifestException>(() => BflatManifest.Load(path));
    }

    [Fact]
    public void AnUnknownKeyIsAnError()
    {
        using var dir = new TempDir();
        string path = dir.File("m/bflat-manifest.json", """{"schema": 2, "name": "m", "checks": {"il_floats": true}}""");

        Assert.Throws<ManifestException>(() => BflatManifest.Load(path));
    }
}
