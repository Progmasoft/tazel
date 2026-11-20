#!/bin/bash
# SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
# SPDX-License-Identifier: MIT
#
# Writes what the macOS runner is doing into the summary of a check run every 20 seconds: the load, the memory, how
# many processes there are and which use the most. The test run on macOS has stopped the whole machine, so that the
# job kept no log; the check run keeps the last summary written before that.
#
# Usage: macos-watchdog.sh, with GH_TOKEN, GITHUB_REPOSITORY and GITHUB_SHA in the environment.
set -u

id=$(gh api "repos/$GITHUB_REPOSITORY/check-runs" -f name="macOS watchdog" -f head_sha="$GITHUB_SHA" \
  -f status=in_progress --jq .id) || exit 1
echo "$id" > "$RUNNER_TEMP/watchdog-check-run"

while true; do
  {
    echo '```text'
    date -u +%FT%TZ
    uptime
    echo "processes: $(ps -ax | wc -l | tr -d ' ')"
    memory_pressure 2>/dev/null | tail -1
    vm_stat | head -8
    echo
    ps -axo pid,ppid,rss,%cpu,etime,command -r | head -25 | cut -c1-200
    echo '```'
  } > "$RUNNER_TEMP/watchdog.md"
  gh api -X PATCH "repos/$GITHUB_REPOSITORY/check-runs/$id" -f "output[title]=$(date -u +%T)" \
    -F "output[summary]=@$RUNNER_TEMP/watchdog.md" > /dev/null 2>&1
  sleep 20
done
