#!/usr/bin/env bash
# Runs the .NET SDK in Docker so no local install is needed: ./dotnet-docker.sh build
# Uses the host network to reach the regtest services from BTCPayServer.Tests/docker-compose.yml.
set -euo pipefail
cd "$(dirname "$0")"
docker volume create meterbolt-nuget >/dev/null
docker run --rm -v meterbolt-nuget:/nuget alpine chown "$(id -u):$(id -g)" /nuget
exec docker run --rm ${DOCKER_ARGS:-} --network host --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v meterbolt-nuget:/nuget -v "$PWD:$PWD" -w "$PWD" \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c 'dotnet tool restore >/dev/null 2>&1; exec dotnet "$@"' _ "$@"
