# Architecture and development

## Modules

| Project | Responsibility |
|---|---|
| Studio.Core | Bounded protocol client, models, scanner, trainer codecs, Iced analysis/relocation, patch review/journaling, storage/logs |
| Studio.Mock | Loopback-only ps4debug-NG subset and deterministic artificial memory |
| Studio.MockHost | Standalone simulator console executable |
| Studio.Desktop | WPF resources/views, first-launch wizard, guided workflow, MVVM commands, advanced tools, Windows smoke harness |
| Studio.Tests | xUnit unit tests and TCP integration tests against the simulator |

No remote server or API key is needed. The desktop embeds the simulator library for an ordinary-user demo; the separate host supports protocol development. Core does not depend on WPF. `IProcessMemory` provides a testable memory target. `MemorySession` owns the shared high-level operation gate; the client separately serializes socket exchanges. UI commands share a busy/cancellation boundary. Do not bypass these boundaries for new writes or scans.

## Build and verify

Use the pinned SDK in `global.json` and checked-in NuGet locks:

```sh
dotnet restore PS4GoldHENCheatStudio.sln --locked-mode
dotnet test tests/Studio.Tests -c Release --logger 'trx;LogFileName=core.trx' --results-directory artifacts/test-results
dotnet build src/Studio.Desktop -c Release --no-restore
dotnet run --project src/Studio.MockHost -- --port 1744
```

On Windows run `dotnet run --project src/Studio.Desktop -c Release`. The self-contained published exe supports `--smoke-test`: it creates a temporary isolated user-data directory, launches the real WPF window, verifies bindings, connects to the embedded TCP simulator, imports a synthetic trainer, analyzes, approves **simulation-only** writes via a test dialog provider, verifies rollback and exports trainer/diagnostics. It saves a rendered screenshot and result text to `artifacts/ui-smoke` and exits nonzero on failure. This flag does not connect to a real PS4 or accept live approval.

CI restores, compiles and tests on `windows-latest`, publishes self-contained desktop/host exes, launches the published WPF app, packages assets with `build/Package.ps1`, and uploads the ZIP/checksum plus evidence. Release tags publish a prerelease after the Windows job succeeds. No developer tools are required on the download recipient's PC.

Publishing uses `-p:StudioPublish=true` to isolate runtime-specific restore locks in each project's ignored `obj` directory. A publish command's RID/single-file flags flow to referenced libraries; their transient restore graphs must not overwrite the canonical development locks. The workflow uses the same package version pins for these publish graphs.

In Codex Linux, `build/install-dotnet.sh` installs a checksum-verified SDK under `/workspace/.dotnet`. Export `DOTNET_ROOT=/workspace/.dotnet`, add it to PATH, set `DOTNET_CLI_HOME=/workspace/.dotnet-home` and `NUGET_PACKAGES=/workspace/.nuget`. This executor's .NET named mutex needs the supported outside-sandbox command execution mode; that is an execution-host constraint, not an app dependency. Use the existing checkout, not another worktree.

## Protocol invariants

Requests have a 12-byte little-endian header: magic `0xFFAABBCC`, command, body length. There is no pointer on the wire. Version/branding replies start with a length, **without** the usual success word; firmware/platform are raw UInt16. Processes have 36-byte entries; maps have 64-byte packed entries; foreground metadata is 132 bytes. Reads are split into 64 KiB requests. Writes consume both the ready status and the truthful terminal status and require capability 1.1. Unexpected status, I/O failure, timeout or cancellation closes the socket so unread data is never interpreted as a later response. Reconnect produces a new identity, invalidating scans/approvals.

`fw.c` returns decimal major × 100 + minor (1352 → 13.52), correcting the inconsistent BCD example in PROTOCOL.md. Source citations and reviewed commits are in REFERENCES.md. Only NG protocol 1.3 with the expected NG brand/platform is accepted. The scan authentication keystream is undocumented and is **not** implemented; scans use ordinary reads and local analysis instead. No kernel opcodes, payload injection or scan-auth bypass are exposed.

Hardware debug attachment opens a listener before sending the packed PID/client-IP packet. The reverse connection must match the chosen console IPv4 and fixed port 755. Interrupt frames are bounded to the documented 1184 bytes; general registers begin at byte 48, RIP at register offset 136 and RSP at 160. Watchpoint length uses DR7 encoded values, not raw byte width. The mock is deterministic and tests framing; hardware handling remains unverified.

## Porting and safety design

Trainer originals seed exact/wildcard signatures. Displacement bytes for relative branches/RIP operands can be masked for discovery. Changed original bytes, multiple matches and unresolved addresses do not generate a testable proposal. Segment overlaps are deduplicated by address; ambiguity is resolved across the complete bounded search, not per window. Cave padding is detected conservatively but is not proven unused. The general scanner contains no CUSA-specific logic.

Iced decodes and re-encodes x86-64 replacement blocks. Branches to known relocated patch starts/ends are mapped; external branch/data targets without a relocation are refused. `DontFixBranches` retains branch sizes; out-of-range or resized code is rejected. Cross-line cave dependencies are derived so cave bytes precede a hook. Cycles/missing dependencies and overlapping writes are rejected.

Review hashes a deep copy of the exact plan. Approval must bind to that hash, the write opt-in and explicit risk acceptance. Apply reacquires the high-level gate and rechecks game, session, maps and originals, persists the journal, writes and reads back. A write error is reported as possible partial modification, never rolled back blindly or reported as successful. User-confirmed gameplay is a separate desktop state and record; historical profile data cannot grant it.

## Extending the application

Add a profile through the UI or `GameProfile` JSON; game support should not need code. Add protocol features only after validating source framing/status/data phases and a meaningful simulator test. Avoid guessing feature support: unknown opcodes can cause a timeout, and debug commands without attachment can receive no response. Preserve cancellation and close-on-desync behavior.

Full equivalent-function detection, control-flow-graph matching, symbolic reasoning, pointer-chain discovery and automatic arbitrary external-reference relocation are future work. The current engine explicitly requires manual verification for those cases; it does not substitute a fabricated confidence result.
