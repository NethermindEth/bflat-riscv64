#!/bin/sh
# Starts compiler-tests of bflat-ts with a bflat image and waits for it.
# The run is in bflat-ts, with its own secrets: this repository gives only the
# image. GH_TOKEN must be allowed to start workflows of bflat-ts.
#
# Usage: run-bflat-ts.sh <image> [<test>]
# With a test, every test of the run must pass (require_pass).
set -eu

image=$1
test=${2:-}

set -- -f ref=main -F return_run_details=true -f "inputs[bflat_image]=$image"
if [ -n "$test" ]; then
    set -- "$@" -f "inputs[test]=$test" -f "inputs[require_pass]=true"
fi
run=$(gh api -X POST repos/NethermindEth/bflat-ts/actions/workflows/compiler-tests.yml/dispatches \
    "$@" --jq .workflow_run_id)
[ -n "$run" ]

url=https://github.com/NethermindEth/bflat-ts/actions/runs/$run
echo "bflat-ts${test:+ ($test)}: $url"
echo "bflat-ts${test:+ ($test)}: $url" >> "$GITHUB_STEP_SUMMARY"

rc=0
gh run watch "$run" -R NethermindEth/bflat-ts --interval 60 --exit-status > /dev/null || rc=$?
gh run view "$run" -R NethermindEth/bflat-ts --json conclusion --jq '"conclusion: " + .conclusion'
exit "$rc"
