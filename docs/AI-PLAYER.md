# AI Player for Windows

Open a paired chiaki-ng stream using its default keyboard mappings. In Cheat Studio, open **AI Player**. **Capture frame** shows what Studio sees. Choose a control and click **Send selected control** to try a 150 ms press. Studio brings chiaki-ng to the foreground for each session.

For automatic play, open the game first. Set the Windows user environment variable `OPENAI_API_KEY` and reopen Studio. Enter a gameplay goal and click **Play with AI**. Each step sends the current Remote Play picture to the OpenAI Responses API, receives a controller decision and captures the next picture. API usage is billed to the configured account. Set `CHEATSTUDIO_VISION_MODEL` to override the default gpt-4.1. Click **Stop / release** to cancel. A run ends after 60 decisions or earlier if the model requests a stop. Network round trips limit reactions to timed prompts. Games may autosave during normal play.

## Control from a local assistant

The Windows executable also accepts `--remote-play OUTPUT.png [MILLISECONDS BUTTON ...]`. Use a new absolute output path each time. With only a path it captures one frame locally. With buttons it holds up to four together for 40–2000 ms, releases them, waits briefly, and captures the result. Wait for the process to exit before reading the PNG or sending another move. Exit code 0 means success. The local interface requires only the paired stream.

Controls include Up, Down, Left, Right, Cross, Circle, Square, Triangle, L1–L3, R1–R3, Options, Share, Touchpad (click), PS, and LeftStick or RightStick followed by Up, Down, Left, or Right. Stick directions use full deflection. Touch gestures, motion controls and partial trigger pressure are outside this keyboard interface.

**Run offline step** still exercises the synthetic observation, decision and button release using the simulated controller.
