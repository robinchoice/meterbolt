#!/usr/bin/env bash
set -euo pipefail

# This script will configure your BTCPay Server developer environement to load the plugin during a debug session.
# Uses a local .NET SDK if there is one, otherwise ./dotnet-docker.sh.

source plugin-env.sh
DOTNET=$(command -v dotnet >/dev/null && echo dotnet || echo ./dotnet-docker.sh)

TARGET_PATH="$($DOTNET build "src/$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath | tail -n 1)"

printf '{ "DEBUG_PLUGINS": "%s" }' "$TARGET_PATH" > "submodules/btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"
