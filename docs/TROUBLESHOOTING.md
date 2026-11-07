# Troubleshooting

## Cannot connect

Check the PS4's current LAN IP and that ps4debug-NG is already running on TCP 744. GoldHEN's payload loader on 9090 and FTP on 2121 are different services. This application does not reinject a debugger. Disable simulator mode for a real console. The connection timeout is configurable (1–120 seconds).

Use **Diagnostics** after a connection, or export the logs after a failure. An unknown brand/protocol/platform is refused. This client intentionally supports the reviewed NG 1.3 protocol, not every legacy ps4debug payload. A timeout closes the connection: reconnect before retrying. Do not assume an open port means a compatible debugger.

## No game/version detected

Start the game and refresh/reconnect. The foreground command reads title/version metadata from NG. With a payload lacking that command, the app reports failure rather than guessing a version. Ordinary patching needs a foreground CUSA `eboot.bin` and exact version.

## Authentication or unsupported-command errors

Console scan commands require an intentionally undocumented challenge response. They are not used. Scans run locally over ordinary process reads. `F0000003` means missing data or authorization refusal; `F0000004` can mean another debug session is attached; `F0000005` indicates an invalid debug index. Stop other debugger clients before attaching. Unexpected statuses close the stream and require reconnecting.

## No candidate / several candidates / original bytes changed

These are expected analysis outcomes, not a successful port. Check CUSA, source version, patch bytes, code/value mode and dump address metadata. Changed relative operands may yield a signature candidate but are not automatically authorized. Inspect surrounding assembly and make a manual patch only when you understand it. Scores do not resolve function equivalence or prove safe caves. There is no bundled verified Silksong 01.05 health patch.

## Scan too large or too many results

The guided search does not choose an address after a partial scan. Increase the guided budget in Advanced Mode (maximum 1024 MiB), use a narrower offline dump, or inspect specific ranges with the scanner. A single range is 64 MiB maximum; unknown scans cap at 500,000 values. Numeric scans are aligned to the range start; use AOB searching for unaligned data. Unknown initial scan and exact scan are the only first-pass comparisons. Cancel is available during scanning or live tracking.

## Trainer rejected

Only fixed checkbox patches with explicit original/on bytes are supported. Values must be nonempty equal-length hex pairs. SHN `ValueOff` strings with trailing `=` and other malformed data are rejected; the app does not guess a correction. Absolute SHN semantics and extended/pointer/script commands need manual mapping/implementation. Export to JSON requires resolved main-module-relative offsets; a nonzero source SHN section cannot just be discarded.

## Patch/restore rejected

The error identifies game/session mismatch, missing region, overlapping instructions or unexpected bytes. No write is attempted before preflight succeeds. Do not bypass the check. Restore requires the same live connection and bytes matching the tested replacement.

If a write fails after starting, some bytes may have changed. Stop gameplay. The error gives the journal ID under `%LOCALAPPDATA%\PS4GoldHENCheatStudio\rollback`. Keep the journal and diagnostics. If still connected, inspect every line in Advanced Mode before authoring a reverse patch with the actual current bytes as expected original. After a reconnect or game restart, automatic restoration is blocked; old addresses may refer to something else. Restarting the game without saving is often preferable to guessing a recovery patch, but the app cannot guarantee that a crash or save corruption can be undone.

## Watchpoint does not stop

Attach explicitly, enable write testing, set an aligned 1/2/4/8-byte write watchpoint, and cause the game to write that address. Supply the PC's LAN IPv4, not `127.0.0.1`, for a real PS4. Allow incoming TCP 755 on the private Windows network only. NG hardcodes callback port 755; changing the saved port cannot change the payload. Remote debug commands without attachment may have no response. A data-watchpoint RIP generally points after the writing instruction. Use nearby disassembly and registers, then resume. Detach when done.

## Windows download does not launch

Extract the ZIP completely; do not run inside the archive. Use Windows x64 (not ARM64/32-bit). The package is self-contained, with no SDK/Visual Studio requirement. Windows may flag the unsigned executable; assess source and checksums before deciding whether to run it. Single-file WPF extracts native components to a user temp directory; it needs writable temp and local-app-data folders. The Linux build host cannot validate a Windows launch; inspect the Windows Actions `Windows-test-evidence` artifact.

## No GitHub artifact

Check that the workflow was pushed and a run completed successfully. Failed Windows/UI tests deliberately prevent packaging. Download artifacts from a successful Actions run, or the prerelease for a `v*` tag. A checked-in pipeline is not evidence that GitHub has run it or produced a release.
