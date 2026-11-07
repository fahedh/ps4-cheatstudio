# Validation record

Initial cloud implementation, 7 November 2026.

- .NET SDK **10.0.403**, obtained from Microsoft's official build distribution with a verified SHA-512 release-metadata checksum.
- **101 / 101 automated tests passed**, zero failed and zero skipped on the Linux cloud executor. Test evidence is generated under `artifacts/test-results/core.trx`; tests execute real loopback TCP traffic against artificial memory. This is not hardware validation.
- Full solution **Release build succeeded**, **0 warnings, 0 errors**. WPF was cross-compiled for `net10.0-windows/win-x64`; the application has not been launched on this Linux executor.
- Desktop and standalone simulator **self-contained win-x64 publish succeeded**. Portable packaging checks the PE machine type and ZIP integrity, includes guides/examples/notices, and generates a SHA-256 checksum.
- Canonical NuGet locks restored in locked mode. Runtime-specific publish graphs use separate ignored lock files so publishing does not corrupt the development locks.
- **Windows Actions validation succeeded** for application commit `5c0129e20d7888811f69143e2b8eca03c3773e70`: [successful run 42416122668](https://github.com/fahedh/PS-AICheatsCreator/actions/runs/42416122668). Restore, Release compilation, the test suite, both standalone publishes, actual WPF launch, portable packaging and artifact upload all passed on `windows-latest`.
- The published executable passed real WPF first-launch wizard navigation, persisted settings, loopback simulator connection, exact game/version/maps diagnostics, trainer import, guided relocation, patch/readback, rollback/readback, candidate/diagnostics export, binding checks and screenshot rendering. No hardware success could be recorded for the simulator.
- The run contains **PS4GoldHENCheatStudio-win-x64** (87.2 MB Actions artifact, including the portable ZIP and checksum) and **Windows-test-evidence** (TRX results, rendered screenshot and smoke result). GitHub sign-in may be needed for Actions downloads. This documentation update does not change the tested application code; the pinned run identifies exactly which build is downloadable.
- **No real PS4 has been contacted or tested. No real Silksong 01.05 Infinite Health patch has been verified or supplied.** Equivalent functions, arbitrary reference relocation and cave safety still need manual investigation; see FEATURE-REPORT.md.

The Windows build pipeline intentionally fails before uploading a package if unit tests or the actual WPF launch workflow fail. A release must not be labeled production ready based on these simulated checks alone.


## 0.2 post-implementation audit — 8 November 2026

Application commit `d0cf40793aa144c167426fde4a6de327a2e63b84` passed [Windows Actions run 42422376905](https://github.com/fahedh/PS-AICheatsCreator/actions/runs/42422376905). The local audit suite passed **164/164 tests, zero failed/skipped**; the Windows unit/integration job also passed. Restore, full Release compilation, self-contained desktop/simulator publishes, actual WPF audit workflow, package generation/ZIP validation and both artifact uploads succeeded.

The expanded WPF test exercises SHN PS5Offset and author round trips; the real multi-line grid and validation; direct and guided multi-line tests with separate cave confirmations and rollback; changed offline code, old evidence and guided editor fallback; wrong-version capture refusal and verified old hook capture; and bindings on all main/advanced screens. Download **PS4GoldHENCheatStudio-win-x64** from that run; inspect **Windows-test-evidence** for TRX, screenshot and the detailed PASS list. Packaging includes the 0.2 audit guide and synthetic examples. No PS4 hardware or real Silksong health behavior was tested. See AUDIT-REPORT.md.
