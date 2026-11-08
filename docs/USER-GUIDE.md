# User guide

## Start with the guided workflow

On first launch, the three-page wizard explains the safety model, saves your connection settings and optionally tests a connection. Setup can finish offline. Reopen the Connect step to change settings later. **Connect / test connection** is one operation after initial setup: handshake, firmware/brand/capability detection, foreground game and exact version, process maps and automatic read-only diagnostics.

Enable the GoldHEN payload loader before connecting. Studio checks the debugger first and can start it when its port refuses the connection. Do not choose simulator mode when you intend to use a real PS4. The green connection label always distinguishes a live connection, a simulator and an offline dump.

Import a `.json` or `.shn` trainer. Select a cheat in **Choose trainer**. Fixed checkbox memory patches are supported; extended commands/scripts/sliders are rejected rather than silently dropped. Known SHN PS5Offset text, original author attributes and genres are preserved during SHN round trips. PS5Offset never overrides PS4 Section/Offset fields. New PS4 target proposals clear old PS5 offsets instead of claiming they apply to another platform/version. SHN section indices and absolute flags are preserved for inspection. Absolute-address SHN patches need manual mapping and are not automatically ported.

For code/instruction cheats leave the instruction option checked. For fixed values clear it. The search requires the source CUSA/process to match the running game, and uses the detected target version. It searches every relevant readable region within the bounded budget (default 256 MiB, adjustable up to 1024 MiB in Advanced Mode). It does not silently scan a small prefix and claim a unique match. If relevant maps exceed the budget, increase it or use a bounded offline dump.

The analysis compares original signatures, x86-64 instruction layout, possible padding/caves, branch relocation and dependencies. A missing signature, ambiguous match, changed original bytes or unknown external jump target stops automatic proposal generation. A cave is **possible padding**, not proof of unused memory. Select a possible cave and choose **Review selected cave**. A proposal remains blocked until you explicitly confirm its use; detected incoming code/data references or invalid executable mappings cannot be bypassed with that confirmation. The score is an evidence indicator, not a percentage chance of success. Equivalent functions, game semantics and safe unused caves are not automatically proven.

## Test with a rollback path

Back up saves. Enable **explicit write testing** and choose **Review and test candidate**. The confirmation window displays the exact game/version, absolute addresses and original/replacement bytes. You must check the risk acceptance box and click **Write these bytes**. Cancel is the default choice.

The app rechecks the connection session, game/version/process, mapped regions and original bytes. It writes a durable local journal before modifying memory, orders cave dependencies before hooks, and reads each patch back. A successful readback only establishes byte transfer. Test the actual effect yourself during gameplay. If wrong, choose **Restore original bytes** and approve the reverse changes. Restoration rejects bytes that no longer match the tested replacement.

Multi-line live patches are not atomic, the game is not automatically paused, and memory can change after a read. A failed write may have modified part of memory. Stop gameplay and inspect the journal. Automatic rollback after a reconnect/game restart is deliberately blocked because addresses may have been recycled. See troubleshooting; do not repeatedly apply a failing candidate.

Only a live-PS4 byte test followed by **I tested it on my real PS4** can record user-confirmed hardware gameplay. The app also checks the current bytes and exact session again. This records your observation, not independent certification. Simulated checks cannot earn this label. Selecting another trainer, changing patch mode or analyzing again resets active confirmation. Historical records never activate a patch.

## Export and install

Export selects JSON or SHN, uses the target version and emits an adjacent `.verification.json` file. Unconfirmed results default to `CUSAxxxxx_XX.YY.candidate.json`; do not install these blindly. The sidecar records simulation/offline status, candidates and observations. In the Export step, verify or edit the main-module base (default: lowest mapped address). A partial offline dump may start above the actual module base. Confirm the value against the original eboot mapping before exporting. Section indices are not stable IDs.

After real gameplay validation, use the exact GoldHEN filename `CUSAxxxxx_XX.YY.json` or `.shn`. Transfer it using your FTP client to `/user/data/GoldHEN/cheats/json/` or `/user/data/GoldHEN/cheats/shn/`. Install only one format per title/version. No FTP transfer, payload injection or trainer activation happens automatically. Keep the sidecar on the PC.

## Library, profiles and version comparison

The library persists locally. Import, select, export or remove a trainer without deleting the original file. **Compare versions** loads another trainer for the same CUSA/process and compares names, offsets and bytes; equal offsets do not establish compatibility. Profiles can be created by entering a game name, CUSA and version. Saving includes section metadata, trainers, candidate evidence, verification history and scan summaries. Loading never trusts saved addresses for active writes; reconnect and analyze again.

## Advanced Mode

