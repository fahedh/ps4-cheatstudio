# AI Player — experimental, offline-first

The `feature/ai-player-foundation` branch adds an **AI Player tab** to CheatStudio,
a hardware-independent game-agent contract, a synthetic Walking Dead-style dialogue
step, and offline tests. It does not yet connect to the PS4 or OpenAI.

## How to test with the PC only
1. Build CheatStudio normally with .NET 10 and open **AI Player**.
2. Click **Run offline step**.
3. Verify that a synthetic dialogue observation and a `Down` decision appear.
4. Check that the activity log shows `Press Down` followed by `Release all`.
5. Test **Stop / release** while an adapter is running.

CLI tests:
`dotnet test tests/Studio.Tests/Studio.Tests.csproj --filter FullyQualifiedName~AiPlayerTests`

## Next integrations, when PC and PS4 are running
- Implement `IGameController` against a **verified** GoldHEN-compatible virtual DS4 interface.
  The existing ps4debug-NG connection reads memory; it does **not** automatically
  supply controller input. Verify pairing, latency and button mappings on hardware.
- Implement `IGameCapture` for a local game capture source (Chiaki-ng or HDMI capture).
- Implement `IGameObserver` for screen-state/dialogue recognition.
- Implement `IGameAgent` with structured OpenAI Responses API output and bounded actions.
  API keys must remain local, never committed.
- Optionally implement `IGameMemoryReader` using the existing read-only
  CheatStudio debug protocol, only after checking addresses for the game version.
- Add continuous-session orchestration, local QTE handling, watchdog, input
  emergency release, manual takeover, latency measurements, and replay tests.

The included synthetic demo **does not actually play The Walking Dead**.
This is a development foundation to prepare while both devices are off.
