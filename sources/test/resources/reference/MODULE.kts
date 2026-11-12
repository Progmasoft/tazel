// The MODULE.kts of the reference module: every setting of the DSL.

module {
    name = "greeter"
    version = "1.2.0"                        // SemVer; no five-field versions
    description = "Greets people."
    authors("Leitwolf", "Helmut")
    license = "MIT"                          // SPDX expression
}

dependencies {
    implementation("Publisher") {            // Publisher = a Progmasoft Account; label @toml//:…
        name = "toml"
        version = "1.0.0"
    }
    implementation("Publisher") {            // label @json-lib//:…
        name = "json"
        version = "2.1.0"
        alias = "json-lib"                   // optional
    }
    testImplementation("Progmasoft") {       // test targets only; label @catch3//:…
        name = "catch3"
        version = "3.1.0"
    }

    localImplementation {                    // label @local-lib//:…
        name = "local-lib"
        include("local.vipkg")
    }
    localImplementation {                    // label @libs//:…, the common name of every package the glob takes;
        name = "libs"                        // the same target name in two of them is an error
        include(glob("libs/*.vipkg"))
    }
    localTestImplementation {                // label @test-lib//:…
        name = "test-lib"
        include("fixtures/test-lib.vipkg")
    }
}

viget {
    push = true                              // default: false
    pushTarget("//core/:core", "//app/:greeter")   // their BUILD.kts must have isPublic = true, otherwise an error
    exclude(glob("docs/**"))
}

cache {
    local = true                             // default: true
    keep = 8                                 // results kept per target; default: 8
    remote("https://cache.example.com") {
        read = true                          // default: true
        write = false                        // default: false
        token = env("TAZEL_CACHE_TOKEN")
        publicKey = env("TAZEL_CACHE_PUBLIC_KEY")
        signingKey = env("TAZEL_CACHE_SIGNING_KEY")
    }
}

execution {
    jobs = 8                                 // default: processors
    memory = 16.gigabytes                    // default: no limit
    keepGoing = false                        // default: false
    remote("https://build.example.com") { token = env("TAZEL_REMOTE_TOKEN") }
}

// There is no plugins { } block: Tazel has no plugin API.
