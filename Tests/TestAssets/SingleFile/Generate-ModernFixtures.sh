#!/usr/bin/env bash
set -euo pipefail

# Bash counterpart for Linux/macOS development hosts. Windows CI uses the
# PowerShell script beside this file; both emit the same fixture.json schema.
# Only POSIX shell commands plus the net10 metadata helper are used here; in
# particular, no GNU realpath/stat/sha256sum extensions are required.
script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
source_root=$(cd "$script_root/Net10" && pwd -P)
output_root="${1:-$source_root/artifacts/net10.0}"
required_sdk='10.0.111'
metadata_project="$script_root/FixtureMetadata/FixtureMetadata.csproj"

# Validate the requested path before any cleanup. The helper accepts only the
# canonical generated-artifact directory and rejects existing symlinks/junctions.
(cd "$source_root" && dotnet run --project "$metadata_project" -- \
  --validate "$source_root" "$output_root")
output_root="$source_root/artifacts/net10.0"

actual_sdk=$(cd "$source_root" && dotnet --version)
if [[ "$actual_sdk" != "$required_sdk" ]]; then
  printf 'The net10 fixture requires SDK %s, but dotnet --version returned %s\n' "$required_sdk" "$actual_sdk" >&2
  exit 1
fi

rm -rf "$output_root"
mkdir -p "$output_root"

publish_variant() {
  local name=$1 self_contained=$2 compressed=$3 symbols=$4
  local variant_root="$output_root/$name"
  local publish_root="$variant_root/publish"
  mkdir -p "$publish_root"

  (
    cd "$source_root"
    dotnet build App.csproj --nologo -c Release -f net10.0 -r win-x64 \
      --self-contained "$self_contained" \
      -p:SingleFileFixtureRoot="$variant_root" \
      -p:PublishSingleFile=true -p:DebugType=portable -p:DebugSymbols=true \
      -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
      -p:PathMap="$source_root=/_/SingleFile" \
      -p:SingleFileFixtureIncludeSymbols="$symbols" \
      -p:SingleFileFixtureCompression="$compressed" \
      -p:EnableCompressionInSingleFile="$compressed" \
      -p:IncludeSymbolsInSingleFile="$symbols"
    dotnet publish App.csproj --nologo -c Release -f net10.0 -r win-x64 \
      --self-contained "$self_contained" -o "$publish_root" --no-build \
      -p:SingleFileFixtureRoot="$variant_root" \
      -p:PublishSingleFile=true -p:DebugType=portable -p:DebugSymbols=true \
      -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
      -p:PathMap="$source_root=/_/SingleFile" \
      -p:SingleFileFixtureIncludeSymbols="$symbols" \
      -p:SingleFileFixtureCompression="$compressed" \
      -p:EnableCompressionInSingleFile="$compressed" \
      -p:IncludeSymbolsInSingleFile="$symbols"
  )

  (cd "$source_root" && dotnet run --project "$metadata_project" -- \
    --variant-root "$variant_root" --publish-root "$publish_root" \
    --variant "$name" --sdk-version "$actual_sdk" \
    --target-framework net10.0 --runtime-identifier win-x64 \
    --self-contained "$self_contained" --compressed "$compressed" \
    --includes-symbols "$symbols")
}

publish_variant fdd-uncompressed false false false
publish_variant scd-uncompressed true false false
publish_variant scd-compressed true true false
publish_variant scd-uncompressed-pdb true false true
publish_variant scd-compressed-pdb true true true
