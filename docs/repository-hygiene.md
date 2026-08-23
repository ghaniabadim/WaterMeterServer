# Repository Hygiene

## Tracked source of truth

- Application, Domain, Infrastructure, Networking, Protocol, tests, and WorkerService projects
- `Protocol Docs/` for protocol and architecture references
- `docs/` for project decisions, API contracts, and phase notes
- Docker and deployment instructions

## Local/generated files

The following must remain local and uncommitted:

- `.tmp/`
- `bin/`, `obj/`, `out/`, and IDE folders
- publish directories under `.deploy/`
- deployment archives under `.deploy/`
- local firmware test binaries
- environment and command-line artifact files

## Current worktree classification (2026-08-23)

- Phase 1 protocol/FOTA changes are source changes.
- `Protocol Docs/` and phase documents are intentional project references.
- The older protocol document is retained under `docs/archive/protocol/` as historical reference.
- Existing tracked deployment binaries are legacy repository content; removing them should be a separate, explicitly reviewed cleanup.

No commit or destructive cleanup is performed by this document.
