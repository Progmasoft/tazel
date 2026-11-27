<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Project files

A module is a directory with a `MODULE.csx`. Each directory below it with a `BUILD.csx` is a package; a directory with
a `MODULE.csx` of its own is another module and is not part of this one. Both files are C# scripts, run by Tazel with
the C# scripting of Roslyn; nothing else needs to be installed. Every project file sees `Progmasoft.Tazel.Dsl.Common`
(platforms, `Glob`, `Visibility`, `TestSize`, sizes) without a `using`.

A package's project file is `BUILD.csx`, looked up case-sensitively on every system; no other name is one. A
directory whose project file has an earlier name (`BUILD.tzl`, `BUILD.tazel`, `BUILD` or `Tazel.build`) stops the build
with a message; `tazel limited-time C#` renames them, until Tazel 1.0.0 (below).

## MODULE.csx

```cs
using System;
using Progmasoft.Tazel.Dsl.Module.Prelude;

module = Module.Define(new() {
    Group = "com.example",
    Name = "greeter",
    Version = "1.2.0",                         // SemVer
    Description = "Greets people.",
    Authors = ["Leitwolf"],
    License = "MIT",                           // an SPDX expression
    Readme = "README.md",                      // the default
    Compatibility = TazelVersion.AtLeast(0, 1) // the Tazel versions it builds with; PATCH may be left out
});

dependencies = Dependencies.Define(
    LocalModule.From(new() {
        Name = "local-lib",                    // its labels: @local-lib//:x
        Include = "deps/local.tazel.tar.xz",   // one archive, no glob
        Sha256 = "<sha256>"                    // or Sha256Sums = "deps/local.tazel.tar.xz.sha256sums"
    })
);

portalModules = PortalModules.Define(new() {
    Push = false,
    Exclude = [new Glob("docs/**"), "legacy/code.cpp"] // left out of the archive of tazel package
});

cache = Cache.Define(new() { Local = true });  // Local = false runs every action every time

execution = Execution.Define(new() { Jobs = 8, KeepGoing = false });

toolchains = Toolchains.Define("//toolchains:clang");
```

- `TazelVersion` has `Exactly`, `AtLeast`, `GreaterThan`, `AtMost`, `LessThan` and `Between`, which includes both
  ends. A Tazel outside the range stops with the range and its own version.
- A dependency is a `LocalModule`: Tazel checks its archive against the SHA-256 and unpacks it once below `.tazel/`.
  Its public targets are `@name//dir:x`. `Module.From`, a module of Tazel Portal Modules, is refused by this Tazel,
  which fetches nothing.
- `plugins` loads local plugin archives ([Plugins](plugins.md)).
- `Cache.Remote` is a remote cache, with its `PublicKey` and `SigningKey` ([Remote cache](remote-cache.md)). This
  Tazel reads `Execution.Remote` and `Execution.Memory` and says, as a warning, that it does not act on them.

## BUILD.csx

```cs
using System;
using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Build.Prelude;

var math = CC.StaticLibrary("math", new()
{
    Sources = new Glob("src/math/*.cpp", exclude: ["src/math/experimental_*.cpp"]),
    Headers = new Glob("include/math/*.hpp"),
    TextualHeaders = ["include/math/details.inl"],
    IncludeDirectories = ["include"],
    CompilerOptions = ["-Wall"],               // every file
    CCompilerOptions = ["-std=c17"],           // .c and .m files
    CppCompilerOptions = ["-std=c++23"],       // .cpp, .cc, .cxx and .mm files
    PreprocessorDefinitions = ["MATH_API=1"],  // this target and its dependents
    LocalPreprocessorDefinitions = ["MATH_BUILD=1"],
    LinkerOptions = ["-lm"],                   // a library's go to whatever links it
    AlwaysLink = false,
    Features = [CC.Enable("warnings")],
    Visibility = Visibility.Public
});

CC.SharedLibrary("greeter_shared", new()
{
    Sources = ["src/greeting.cpp", "src/legacy.c"],
    Depends = ["//:math"],
    LibraryVersion = new Version(1, 2, 0),     // libgreeter_shared.so.1.2.0, with the soname libgreeter_shared.so.1
    TargetConstraints = [Platform.LinuxAmd64]
});

CC.Executable("greeter", new()
{
    Sources = ["src/main.cpp"],
    Depends = ["//:math", ":greeter_shared"],
    LinkerOptions = Build.Mode == BuildMode.Release ? ["-s"] : [],
    LinkingMode = LinkingMode.PreferStatic,    // PreferShared, FullyStatic
    RuntimeData = ["assets/messages.txt"],
    Arguments = ["--language", "en"],
    EnvironmentVariables = new() { ["GREETER_MODE"] = "cli" },
    StampBuildInformation = true               // TAZEL_BUILD_LABEL and TAZEL_BUILD_VERSION
});

CC.Test("math_test", new()
{
    Sources = ["tests/math_test.cpp"],
    Depends = ["//:math", "@catch3//:main"],
    TestSize = TestSize.Small,                 // Small, Medium, Large, Enormous: 1, 5, 15, 60 minutes
    Timeout = TimeSpan.FromMinutes(2),
    Flaky = false,                             // true: a failure runs again, up to three times
    ShardCount = 2,                            // TEST_TOTAL_SHARDS and TEST_SHARD_INDEX
    InheritedEnvironmentVariables = ["HOME"]
});
```

