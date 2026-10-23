#!/usr/bin/env bash
set -euo pipefail
cd /workspace/club
export DOTNET_ROOT=/workspace/.tools/dotnet
export DOTNET_CLI_HOME=/workspace/.tools/dotnet-home
export NUGET_PACKAGES=/workspace/.tools/nuget
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$PATH"
exec dotnet "$@"
