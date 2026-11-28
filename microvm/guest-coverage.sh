#!/bin/bash
# SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
# SPDX-License-Identifier: MIT

# The coverage of the agent, which runs only as the first process of the MicroVM's guest.
#
# The agent is published with its symbols and without trimming, and Coverlet instruments it. The MicroVM image is built
# from that agent and the MicroVM tests boot it; the guest sees the directory Coverlet writes its hits to at the same
# path (TAZEL_MICROVM_COVERAGE), and the agent runs Coverlet's exit handlers before it powers off. Coverlet writes the
# report once the tests end.
#
# Usage: microvm/guest-coverage.sh <work directory> <output directory for the image> <coverage report>
# Coverlet runs the second stage itself: microvm/guest-coverage.sh --boot <work> <image> <agent> <hits>
set -euo pipefail
repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$repository"

if [ "${1:-}" = "--boot" ]; then
  work=$2 image=$3 agent=$4 hits=$5
  # The hits path is fixed when Coverlet instruments; the tests keep the temporary directory of the caller, so their
  # modules are not under the directory the guest shares for the hits.
  export TMPDIR="$GUEST_COVERAGE_TMPDIR"
  dotnet run microvm/build.cs -- "$work" "$image" --agent "$agent"
  # The host's side of the same tests is measured as well, next to the agent's report.
  TAZEL_MICROVM_IMAGE="$image" TAZEL_MICROVM_COVERAGE="$hits" TAZEL_REQUIRE_MICROVM=1 \
    dotnet test --project tests/Progmasoft.Tazel.Tests -c Release --no-build \
    --filter-class Progmasoft.Tazel.Tests.MicroVMBootTests --filter-class Progmasoft.Tazel.Tests.MicroVMProtocolTests \
    --filter-class Progmasoft.Tazel.Tests.MicroVMFilesTests \
    --coverlet --coverlet-output-format cobertura --results-directory "$GUEST_COVERAGE_RESULTS"
  exit
fi

work=$(realpath -m "$1")
image=$(realpath -m "$2")
report=$(realpath -m "$3")
agent="$work/agent-coverage"
hits="$work/coverage-hits"
rm -rf "$agent" "$hits"
mkdir -p "$hits" "$(dirname "$report")"

dotnet tool restore
dotnet publish src/Progmasoft.Tazel.Guest -c Release -r linux-musl-x64 --self-contained \
  -p:PublishTrimmed=false -p:DebugType=portable -o "$agent"
dotnet build tests/Progmasoft.Tazel.Tests -c Release

# Coverlet names its hits file under the temporary directory: here, the directory the guest shares. The paths hold no
# spaces (realpath of the arguments, chosen by the caller), so the target's arguments need no quoting.
GUEST_COVERAGE_RESULTS=$(dirname "$report") GUEST_COVERAGE_TMPDIR="${TMPDIR:-/tmp}" TMPDIR="$hits" dotnet coverlet "$agent" --include '[tazel-guest]*' --format cobertura --output "$report" \
  --target "$repository/microvm/guest-coverage.sh" --targetargs "--boot $work $image $agent $hits"
