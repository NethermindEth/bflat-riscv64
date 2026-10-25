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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

using Xunit;

namespace Bflat.UnitTests;

public class PackageSignatureTests
{
    private static readonly Dictionary<string, string> Entries = new()
    {
        ["pkg.nuspec"] = "<package/>",
        ["runtimes/linux-riscv64/native/zisk.bflat.manifest"] = "{}",
        ["runtimes/linux-riscv64/native/libziskos.a"] = "!<arch>\n",
    };

    [Fact]
    public void ParseKeys_ReadsBase64ListsAndPem()
    {
        using var a = new TestSigner();
        using var b = new TestSigner();

        Assert.Empty(PackageSignature.ParseKeys(null));
        Assert.Empty(PackageSignature.ParseKeys("  "));
        Assert.Equal(2, PackageSignature.ParseKeys($"{a.PublicKeyBase64}, {b.PublicKeyBase64}").Count);
        Assert.Equal(2, PackageSignature.ParseKeys($"{a.PublicKeyBase64}\n{b.PublicKeyBase64}").Count);

        string pem = $"-----BEGIN PUBLIC KEY-----\n{a.PublicKeyBase64}\n-----END PUBLIC KEY-----\n";
        Assert.Equal(a.PublicKey, Assert.Single(PackageSignature.ParseKeys(pem)));
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]
    [InlineData("-----BEGIN PUBLIC KEY-----\n-----END CERTIFICATE-----")]
    public void ParseKeys_RejectsWhatIsNotAKey(string text)
    {
        Assert.Throws<PackageSignatureException>(() => PackageSignature.ParseKeys(text));
    }

    [Fact]
    public void VerifyNupkg_AcceptsASignedPackage()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries);

        Assert.Equal(PackageSignature.Status.Valid, PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));
    }

    [Fact]
    public void VerifyNupkg_AcceptsTheSecondOfTwoKeys()
    {
        using var dir = new TempDir();
        using var old = new TestSigner();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries);

        Assert.Equal(PackageSignature.Status.Valid,
            PackageSignature.VerifyNupkg(nupkg, new[] { old.PublicKey, signer.PublicKey }));
    }

    [Fact]
    public void VerifyNupkg_ReportsAnUnsignedPackage()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries, sign: false);

        Assert.Equal(PackageSignature.Status.Unsigned, PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));
    }

    [Fact]
    public void VerifyNupkg_IgnoresTheNuGetRepositorySignature()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries);
        using (ZipArchive zip = ZipFile.Open(nupkg, ZipArchiveMode.Update))
        {
            using Stream s = zip.CreateEntry(".signature.p7s").Open();
            s.Write(new byte[] { 1, 2, 3 });
        }

        Assert.Equal(PackageSignature.Status.Valid, PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));
    }

    public static TheoryData<string, Action<Dictionary<string, byte[]>>> Tampering => new()
    {
        { "changed entry", d => d["pkg.nuspec"] = Encoding.UTF8.GetBytes("<evil/>") },
        { "extra entry", d => d["runtimes/linux-riscv64/native/extra.o"] = new byte[] { 0x7f } },
        { "missing entry", d => d.Remove("runtimes/linux-riscv64/native/libziskos.a") },
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public void VerifyNupkg_RejectsAPackageThatDiffersFromItsList(string what, Action<Dictionary<string, byte[]>> tamper)
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries, tamper: tamper);

        var ex = Assert.Throws<PackageSignatureException>(() => PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));
        Assert.Contains("list", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(what);
    }

    [Fact]
    public void VerifyNupkg_RejectsTheSignatureOfAnotherKey()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        using var other = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries);

        var ex = Assert.Throws<PackageSignatureException>(() => PackageSignature.VerifyNupkg(nupkg, new[] { other.PublicKey }));
        Assert.Contains("not valid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyNupkg_RejectsAListWithoutSignature()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries, sign: false);
        using (ZipArchive zip = ZipFile.Open(nupkg, ZipArchiveMode.Update))
        {
            using Stream s = zip.CreateEntry(PackageSignature.ListName).Open();
            s.Write(Encoding.UTF8.GetBytes("x"));
        }

        Assert.Throws<PackageSignatureException>(() => PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));
    }

    [Theory]
    [InlineData("0123  short-hash\n")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 one-space\n")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  a\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  a\n")]
    public void VerifyList_RejectsABadList(string text)
    {
        using var signer = new TestSigner();
        byte[] list = Encoding.UTF8.GetBytes(text);

        Assert.Throws<PackageSignatureException>(() =>
            PackageSignature.VerifyList(list, signer.Sign(list), new[] { signer.PublicKey }, "test"));
    }

    [Fact]
    public void VerifyFiles_ChecksEachFileByItsAssetName()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string a = dir.File("cache/libs-1.zip", "libs");
        string b = dir.File("cache/refs-1.zip", "refs");
        byte[] list = TestSigner.List(new[] { ("libs.zip", Encoding.UTF8.GetBytes("libs")), ("refs.zip", Encoding.UTF8.GetBytes("refs")) });
        byte[] sig = signer.Sign(list);
        byte[][] keys = { signer.PublicKey };

        PackageSignature.VerifyFiles(list, sig, keys, new[] { (a, "libs.zip"), (b, "refs.zip") }, "release");

        Assert.Throws<PackageSignatureException>(() =>
            PackageSignature.VerifyFiles(list, sig, keys, new[] { (a, "refs.zip") }, "release"));
        Assert.Throws<PackageSignatureException>(() =>
            PackageSignature.VerifyFiles(list, sig, keys, new[] { (a, "other.zip") }, "release"));
    }

    /// <summary>
    /// The real signer: sign_package.py with openssl. Without python3 or
    /// openssl on the machine, the test has nothing to run and passes.
    /// </summary>
    [Fact]
    public void SignPackageScript_MakesWhatBflatAccepts()
    {
        using var dir = new TempDir();
        using var signer = new TestSigner();
        string nupkg = signer.Nupkg(Path.Combine(dir.Path, "p.nupkg"), Entries, sign: false);
        string asset = dir.File("assets/libs.zip", "libs");
        string script = Path.Combine(Repo.Root, "src", "bflat", "scripts", "sign_package.py");

        if (!Run("python3", "--version", null) || !Run("openssl", "version", null))
            return;

        Assert.True(Run("python3", $"-I \"{script}\" nupkg \"{nupkg}\"", signer.PrivateKeyPem));
        Assert.Equal(PackageSignature.Status.Valid, PackageSignature.VerifyNupkg(nupkg, new[] { signer.PublicKey }));

        string outDir = Path.Combine(dir.Path, "sig");
        Assert.True(Run("python3", $"-I \"{script}\" files \"{outDir}\" \"{asset}\"", signer.PrivateKeyPem));
        PackageSignature.VerifyFiles(
            File.ReadAllBytes(Path.Combine(outDir, PackageSignature.ListName)),
            File.ReadAllBytes(Path.Combine(outDir, PackageSignature.SignatureName)),
            new[] { signer.PublicKey }, new[] { (asset, "libs.zip") }, "release");
    }

    private static bool Run(string program, string args, string key)
    {
        var psi = new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (key != null)
            psi.Environment["BFLAT_SIGNING_KEY"] = key;
        try
        {
            using Process p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
