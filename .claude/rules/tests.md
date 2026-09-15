---
paths:
  - "tests/**/*.cs"
---

# Backend test conventions

One xunit project at `tests/` covers all four backend projects. Structure mirrors the source: `tests/Features/...`.

## Shape

- Name the class `{TestedClassName}Tests` and inherit `BaseTests` (`tests/Common/BaseTests.cs`).
- Annotate with traits. `[Trait("Category", "...")]` is the common one, with `[Trait("Method", "...")]` and `[Trait("Area", "...")]` used to narrow further.
- Call `Init()` before adding any test data. It takes an optional configuration callback:
  ```csharp
  Init(services => services.WithSingleton(myMock.Object));
  Init(services => services.Without<IServiceToRemove>());
  ```
  Repository data added before `Init()` will not survive it.
- Write Given / When / Then.

## Builders and mocks

Use the fluent builders in `tests/Builders/` (`AudiobookBuilder`, `DownloadBuilder`, `ApplicationSettingsBuilder` and friends) with `.With...().Build()`. When a test needs a coherent entity that no builder covers, add a builder rather than repeating a large inline object initializer.

HTTP-level fakes live in `tests/Mocks/Api/` and inherit `BaseApiMock`, exposing `GetCallCount()`, `GetLastRequest()` and `GetLastContent()`. Other service fakes sit directly in `tests/Mocks/`.

## Running them

Use `scripts/test-backend-docker.sh`, not `dotnet test`. The native macOS run cannot work: the filesystem layer refuses non-x64 macOS, and the suite deadlocks partway through. The script takes the same arguments, so `scripts/test-backend-docker.sh --filter FullyQualifiedName~YourTestClass` works for a tight loop.

Baseline is 3116 passed, 2 failed, 130 skipped. The two failures are container-as-root artifacts, listed in the root `CLAUDE.md`. Treat anything else as yours.

Passing tests are evidence, not proof. When reviewing a change, look for the cases that were not written, tests that only restate the implementation, and platform tests that were skipped on the host rather than actually run.
