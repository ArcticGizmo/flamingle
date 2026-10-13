#!/usr/bin/env pwsh
# Regenerates every icon asset from the source-of-truth SVG (flamingle.svg).
#
#   src/Flamingle.App/Assets/flamingle.png              256x256 PNG    (in-app logo)
#   src/Flamingle.App/Assets/flamingle.ico              multi-res ICO  (window + .exe ApplicationIcon)
#   src/Flamingle.App/Assets/tray/flamingle-<n>.png     exact-size tray frames, one per DPI scale
#   src/Flamingle.App/Assets/tray/flamingle-dev-<n>.png the same, green, for a dev instance
#   landing-icon.png                                     512x512 PNG    (README header)
#
# The rasters are produced by tools/IconGen (renders the SVG via System.Drawing, Windows-only).
#
# Run this after editing flamingle.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