- **Files.** A string is one path below the project file; `new Glob(pattern, exclude: [...])` is a pattern, with `*`,
  `?` and `**`. A glob leaves out hidden files and subpackages. Lists may mix both.
- **Labels.** `//:x` is target `x` of the root `BUILD.csx`, `//dir:x` of `dir/BUILD.csx`, `:x` of the same file,
  `@name//dir:x` of a dependency. `Depends` also takes the value a declaration returned.
- **Visibility.** `Private` (the default) is this file only, `Module` every package of the module, `Public` also the
  modules that depend on it.
- **Platforms.** `TargetConstraints` names the platforms a target builds for; without it, every platform, except that
  `ObjectiveC` targets build for the two macOS platforms. `//...` leaves out a target that does not build for the
  platform of the command, with every target that depends on it, and says how many it left out; naming it is an error.
  `Tags = ["manual"]` keeps a target out of `//...` as well.
- **Conditions** are C#: `Build.Mode` (`BuildMode.Debug`, `Release`), `Build.Platform`, `Build.OS`, `Build.Arch`.
- **Headers** of a target are found below the root of its module and below its `IncludeDirectories`, by it and by
  what depends on it. An entry is a path below the package, or `"."` for the package's own directory.

### Objective-C

```cs
ObjectiveC.StaticLibrary("objc_core", new()
{
    Sources = ["objc/Greeter.m", "objc/GreeterExtension.mm"],
    Headers = ["objc/Greeter.h"],
    NonAutomaticReferenceCountingSources = ["objc/LegacyMemory.m"], // every other file has ARC
    EnableClangModules = true,
    ModuleName = "GreeterCore",
    ModuleMap = "objc/module.modulemap",
    SdkFrameworks = ["Foundation"],
    WeakSdkFrameworks = ["Metal"],
    SdkDynamicLibraries = ["z"],
    Visibility = Visibility.Public
});

ObjectiveC.Executable("objc_greeter", new()
{
    Sources = ["objc/main.m"],
    Depends = ["//:objc_core"],
    Bundle = new() { Identifier = "com.example.greeter", InfoPlist = "objc/Info.plist" }
});

ObjectiveC.Test("objc_core_test", new()
{
    Sources = ["objc/tests/GreeterTests.m"],
    Depends = ["//:objc_core"],
    SdkFrameworks = ["Foundation", "XCTest"],
    TestRunner = ObjectiveCTestRunner.XCTest
});
```

`CC` targets take `SdkFrameworks`, `WeakSdkFrameworks` and `SdkDynamicLibraries` too. This Tazel builds an executable
with a `Bundle` as a plain program, and runs every test as an executable judged by its exit status.

### Zig

```cs
Zig.Module("zig_core", new() { RootSourceFile = "zig/core.zig", Visibility = Visibility.Public });

Zig.StaticLibrary("zig_math", new()
{
    RootSourceFile = "zig/math.zig",
    ModuleImports = new Dictionary<string, string> { ["core"] = "//:zig_core" },
    OptimizationMode = ZigOptimization.ReleaseSafe,   // Debug, ReleaseSafe, ReleaseFast, ReleaseSmall
    PositionIndependentCode = true,
    LinkCStandardLibrary = true
});

Zig.Executable("zig_greeter", new()
{
    RootSourceFile = "zig/main.zig",
    ModuleImports = new Dictionary<string, string> { ["core"] = "//:zig_core" },
    Depends = ["//:zig_math", math],                   // Zig libraries and C-family libraries
    CSources = ["zig/shim.c"],                         // compiled by zig, with the C options of the target
    ZigCompilerOptions = ["-fno-omit-frame-pointer"]
});

Zig.Test("zig_core_test", new() { RootSourceFile = "zig/core.zig", TestFilter = "parsing" });
```

- `Sources` names the files the root imports, for the cache; without it, every `.zig` file beside the root file and
  below it.
- `OptimizationMode` defaults to `Debug` in a debug build and `ReleaseSafe` in a release build.
- `EmitHeader` passes `-femit-h`; the zig compilers since 0.11 accept it and write no header, so a build that needs the
  header stops with that reason.

