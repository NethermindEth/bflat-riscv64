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
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bflat.UnitTests;

/// <summary>A directory that is deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bflat-unit-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string text)
    {
        string path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A signing key and the signer that sign_package.py is: the same list
/// format and the same DER signature, made with .NET instead of openssl.
/// </summary>
internal sealed class TestSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    public string PrivateKeyPem => _key.ExportPkcs8PrivateKeyPem();

    public static byte[] List(IEnumerable<(string Name, byte[] Data)> files) =>
        Encoding.UTF8.GetBytes(string.Concat(files
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant()}  {f.Name}\n")));

    public byte[] Sign(byte[] list) =>
        _key.SignData(list, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    /// <summary>
    /// Writes a .nupkg with <paramref name="entries"/>. With
    /// <paramref name="sign"/>, the list and the signature are two more
    /// entries, as sign_package.py adds them. <paramref name="tamper"/> can
    /// change the entries after the list was made.
    /// </summary>
    public string Nupkg(string path, IDictionary<string, string> entries, bool sign = true,
        Action<Dictionary<string, byte[]>> tamper = null)
    {
        var data = entries.ToDictionary(e => e.Key, e => Encoding.UTF8.GetBytes(e.Value), StringComparer.Ordinal);
        byte[] list = List(data.Select(e => (e.Key, e.Value)));
        tamper?.Invoke(data);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach ((string name, byte[] bytes) in data)
                Write(zip, name, bytes);
            if (sign)
            {
                Write(zip, PackageSignature.ListName, list);
                Write(zip, PackageSignature.SignatureName, Sign(list));
            }
        }
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using Stream s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    public void Dispose() => _key.Dispose();
}

internal static class Repo
{
    /// <summary>The repository root: the directory above the test output that has src/bflat/modules.</summary>
    public static string Root
    {
        get
        {
            for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "src", "bflat", "modules")))
                    return dir;
            }
            throw new DirectoryNotFoundException("no src/bflat/modules above " + AppContext.BaseDirectory);
        }
    }

    public static string Modules => Path.Combine(Root, "src", "bflat", "modules");
}
