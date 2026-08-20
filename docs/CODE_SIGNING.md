# Code signing policy

This policy describes the code-signing responsibilities for CodexQuotaTray. It is
prepared for the SignPath Foundation application; it does not enable SignPath,
change the current GitHub Actions workflows, or imply that existing releases are
signed.

**Free code signing provided by SignPath.io, certificate by SignPath Foundation.**

## Project and signing scope

CodexQuotaTray is a public, MIT-licensed, read-only Codex quota client. The
Windows application displays quota, reset-time, alert, refresh, and local token
statistics information. It does not consume reset credit or perform account write
operations.

If the project is accepted, this policy applies to CodexQuotaTray's own Windows
executables, libraries, and installer built from the repository's source and build
scripts. Signed artifacts must correspond to a verifiable source commit and the
release rules in [`docs/RELEASE.md`](RELEASE.md). Third-party components are not
signed as if they were CodexQuotaTray components; their provenance and licenses
remain tracked in [`docs/DEPENDENCIES.md`](DEPENDENCIES.md).

## Team roles and members

The current project is maintained by one GitHub account. The role split is
documented explicitly so it can be updated if the maintainer team changes.

### Committers/Authors

- [`DDJang`](https://github.com/DDJang) — repository owner and maintainer; trusted
  to modify CodexQuotaTray source code and build scripts.

### Reviewers

- [`DDJang`](https://github.com/DDJang) — reviews pull requests from contributors
  who do not have committer access. A change is not merged until the required
  review is complete.

### Approvers

- [`DDJang`](https://github.com/DDJang) — release owner; decides whether a release
  is ready to request for signing after checking the source commit, version,
  build, tests, and release artifacts.

The same maintainer currently holds all three project roles. If additional
maintainers receive repository or signing access, this list and the corresponding
permission groups must be updated before that access is used. Every signing
request is intended to remain a manual approval decision by an approver.

## Change, build, and release controls

- Changes from non-committers are submitted as pull requests and reviewed before
  merge; source code, build scripts, and CI configuration are reviewed together.
- Release artifacts are produced by the existing platform-specific workflow from
  a `main` commit and a matching platform tag, then checked against the published
  version and SHA256 manifest.
- Only CodexQuotaTray's own source and build outputs may be submitted for signing.
  System libraries and documented open-source runtime dependencies may be included
  in a package, but are not represented as project-owned source.
- A release must pass the existing release validation and receive an explicit
  manual approval before any future signing request. No certificate, private key,
  or SignPath credential is stored in this repository.
- Maintainers will investigate and cooperate with reports of malware, unwanted
  behavior, or a violation of this policy.

## Privacy, security, and uninstallation

The single project privacy policy is [`docs/PRIVACY.md`](PRIVACY.md). It is the
authoritative privacy document; this policy does not duplicate it. The application
does not include features intended to exploit vulnerabilities or bypass system
security measures.

The Windows installer provides an automated uninstall path, including removal of
the installed program and an explicit choice about retaining user data. User-facing
download and installation details are in [`docs/RELEASE.md`](RELEASE.md).

All maintainers must use multi-factor authentication for source-repository access
and, once enabled, SignPath access. MFA enrollment and the SignPath application
are operational steps outside this repository change.

## Current integration status

SignPath is not currently connected to this repository. This document is a
pre-application policy only: no SignPath API call, secret, certificate, workflow
step, or release behavior is added by this change.
