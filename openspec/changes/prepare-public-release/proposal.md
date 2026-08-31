# Prepare a reproducible public release

## Why

The repository is moving from a local, organization-specific worktree to a public GitHub
project. The public tree needs reproducible version metadata, safe defaults, generic report
templates and an auditable build/release path.

## Scope

- Remove personal and organization-specific values from tracked templates and defaults.
- Keep private templates local under `Templates/private/`.
- Centralize semantic version metadata in `Directory.Build.props`.
- Restore CI, add tag-based Windows packaging, and document release checks.
- Expand the README and contribution guidance without changing the core evidence model.

## Acceptance criteria

- A clean clone builds and tests without any private template or credential.
- Default automation is disabled and points only at reserved example values.
- Generic templates export successfully for both weekly formats.
- CI covers the .NET and AgentRuntime test suites.
- A `vMAJOR.MINOR.PATCH` tag produces a GitHub Release archive.
