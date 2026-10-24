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
using System.Text.RegularExpressions;

// Signatures of the packages that bflat downloads (docs/signing.md).
//
// A signed set of files has two more files:
//   bflat-signature.sha256      one line per file, "<sha256 hex>  <name>\n",
//                               the format of sha256sum
//   bflat-signature.sha256.sig  the ECDSA P-256 / SHA-256 signature of the
//                               list, DER, as openssl dgst -sha256 -sign
//                               writes it
// In a .nupkg both are entries at the root of the archive, and the list
// names every other entry. In a dotnet-riscv release both are assets of the
// release, and the list names the other assets.
//
// bflat and bflat-modules compile this file. The signer is
// scripts/sign_package.py: openssl makes the signature, and only this file
// reads it.
internal static class PackageSignature
{
    public const string ListName = "bflat-signature.sha256";
    public const string SignatureName = "bflat-signature.sha256.sig";

    /// <summary>The environment variable (and MSBuild property) with the trusted public keys.</summary>
    public const string KeysVariable = "BFLAT_SIGNING_PUBLIC_KEY";

    // NuGet.org adds its repository signature to a package after the push,
    // so the list cannot name it.
    private const string NuGetSignatureName = ".signature.p7s";

    public enum Status
    {
        Valid,
        Unsigned,
    }

    /// <summary>
    /// The trusted keys in <paramref name="text"/>: base64 DER
    /// SubjectPublicKeyInfo values, separated by commas or white space, or
    /// PEM "PUBLIC KEY" blocks.
    /// </summary>
    public static List<byte[]> ParseKeys(string text)
    {
        var keys = new List<byte[]>();
        if (string.IsNullOrWhiteSpace(text))
            return keys;

        IEnumerable<string> values = text.Contains("-----BEGIN", StringComparison.Ordinal)
            ? Regex.Matches(text, "-----BEGIN PUBLIC KEY-----(.*?)-----END PUBLIC KEY-----", RegexOptions.Singleline)
                .Select(m => Regex.Replace(m.Groups[1].Value, @"\s", ""))
            : text.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string value in values)
        {
            byte[] der;
            try
            {
                der = Convert.FromBase64String(value);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(der, out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new PackageSignatureException($"{KeysVariable} has a value that is not an EC public key: {ex.Message}");
            }
            keys.Add(der);
        }
        if (keys.Count == 0)
            throw new PackageSignatureException($"{KeysVariable} is set but has no public key");
        return keys;
    }

    /// <summary>
    /// Checks that one of <paramref name="keys"/> signed <paramref name="list"/>,
    /// and returns the list as name -> SHA-256 hex.
    /// </summary>
    public static Dictionary<string, string> VerifyList(byte[] list, byte[] signature, IReadOnlyList<byte[]> keys, string what)
    {
        bool signed = false;
        foreach (byte[] key in keys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            if (ecdsa.VerifyData(list, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                signed = true;
                break;
            }
        }
        if (!signed)
            throw new PackageSignatureException($"{what}: the signature is not valid for a trusted key");

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(list);
        }
        catch (DecoderFallbackException)
        {
            throw new PackageSignatureException($"{what}: {ListName} is not UTF-8");
        }
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            Match m = Regex.Match(line, "^([0-9a-f]{64})  (.+)$");
            if (!m.Success)
                throw new PackageSignatureException($"{what}: {ListName} has a line that is not '<sha256>  <name>': {line}");
            if (!hashes.TryAdd(m.Groups[2].Value, m.Groups[1].Value))
                throw new PackageSignatureException($"{what}: {ListName} names '{m.Groups[2].Value}' two times");
        }
        return hashes;
    }

    public static string Sha256Hex(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    /// <summary>
    /// Checks the signature inside a .nupkg. A package with no signature is
    /// <see cref="Status.Unsigned"/>; any other defect throws.
    /// </summary>
    public static Status VerifyNupkg(string path, IReadOnlyList<byte[]> keys)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            if (!entries.TryAdd(entry.FullName, entry))
                throw new PackageSignatureException($"{path}: the archive has the entry '{entry.FullName}' two times");
        }

        bool hasList = entries.TryGetValue(ListName, out ZipArchiveEntry listEntry);
        bool hasSignature = entries.TryGetValue(SignatureName, out ZipArchiveEntry signatureEntry);
        if (!hasList && !hasSignature)
            return Status.Unsigned;
        if (!hasList || !hasSignature)
            throw new PackageSignatureException($"{path}: the package has only one of {ListName} and {SignatureName}");

        Dictionary<string, string> hashes = VerifyList(ReadAll(listEntry), ReadAll(signatureEntry), keys, path);

        int named = 0;
        foreach ((string name, ZipArchiveEntry entry) in entries)
        {
            if (name == ListName || name == SignatureName || name == NuGetSignatureName)
                continue;
            if (!hashes.TryGetValue(name, out string expected))
                throw new PackageSignatureException($"{path}: '{name}' is not in the signed list");
            using Stream s = entry.Open();
            if (Sha256Hex(s) != expected)
                throw new PackageSignatureException($"{path}: '{name}' does not agree with the signed list");
            named++;
        }
        if (named != hashes.Count)
        {
            string missing = hashes.Keys.First(n => !entries.ContainsKey(n));
            throw new PackageSignatureException($"{path}: '{missing}' is in the signed list but not in the package");
        }
        return Status.Valid;
    }

    /// <summary>
    /// Checks downloaded files against a signed list of release assets. Each
    /// file is given with the asset name that the list uses for it.
    /// </summary>
    public static void VerifyFiles(byte[] list, byte[] signature, IReadOnlyList<byte[]> keys,
        IEnumerable<(string Path, string Name)> files, string what)
    {
        Dictionary<string, string> hashes = VerifyList(list, signature, keys, what);
        foreach ((string path, string name) in files)
        {
            if (!hashes.TryGetValue(name, out string expected))
                throw new PackageSignatureException($"{what}: '{name}' is not in the signed list");
            using FileStream s = File.OpenRead(path);
            if (Sha256Hex(s) != expected)
                throw new PackageSignatureException($"{what}: '{path}' does not agree with the signed list entry '{name}'");
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

internal sealed class PackageSignatureException : Exception
{
    public PackageSignatureException() { }
    public PackageSignatureException(string message) : base(message) { }
    public PackageSignatureException(string message, Exception inner) : base(message, inner) { }
}
