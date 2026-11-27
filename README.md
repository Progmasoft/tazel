<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Tazel

Tazel is a build system and package manager for C, C++, Objective-C, Objective-C++ and Zig, written in C# for .NET 10.
A module has a `MODULE.csx` at its root and a `BUILD.csx` in each package; both are C# scripts. Targets are named by
labels, as in Bazel: `//app:greeter`, `//:math`, `@dep//:lib`.

```cs
// BUILD.csx
using Progmasoft.Tazel.Dsl.Build.Prelude;

var math = CC.StaticLibrary("math", new()
{
    Sources = new Glob("src/math/*.cpp"),
    Headers = new Glob("include/math/*.hpp"),
    IncludeDirectories = ["include"],
    CppCompilerOptions = ["-std=c++23"],
    Visibility = Visibility.Public
});

CC.Executable("greeter", new()
{
    Sources = ["src/main.cpp"],
    Depends = [math],
    RuntimeData = ["assets/messages.txt"]
});

CC.Test("math_test", new() { Sources = ["tests/math_test.cpp"], Depends = [math] });
```

```text
tazel build //...
tazel run //:greeter -- --language en
tazel test //... --feature asan
tazel build //... --platform linux-aarch64 --mode release
tazel package
```

- **Families:** `CC` (C and C++; the language of each file comes from its extension), `ObjectiveC` (with C and C++
  files beside), and `Zig` (modules, libraries, executables and tests from a root file).
- **Toolchains** are declared as in Bazel, with features that hold flags per action, and registered in `MODULE.csx`;
  without one, Tazel uses the clang, gcc or zig of this machine.
- **Builds** run each compiler command directly, many at once. An action reruns only when its command, an input or
  an output changed by content.
- **Dependencies** on other modules come from their reproducible `.tazel.tar.xz` archives, checked against their
  SHA-256.

The [documentation](docs/README.md) covers the project files, the commands and how Tazel is built.

## Installing Tazel

Tazel runs on the .NET 10 runtime. Each [release](https://github.com/Progmasoft/tazel/releases) has an archive for
Linux (`linux-x64`, `linux-arm64`, and `linux-musl-x64`, `linux-musl-arm64` for musl systems such as Alpine), macOS
(`osx-arm64`, `osx-x64`) and Windows (`win-x64`, `win-arm64`), and a `.sha256sums` file to check them against:

```text
sha256sum --check --ignore-missing tazel-0.2.0.sha256sums
tar -xJf tazel-0.2.0-linux-x64.tar.xz
tazel-0.2.0-linux-x64/tazel version
```

Put the unpacked directory on `PATH`. On Windows, unpack the `.zip` and run `tazel.exe`. Plugins are built with the
NuGet packages `Progmasoft.Tazel.PluginSdk` and `Progmasoft.Tazel.PluginApi` ([plugins](docs/plugins.md)).

## Building Tazel

```text
dotnet build Tazel.slnx
dotnet test --solution Tazel.slnx
dotnet run --project src/Progmasoft.Tazel -- version
```

## License

Tazel is licensed under the [MIT License](LICENSE.txt), with the [Progmasoft Patent Grant](PATENTS.md). The
libraries it ships with are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
