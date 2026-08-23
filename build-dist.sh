#!/usr/bin/env bash
# 배포용 단일 실행 파일 묶음을 만든다. dotnet SDK가 PATH에 있어야 한다.
set -euo pipefail
cd "$(dirname "$0")"

DIST="publish/dist"
rm -rf "$DIST"
mkdir -p "$DIST"

# win-x64 / linux-x64는 실제 실행까지 검증됨. osx-*는 크로스 컴파일만 되고 Mac에서 실행 검증은 못 했다.
VERIFIED=(win-x64 linux-x64)
UNVERIFIED=(osx-x64 osx-arm64)

for rid in "${VERIFIED[@]}" "${UNVERIFIED[@]}"; do
  echo "== publishing $rid =="
  dotnet publish PixelPair.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -o "$DIST/$rid"
done

cp -r mcp "$DIST/mcp"
rm -rf "$DIST/mcp/node_modules"

# Keep PixelPair's license and runtime third-party notices alongside every release archive.
cp LICENSE THIRD_PARTY_NOTICES.md "$DIST/"
cp -r THIRD_PARTY_LICENSES "$DIST/THIRD_PARTY_LICENSES"

cat > "$DIST/UNVERIFIED.txt" <<EOF
The osx-x64 and osx-arm64 packages are cross-compiled but have not yet been runtime-tested on real macOS hardware.
Please report any macOS-specific problems through the repository issue tracker.
EOF

echo
echo "완료: $DIST"
