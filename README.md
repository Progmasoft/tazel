<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0 -->

# Tazel

Tazel is a build system and package manager for C, C++, Objective-C, Objective-C++ and Zig. A module is described by a
`MODULE.kts` at its root and one `BUILD.kts` per package, both Kotlin scripts; targets are named by labels such as
`//app/:greeter`. Packages come from [ViGet](https://viget.progmasoft.com).

```kotlin
// app/BUILD.kts
cpp.bin("greeter") {
    sources(glob("src/**/*.cpp"))
    depends("//core/:core")
    compiler = Compiler.CLANG
    standard = CppStandard.CPP23
}
```

```text
tazel build //app/:greeter
tazel run //app/:greeter
tazel test //tests/...
tazel fmt --check //...
tazel lint //...
```

Tazel is under development; this repository starts its history again at 0.1.0. The earlier Tazel, the build system of
Tala (0.1.0 to 0.4.0.3), is archived as [Progmasoft/tazel-legacy](https://github.com/Progmasoft/tazel-legacy).

## Building

Tazel is a Kotlin program for the JVM and needs a JDK 25.

```bash
./gradlew check installDist
build/install/tazel/bin/tazel version
```

## License

[MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0](LICENSE.txt). The site is [tazel.net](https://tazel.net).
