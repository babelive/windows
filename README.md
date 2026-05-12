# Babelive (.NET)

[![CI](https://github.com/babelive/windows/actions/workflows/ci.yml/badge.svg)](https://github.com/babelive/windows/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/babelive/windows?include_prereleases&sort=semver)](https://github.com/babelive/windows/releases/latest)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Avalonia 12](https://img.shields.io/badge/Avalonia-12.0-8B5CF6?logo=avalonia&logoColor=white)](https://avaloniaui.net/)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)](#requirements)

Avalonia 12 desktop app that captures Windows audio output (any app's playback), streams it to OpenAI's realtime translation model (`gpt-realtime-translate`), and renders the translated audio + dual-language transcript live. Windows-only today; the audio layer is being abstracted for a macOS port.

<img width="2065" height="314" alt="image" src="https://github.com/user-attachments/assets/0045411d-72c0-4323-81f9-a6d2906527c6" />


## Stack

- **.NET 9** + **Avalonia 12** (Fluent theme, Inter font fallback)
- **NAudio** — `WasapiLoopbackCapture` for input, `WasapiOut`/`WaveOutEvent` for playback, `WdlResamplingSampleProvider` for high-quality 48 kHz → 24 kHz resampling
- **`MessageBox.Avalonia`** — dialog replacement (Avalonia has no built-in `MessageBox`)
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
                    WasapiOut device                                              Avalonia TextBox
```

## Requirements

- Windows 10 / 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- An OpenAI API key with access to `gpt-realtime-translate`

## Setup & run

```powershell
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

Produces a single `Babelive.exe` at
`bin\Release\net9.0-windows\win-x64\publish\` (~80–90 MB, bundles the
.NET 9 runtime + Avalonia/Skia/HarfBuzz native libs, single-file compressed).
Just ship that one file.

## Using it

1. Pick a target language.
2. Pick a **Capture source** — recommended is **All system audio (no echo)** which uses Win10 build 20348+ Process Loopback to exclude Babelive's own playback. Per-app entries (Teams, Chrome, Spotify, …) and legacy device loopbacks are also available.
3. Pick a **Playback device** for the translated audio. **Read the feedback warning below.**
4. Click **Start**, then play any video / call / song.

The settings window is hide-on-close — closing it leaves the lyric overlay + tray icon running. **Exit** from the tray menu fully quits.

## Microsoft Teams / Skype audio

Teams and Skype set `AUDCLNT_STREAMFLAGS_PREVENT_LOOPBACK_CAPTURE` on their call audio for privacy, so Windows' Process Loopback API returns silence for them. Babelive auto-detects this and, **if VB-CABLE is installed**, redirects the Teams/Skype process tree to `CABLE Input` via `IAudioPolicyConfig` per-app routing, then loopback-captures from the cable. No manual Teams/Skype audio config needed.

Without VB-CABLE installed, Teams/Skype audio cannot be captured — this is a Windows DRM-style restriction, not a Babelive bug.

Zoom / Discord / Google Meet / WebEx / Slack use WebRTC and don't set the flag — they work via plain Process Loopback.

## ⚠️ Feedback loop warning

If translated audio plays through the same speakers you're capturing, the loopback re-translates it forever. Three fixes:

1. **Use headphones** for playback (different physical device than the captured speakers).
2. **Install [VB-CABLE](https://vb-audio.com/Cable/)** — free virtual audio cable. Send the source app's output to `CABLE Input`; Babelive can then loopback-capture the cable while playing translation through your real speakers / headphones.
3. **Tick "Transcript only"** — only spoken text appears, nothing replays.

## File layout

```
Babelive/
├── Babelive.csproj
├── Program.cs                              ← Avalonia entry point
├── App.axaml / App.axaml.cs                ← single-instance gate + window/tray bootstrap
├── MainWindow.axaml / .axaml.cs            ← settings window + audio orchestration
├── LyricWindow.axaml / .axaml.cs           ← transparent topmost desktop-lyrics overlay
├── ApiSettingsWindow.axaml / .axaml.cs     ← API endpoint + key dialog
├── TrayIconHost.cs                         ← system tray (Avalonia TrayIcon + NativeMenu)
├── AppIcon.cs                              ← runtime-generated amber 译 disc icon
├── AppSettings.cs / LanguageCodes.cs       ← persisted prefs + dropdown options
├── Styles/
│   └── Controls.axaml                      ← Fluent overrides (buttons, combos, etc.)
├── Audio/
│   ├── LoopbackCapture.cs                  ← WASAPI / Process Loopback → 24 kHz mono PCM16
│   ├── AudioPlayer.cs                      ← plays translated PCM16 chunks
│   ├── AudioDucker.cs                      ← lowers other apps' session volumes during translation
│   ├── EndpointMuter.cs                    ← "Mute other speakers" — driver-stage endpoint mute
│   ├── DefaultDeviceSetter.cs              ← IPolicyConfigVista default-render-device override
│   ├── AppAudioRouter.cs                   ← IAudioPolicyConfig per-app device routing (Teams)
│   ├── ProcessLoopbackCapture.cs           ← Win10 20348+ process-include/exclude loopback
│   └── ProcessTree.cs                      ← Toolhelp32 process-tree walker (Teams PID family)
└── Translation/
    └── RealtimeTranslatorClient.cs         ← async ClientWebSocket
```

## API quirks / things that may need tuning

The realtime translation API is new. The exact event/field names in `RealtimeTranslatorClient.cs` are best-effort based on <https://developers.openai.com/api/docs/guides/realtime-translation> plus the standard `/v1/realtime` event conventions. If your account sees errors:

- **Endpoint**: defaults to `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate`. Tick **"Alt endpoint"** in the UI to fall back to `wss://api.openai.com/v1/realtime?model=gpt-realtime-translate`.
- **Session config**: `RealtimeTranslatorClient.SendSessionUpdateAsync` sends `session.update` with `input_audio_format=pcm16`, `output_audio_format=pcm16`, and `translation.target_language=<code>`. Adjust if the official schema differs.
- **Event names**: `Dispatch` matches both the `output_*.delta` and `response.output_*.delta` shapes. If transcripts/audio don't arrive, log every incoming event and adjust.

## Quick sanity test

Open YouTube in any non-target language, hit **Start**, and the translation should start streaming into the lyric overlay (and the settings window's transcript panes) within a second or two of the source audio playing.
