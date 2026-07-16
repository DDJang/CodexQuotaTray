# Release and packaging guide

## Artifact

P4 uses a per-user, no-admin ZIP package. It contains the native x64 executable, PowerShell install/uninstall scripts, a SHA-256 manifest, privacy and dependency documents, and complete third-party license files collected from Cargo metadata.

Build it from a clean checkout with the locked dependency graph:

```powershell
pwsh -NoProfile -File .\scripts\package.ps1 -Cargo C:\Users\<user>\.cargo\bin\cargo.exe
```

The output is `dist\CodexQuotaTray-<version>-win-x64.zip`. The script runs locked release compilation through `cargo rustc`, remaps the local repository path in the final crate, uses the checked-in Cargo.lock, restricts metadata to `x86_64-pc-windows-msvc`, verifies that every dependency has license material, and writes `MANIFEST.sha256` before compression. This is a repeatable build procedure, not a claim of bit-for-bit reproducibility: ZIP metadata and the local Rust toolchain can change artifact bytes and therefore the archive hash.

Install for the current user:

```powershell
Expand-Archive .\CodexQuotaTray-0.1.0-win-x64.zip .\CodexQuotaTray
pwsh -NoProfile -ExecutionPolicy Bypass -File .\CodexQuotaTray\install.ps1 -StartWithWindows
```

The install location is `%LOCALAPPDATA%\Programs\CodexQuotaTray`. Running the installer again performs an in-place upgrade after requesting normal shutdown of the existing instance. Uninstall with the installed `uninstall.ps1`; user data is deleted unless `-KeepUserData` is supplied.

## Signing strategy

Current local artifacts are intentionally unsigned and must be labeled as developer builds. They may trigger Windows SmartScreen warnings.

The in-package SHA-256 manifest detects accidental corruption and a file changed without updating the manifest. Because the developer package is unsigned, an attacker who can replace both files and manifest can forge that check; it is not an authenticity boundary. Public release authenticity requires the signing and separately published checksum process below.

A public release requires an organization-controlled Authenticode certificate or Microsoft Trusted Signing identity. The release operator should build in a controlled CI environment, sign the EXE and PowerShell scripts, verify every signature, regenerate the SHA-256 manifest after signing, create the ZIP, publish the ZIP checksum, and retain a provenance record. Credentials must stay in the signing service/HSM and never enter this repository or package.

No signing or publishing step is automated in this repository because the required identity is not available and the user explicitly prohibited automatic publication.

## Validation matrix

- Automated: locked release build, repository-path redaction, manifest verification, install, same-version overwrite/upgrade, isolated start-with-Windows registry value, shortcut creation, uninstall, default user-data removal, `-KeepUserData` preservation, size, and package-content checks.
- Completed manually: Windows 11 10.0.26200 x64 tray/runtime smoke.
- Required before public release: Windows 10 x64 install/upgrade/uninstall/tray smoke; Windows 11 stable-channel smoke; 100/125/150/200% DPI and multi-monitor positioning; signed artifact verification; real official Usage navigation.
- Stability: the P1 24-hour soak is the MVP gate currently in progress. The PRD's seven-day target is a release-quality objective and must be reported honestly until a full run completes.