## Toolchains

Toolchains are declared in a `BUILD.csx`, usually `toolchains/BUILD.csx`, and registered by label in `MODULE.csx`.
For each family Tazel takes the first registered toolchain that runs on this machine and builds for the platform of
the command. Without one, it uses this machine's own tools (clang, then gcc, then `zig cc`; zig), and only when the
platform is this machine's.

```cs
using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;

var warnings = CC.Feature("warnings", new()
{
    Enabled = true,
    FlagSets = [ new() { Actions = [CCAction.CCompile, CCAction.CppCompile], Flags = ["-Wall", "-Wextra"] } ]
});

var crossToArm = CC.Feature("linux-aarch64", new()
{
    Enabled = true,
    FlagSets =
    [
        new()
        {
            Actions = [CCAction.CCompile, CCAction.CppCompile, CCAction.CppLinkExecutable, CCAction.CppLinkSharedLibrary],
            Flags = ["--target=aarch64-linux-gnu", "--sysroot=/opt/sysroots/aarch64"]
        }
    ]
});

CC.Toolchain("clang-linux-aarch64", new()
{
    ExecutionPlatform = Platform.LinuxAmd64,
    TargetPlatform = Platform.LinuxAarch64,
    Tools = new() { CCompiler = "clang", CppCompiler = "clang++", Linker = "clang++", Archiver = "llvm-ar" },
    Features = [warnings, crossToArm]
});

Zig.Toolchain("zig", new()
{
    ExecutionPlatform = Platform.LinuxAmd64,
    TargetPlatform = Platform.LinuxAmd64,
    Tools = new() { Zig = "/opt/zig/zig" }
});
```

- The actions are `CCompile`, `CppCompile`, `ObjectiveCCompile`, `ObjectiveCppCompile`, `Assemble`,
  `CppLinkExecutable`, `CppLinkSharedLibrary` and `CppLinkStaticLibrary` (the archiver).
- A command line of a compilation is: the compiler, `-g` in a debug build or `-O2 -DNDEBUG` in a release build, the
  flags of the features that are on, `-fPIC` for libraries, the include directories and macros, `CompilerOptions`,
  `CCompilerOptions` or `CppCompilerOptions`, and then the options of the command line.
- A feature is on when it is `Enabled`, or `--feature` names it, and off when `--no-feature` names it; a target's
  `Features` win over both.
- The platforms are `LinuxAmd64` (`linux-amd64`), `LinuxAarch64`, `WindowsX64`, `WindowsArm64`, `MacOSX86_64`
  (`macos-x86_64`), `MacOSArm64`, `FreeBSDAmd64`, `FreeBSDAarch64`, `OpenBSDAmd64`, `OpenBSDAarch64` and
  `DragonFlyBSDAmd64`.
- The BSD platforms are platforms to build for: Tazel runs on Linux, macOS and Windows. This machine's own compilers
  build only for this machine, so another platform takes a registered toolchain. zig brings the libc of FreeBSD and
  OpenBSD, so a `Zig.Toolchain` whose `Zig` is the zig on PATH builds Zig, C and C++ for them; a `CC.Toolchain` for
  them names a compiler that targets them (zig cc does, wrapped in a script that adds `-target aarch64-freebsd`). zig
  has no libc for DragonFly BSD: for `DragonFlyBSDAmd64` it builds static libraries, and linking needs a CC toolchain
  whose compiler has a DragonFly sysroot.

## From earlier project files

Until Tazel 1.0.0, `tazel limited-time C#` moves a tree of modules to what Tazel reads now, and is removed in 1.0.0:

- every project file named `BUILD.tzl`, `BUILD.tazel`, `BUILD` or `Tazel.build` below the working directory becomes
  `BUILD.csx`;
- in every `BUILD.csx`, a target that `Depends` names by its variable is written as its label, and the variable is left
  out when nothing else uses it:

```cs
// before
var mathematics = CC.StaticLibrary("math", new() { Sources = ["math.cpp"] });
CC.Executable("app", new() { Sources = ["main.cpp"], Depends = [mathematics] });

// after
CC.StaticLibrary("math", new() { Sources = ["math.cpp"] });
CC.Executable("app", new() { Sources = ["main.cpp"], Depends = ["//:math"] });
```

It reads the files with Roslyn and runs none of them, and it walks every directory below the working directory but
hidden ones (`.tazel`, `.git`) and links, through nested modules too, each package labelled from its own module's
root. It checks everything before it writes anything: two project files in one directory, or a target named in a way
that is not a variable set by a declaration of the same file (a method's result, a variable of another kind), is
reported and changes no file. Run again, it finds nothing to do; after an interrupted run, a `BUILD.csx` that already
holds what the earlier file becomes is kept and the earlier file removed.
