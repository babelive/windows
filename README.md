# Babelive (.NET)

WPF desktop app that captures Windows audio output (any app's playback), streams it to OpenAI's realtime translation model (`gpt-realtime-translate`), and renders the translated audio + dual-language transcript live.

<img width="2065" height="314" alt="image" src="https://github.com/user-attachments/assets/0045411d-72c0-4323-81f9-a6d2906527c6" />


## Stack

- **.NET 8** + **WPF** (Windows Desktop)
- **NAudio** — `WasapiLoopbackCapture` for input, `WasapiOut`/`WaveOutEvent` for playback, `WdlResamplingSampleProvider` for high-quality 48 kHz → 24 kHz resampling
- **`System.Net.WebSockets.ClientWebSocket`** (built-in) for the realtime API
- **`System.Text.Json`** (built-in) for protocol serialization

## How it works

```
[Any Windows app] ──► WasapiLoopbackCapture ──► downmix ──► WDL resample to 24 kHz ──► PCM16
                                                                                          │
                                                                                          ▼
                                                              ClientWebSocket → OpenAI Realtime
                                                                                          │
                          ┌───────────────────────────────────────────────────────────────┴───┐
                          ▼                                                                   ▼
              translated audio (PCM16)                                       dual transcript deltas
                          │                                                                   │
                          ▼                                                                   ▼
                    WasapiOut device                                                   WPF TextBox
```

## Requirements

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- An OpenAI API key with access to `gpt-realtime-translate`

## Setup & run

```powershell
cd D:\temp\Babelive-net
dotnet restore
dotnet run
```

On first launch, click the **API…** button in the settings panel and paste
your `sk-…` key. The key is stored locally at
`%APPDATA%\Babelive\settings.json` (plain JSON, never transmitted anywhere
except to the configured API endpoint).

For a self-contained release build:

```powershell
dotnet publish -c Release
```

Produces a single ~68 MB `Babelive.exe` at
`bin\Release\net8.0-windows\win-x64\publish\` — bundles the .NET 8 runtime,
all WPF native DLLs, and is compressed. Just ship that one file.

## Using it

1. Pick a target language.
2. Pick a **Capture device** (any active render endpoint — its loopback feed is what gets captured). Defaults to the system default playback device.
3. Pick a **Playback device** for the translated audio. **Read the feedback warning below.**
4. Click **Start**, then play any video / call / song.

## ⚠️ Feedback loop warning

If translated audio plays through the same speakers you're capturing, the loopback re-translates it forever. Three fixes:

1. **Use headphones** for playback (different physical device than the captured speakers).
2. **Install [VB-CABLE](https://vb-audio.com/Cable/)** — free virtual audio cable. Send translated audio to `CABLE Input` and you can monitor it without it leaking into the loopback.
3. **Tick "Transcript only (no audio playback)"** — only spoken text appears, nothing replays.

## File layout

```
Babelive/
├── Babelive.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs   ← WPF UI + orchestration
├── LanguageCodes.cs                        ← dropdown options
├── Audio/
│   ├── LoopbackCapture.cs                  ← WASAPI loopback → 24 kHz/mono PCM16
│   └── AudioPlayer.cs                      ← plays translated PCM16 chunks
└── Translation/
    └── RealtimeTranslatorClient.cs         ← async ClientWebSocket
```

## API quirks / things that may need tuning

The realtime translation API is new. The exact event/field names in `RealtimeTranslatorClient.cs` are best-effort based on <https://developers.openai.com/api/docs/guides/realtime-translation> plus the standard `/v1/realtime` event conventions. If your account sees errors:

- **Endpoint**: defaults to `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate`. Tick **"Use alt endpoint"** in the UI to fall back to `wss://api.openai.com/v1/realtime?model=gpt-realtime-translate`.
- **Session config**: `RealtimeTranslatorClient.SendSessionUpdateAsync` sends `session.update` with `input_audio_format=pcm16`, `output_audio_format=pcm16`, and `translation.target_language=<code>`. Adjust if the official schema differs.
- **Event names**: `Dispatch` matches both the `output_*.delta` and `response.output_*.delta` shapes. If transcripts/audio don't arrive, log every incoming event and adjust.

## Quick sanity test

Open YouTube in any non-target language, hit **Start**, and the translation should start streaming into the bottom panel within a second or two of the source audio playing.
