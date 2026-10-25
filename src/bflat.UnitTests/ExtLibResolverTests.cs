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
using System.IO;
using System.Threading.Tasks;

using Xunit;

namespace Bflat.UnitTests;

public class ExtLibResolverTests
{
    private const string Manifest = """
        {
          "schema": 2,
          "name": "pkg",
          "objects": [{"file": "libpkg.a"}]
        }
        """;

    private static readonly Dictionary<string, string> Entries = new()
    {
        ["pkg.nuspec"] = "<package/>",
        ["runtimes/linux-riscv64/native/pkg.bflat.manifest"] = Manifest,
        ["runtimes/linux-riscv64/native/libpkg.a"] = "!<arch>\n",
    };

    [Fact]
    public async Task ASignedNupkgGivesItsManifests()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "pkg.nupkg"), Entries);
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: false);

        List<string> manifests = await ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust);

        string manifest = Assert.Single(manifests);
        Assert.EndsWith("pkg.bflat.manifest", manifest, StringComparison.Ordinal);
        BflatManifest m = Assert.Single(ExtLibResolver.LoadPackage(manifests, "riscv64", "linux", null));
        Assert.Equal("pkg", m.Name);
    }

    [Fact]
    public async Task AnUnsignedNupkgIsAnError()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "pkg.nupkg"), Entries, sign: false);
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: false);

        var ex = await Assert.ThrowsAsync<PackageSignatureException>(() => ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));
        Assert.Contains("--allow-unsigned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowUnsignedAcceptsAnUnsignedNupkg()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "pkg.nupkg"), Entries, sign: false);
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: true);

        Assert.Single(await ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));
    }

    [Fact]
    public async Task AllowUnsignedDoesNotAcceptABadSignature()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "pkg.nupkg"), Entries,
            tamper: d => d["runtimes/linux-riscv64/native/libpkg.a"] = new byte[] { 0x7f, (byte)'E' });
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: true);

        await Assert.ThrowsAsync<PackageSignatureException>(() => ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));
    }

    [Fact]
    public async Task WithoutKeysNothingIsChecked()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "pkg.nupkg"), Entries, sign: false);
        var trust = new PackageTrust(Array.Empty<byte[]>(), allowUnsigned: false);

        Assert.Single(await ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));
    }

    [Fact]
    public async Task ALocalManifestIsNotChecked()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string manifest = dir.File("out/pkg.bflat.manifest", Manifest);
        dir.File("out/other.bflat.manifest", Manifest.Replace("\"pkg\"", "\"other\"", StringComparison.Ordinal));
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: false);

        List<string> manifests = await ExtLibResolver.ResolveManifests(manifest, dir.Path, false, trust);

        Assert.Equal(2, manifests.Count);
        Assert.Equal(Path.GetFullPath(manifest), Path.GetFullPath(manifests[0]));
    }

    [Fact]
    public void AManifestFromAUrlCannotBeSigned()
    {
        using var signer = new TestSigner();
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: false);

        Assert.Throws<PackageSignatureException>(() => trust.CheckUnsignable("https://example.com/pkg.bflat.manifest"));
        new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: true).CheckUnsignable("https://example.com/pkg.bflat.manifest");
    }

    [Fact]
    public async Task AChangedPackageIsExtractedAgain()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        var trust = new PackageTrust(new[] { signer.PublicKey }, allowUnsigned: false);
        string nupkg = Path.Combine(dir.Path, "pkg.nupkg");

        signer.Nupkg(nupkg, Entries);
        string first = Assert.Single(await ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));
        File.Delete(nupkg);
        var changed = new Dictionary<string, string>(Entries) { ["runtimes/linux-riscv64/native/libpkg.a"] = "!<arch>\nnew" };
        signer.Nupkg(nupkg, changed);
        string second = Assert.Single(await ExtLibResolver.ResolveManifests(nupkg, dir.Path, false, trust));

        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
    }
}
