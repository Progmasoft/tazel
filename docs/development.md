<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Development

Tazel needs the .NET 10 SDK; `global.json` holds it to a 10.0 SDK and runs tests on Microsoft.Testing.Platform.

```text
dotnet build Tazel.slnx
dotnet test --solution Tazel.slnx
dotnet test --solution Tazel.slnx --coverlet --coverlet-output-format cobertura
dotnet run --project src/Progmasoft.Tazel -- build //... (in a module)
```

Every warning of the compiler and of the .NET analyzers is an error. Packages and their versions are in
`Directory.Packages.props`; the tools (SharpFuzz, DocFX) are in `dotnet-tools.json`, restored by `dotnet tool restore`.

## Tests

The tests that build with a compiler or zig run when one is on PATH and are skipped otherwise.

Snapshots are kept with [Snapshooter](https://github.com/SwissLife-OSS/Snapshooter) in
`tests/Progmasoft.Tazel.Tests/__snapshots__/`, through `Snapshots.Match`. A snapshot that differs fails with the lines
that differ, a few lines around each, and the path of the new snapshot in `__snapshots__/__mismatch__/`; when the
change is right, move it over the stored one and commit it. A test without a stored snapshot writes one, except with
`SNAPSHOOTER_STRICT_MODE=true`, as in CI, where it fails.

## Fuzzing

The readers of what users write (`tazel_custom.txt`, `tazel_options.txt`, `tazel_envs.txt`, labels, depfiles, command lines, the `License` of a module) are fuzz
targets. With [libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet):

```text
dotnet tool restore
dotnet publish fuzz/Progmasoft.Tazel.Fuzz -c Release -o artifacts/fuzz
dotnet sharpfuzz artifacts/fuzz/tazel.dll
libfuzzer-dotnet --target_path=artifacts/fuzz/Progmasoft.Tazel.Fuzz --target_arg=custom fuzz/corpus/custom
```

A target may refuse an input with the exception of its kind; any other exception, a hang or a crash is a finding.
`fuzz/corpus/` holds the seeds.

## Benchmarks

```text
dotnet run -c Release --project benchmarks/Progmasoft.Tazel.Benchmarks -- --filter '*'
```

## CI

| Workflow | What it runs |
| --- | --- |
| `ci.yml` | build and tests on Ubuntu 26.04 (with Zig), Windows Server 2025 and macOS 15, coverage to Codecov, DocFX |
| `fuzzing.yml` | each fuzz target for one minute on a pull request and thirty minutes every night |
| `codeql.yml` | CodeQL for C# and the workflows |
| `dependency-review.yml` | the dependencies a pull request adds |
| `zizmor.yml` | the security of the workflows |

Renovate proposes updates of the NuGet packages, the tools and the actions once a week, a week after their release.