- **Processes:** refresh processes and view maps, sizes, permissions and indices. Ordinary patching remains restricted to the foreground CUSA `eboot.bin`, even though process names can be inspected.
- **Scanner:** exact or unknown initial values; byte, Int16/32/64, float, double or hexadecimal data. Use changed/unchanged/increased/decreased or exact rescans. Numeric scans use natural alignment from the selected range start. Int64 comparison preserves every bit. AOB searches accept `??` whole-byte wildcards and can match across 64 KiB reads. Numeric hits are bounded to 500,000; the UI displays at most 5,000. A range is at most 64 MiB. Save/open scans locally; a rescan needs the original live session.
- **Code & patches:** read hex and disassemble x86-64 locally with Iced, inspect resolved branch destinations and padding, create whole-instruction NOPs or rel32 jumps, add an unverified manual draft to the library, or explicitly review/test a manual patch. Hex editing is expressed as an original/replacement patch, never as an unreviewed direct edit. The separate **Cheat editor** supports multi-line drafts without raw XML; see below.
- **Live tracking:** polls the manual patch address with the selected numeric type every 500 ms. Cancel stops tracking. It does not freeze or automatically write values.
- **Offline dump:** set the original hexadecimal base address before opening a raw file (1 byte–64 MiB). Loading disconnects the console. Import a trainer, set the target version, then analyze. No live tests or hardware confirmation are available. Captured ranges include a `.map.json` address sidecar.
- **Watchpoints:** enable write testing, supply this PC's LAN IPv4, explicitly attach, and set an aligned hardware write watchpoint in slot 0 (1/2/4/8 bytes). Interrupts show thread, RIP, RSP and registers. RIP is normally after the memory access: inspect preceding instructions. Read registers, step a thread, resume, clear or detach. All four slots are supported by the protocol API; the UI exposes slot 0. Detach clears debugger state. This is manual tracing, not automatic code-flow recovery.

## Local data and privacy

Settings, library, verification history, structured logs and rollback journals live under `%LOCALAPPDATA%\PS4GoldHENCheatStudio`. Logs rotate at 5 MiB. Diagnostics are generated automatically on connect and can be rerun/exported. Reports include addresses/settings/log messages but exclude memory dumps and save data; inspect them before sharing. No telemetry, updater, cheat download or cloud API runs in the background.


## Graphical multi-line editor (0.2)

Use **Cheat editor → Edit selected library cheat**, or **New cheat**. Add/remove lines, set stable line IDs, decimal sections, hexadecimal offsets/original/replacement bytes, comma-separated dependencies, absolute flags and optional PS5 metadata. The Cave checkbox declares a possible cave; it does not approve it. Clear Instruction/code patch for fixed data-value patches.

Validation runs as fields change. Length mismatches, bad hex, overlapping lines, missing/cyclic dependencies and branches into replacement-instruction interiors prevent saving/testing. Hook-to-cave branches require that cave's line ID in Dependencies. Tabs show both original/replacement disassembly and branch/return destinations. Cross-section jump inspection needs explicit section=base mappings, e.g. `14=400000;18=600000`; offsets alone cannot establish those relationships.

**Save unverified copy to library** preserves the original file and library entry. Other cheats and original authors remain in the copy unless **Keep only the edited cheat** is selected. A target-version/title/process change with multiple cheats requires that option: changing metadata cannot port untouched cheats. Edited SHN preserves PS5Offset; JSON cannot represent SHN PS5 metadata or nonzero section mappings. Every editor export also saves a `.studio-project.json` sidecar containing full IDs/dependencies/metadata. **Save/Open editable project** keeps these fields without adding undocumented operational commands to GoldHEN files. Standard export orders dependencies before hooks, but GoldHEN's own activation remains non-atomic.

**Review/test edited cheat** requires a matching live CUSA/version/process, fresh expected-byte/mapping checks, cave/reference analysis where applicable, separate cave acknowledgment and the exact-byte write confirmation. Offline editor work cannot become a live or hardware-confirmed test. For the ordinary guided path, save the draft to the library, analyze it, test with rollback, then record actual gameplay separately.

## When the old instruction changed

The app searches original/relocation-independent signatures, operand shapes and, when old context exists, surrounding instruction anchors. It aligns old/new instructions, describes changed registers/field offsets/layout and compares internal branch destinations. Candidates are ranked with explanations; external callees/data references remain unproven. A changed candidate never receives the old replacement automatically.

Expand **Old-version context** in Analyze. A captured `.studio-evidence.json` includes CUSA/version/process, original bytes and coordinates. Alternatively load a raw old dump after entering its original section number, section base and dump start. Optional independently known function start/length (up to 4096 bytes) permits bounded entry/return and instruction/branch-structure comparison. Function bounds are not invented, and that comparison does not prove semantic equivalence.

To collect missing data automatically, connect to the **exact old game version**, import its trainer, and choose **Capture old context**. The app reads the relevant ranges and verifies the trainer's original bytes. If section indices moved, supply original section=base mappings. Save the package locally, reconnect to the target version, then analyze. The new version cannot supply old-version evidence. No dumps are uploaded.

Select a changed candidate and choose **Inspect in editor**. The draft uses actual captured target originals and shows the old replacement unchanged for review; altered length/layout may make it invalid until you redesign it. Use memory tools/watchpoints during gameplay to determine which instruction really writes health/currency. A generated draft is not gameplay verification.

### Try the complete modified-code fallback offline

All these samples are artificial, not game dumps or usable health cheats:

1. In Advanced → Code & patches, set dump base `400000` and open `examples/silksong-synthetic-modified.bin`.
2. Import `examples/silksong-context-demo.shn`. Automatic analysis finds a changed operand and refuses to apply the old patch.
3. In Analyze, load `examples/silksong-synthetic-old.studio-evidence.json`. Inspect the added context/function evidence and differences.
4. Choose **Inspect in editor**, review/change the draft, save an unverified copy and analyze again. Live testing stays disabled while offline.

The separate `examples/silksong-metadata-structure.shn` exercises Section 14, offset 1B6BFED, PS5Offset, alternate authors, multiple cheats and the old cave reference 4783800. Its replacement/trampoline bytes are synthetic and must not be treated as a real Infinite Health trainer.
