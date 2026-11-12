// The tests/BUILD.kts of the reference module.
cpp.test("unit") {
    sources(glob("*_test.cpp"))
    depends("//core/:core", "@catch3//:main")   // the framework is a dependency
    data(glob("fixtures/*"))

    args("--reporter", "compact")
    env("LANG" to "C", "TZ" to "UTC")
    size = TestSize.SMALL                    // SMALL, MEDIUM, LARGE, ENORMOUS; default: MEDIUM
    timeout = 2.minutes                      // default: from size (1m, 5m, 15m, 60m)
    flaky = false                            // true: a failure is retried up to 3 times
    shards = 4                               // TEST_TOTAL_SHARDS / TEST_SHARD_INDEX; default: 1
    tags(TestTag.MANUAL, TestTag.EXCLUSIVE)  // MANUAL, EXCLUSIVE, LOCAL, NO_CACHE

    compiler = Compiler.CLANG                // every build setting of a bin
    standard = CppStandard.CPP23
    defines("TESTING=1")
    linkStatic = true                        // default: true for tests
    if (BUILD_MODE == BuildMode.DEBUG) sanitize(Sanitizer.ADDRESS, Sanitizer.UNDEFINED)
}

c.test("core-c") {
    sources("core_test.c")
    depends("//core/:core")
}

objc.test("mac") {
    sources(glob("mac/*_test.m"))
    depends("//mac/:bridge")
    targetTriple("aarch64-apple-darwin")
    frameworks("XCTest")
}

zig.test("tool") {
    sources("../tools/main.zig")             // runs with zig test
}
