# Pending real-hardware validation

All items remain pending until explicitly tested. Target reference: PS4 Fat, firmware 13.52, GoldHEN 2.4b18, ps4debug-NG 1.3.4. The upstream project itself distinguishes source/kernel-offset validation from on-hardware validation for newer firmware.

- [ ] Download a successful Windows artifact, verify its checksum, extract and launch without .NET/SDK/Visual Studio installed.
- [ ] Complete first-launch setup on Windows 10/11 x64; verify DPI, keyboard navigation and long trainer names/error messages.
- [ ] Back up saves. Use a trusted local network. Confirm console IP/ports.
- [ ] Load debugger separately. Confirm the app connects without injecting a payload or starting FTP transfer.
- [ ] Verify detected firmware, brand, capability and exact foreground CUSA/version against the console.
- [ ] Compare process maps to a known debugger; confirm entry sizes, permissions, main-module base and section interpretation.
- [ ] Read a known game-memory range and export its map metadata; verify bytes independently.
- [ ] Run exact/unknown/rescan/pattern tests against a harmless known value. Verify cancellation, read bounds, reboot and reconnect invalidation.
- [ ] Import the actual source trainer. The v01.04 Silksong references are **section 14, offset 0x1B6BFED, bytes 89 43 60 48 83 C4 08, old cave 0x4783800**. Do not treat them as v01.05 locations.
- [ ] For CUSA39420 v01.05, collect current original/context bytes, inspect candidate function and actual health-write semantics. Confirm no maximum-health inflation/UI distortion.
- [ ] Inspect cave contents/references/lifetime. Padding alone is insufficient. Confirm all branches, RIP references, copied instructions and return targets.
- [ ] Confirm explicit review rejects deliberately mismatched bytes/game/version and read-only mode blocks writes.
- [ ] Test a minimal reversible patch, check readback and actual gameplay, then restore and independently check originals. Do not save progress during a doubtful test.
- [ ] Test multi-line dependency order, controlled failure reporting, durable journal and refusal of blind restoration after reconnect.
- [ ] Set PC LAN IP/firewall for TCP 755. Explicitly attach; verify watchpoint width encoding, interrupt frame size, GP registers/RIP/RSP, step/resume/detach with an independent debugger.
- [ ] Test sleep/network interruption, other debug clients, game exit/restart and console reboot; ensure no stale approval survives.
- [ ] Export JSON/SHN with independently verified main-module base. Transfer one format at a time to the documented GoldHEN path and verify toggling/disable behavior.
- [ ] Record the real gameplay observation separately from byte-transfer success, including game/version, exact trainer bytes, tester, console/payload details and limitations.

The supplied synthetic NOP replacement is **not** a finished Infinite Health patch. The correct Silksong 01.05 cheat cannot be established without real target code/gameplay evidence. Do not label a release production ready based only on simulator or compile results.


0.2 audit additions:

- [ ] Import the user's actual v01.04 SHN with PS5Offset; verify all PS4 section/offset/bytes, original authors and multiple lines survive export/reimport. Check that unsupported operational commands are reported by name rather than removed.
- [ ] Capture old hook/function context on the exact old version, with independently verified section bases. New-version data must be refused as old evidence. Old padding is not treated as old-function evidence.
- [ ] On v01.05, inspect changed operands/layouts, function/branch evidence and ambiguous candidates. Verify the proposed health writer with a gameplay watchpoint; matching/ranking alone is insufficient.
- [ ] Review cave lifetime, executable mappings, instruction boundaries and incoming references before the separate acknowledgment. Verify both hook and return destinations and dependency order on real code.
- [ ] Exercise multi-line graphical editing, live preflight rejection, direct/guided byte tests, same-session rollback and exported GoldHEN enable/disable behavior with backed-up saves.
