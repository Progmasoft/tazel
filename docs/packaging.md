<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Progmasoft.Tazel.Packaging

The NuGet package Progmasoft.Tazel.Packaging reads and writes Tazel packages, as NuGet.Packaging does for NuGet. Tazel
itself and the plugin SDK use it; a tool that makes, checks or serves Tazel modules and plugins can use it too. Its
only dependency is Lzma.Net.

| Type | What it does |
| --- | --- |
| `TazelPackageIdentity` | a group, a name and a SemVer 2.0.0 version, checked; `BaseName` is `$group.$name-$version`, `FileName` adds `.tazel.tar.xz` |
| `TazelPackageArchive` | `Write` makes a `.tazel.tar.xz` that is the same bytes for the same files; `Unpack` refuses links, devices, absolute paths, `..`, entries outside the one top directory, more than 100 000 entries or 1 GiB |
| `Sha256Sums` | the SHA-256 of a file, its line of a `.sha256sums` file, and the sum a `.sha256sums` file gives for a name |
| `TazelPluginManifest` | `tazel-plugin.json`: read and checked, or written with its keys sorted |
| `Spdx.SpdxExpressionParser` | SPDX license expressions to a tree of `SpdxLicense`, `SpdxLicenseReference`, `SpdxWith`, `SpdxAnd` and `SpdxOr` |
| `Spdx.SpdxLicenseList` | the SPDX License List 3.29.0 and the check of an expression against it |

A problem of a package is a `TazelPackageException`, and a malformed expression an `SpdxExpressionException` whose
`Reason` says what is wrong and `Position` where.

## SPDX expressions

```csharp
using Progmasoft.Tazel.Packaging.Spdx;

var expression = SpdxExpressionParser.Parse("MIT OR GPL-2.0-only WITH Classpath-exception-2.0");
foreach (var problem in SpdxLicenseList.Current.Check(expression))
{
    Console.WriteLine($"{problem.Kind} {problem.Id}: {string.Join(", ", problem.Suggestions)}");
}
```

AND binds tighter than OR, and WITH tighter than both; `ToString` writes the parentheses the tree needs and no others,
so a written expression reads back to the same tree. Identifiers and operators are read in any case. The parser never
overflows the stack: parentheses nest at most 64 deep and an expression names at most 1000 licenses. `Check` reports
licenses and exceptions that are not on the list, with up to three close ones, and deprecated ones, with what replaces
them (`GPL-2.0-only` and `GPL-2.0-or-later` for `GPL-2.0`, only `GPL-2.0-or-later` for `GPL-2.0+`).

The list is `spdx/licenses.json` and `spdx/exceptions.json` of the repository, unmodified; `dotnet run spdx/update.cs --
<version>` replaces them with those of another release of spdx/license-list-data.
