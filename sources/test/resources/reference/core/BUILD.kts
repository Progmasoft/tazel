// The core/BUILD.kts of the reference module.
isPublic = true

c.staticlib("core") {                        // //core/:core
    sources(glob("src/**/*.c"))
    exclude(glob("src/experimental/**"))
    headers("include")                       // seen here and by dependents
    privateHeaders("src")                    // here only
    textualHeaders(glob("include/**/*.inc")) // included, never compiled alone
    includePrefix = "greeter"                // #include <greeter/core.h>; default: none
    stripIncludePrefix = "include"           // default: none
    targetTriple(
        "x86_64-unknown-linux-gnu",
        "aarch64-unknown-linux-gnu",
        "aarch64-apple-darwin",
        "x86_64-apple-darwin",
        "x86_64-pc-windows-msvc",
    )                                        // default: the host's triple

    compiler = Compiler.CLANG                // CLANG, GCC, MSVC, CLANG_CL, ZIG_CC; default: found on the machine
    standard = CStandard.C23                 // C89, C99, C11, C17, C23; default: newest the compiler supports
    gnuExtensions = false

    defines("GREETER_API=1")
    privateDefines("CORE_INTERNAL")
    flags("-Wshadow")
    warnings = Warnings.ALL                  // ALL, DEFAULT, NONE; default: DEFAULT
    warningsAsErrors = false
    precompiledHeader = "src/pch.h"          // default: none
    debugInfo = DebugInfo.SPLIT              // NONE, FULL, SPLIT; default: FULL in DEBUG, NONE in RELEASE
    pic = true                               // staticlib only; default: true when a sharedlib depends on it
    alwaysLink = false                       // staticlib only; whole-archive; default: false
    outputName = "greeter-core"              // default: the target name

    if (BUILD_MODE == BuildMode.RELEASE) optimize = Optimize.SPEED
}

c.sharedlib("core-shared") {                 // //core/:core-shared
    sources(glob("src/**/*.c"))
    headers("include")
    depends(":core")
    exports("core_*")                        // sharedlib only; default: every symbol that is not hidden
    soVersion = "1"                          // sharedlib only; libgreeter-core.so.1; default: none
    rpath("\$ORIGIN")
    if (OS == Os.WINDOWS) defFile = "core.def"     // sharedlib only
    if (OS == Os.LINUX) linkerScript = "core.lds"
}

