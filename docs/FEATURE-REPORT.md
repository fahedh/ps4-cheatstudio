# Feature completion report — 0.1.0 experimental milestone

This report describes actual implemented paths and their limits. It does not certify a complete production application or a working real-game cheat. Source builds and simulated test evidence are distinct from Windows runtime and hardware evidence.

| Area | Implemented | Limits / remaining work |
|---|---|---|
| Primary user flow | First-launch setup, one-click saved connection, game/version detection, trainer selection, automatic bounded search, plain explanations, explicit test/restore/export | Polish/accessibility and launch behavior must be assessed on Windows; no user study |
| Connection | NG framing, brand/capability/firmware/platform, timeout, status errors, reconnect/new session, read-only diagnostics | Reviewed protocol 1.3 only; no legacy fallback, UDP discovery or reinjection |
| Explorer | Process list, foreground CUSA/version/process, maps, sizes, permissions | Ordinary writes limited to foreground CUSA eboot; no arbitrary system process editing |
| Simulator | Actual loopback TCP server, fragmented responses, deterministic process/maps/bytes, writes/status, reverse interrupts, fault knobs | Artificial memory, not game logic, CPU execution or firmware emulation |
| Trainer library | Fixed GoldHEN JSON/SHN import/export, strict bytes, local persistence/removal, version comparison, game profiles | Sliders, scripts, pointer commands, MC4 and extended SHN unsupported and rejected. Absolute SHN is preserved but cannot be auto-ported |
| Value scanning | Exact/unknown/increased/decreased/changed/unchanged, typed integers/float/double/hex, saved sessions, local range export, cancellation/progress | Bounded snapshots; numeric alignment from range start; no pointer-chain solver or native authenticated scan acceleration |
| Pattern scanning | AOB, whole-byte wildcards, boundary-spanning reads/windows, bounded results | No nibble masks, whole-process unbounded scan or GPU acceleration |
| Analyzer | Iced x86-64 disassembly, hex, instruction boundaries, relative jumps, branch targets, nearby code, possible padding/caves | No full decompiler, function/CFG recovery, symbolic execution, call graph or proof a cave is unused |
| Creator | Memory-value/code patch drafts, NOPs, rel32 calculation, library save, exact-byte reviewed manual tests | Multi-line trainers work in import/API/tests; no multi-line graphical editor. No automatic executable allocation or protection changes |
| Porting | Original-byte signatures, relative operand masks, match ranking/reasons, global ambiguity checks, cross-region mapping, cave/dependency analysis, known-target jump recalculation, candidate export | Function equivalence is unproven. Changed originals/external branch or RIP targets/ambiguous caves/overlapping old section offsets require manual work. No fabricated address or automatic activation |
| Live debugging | Value tracking; explicit attachment, slot-0 write watchpoint UI/all slots API, GP register display, thread step/resume, interrupts, detach | Manual trace only; no automatic writer-to-cheat synthesis, automatic trace capture, software breakpoint UI, register writes or thread browser |
| Patch safety | Session/game/version/maps/original verification, exact-plan approval digest, durable journals, dependency order, readback, same-session rollback | Not atomic; game not paused automatically. Partial/reconnected recovery requires manual review, not blind restore |
| Verification | Candidate vs byte-readback vs explicit user gameplay record; verification sidecars; simulator cannot earn hardware status | User gameplay observations are not independent verification. No hardware tests performed |
| Diagnostics | Automatic on connect, health checks, JSON-lines log rotation, cancellation/errors, exportable local report | Not a complete packet capture or crash-dump analyzer |
| Offline | Dump analysis, trainer library/profiles/export without network or API keys | Raw dump ≤64 MiB with manually supplied base/version; one dump loaded at a time; no multi-dump project UI |
| Distribution | Windows Actions restore/build/tests, published-exe WPF smoke, self-contained x64 packaging, artifacts, tagged prereleases | Check actual Actions runs for execution/artifact evidence. No signing, auto-updater, MSI or ARM64 package |

## Validation evidence

The automated suite covers framing, fragmented/short responses, errors, enumeration/maps, chunked reads, cancellation/timeouts/reconnect, scanner types/wildcards/boundaries/rescans, strict SHN/JSON handling, versions/hex, seven synthetic acceptance fixtures, disassembly/branches, candidate ambiguity, cave rejection, dependency ordering, approval mutation, original mismatch, identity changes, patch readback/rollback and journal retention. Result files are generated afresh under `artifacts/test-results`.

The WPF smoke harness is an additional Windows-only check. It opens the real window and exercises guided commands with real socket traffic to the simulator, writes a screenshot and fails on binding errors. Cross-compilation alone is not evidence that this launch test ran. See the current work report/Actions test artifacts for actual counts and execution status.

**Unimplemented substantive automation:** equivalent-function/CFG matching, automatic code-cave safety proof, arbitrary external jump/data-reference recovery, autonomous creation of a semantically correct new cheat from live events, pointer solving and automatic trace analysis. These are exposed as limitations rather than fake completed features. The real Silksong 01.05 Infinite Health acceptance goal remains unverified and unsolved until suitable real code/gameplay testing is available.
