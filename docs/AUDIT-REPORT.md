# Post-implementation audit — 0.2.0

8 November 2026. Existing WPF/MVVM, protocol, scanner, simulator, safety/journaling and packaging architecture retained. This is a cloud-validated experimental milestone, not production or real-PS4 certification.

## Confirmed corrections

1. **Valid PS4 SHN rejected because of PS5Offset:** the parser explicitly accepts the known non-executable metadata leaf. PS4 Section/Offset/ValueOn/ValueOff remain authoritative. PS5 text is not treated as a PS4 address. Multi-line Section 14 / CUSA39420 v01.04 structure and alternate trainer authors have regression coverage.
2. **Original author attribution overwritten:** SHN Moder/Author/Creator attributes and genres round-trip; JSON credits round-trip. New PS4 target proposals clear old PS5 offsets rather than claiming those coordinates were ported. Edited SHN preserves them; Studio sidecars retain metadata that GoldHEN JSON cannot represent.
3. **Padding could produce a testable cave proposal without a separate cave judgment:** padding now requires explicit address/expected-byte-bound confirmation. Invalid executable mappings, expected-byte differences and detected branch/RIP data references block use. The patch-service boundary also rejects undeclared/unconfirmed padding in code mode.
4. **Code patches could pass mapped-region checks on non-executable data:** instruction patches/caves now require readable executable mappings at fresh preflight.
5. **Unknown nonempty version could authorize writes:** only known XX.YY version metadata can pass the patch boundary. Firmware failure value 0xFFFF displays Unavailable.
6. **Failed attach after reverse callback could leave an accepted socket undisposed:** failed attachment now cleans up the callback socket/listener and invalidates the command connection, with reconnect regression coverage.
7. **Relocation target ambiguity:** branch targets inside instructions are rejected; internal targets move to destination coordinates, while externally mapped destinations are not mistaken for old internal instruction addresses. Return-jump and explicitly mapped cross-section tests exercise the result.

Unknown operational SHN elements/attributes are still rejected by name. Nested commands inside allowed metadata, duplicate fields and unsupported Items operations are not silently discarded. Structural validation is not semantic cheat verification.

## Implemented improvements

- Operand-aware instruction shapes plus LCS alignment expose register/memory/immediate changes, insertions/deletions and altered layouts. Relocation-independent branch/RIP signatures and old surrounding anchors find candidates beyond byte-identical code.
- Internal branch destination/condition comparison and optional supplied old function bounds produce bounded function-body evidence. External callees/data identity, all paths and semantics remain unproven. Multiple candidates are ranked and explained without inventing an automatic choice or overwrite span.
- Original section-base mappings permit checked cross-section relocation. Hooks depend on cave lines; standard exports are dependency ordered. Cave safety is never inferred from zeros/NOPs alone, and reference scans are bounded static evidence, not lifetime proof.
- A graphical multi-line editor adds/removes lines, edits sections/offsets/original/replacement bytes/dependencies/PS5 metadata, shows original/replacement disassembly and jump/return destinations, reports validation errors, saves unverified library copies and exports SHN/JSON with editable Studio sidecars. Other cheats/authors remain unless the user chooses to keep only the edited cheat. Version/title/process changes cannot silently relabel untouched cheats as ported.
- Guided fallback loads old raw/context packages or captures context from the exact old game/version with original-byte verification. Changed candidates open as unverified target editor drafts; old replacements are shown for review, not automatically adapted or applied.
- Synthetic modified-code and metadata fixtures are shipped. No real 01.05 Infinite Health patch or game dump was supplied. Main import automatically runs read-only target analysis when a target is available.

## Protocol audit

Re-fetched upstream `Pharaoh2k/ps4debug-NG`, current revision `8be828fe09f05f9327d7babdf9dcec4c67fbb816`, protocol 1.3 / v1.3.4. Compared PROTOCOL.md with server/fw/debug source: raw info replies, 132-byte foreground metadata, 36-byte processes, 64-byte maps, packed read/write phases, truthful terminal write status, decimal firmware representation, fixed reverse port 755, 1184-byte event frames, register offsets and DR7 width encoding match the reviewed implementation. Authentication remains intentionally unimplemented for the undocumented scan family; local read-based scans do not need it. Literal-byte handshake regressions provide a check independent of the simulator's shared Wire helpers.

## Validation and downloads

Run every existing and new unit/integration test, then compile the full Release solution and execute the expanded **published-executable** WPF smoke on Windows Actions. Local audit results: **164/164 tests passed, zero failed/skipped; full Release solution and WPF cross-compilation passed with zero warnings/errors**. The new Windows run and downloadable artifact will be recorded when confirmed; the earlier 0.1 artifact is not presented as 0.2 evidence.

The WPF audit smoke exercises first-launch setup, simulator connection, the original guided flow, PS5Offset import/author preservation, graphical multi-line add/remove/edit/errors, project/SHN export, explicit cave review, dependency-ordered multi-line patch/readback and rollback/readback, modified offline code with old context/function evidence, editor fallback, and command bindings on every screen. Packaging verifies actual Windows x64 PE headers, ZIP integrity and required assets.

## Remaining manual work / hardware tests

Full function equivalence/whole-program CFG recovery, arbitrary external reference relocation, safe allocation-lifetime proof, automatic health-writer semantics and automatic correct-cheat synthesis remain unimplemented. Changed code requires manual replacement design; unresolved/missing/ambiguous evidence cannot activate an old patch. GoldHEN's file activation and live multi-line writes remain non-atomic.

When home, verify the actual PS4 Fat / 13.52 / GoldHEN 2.4b18 / NG 1.3.4 handshake and maps, import the actual v01.04 trainer, capture old context where possible, analyze CUSA39420 v01.05, inspect health-writer operands/function paths with a watchpoint, verify cave allocation/references and both jump directions, then perform explicitly confirmed readback/rollback and gameplay tests with backed-up saves. Only actual gameplay can validate Infinite Health without maximum-health/UI distortion. Verify exported GoldHEN toggles separately. See HARDWARE-CHECKLIST.md.
