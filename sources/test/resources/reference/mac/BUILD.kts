// The mac/BUILD.kts of the reference module.
isPublic = true                              // //mac/:bridge is used by //tests/

objc.bin("greeter-mac") {                    // .m
    sources(glob("src/*.m"))
    depends("//core/:core", ":bridge")
    targetTriple("aarch64-apple-darwin", "x86_64-apple-darwin")
    arc = true                               // objc and objcpp only; default: true
    frameworks("Foundation", "AppKit")

    bundle {                                 // objc and objcpp bin only; a .app bundle; default: a plain program
        identifier = "com.example.greeter"
        infoPlist = "Info.plist"
        resources(glob("Resources/**"))
        codesignIdentity = env("TAZEL_CODESIGN_IDENTITY")   // default: not signed
    }
}

objcpp.staticlib("bridge") {                 // .mm
    sources(glob("bridge/*.mm"))
    headers("bridge/include")
    depends("//core/:core")
    targetTriple("aarch64-apple-darwin", "x86_64-apple-darwin")
    standard = CppStandard.CPP23
    arc = true
    frameworks("Foundation")
}

