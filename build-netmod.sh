#!/usr/bin/env bash
# 打包出 artifacts/SurvivalcraftGenius.netmod(传到 Windows 的 NetMods 里测试)
set -e
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd "$(dirname "$0")"
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1 -SurvivalcraftDir "${SURVIVALCRAFT_DIR:-$HOME/sc-libs/}"
