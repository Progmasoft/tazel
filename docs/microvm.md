<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# The MicroVM

`tazel_options.txt` asks for a sandbox:

```text
sandboxing-with-microvm: true;
microvm-memory: 1024;   # MiB, or "auto" (1024)
microvm-cpus: 2;        # or "auto" (half the processors, 1 to 4)
```

Then `tazel custom` runs its commands inside a MicroVM: a Linux guest under
[Cloud Hypervisor](https://www.cloudhypervisor.org) on KVM. The guest sees the module (or the directory of the command
when it is in no module) at the same path, writable, and the host's `/usr`, read-only, so commands run with the tools
of the host; nothing else of the host is visible. Commands get `PATH`, `HOME=/root`, `TMPDIR=/tmp` and
`LANG=C.UTF-8`, and none of the host's environment. A shell step, `s("...")`, goes to the guest's `/bin/sh`.

Builds, tests and plugins do not run in the MicroVM, so a sandbox asked for stops them; `microvm-network: true` is
refused, as the MicroVM has no network.

## What it needs

- x86_64 Linux with KVM: `/dev/kvm`, readable and writable by the user.
- virtiofsd, which shares the directories (`/usr/libexec/virtiofsd`, `/usr/lib/virtiofsd`, PATH, or `TAZEL_VIRTIOFSD`).
- Cloud Hypervisor v55.0: fetched once into `~/.cache/tazel/cloud-hypervisor` and checked against its SHA-256, or
  `TAZEL_CLOUD_HYPERVISOR`.
- The image of the guest: fetched once from the release of this Tazel into `~/.cache/tazel/microvm`, or a directory
  named by `TAZEL_MICROVM_IMAGE`. Every file is checked against the `SHA256SUMS` of the image before it boots.

## How it works

```text
tazel ──vsock (Cloud Hypervisor's Unix socket, CONNECT 1024)──▶ tazel-guest (PID 1 in the guest)
  │                                                                │
  ├─ cloud-hypervisor  (a process of its own)                      ├─ mounts /proc, /sys, /dev, /tmp, /run
  ├─ virtiofsd × 3     (the module, /usr read-only, /etc)          ├─ mounts the shares (virtio-fs)
  └─ waits for Ready, sends Run, prints Output, returns Exit       └─ runs each command, streams its output
```

- The guest kernel is a long-term Linux of kernel.org on top of `tinyconfig` with `microvm/kernel.config`: virtio
  (PCI, console, vsock, file system), PVH boot, no modules and no network device.
- The initramfs holds `tazel-guest` (`src/Progmasoft.Tazel.Guest`), published self-contained for linux-musl and
  trimmed, beside musl's loader, and the libraries of Alpine it needs (musl, libgcc, libstdc++), with `/dev/console`,
  `/dev/random` and `/dev/urandom`, which the agent needs before it mounts `/dev`. The kernel starts the
  loader as the first process, so the guest's `/lib` is free to be the host's `/usr/lib`.
- The agent reaches the C library through `[LibraryImport]`; it refuses a working directory outside what was shared
  for writing.
- The messages are a kind byte, a little-endian length and a payload (`src/Shared/MicroVMProtocol.cs`, the same file
  on both sides).
- Cloud Hypervisor and virtiofsd are processes of their own: when one of them dies, the command fails with E0504 and
  the end of the guest's console; Tazel goes on. At the end Tazel asks the guest to power off, then stops what is left
  and removes its runtime directory.

## Building the image

```text
dotnet run microvm/build.cs -- <work directory> <output directory>
TAZEL_MICROVM_IMAGE=<output directory> tazel custom <command>
```

`--agent <directory>` takes an agent published elsewhere instead of publishing one.

The agent runs only as the guest's first process, so its coverage is measured inside the guest:
`microvm/guest-coverage.sh <work directory> <output directory> <report>` publishes it with its symbols, has Coverlet
instrument it, builds the image from it and runs the MicroVM tests with `TAZEL_MICROVM_COVERAGE` naming the directory of
Coverlet's hits. Tazel shares that directory with the guest at the same path, and the agent runs the process-exit
handlers before it powers off, so Coverlet's are among them. CI runs it in the MicroVM job.

It needs make, a C compiler, flex, bison and bc for the kernel. Every download (the kernel and the three Alpine
packages) is checked against the SHA-256 in `microvm/build.cs`, and the same inputs give the same bytes.

The kernel is licensed under GPL-2.0, musl under MIT, and libgcc and libstdc++ under GPL-3.0 with the GCC Runtime
Library Exception; a release of the image comes with the exact kernel source it was built from.
