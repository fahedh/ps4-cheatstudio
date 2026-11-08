# PS4 GoldHEN Cheat Studio

Standalone Windows x64 app for creating and porting GoldHEN trainers. Start the game on a GoldHEN-enabled PS4, connect from Studio, and import an older SHN for the same title. Studio reads the running game and compares its instructions with the trainer. PS4CheaterNeo is optional.

The primary interface is a first-launch setup wizard followed by **Connect → Choose trainer → Analyze → Test → Export**. Advanced Mode contains process maps, value/pattern scanning, hex/disassembly, manual patch creation, live value tracking and hardware watchpoints. Buttons invoke real implementations, not mock success notifications. The optional simulator is explicitly labeled throughout.

## Download and run on Windows

1. Open [Windows build runs](https://github.com/fahedh/ps4-cheatstudio/actions/workflows/windows.yml).
2. Select a **successful** run, then download the **PS4GoldHENCheatStudio-win-x64** artifact. GitHub may require sign-in for Actions artifact downloads.
3. Extract the artifact, then extract `PS4GoldHENCheatStudio-win-x64.zip` into a writable folder.
4. Run **PS4GoldHENCheatStudio.exe**. No Visual Studio or .NET installation is required. Windows 10/11 x64 is the intended platform. The executable is unsigned.

Use a successful build for the current revision. Earlier build artifacts contain earlier versions of the app.

Version tags (`v*`) also publish a prerelease ZIP and SHA-256 checksum to [Releases](https://github.com/fahedh/ps4-cheatstudio/releases) after Windows tests and WPF launch checks pass. A workflow file in source alone does not mean an artifact has been published: check that a run succeeded.

## Try it without a PS4

In setup, choose **Use a local simulated console**, then Connect. Import `examples/synthetic-demo.json`, or choose **Use synthetic demo**. Search for candidates: the simulator relocates the reference instruction to `0x402800`. Enable explicit write testing, review the byte changes, test and restore. Export produces an **unverified candidate**, never hardware-confirmed output.

The embedded simulator requires no separate server. A standalone copy is also packaged under `simulator/Studio.MockHost.exe`; run it with `--port 1744`, then connect to `127.0.0.1:1744`. It deliberately does not simulate health or gameplay semantics.

## First PS4 connection

Enable GoldHEN and its BinLoader server. Studio first tries the debugger handshake. On connection refusal it sends the official ps4debug-NG 1.3.4 payload once to the configured loader, then retries the handshake. A timeout or incompatible debugger stops the connection. On first use Studio downloads the pinned GitHub release and verifies its SHA-256. It caches the verified file for subsequent sessions. For offline first use, place ps4debug-ng_v1.3.4_release_2026-11-05.bin from the official Pharaoh2k/ps4debug-NG 1.3.4 release beside the executable. Start your game. Use the configured LAN address (default `192.168.8.188`), debugger TCP `744`, FTP `2121`, payload loader `9090`. All settings are editable. Ordinary reading/scanning does not need the reverse interrupt port. Watchpoints require the PC's LAN IPv4 address and incoming TCP `755`; upstream NG fixes that port, and the app reports alternate interrupt ports as unsupported rather than pretending they work.

The default firmware reference is **13.52 / GoldHEN 2.4b18**, not a claim that this combination has been tested. Follow the [hardware checklist](docs/HARDWARE-CHECKLIST.md) before writing.

## Port an older SHN to the running version

Connect with the target game in the foreground and import its older SHN. Select a cheat and analyze. For repeated instructions, Studio checks other trainer lines from the same source section at their relative offsets. Matching neighbors raise candidate rankings. Review the candidates, branch destinations and any proposed code cave before testing or exporting. Ambiguous or changed code may require old-version context or manual redesign. Export the target-version SHN after review. On a Silksong CUSA39420 01.05 hardware session, a separate read-only comparison used this neighboring-instruction technique to distinguish one health hook among 47 identical byte sequences. Health, silk and flight were subsequently confirmed by the user in gameplay.

## Development

Install the pinned .NET SDK in `global.json` (10.0.403). The existing checkout is the working directory; do not create a worktree just to run the app.

```sh
dotnet restore PS4GoldHENCheatStudio.sln --locked-mode
dotnet test tests/Studio.Tests -c Release
dotnet build src/Studio.Desktop -c Release
dotnet run --project src/Studio.MockHost -- --port 1744
```

Windows UI: `dotnet run --project src/Studio.Desktop -c Release`. Linux can execute the core/simulator tests and cross-compile/publish the Windows target, but cannot run WPF. The Windows Actions workflow starts the **published self-contained executable**, exercises the actual view model and bindings against a loopback simulator, and uploads the result and screenshot.

```powershell
dotnet publish src/Studio.Desktop -c Release -r win-x64 --self-contained true -p:StudioPublish=true -o artifacts/publish/app
dotnet publish src/Studio.MockHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:StudioPublish=true -o artifacts/publish/mock
./build/Package.ps1
```

## Documentation

- [User guide](docs/USER-GUIDE.md)
- [Architecture and developer guide](docs/DEVELOPMENT.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Safety and security](docs/SAFETY.md)
- [Feature completion and limitations](docs/FEATURE-REPORT.md)
- [Pending hardware checks](docs/HARDWARE-CHECKLIST.md)
- [Reference investigation and licenses](docs/REFERENCES.md), [third-party notices](THIRD-PARTY-NOTICES.md)

The 0.2 audit adds real-world SHN metadata compatibility, contextual instruction comparison, explicit cave review, a graphical multi-line editor and a guided changed-code fallback. See [audit corrections and current validation](docs/AUDIT-REPORT.md).

Code is MIT licensed. No source, payloads, trainers or save-editing scripts were copied from projects with unclear or incompatible redistribution terms. Examples are synthetic, using the old instruction metadata supplied in this project's specification.
