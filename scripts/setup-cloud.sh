#!/usr/bin/env bash
set -euo pipefail
cd /workspace/club
export DOTNET_ROOT=/workspace/.tools/dotnet
export DOTNET_CLI_HOME=/workspace/.tools/dotnet-home
export NUGET_PACKAGES=/workspace/.tools/nuget
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$PATH"
sdk_version=10.0.402
if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! dotnet --list-sdks | awk '{print $1}' | grep -qx "$sdk_version"; then
  mkdir -p "$DOTNET_ROOT"
  sdk_archive=$(mktemp /tmp/club-dotnet.XXXXXX.tar.gz)
  trap 'rm -f "$sdk_archive"' EXIT
  curl --fail --silent --show-error --location --retry 2 "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" -o "$sdk_archive"
  printf '%s  %s\n' '1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2' "$sdk_archive" | sha512sum -c -
  tar -xzf "$sdk_archive" -C "$DOTNET_ROOT"
fi
dotnet --version
if [[ -f Club.slnx ]]; then
  dotnet restore Club.slnx --locked-mode
  dotnet build Club.slnx --no-restore -c Release
fi
if [[ -f tests/browser/package-lock.json ]]; then
  npm ci --prefix tests/browser --cache /workspace/.tools/npm-cache --ignore-scripts --no-audit --no-fund
fi
