# Documentation Index

The documents in this directory have two lifecycles:

- The root documents describe current project facts and are the authoritative references for ongoing work.
- [`investigations/`](investigations/README.md) records engineering investigations, experiments, failure
  analysis, implementation decisions, and their historical outcomes.

## Current project facts

| Area | Document | Purpose |
| --- | --- | --- |
| Product | [PRD](PRD.md) | Product requirements and boundaries |
| Windows | [Roadmap](ROADMAP.md) | Windows platform direction |
| Android | [Roadmap](ANDROID_ROADMAP.md) | Android platform direction |
| Architecture | [TECH_DESIGN](TECH_DESIGN.md) | System architecture and identity boundaries |
| Protocol | [API_CONTRACT](API_CONTRACT.md) | Protocol and persistence contracts |
| Privacy | [PRIVACY](PRIVACY.md) | Privacy and read-only boundaries |
| Dependencies | [DEPENDENCIES](DEPENDENCIES.md) | Dependency and license records |
| Signing | [CODE_SIGNING](CODE_SIGNING.md) | Code-signing policy |
| Release | [RELEASE](RELEASE.md) | User-facing release guidance |
| Release execution | [RELEASE_PROCESS](RELEASE_PROCESS.md) | Release execution state machine |

## Historical engineering records

- [Engineering investigations](investigations/README.md)

## Placement rules

Use this decision before adding a document:

1. If the document states what the project currently promises or requires, keep it in the `docs/` root and
   update the relevant authoritative document instead of creating a second source of truth.
2. If it explains an investigation, experiment, root cause, rejected approach, performance result, or fix
   decision, place it under [`investigations/`](investigations/README.md) by platform.
3. If it is a diagram or shared visual asset, place it under `assets/`.

Investigation files use stable kebab-case names. Keep them in place after completion and record lifecycle
status, outcome, `Resolved by` (use `—` while `Active`; fill in the corresponding implementation or conclusion
commit once `Resolved` or `Closed`), and last verification date in the investigation index. Do not create
`active`, `resolved`, or `archive` directories, and rename an implemented `*-plan.md` into a record name.

## Supporting assets

- [Architecture diagrams](assets/)
