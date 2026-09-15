---
paths:
  - "fe/src/**"
---

# Frontend rules

Vue 3 with TypeScript, Pinia, Vue Router and Vite. Tests are vitest, colocated under `fe/src/__tests__/`.

## Components

- `<script setup lang="ts">` for everything new.
- Declare props with types and defaults, emit events through `defineEmits`, use `v-model` for two-way binding.
- Derived state is a `computed`, never a watcher writing to a ref. Reserve watchers for side effects.
- `provide`/`inject` for deep communication rather than prop drilling.
- Lazy-load routes and heavy components so they stay out of the initial bundle.
- `v-memo` on large lists needs every reactive value the row renders from in its dependency array, or you get stale rows.

## State

Stores live in `fe/src/stores/`. Mutate through actions, never by assigning to store state from a component. `downloads.ts` filters terminal statuses (`Completed`, `Moved`, `Failed`, `Cancelled`) out of the active set, so read active downloads through the store's computed rather than filtering again at the call site.

SignalR updates arrive through `@microsoft/signalr`. Connections drop; handle reconnection rather than assuming the first connect holds.

## Types

Every API response shape needs a type in `fe/src/types/index.ts`, and it must match what the backend actually serialises. When you change a backend DTO, change the type in the same commit. A mismatch here is invisible until runtime.

## Checks

`vue-tsc --build tsconfig.app.json` and `vitest run` both gate `git push`, so run them before you get there:

```bash
cd fe
npm run type-check
npm run test:unit
npm run lint:check
npm run format:prettier   # writes; format:prettier:check only reports
```

If type-check or the tests fail in a way that looks unrelated to your change, check that `fe/node_modules` matches the lockfile. A stale install fails all 89 test files at import and produces spurious type errors. `npm ci` resolves it.
