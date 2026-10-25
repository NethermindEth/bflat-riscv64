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
| The release archive of a module (`external` with `release`: uGC for `ugc-zero`) | When the modules are built (`build.sh modules`) | Two assets of the release | `build.sh`, with `bflat-modules verify-release` |
| An `--extlib` `.nupkg` (bflat-libziskos, bflat-sp1, bflat-openvm) | When a guest is built | Two entries in the package | `bflat build` |

A local `.bflat.manifest` is not checked: it is a build of the developer
(for example `platform/build.sh` output, or `BFLAT_PLATFORMS` in the
tests). A `.bflat.manifest` from a URL cannot have a signature, so bflat
treats it as unsigned. A module `external` that names no `release` is a
git clone of a tag, and has no signature.

## Keys

There is one key pair for all the repositories.

| Key | Where it is | Who uses it |
|-----|-------------|-------------|
| Private key | The GitHub Actions **secret** `BFLAT_SIGNING_KEY` (PEM) | The sign job (`sign.yml`) of the release workflows of bflat-libziskos, bflat-sp1, bflat-openvm, dotnet-riscv and uGC |
| Public key | The GitHub Actions **variable** `BFLAT_SIGNING_PUBLIC_KEY` (base64 DER) | The bflat build and `build.sh modules`. bflat writes it into its binary. The publish jobs of the release workflows check the signature with it before they publish. |

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

`src/bflat/scripts/sign_package.py` writes the lists, and openssl makes the
signatures (see [The release jobs](#the-release-jobs)).
`src/bflat/PackageSignature.cs` reads them; bflat and bflat-modules compile
it.

## Rules

| Case | bflat build of a guest | bflat build | `build.sh modules` |
|------|------------------------|-------------|--------------------|
| Signature is valid | The package is used. | The release is used. | The archive is unpacked. |
| Signature is not valid, or a file does not agree with the list | Error. | Error. | Error. |
| No signature | Error, unless `--allow-unsigned`; then a warning. | Error, unless `-p:AllowUnsignedRuntime=true`; then a message. | Error, unless `ALLOW_UNSIGNED_EXTERNAL=true`; then a message. |
| No trusted key | A warning. Nothing is checked. | A message. Nothing is checked. | A message. Nothing is checked. |

A bad signature is always an error: no option accepts it.

The bflat build checks again when a download changes. It writes the stamp
`obj/bflat-signature-<tag>.verified` after a good check.

bflat extracts a `.nupkg` into a directory with the hash of the package in
its name. Thus a cached extraction is used only for the same package bytes
that bflat checked.

## The release jobs

The private key is only in the job of the reusable workflow
`.github/workflows/sign.yml` of this repository. That job runs no build:
code that a release job builds or downloads never runs where the key is.
A release job has three jobs:

1. The build job makes the packages and writes the lists of their SHA-256
   hashes into an artifact. It needs no key.

   ```console
   $ python3 scripts/sign_package.py list-nupkg signature artifacts/package/release/*.nupkg
   $ python3 tools/sign_package.py list-files signature <asset>...
   ```

   `list-nupkg` writes `<package>.nupkg.bflat-signature.sha256` for each
   package. `list-files` writes `bflat-signature.sha256` for the assets of a
   release. When the assets of one release come from more than one job (as
   in dotnet-riscv), each job uploads the list of its assets, and a small
   job joins them into one list with
   `sign_package.py merge <out-dir> <list>...`.

2. The sign job calls `sign.yml` with the name of that artifact and the
   secret. It signs each `*.sha256` list with
   `openssl dgst -sha256 -sign`, and writes each list and its `.sig` into the
   artifact `bflat-signatures`. It signs only a file of `<sha256>  <name>`
   lines, and it shows each list in the log.

   ```yaml
   sign:
     needs: build-nupkg
     uses: NethermindEth/bflat-riscv64/.github/workflows/sign.yml@master
     with:
       lists: bflat-signature-lists
     secrets:
       BFLAT_SIGNING_KEY: ${{ secrets.BFLAT_SIGNING_KEY }}
   ```

3. The publish job adds the signatures and publishes. For a package,
   `embed` makes sure that the package agrees with its list and adds the list
   and the signature as two entries. For a release, `check-files` makes sure
   that the assets agree with the list, and the two files are two more
   assets. With the public keys in `BFLAT_SIGNING_PUBLIC_KEY`, both also check
   the signature, as bflat does.

   ```console
   $ python3 scripts/sign_package.py embed signature nupkg/*.nupkg
   $ python3 tools/sign_package.py check-files signature <asset>...
   ```

The package repositories sign before `dotnet nuget push` and the GitHub
release. The sign job runs only when the release job publishes, so a build
that publishes nothing needs no secret.

To sign on one machine with the private key in `BFLAT_SIGNING_KEY`,
`sign_package.py nupkg <package>...` and
`sign_package.py files <out-dir> <file>...` do the three steps at once. The
script stops with an error if `BFLAT_SIGNING_KEY` is not set.

`src/bflat/scripts/sign_package.py` is the original. The packages,
dotnet-riscv and uGC have copies (`scripts/` or `tools/`); keep them the
same.

To check a package by hand, with the public key in `BFLAT_SIGNING_PUBLIC_KEY`:

```console
$ dotnet run --project src/bflat-modules -- verify-nupkg <package>.nupkg
```
