# Listenarr

Audiobook collection manager in the *arr mould: searches indexers, drives download clients, imports and organises files. ASP.NET Core on .NET 10 with a Vue 3 frontend. This checkout is a fork; `origin` is the fork, `upstream` is the source project.

## Layout

Clean architecture, four backend projects plus a frontend. Dependencies point inward only.

| Path | Holds |
|---|---|
| `listenarr.domain/` | Entities and enums. No dependencies on the other layers. |
| `listenarr.application/` | Orchestration services and the ports they depend on. |
| `listenarr.infrastructure/` | EF Core persistence, download client adapters, filesystem, metadata providers, hosted services. |
| `listenarr.api/` | Controllers and DTOs, organised as feature folders under `Features/`. Composition root is `Program.cs`. |
| `tests/` | One xunit project covering all of the above. |
| `fe/src/` | Vue 3 frontend. |

Notable files: `listenarr.infrastructure/Persistence/ListenArrDbContext.cs`, migrations in `listenarr.infrastructure/Persistence/Migrations/`, solution is `listenarr.slnx` (slnx format, not .sln).

## Commands

Run everything from the repository root. Running the API from `bin/Debug` creates a second, empty database.

```bash
npm run dev            # API on :4545 and Vite on :5173
npm run build          # both
dotnet build listenarr.slnx
scripts/test-backend-docker.sh    # backend tests, see below: do NOT use dotnet test
cd fe && npm run test:unit
```

Dev runtime state lives under `.env/development/config/`: SQLite at `database/listenarr.db`, logs at `logs/listenarr-YYYYMMDD.log`. Migrations apply on startup. `dev:api` is plain `dotnet run`, not `dotnet watch`, so restart the API after backend edits.

## Backend tests: use the container

Do not run `dotnet test` on macOS. It cannot work and it deadlocks partway through. Run this instead:

```bash
scripts/test-backend-docker.sh
scripts/test-backend-docker.sh --filter FullyQualifiedName~RootFolderRelocation
REBUILD=1 scripts/test-backend-docker.sh    # discard cached build output
```

Needs Docker running. First run takes a few minutes to restore and build; after that the suite is about 40 seconds.

Current baseline: 3116 passed, 2 failed, 130 skipped. The two failures are artifacts of the container running as root (`MoveSourceManifestServiceTests.BuildAsync_CompanionAuthorizationRootTemporarilyUnavailable_DoesNotSilentlyOmitCompanion` and `FileSystemSafetyDeletionTests.TryDeleteFile_InaccessibleParent_IsNotTreatedAsMissing`, both asserting on permission-denied paths that cannot occur as root). Anything beyond those two is yours.

Two reasons the native run fails, both worth knowing before you touch this code:

- `FileSystem/UnixOpenFlags.cs` refuses any macOS process architecture except x64, so on Apple Silicon every directory-handle path throws `PlatformNotSupportedException`. The flag constants in that file are correct for arm64 (verified against the macOS SDK headers, and the syscalls behave correctly including `O_NOFOLLOW` returning ELOOP), so the restriction is a release-matrix policy rather than a technical limit.
- `FileSystem/FileSystemSemanticsResolver.cs` accepts a case-sensitivity answer only from an allowlisted filesystem: ext family, f2fs, tmpfs, bcachefs. Tests work in `Path.GetTempPath()`, so that path has to be one of those. This is why the script mounts `--tmpfs /tmp`; on plain overlayfs about 250 tests fail and the run then deadlocks.

## Enforced gates

These run as git hooks, so a violation stops the commit or push rather than showing up in review.

Pre-commit (`.husky/pre-commit`):
- `node scripts/lint-staged.mjs` over staged files
- `listenarr.api/**/*.cs` must not mention `Listenarr.Infrastructure`, except `Program.cs`
- `listenarr.application/**/*.cs` must not mention `Listenarr.Infrastructure` at all
- no `async void` anywhere in the four backend projects

Pre-push (`.husky/pre-push`):
- `node scripts/validate-repository-paths.mjs`
- `node scripts/sync-fe-version-from-csproj.mjs`
- `dotnet format listenarr.slnx --no-restore --verify-no-changes`
- `cd fe && vue-tsc --build tsconfig.app.json`
- `cd fe && vitest run`

The formatter rejects alignment padding. Write `["ca"] = ("www.audible.ca", "www.amazon.ca")`, never spaces added to line values into columns. `dotnet format` fixes it.

## Branching

`canary` is the integration branch and the target for all feature PRs. `beta` is for stabilisation by org members, `main` is written by the release workflow only. Branch off the latest `canary`, name branches after what they do (`123-audible-integration`, `bugfix/search-results`). PRs against `canary` need exactly one of the `patch`, `minor` or `major` labels.

## Working here

- Update tests under `tests/` when you change public behaviour or a DI constructor signature.
- Every API response shape needs a matching type in `fe/src/types/index.ts`.
- Log flow transitions at INFO and verbose payloads at DEBUG, with structured placeholders: `_logger.LogInformation("Processing {Id}", id)`.
- Backend projects use `packages.lock.json`, so a dependency change needs the lock file regenerated.
- Never hardcode credentials or API keys. Validate any user-supplied path before touching the filesystem.

## Older guidance

`.github/` holds a large set of AI instruction files (`copilot-instructions.md`, `.cursorrules`, `AGENTS.md`, `RULES.md`, and per-vendor variants). Treat them as background, not fact. They describe a `listenarr.api/Controllers|Models|Services` layout that no longer exists and reference `CompletedDownloadProcessor.cs`, which is not in the tree. Verify anything you take from them against the code.
