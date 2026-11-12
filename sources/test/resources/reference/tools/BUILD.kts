// The tools/BUILD.kts of the reference module.
zig.bin("tool") {                            // zig targets have no compiler and no standard
    sources("main.zig")                      // the root file
    depends("//core/:core")
    targetTriple("x86_64-unknown-linux-gnu", "aarch64-apple-darwin")
    args("--verbose")
    if (BUILD_MODE == BuildMode.RELEASE) optimize = ZigOptimize.SAFE   // SAFE, SPEED, SIZE
}

zig.staticlib("zig-lib") {
    sources("lib.zig")
    headers("include")                       // the C header it offers
}

