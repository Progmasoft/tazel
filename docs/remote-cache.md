<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Remote cache

A remote cache keeps the outputs of actions so that another machine, or another checkout on the same one, takes them
instead of running the compiler again. Tazel speaks Bazel's HTTP caching protocol, so a cache such as
[bazel-remote](https://github.com/buchgr/bazel-remote) serves it.

```cs
using System;

cache = Cache.Define(new() {
    Remote = Remote.From("https://cache.example.com", new() {
        Read = true,                                              // default: true
        Write = Environment.GetEnvironmentVariable("CI") == "true", // default: false
        Token = Environment.GetEnvironmentVariable("TAZEL_CACHE_TOKEN")
    })
});
```

- The URL is `https`; `http` only for a cache on this machine (`localhost`, `127.0.0.1`, `[::1]`).
- `Token` goes in the `Authorization` header. One that starts with the word `Basic` or `Bearer` and a space is sent as
  it is, so a cache behind HTTP basic authentication takes `Basic`, a space, and the base64 of `user:password`; any
  other token is sent after `Bearer` and a space.
- `Read` takes results from the cache before an action runs; `Write` gives it the results of actions that ran here.
  A machine that only reads, such as a pull request build, never writes.

## What is shared

An action's key is the SHA-256 of what decides its outputs on any machine: the platform Tazel runs on, the command
line, the digest of the program it runs, its environment without `HOME`, `TMPDIR`, `TMP` and `TEMP`, and the path and
digest of each input it declares. The directory the module is in is not part of it: the commands of Tazel name files
relative to the module, so two checkouts share their results.

Under that key the cache keeps an `ActionResult` of the Remote Execution API, at `/ac/<key>`, with each output and
the depfile at `/cas/<sha256>`. The headers a compiler finds by itself are known only after it ran; Tazel stores
them, each with its digest, as one more file of the result. A result is taken only when every one of those headers
has here the digest it had where it was made, so a header that differs makes the action run.

Each file is checked against its SHA-256 and its size before it is put in place, and a result that would write
outside the module is refused. Steps of plugins run in Tazel itself and are never taken from the cache.

## When the cache is not there

A cache that cannot be reached, or answers with an error, is said once as a warning, and the build goes on without
it. A build never fails because of its remote cache.

## Signed results

Without keys, whoever may write to the cache decides what readers take. With keys, readers take only what the holder
of the signing key wrote:

```text
tazel cache keygen                      # ed25519
tazel cache keygen --algorithm=rsa-4096
tazel cache keygen --algorithm=ml-dsa   # ML-DSA-65, FIPS 204
```

```cs
using System;

cache = Cache.Define(new() {
    Remote = Remote.From("https://cache.example.com", new() {
        Write = Environment.GetEnvironmentVariable("CI") == "true",
        Token = Environment.GetEnvironmentVariable("TAZEL_CACHE_TOKEN"),
        PublicKey = "ed25519:…",                                          // every reader
        SigningKey = Environment.GetEnvironmentVariable("TAZEL_CACHE_SIGNING_KEY")   // the writers only
    })
});
```

- A key says its algorithm: `ed25519:`, `rsa-4096:` or `ml-dsa-65:`, then the key in base64. Nothing else chooses
  it. A key Tazel cannot read stops the build with `error[E0505]`.
- With a `SigningKey`, each result written gets one more file, its signature record: the algorithm and the signature
  of the action's key and of the result's other files. Ed25519 and ML-DSA-65 come from BouncyCastle, RSA-4096 is
  RSA-PSS with SHA-256 from .NET.
- With a `PublicKey`, a result is taken only when its record names the algorithm of that key and the key verifies
  its signature. An unsigned result, one signed by another key, and one signed with another algorithm are not taken;
  that is said once as a warning, and the actions run here.
- Writing with a `PublicKey` and no `SigningKey` is said as a warning: no reader that trusts the key takes what such a
  build writes.

The public key belongs in `MODULE.csx`. Keep the signing key as a secret of the machines that write, such as the CI of
the default branch.
