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

# The source volume is namespaced by checkout path. Git worktrees each get their
# own, so two of them can run the suite concurrently without overwriting each
# other's synced tree. The NuGet cache is content-addressed, so it stays shared.
CHECKOUT_ID="$(printf '%s' "$REPO_ROOT" | shasum -a 256 2>/dev/null || printf '%s' "$REPO_ROOT" | sha256sum)"
CHECKOUT_ID="${CHECKOUT_ID%% *}"
SRC_VOLUME="listenarr-src-${CHECKOUT_ID:0:12}"
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
  -v "$NUGET_VOLUME":/nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e ASPNETCORE_ENVIRONMENT=Test \
  -e Playwright__Enabled=false \
  -e NUGET_PACKAGES=/nuget \
  -e LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES=DirectorySymbolicLinks,FileSymbolicLinks \
  "$IMAGE" \
  sh -euc '
    # Sync every run so local edits are what actually gets tested. bin/obj stay
    # in the volume between runs so the build is incremental.
    #
    # Drop every source file first. tar only adds and overwrites, so without
    # this a file deleted from the checkout lingers in the volume for ever and
    # tests keep passing against a file that no longer exists. bin and obj are
    # pruned so the incremental build survives; the empty directories left
    # behind are harmless and tar refills them.
    find /src -mindepth 1 \
      \( -type d \( -name bin -o -name obj \) \) -prune -o \
      -type f -print0 | xargs -0 --no-run-if-empty rm -f

    tar -C /mnt \
      --exclude=node_modules --exclude=.git --exclude=.claude --exclude=bin \
      --exclude=obj --exclude=dist \
      -cf - . | tar -C /src -xf -
    cd /src

    # Restore and build as root, which owns the volumes.
    dotnet build listenarr.slnx -c Release

    # Then run the tests as an unprivileged user. Several tests assert on
    # permission-denied behaviour and can only pass when the process is not
    # root; running as root made them permanent, meaningless failures and made
    # the local run diverge from CI, which runs unprivileged on ubuntu.
    id -u tester >/dev/null 2>&1 || useradd -m tester
    chown -R tester:tester /src /nuget
    exec su tester -s /bin/sh -c "cd /src && exec dotnet test listenarr.slnx -c Release --no-build \"\$@\"" -- sh "$@"
  ' -- "$@"
