---
layout: default
title: Package signatures
eyebrow: Specification
lead: >
  bflat downloads code that goes into each guest: the dotnet-riscv runtime
  when bflat is built, and the bflat-* packages when a guest is built. A
  signature tells bflat that this code comes from our CI.
---

# Package signatures

## What is signed

| Download | When bflat downloads it | Where the signature is | What checks it |
|----------|-------------------------|------------------------|----------------|
| The dotnet-riscv release (runtime zips, `bflat.compiler` nupkg) | When bflat is built | Two assets of the release | `bflat.csproj`, with `bflat-modules verify-release` |
| An `--extlib` `.nupkg` (bflat-libziskos, bflat-sp1, bflat-openvm) | When a guest is built | Two entries in the package | `bflat build` |

A local `.bflat.manifest` is not checked: it is a build of the developer
(for example `platform/build.sh` output, or `BFLAT_PLATFORMS` in the
tests). A `.bflat.manifest` from a URL cannot have a signature, so bflat
treats it as unsigned.

## Keys

There is one key pair for all the repositories.

| Key | Where it is | Who uses it |
|-----|-------------|-------------|
| Private key | The GitHub Actions **secret** `BFLAT_SIGNING_KEY` (PEM) | The release jobs of bflat-libziskos, bflat-sp1, bflat-openvm and dotnet-riscv |
| Public key | The GitHub Actions **variable** `BFLAT_SIGNING_PUBLIC_KEY` (base64 DER) | The bflat build. bflat writes it into its binary. |

`BFLAT_SIGNING_PUBLIC_KEY` can have more than one key, separated by commas.
A signature is valid if one of the keys made it. Use this to change the key:
add the new public key, sign with the new private key, and then remove the
old public key.

To make the keys:

```console
$ openssl ecparam -name prime256v1 -genkey -noout -out bflat-signing.pem
$ openssl ec -in bflat-signing.pem -pubout -outform DER | base64 -w0
```

Put the content of `bflat-signing.pem` in the secret, and the base64 line
in the variable. Keep no other copy of the private key in a repository.

`bflat --info` shows the SHA-256 of each key that the binary trusts.

## Format

A signed set of files has two more files:

- `bflat-signature.sha256` has one line for each file, in the format of
  `sha256sum`: `<sha256 in lowercase hex>  <name>\n`.
- `bflat-signature.sha256.sig` is the ECDSA P-256 signature of the list with
  SHA-256, in DER. This is the output of `openssl dgst -sha256 -sign`.

In a `.nupkg`, both files are entries at the root of the archive. The list
names every other entry of the archive. bflat refuses a package that has an
entry that is not in the list, or that does not have an entry of the list.
The one exception is `.signature.p7s`: NuGet.org adds it after the push.

In a dotnet-riscv release, both files are assets of the release. The list
names the other assets.

`src/bflat/scripts/sign_package.py` makes the signatures. The packages and
dotnet-riscv have copies of it. `src/bflat/PackageSignature.cs` reads them;
bflat and bflat-modules compile it.

## Rules

| Case | bflat build of a guest | bflat build |
|------|------------------------|-------------|
| Signature is valid | The package is used. | The release is used. |
| Signature is not valid, or a file does not agree with the list | Error. | Error. |
| No signature | Error, unless `--allow-unsigned`; then a warning. | Error, unless `-p:AllowUnsignedRuntime=true`; then a message. |
| No trusted key | A warning. Nothing is checked. | A message. Nothing is checked. |

A bad signature is always an error: no option accepts it.

The bflat build checks again when a download changes. It writes the stamp
`obj/bflat-signature-<tag>.verified` after a good check.

bflat extracts a `.nupkg` into a directory with the hash of the package in
its name. Thus a cached extraction is used only for the same package bytes
that bflat checked.

## The release jobs

The package repositories sign the `.nupkg` after `dotnet pack` and before
`dotnet nuget push` and the GitHub release:

```console
$ BFLAT_SIGNING_KEY=... python3 scripts/sign_package.py nupkg artifacts/package/release/*.nupkg
```

dotnet-riscv signs the assets of the release before it makes the release:

```console
$ BFLAT_SIGNING_KEY=... python3 tools/sign_package.py files <dir> <asset>...
```

The script stops with an error if `BFLAT_SIGNING_KEY` is not set. A release
job thus cannot publish an unsigned release by mistake.

To check a package by hand, with the public key in `BFLAT_SIGNING_PUBLIC_KEY`:

```console
$ dotnet run --project src/bflat-modules -- verify-nupkg <package>.nupkg
```
