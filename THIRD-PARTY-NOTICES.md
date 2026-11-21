<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Third-party notices

Tazel ships with the libraries below. Their licenses are in [LICENSES/](LICENSES/) and, for MIT, in
[LICENSE.txt](LICENSE.txt), whose text is the same with another copyright line.

| Library | License | Copyright |
| --- | --- | --- |
| [Spectre.Console](https://github.com/spectreconsole/spectre.console) | MIT | Copyright (c) Patrik Svensson, Phil Scott, Nils Andresen, Cédric Luthi |
| [Roslyn](https://github.com/dotnet/roslyn): Microsoft.CodeAnalysis.CSharp.Scripting and its assemblies | MIT | Copyright (c) .NET Foundation and Contributors |
| [NuGet.Versioning](https://github.com/NuGet/NuGet.Client) | Apache-2.0 | Copyright (c) .NET Foundation and Contributors |
| [Lzma.Net](https://github.com/zcsizmadia/Lzma.Net) | 0BSD | Copyright (c) 2026 Zoltan Csizmadia |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | MIT | Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. |

The libraries that only build and test Tazel (xUnit, CsCheck, Snapshooter, Coverlet, BenchmarkDotNet, SharpFuzz,
DocFX) are not part of what is shipped.

## The image of the MicroVM

The guest image (`microvm/build.cs`) holds, besides the .NET runtime of `tazel-guest` (MIT, .NET Foundation and
Contributors):

| Component | License | Source |
| --- | --- | --- |
| Linux 6.18.64 | GPL-2.0-only WITH Linux-syscall-note | <https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.18.64.tar.xz> |
| musl 1.2.6 (Alpine musl-1.2.6-r3) | MIT | <https://musl.libc.org> |
| libgcc and libstdc++ of GCC 15.2.0 (Alpine 15.2.0-r5) | GPL-3.0-or-later WITH GCC-exception-3.1 | <https://gcc.gnu.org> |

Cloud Hypervisor (Apache-2.0 AND BSD-3-Clause) and virtiofsd (Apache-2.0 AND BSD-3-Clause) are not shipped: Tazel
fetches Cloud Hypervisor from its release and uses the host's virtiofsd.
