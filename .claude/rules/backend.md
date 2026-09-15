---
paths:
  - "listenarr.domain/**/*.cs"
  - "listenarr.application/**/*.cs"
  - "listenarr.infrastructure/**/*.cs"
  - "listenarr.api/**/*.cs"
---

# Backend rules

## Layering

Dependencies point inward: infrastructure and api depend on application, application depends on domain. The pre-commit hook greps for violations, so these are hard failures rather than review comments.

- `listenarr.api/**/*.cs` may not mention `Listenarr.Infrastructure`. The one exception is `Program.cs`, the composition root.
- `listenarr.application/**/*.cs` may not mention `Listenarr.Infrastructure` at all.

When application code needs something infrastructure-shaped (a filesystem, an HTTP client, a download client), define the port in `listenarr.application` and put the adapter in `listenarr.infrastructure`. Do not reach across.

## Hard rules

- No `async void` in any of the four projects. Use `async Task`. The hook rejects it.
- No alignment padding. The formatter treats extra spaces used to line up values into columns as a whitespace error. Run `dotnet format` before pushing.
- All I/O is async. Use `AsNoTracking()` for read-only EF queries.
- Constructor injection for services. When you change a constructor signature, update the affected tests in the same change.
- Never hardcode secrets. Read configuration through `IConfiguration`.
- Validate user-supplied paths before any filesystem call.

## Persistence

`ListenArrDbContext` lives in `listenarr.infrastructure/Persistence/`, migrations alongside it in `Persistence/Migrations/`. Migrations apply automatically at startup, so there is no manual `dotnet ef database update` step in normal development.

A migration becomes an immutable historical artifact once it reaches the target branch. While your branch is unmerged, delete superseded branch-only migrations, restore the target branch snapshot, and regenerate the minimum final migration from the cleaned model rather than stacking corrective migrations.

## Dependency injection

Registrations live under `listenarr.infrastructure/DependencyInjection/` and `listenarr.api/Startup/`. When you add or change a registration, trace the constructor graph and check lifetimes. A singleton or hosted service capturing a scoped dependency such as a DbContext or repository is a bug even when the tests pass, because the test host often builds a different graph than production.

## Packages

Central package management: versions live in `Directory.Packages.props`, and each project has a `packages.lock.json`. A dependency change means editing the props file and regenerating the lock files, not adding a version attribute to the `PackageReference`.

## Filesystem code

`listenarr.infrastructure/FileSystem/` is platform-sensitive and deliberately restricted to the published release matrix: Linux x64 and arm64, macOS x64. macOS arm64 throws by design. Changes here need validation on real Linux, not a macOS run with the platform tests skipped.
