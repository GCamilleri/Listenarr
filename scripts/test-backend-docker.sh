#!/usr/bin/env bash
# Run the backend test suite in a Linux container.
#
# Why this exists: the backend suite cannot run natively on macOS.
#   1. listenarr.infrastructure/FileSystem/UnixOpenFlags.cs refuses any macOS
#      process architecture other than x64, so on Apple Silicon roughly 575
#      tests die on PlatformNotSupportedException.
#   2. FileSystemSemanticsResolver only accepts a case-sensitivity answer from
#      an allowlisted filesystem (ext family, f2fs, tmpfs, bcachefs). The tests
#      work in Path.GetTempPath(), so /tmp must be one of those. In a container
#      /tmp defaults to overlayfs, which is not, and roughly 250 tests fail and
#      the run then deadlocks. --tmpfs /tmp is what makes the suite complete.
#
# Source is synced into a named volume rather than bind-mounted, because the
# macOS bind mount does not give Linux filesystem semantics either.
#
# Usage:
#   scripts/test-backend-docker.sh
#   scripts/test-backend-docker.sh --filter FullyQualifiedName~RootFolderRelocation
#   REBUILD=1 scripts/test-backend-docker.sh      # discard cached build output

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
SRC_VOLUME="listenarr-src"
NUGET_VOLUME="listenarr-nuget"

if ! docker info >/dev/null 2>&1; then
  echo "Docker daemon is not running. Start Docker Desktop and retry." >&2
  exit 1
fi

if [ "${REBUILD:-0}" = "1" ]; then
  echo "Removing cached source volume..."
  docker volume rm "$SRC_VOLUME" >/dev/null 2>&1 || true
fi

exec docker run --rm -i \
  --tmpfs /tmp:exec,mode=1777,size=4g \
  -v "$REPO_ROOT":/mnt:ro \
  -v "$SRC_VOLUME":/src \
  -v "$NUGET_VOLUME":/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e ASPNETCORE_ENVIRONMENT=Test \
  -e Playwright__Enabled=false \
  -e LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES=DirectorySymbolicLinks,FileSymbolicLinks \
  "$IMAGE" \
  sh -euc '
    # Sync every run so local edits are what actually gets tested. bin/obj stay
    # in the volume between runs so the build is incremental.
    tar -C /mnt \
      --exclude=node_modules --exclude=.git --exclude=bin \
      --exclude=obj --exclude=dist \
      -cf - . | tar -C /src -xf -
    cd /src
    exec dotnet test listenarr.slnx -c Release "$@"
  ' -- "$@"
