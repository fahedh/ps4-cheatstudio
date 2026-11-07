# Validation record

Initial cloud implementation, 7 November 2026.

- .NET SDK **10.0.403**, obtained from Microsoft's official build distribution with a verified SHA-512 release-metadata checksum.
- **101 / 101 automated tests passed**, zero failed and zero skipped on the Linux cloud executor. Test evidence is generated under `artifacts/test-results/core.trx`; tests execute real loopback TCP traffic against artificial memory. This is not hardware validation.
- Full solution **Release build succeeded**, **0 warnings, 0 errors**. WPF was cross-compiled for `net10.0-windows/win-x64`; the application has not been launched on this Linux executor.
- Desktop and standalone simulator **self-contained win-x64 publish succeeded**. Portable packaging checks the PE machine type and ZIP integrity, includes guides/examples/notices, and generates a SHA-256 checksum.
- Canonical NuGet locks restored in locked mode. Runtime-specific publish graphs use separate ignored lock files so publishing does not corrupt the development locks.
- Windows-only first-launch/guided-workflow/binding smoke tests are implemented in the Actions workflow and run against the **published executable**. This file does not claim a Windows Actions run has succeeded or that a GitHub artifact is available. Inspect the repository's actual workflow run and test-evidence artifact for that result.
- **No real PS4 has been contacted or tested. No real Silksong 01.05 Infinite Health patch has been verified or supplied.** Equivalent functions, arbitrary reference relocation and cave safety still need manual investigation; see FEATURE-REPORT.md.

The Windows build pipeline intentionally fails before uploading a package if unit tests or the actual WPF launch workflow fail. A release must not be labeled production ready based on these simulated checks alone.
