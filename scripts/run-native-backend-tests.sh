#!/usr/bin/env bash
# Run the backend suite natively on a CI runner.
#
# On Linux this first builds a real read-only bind mount and points
# LISTENARR_READONLY_LIBRARY_PATH at it, so the [ReadOnlyBindMountFact] tests
# exercise a genuine read-only filesystem rather than skipping. Afterwards it
# proves the suite left no Listenarr artifacts behind on the read-only source.
#
# A capability preflight runs first: if the runner cannot provide the
# capabilities this job declares, the whole run fails loudly instead of
# silently skipping the tests that need them.
#
# Usage:
#   scripts/run-native-backend-tests.sh
#   scripts/run-native-backend-tests.sh --results-directory ./artifacts/test-results
#   scripts/run-native-backend-tests.sh --results-directory ./out --coverage
#
# Works under Linux and macOS bash and under Git Bash on Windows runners
# (`shell: bash`), which is why it is not PowerShell: pwsh ships on the GitHub
# runner images but not on a typical developer machine, so the PowerShell
# version could not be run or syntax-checked locally.

set -uo pipefail

RESULTS_DIRECTORY=""
COVERAGE=0

while [ $# -gt 0 ]; do
  case "$1" in
    --results-directory)
      RESULTS_DIRECTORY="${2:-}"
      shift 2
      ;;
    --coverage)
      COVERAGE=1
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [ -z "${LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES:-}" ]; then
  echo "LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES must declare the native capabilities required by this CI job." >&2
  exit 1
fi

echo "Required native test capabilities: $LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES"
echo "Runner OS: ${RUNNER_OS:-$(uname -s)}"
echo "Runner architecture: ${RUNNER_ARCH:-$(uname -m)}"
echo "Runner image: ${ImageOS:-unknown} ${ImageVersion:-unknown}"

# The preflight only proves the capability contract, so it takes the results
# directory but never coverage; the main run takes both.
preflight_args=()
suite_args=()
if [ -n "$RESULTS_DIRECTORY" ]; then
  mkdir -p "$RESULTS_DIRECTORY"
  preflight_args+=(--results-directory "$RESULTS_DIRECTORY")
  suite_args+=(--results-directory "$RESULTS_DIRECTORY")
fi
if [ "$COVERAGE" = "1" ]; then
  suite_args+=(--collect "XPlat Code Coverage")
fi

readonly_source_root=""
readonly_mount_root=""
readonly_mount_active=0

cleanup() {
  unset LISTENARR_READONLY_LIBRARY_PATH
  if [ "$readonly_mount_active" = "1" ] && [ -n "$readonly_mount_root" ]; then
    if ! sudo umount "$readonly_mount_root"; then
      echo "Warning: could not unmount read-only validation path $readonly_mount_root." >&2
    fi
  fi
  [ -n "$readonly_mount_root" ] && rm -rf "$readonly_mount_root" 2>/dev/null
  [ -n "$readonly_source_root" ] && rm -rf "$readonly_source_root" 2>/dev/null
  return 0
}
trap cleanup EXIT

if [ "$(uname -s)" = "Linux" ]; then
  mount_id="$(cat /proc/sys/kernel/random/uuid | tr -d '-')"
  tmp_root="${TMPDIR:-/tmp}"
  tmp_root="${tmp_root%/}"
  readonly_source_root="$tmp_root/listenarr-readonly-source-$mount_id"
  readonly_mount_root="$tmp_root/listenarr-readonly-mount-$mount_id"
  book_directory="$readonly_source_root/Author/Book B012345678"

  mkdir -p "$book_directory" "$readonly_mount_root"
  printf 'audio' > "$book_directory/01.m4b"

  if ! sudo mount --bind "$readonly_source_root" "$readonly_mount_root"; then
    echo "Could not create the native read-only validation bind mount." >&2
    exit 1
  fi
  readonly_mount_active=1

  if ! sudo mount -o remount,bind,ro "$readonly_source_root" "$readonly_mount_root"; then
    echo "Could not remount the native validation bind mount read-only." >&2
    exit 1
  fi

  mount_options="$(findmnt -no OPTIONS --target "$readonly_mount_root")"
  case ",$mount_options," in
    *,ro,*) ;;
    *)
      echo "Expected a read-only bind mount, got: $mount_options" >&2
      exit 1
      ;;
  esac

  export LISTENARR_READONLY_LIBRARY_PATH="$readonly_mount_root"
  echo "Read-only scan validation mount: $readonly_mount_root"
fi

preflight_filter='FullyQualifiedName=Listenarr.Tests.Features.Architecture.NativeTestCapabilityContractTests.RequiredNativeTestCapabilities_AreAvailable'

# A distinct .trx per run so a preflight failure is legible in the published
# report rather than looking like a missing suite.
dotnet test tests/Listenarr.Tests.csproj \
  -c Release \
  --no-build \
  --filter "$preflight_filter" \
  --logger 'console;verbosity=normal' \
  --logger 'trx;LogFileName=preflight.trx' \
  ${preflight_args[@]+"${preflight_args[@]}"}
exit_code=$?

if [ "$exit_code" -eq 0 ]; then
  dotnet test listenarr.slnx \
    -c Release \
    --no-build \
    --logger 'console;verbosity=normal' \
    --logger 'trx;LogFileName=backend.trx' \
    ${suite_args[@]+"${suite_args[@]}"}
  exit_code=$?
fi

if [ "$exit_code" -eq 0 ] && [ -n "$readonly_source_root" ]; then
  # -iname, because the PowerShell version this replaced compared with
  # OrdinalIgnoreCase.
  artifacts="$(find "$readonly_source_root" -iname '.listenarr*' -print 2>/dev/null | tr '\n' ' ')"
  if [ -n "${artifacts// /}" ]; then
    echo "Read-only scan validation found Listenarr filesystem artifacts: $artifacts" >&2
    exit_code=1
  fi
fi

exit "$exit_code"
