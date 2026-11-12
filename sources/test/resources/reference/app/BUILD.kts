// The app/BUILD.kts of the reference module.
isPublic = true                              // //app/:greeter is pushed to ViGet
cpp.bin("greeter") {                         // //app/:greeter
    sources(glob("src/**/*.cpp"))
    depends("//core/:core", "@toml//:toml", "@json-lib//:json")
    targetTriple(
        "x86_64-unknown-linux-gnu",
        "aarch64-unknown-linux-gnu",
        "aarch64-apple-darwin",
        "x86_64-pc-windows-msvc",
    )

    compiler = Compiler.CLANG
    standard = CppStandard.CPP23             // CPP11, CPP14, CPP17, CPP20, CPP23, CPP26

    links("m", "pthread")
    linkFlags("-Wl,--as-needed")
    rpath("\$ORIGIN/../lib")

    args("--port", "8080")                   // bin and test only: what tazel run passes
    env("GREETER_LOG" to "debug")            // bin and test only: the environment of tazel run

    when (BUILD_MODE) {
        BuildMode.DEBUG -> {
            optimize = Optimize.NONE         // NONE, SIZE, SPEED; default: NONE in DEBUG, SPEED in RELEASE
            sanitize(Sanitizer.ADDRESS, Sanitizer.UNDEFINED)   // ADDRESS, UNDEFINED, THREAD, MEMORY, LEAK
        }
        BuildMode.RELEASE -> {
            optimize = Optimize.SPEED
            lto = Lto.THIN                   // NONE, THIN, FULL; default: NONE
            strip = true
            debugInfo = DebugInfo.SPLIT
        }
    }

    when (OS) {
        Os.WINDOWS -> {
            defines("WIN32_LEAN_AND_MEAN")
            links("ws2_32")
            resources("greeter.rc")          // icon, version, manifest
            subsystem = Subsystem.CONSOLE    // bin only; CONSOLE, WINDOWS; default: CONSOLE
        }
        Os.LINUX -> links("dl")
        Os.MACOS -> frameworks("CoreFoundation")
        Os.BSD, Os.UNIX -> {}
    }
    if (ARCH == Arch.ARM64) defines("NEON=1")

    data("assets/greetings.txt")             // copied beside the program
}

