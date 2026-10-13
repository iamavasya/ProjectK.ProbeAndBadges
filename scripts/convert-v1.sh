#!/usr/bin/env bash
# Rebuilds catalog/ (format v2) from PlastBadgesParser output and this repo's v1 seeds.
#   scripts/convert-v1.sh <path-to-PlastBadgesParser>
# Needs .NET 10 and Node (for svgo). The SVGs exported from Illustrator carry hidden raster
# tracing layers of up to 5 MB; svgo drops them, which takes the images from ~49 MB to ~5 MB
# without a visible change.
set -euo pipefail

parser="${1:?path to PlastBadgesParser checkout}"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp -r "$parser/badges_images" "$work/images"
npx --yes svgo@3 -q -rf "$work/images" >/dev/null

rm -rf "$root/catalog"
dotnet run --project "$root/tools/ProjectK.Catalog.Cli" -- convert-v1 \
  --badges "$parser/badges.json" \
  --images "$work/images" \
  --probes "$root/src/ProjectK.ProbeAndBadges/Data/probes.seed.json" \
  --awards-images "$root/src/ProjectK.ProbeAndBadges/Data/awards-images" \
  --out "$root/catalog"
