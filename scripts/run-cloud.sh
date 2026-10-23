#!/usr/bin/env bash
set -euo pipefail
cd /workspace/club
export Club__DataDirectory=/workspace/club/.data/development
exec bash scripts/dotnet.sh run --project src/Club.Server -c Release --no-build -- "$@"
