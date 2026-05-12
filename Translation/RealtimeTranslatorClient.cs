using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Babelive.Translation;

/// <summary>
/// WebSocket client for OpenAI's Realtime Translation API.
/// Endpoint and event names follow
/// https://developers.openai.com/api/docs/guides/realtime-translation
/// plus the standard /v1/realtime conventions.
/// </summary>
public sealed class RealtimeTranslatorClient : IDisposable
{
    public const string DefaultBase = "wss://api.openai.com";
    public const string DefaultUrl =
        DefaultBase + "/v1/realtime/translations?model=gpt-realtime-translate";
    public const string AltUrl =
        DefaultBase + "/v1/realtime?model=gpt-realtime-translate";

    /// <summary>
    /// Build the GA translations endpoint URL, optionally rooted at a
    /// custom base (for users who reverse-proxy the OpenAI API). The base
    /// can be ws/wss/http/https — http/s is auto-converted to ws/s. Pass
    /// null/empty for the official endpoint. Trailing slash is stripped,
    /// so a base like "https://my-proxy.example.com/openai" yields
    /// "wss://my-proxy.example.com/openai/v1/realtime/translations?...".
    /// </summary>
    public static string BuildDefaultUrl(string? baseUrl) =>
        NormalizeBase(baseUrl) + "/v1/realtime/translations?model=gpt-realtime-translate";

    /// <summary>Same idea for the legacy /v1/realtime endpoint.</summary>
    public static string BuildAltUrl(string? baseUrl) =>
        NormalizeBase(baseUrl) + "/v1/realtime?model=gpt-realtime-translate";

    private static string NormalizeBase(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return DefaultBase;
        var b = baseUrl.Trim().TrimEnd('/');
        if (b.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            b = "wss://" + b[8..];
        else if (b.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            b = "ws://" + b[7..];
        return b;
    }

    private readonly string _apiKey;
    private readonly string _targetLanguage;
    private readonly string _url;
    private readonly ChannelReader<byte[]> _audioInput;
    private readonly ClientWebSocket _ws = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    // --- echo suppression ---
    // When playback shares the device captured by loopback, our translated
    // audio gets re-captured and re-translated. While translated audio is
    // flowing out, we send PCM16 silence into the API instead of real
    // loopback samples.
    //
    // Critical: the server bursts translation chunks (e.g. 5s of audio in
    // 100ms). We track the projected END of playback (cumulative chunk
    // durations added to a running timeline), not when the chunks arrived.
    // Otherwise suppression expires in the middle of playback and the model
    // starts hearing its own echo again -> nonsense translations / stuck.
    // Default OFF: feeding seconds of silence into the model after every
    // turn appears to put it into an "idle" state from which it doesn't
    // resume translating. With echo suppression off the model produces
    // continuous translations even when fed its own playback (the cost is
    // duplicate translations of the echo). Different devices for capture vs
    // playback (or VB-CABLE / headphones) is the real fix.
    public bool EchoSuppressionEnabled { get; set; } = false;
    public TimeSpan EchoSuppressionHangover { get; set; } = TimeSpan.FromMilliseconds(400);
    private long _playbackEndTicks; // when buffered playback should finish; 0 == idle
    private static readonly long PlayerLatencyTicks = TimeSpan.FromMilliseconds(100).Ticks;
    private const int OutputSampleRate = 24000;

    // --- diagnostic log file ---
    // Best-effort. Writes to %TEMP%\Babelive\events.log so we can see
    // which events the server actually sends and how often, when something
    // goes wrong.
    private StreamWriter? _log;
    public string? LogPath { get; private set; }
    private int _sentReal;
    private int _sentSilence;
    private DateTime _lastSenderReport = DateTime.UtcNow;

    public event Action<string>? SourceTranscript;
    public event Action<string>? TranslatedTranscript;
    public event Action<byte[]>? AudioOut;
    public event Action<string>? Status;
    public event Action<string>? Error;

    public RealtimeTranslatorClient(
        string apiKey,
        string targetLanguage,
        ChannelReader<byte[]> audioInput,
        string? url = null)
    {
        _apiKey = apiKey;
        _targetLanguage = targetLanguage;
        _audioInput = audioInput;
        _url = url ?? DefaultUrl;

        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        // The dedicated /v1/realtime/translations endpoint is GA and rejects
        // the realtime=v1 beta header. The fallback /v1/realtime endpoint is
        // still beta and needs it. Detect by path EndsWith so reverse-proxy
        // hosts (e.g. https://my-proxy.com/openai/v1/realtime) still match.
        try
        {
            var uri = new Uri(_url);
            if (uri.AbsolutePath.EndsWith("/v1/realtime", StringComparison.OrdinalIgnoreCase))
                _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        }
        catch { /* malformed URL — fall through; ConnectAsync will surface it */ }

        TryInitLog();
    }

    private void TryInitLog()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Babelive");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "events.log");
            // Cap file at 4 MB so it doesn't grow forever
            if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                File.Delete(path);
            _log = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            LogPath = path;
            Log($"==== {DateTime.Now:O} session start, target={_targetLanguage}, url={_url}, echo_suppress={EchoSuppressionEnabled} ====");
        }
        catch { /* logging is best-effort */ }
    }

    private void Log(string msg)
    {
        try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}"); }
        catch { /* ignore */ }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stopping",
                                     CancellationToken.None);
        }
        catch { /* ignore */ }
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Status?.Invoke("connecting…");
            await _ws.ConnectAsync(new Uri(_url), ct);
            Status?.Invoke("connected");
            await SendSessionUpdateAsync(ct);

            var sender = SenderLoopAsync(ct);
            var receiver = ReceiverLoopAsync(ct);
            var first = await Task.WhenAny(sender, receiver);

            // surface any exception from whichever finished first
            try { await first; } catch (OperationCanceledException) { }
            catch (Exception e) { Error?.Invoke($"loop error: {e.Message}"); }

            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { await Task.WhenAll(sender.AsCompletedTask(), receiver.AsCompletedTask()); }
            catch { /* ignore */ }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException wse)
        {
            Error?.Invoke($"websocket error: {wse.Message}");
        }
        catch (Exception e)
        {
            Error?.Invoke($"connection error: {e.Message}");
        }
        finally
        {
            Status?.Invoke("disconnected");
        }
    }

    private async Task SendSessionUpdateAsync(CancellationToken ct)
    {
        // Schema per https://developers.openai.com/api/docs/guides/realtime-translation
        // - audio.input.transcription enables Source-panel ASR (whisper-1).
        // The realtime translation API rejects unknown fields and returns
        // `Unknown parameter` errors; the WHOLE session.update is then
        // discarded (silently — no language, no transcription). So we keep
        // this minimal and only add fields we've confirmed are accepted.
        // Tried-and-rejected so far: session.instructions.
        var msg = new
        {
            type = "session.update",
            session = new
            {
                audio = new
                {
                    input = new
                    {
                        transcription = new { model = "whisper-1" },
                    },
                    output = new { language = _targetLanguage },
                },
            },
        };
        await SendJsonAsync(msg, ct);
    }

    private async Task SenderLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _audioInput.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                if (_ws.State != WebSocketState.Open) break;

                byte[] toSend = chunk;
                bool silenced = false;
                if (EchoSuppressionEnabled && IsInsideEchoWindow())
                {
                    // Replace this slice with PCM16 silence (all-zero bytes)
                    // so the model isn't fed our own translated audio coming
                    // back through loopback. Same length keeps stream timing
                    // continuous from the model's POV.
                    toSend = new byte[chunk.Length];
                    silenced = true;
                }

                var msg = new
                {
                    type = "session.input_audio_buffer.append",
                    audio = Convert.ToBase64String(toSend),
                };
                await SendJsonAsync(msg, ct);

                if (silenced) _sentSilence++; else _sentReal++;
                if (DateTime.UtcNow - _lastSenderReport > TimeSpan.FromSeconds(5))
                {
                    Log($"sender 5s: {_sentReal} real chunks, {_sentSilence} silence chunks");
                    _sentReal = _sentSilence = 0;
                    _lastSenderReport = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* connection lost */ }
    }

    private bool IsInsideEchoWindow()
    {
        long endTicks = Interlocked.Read(ref _playbackEndTicks);
        if (endTicks == 0) return false;
        long now = DateTime.UtcNow.Ticks;
        return now < endTicks + EchoSuppressionHangover.Ticks;
    }

    /// <summary>
    /// Update the projected end-of-playback timestamp by appending this
    /// chunk's duration. If a previous chunk's playback hasn't finished yet,
    /// stack on top; otherwise schedule starting "now + player latency".
    /// </summary>
    private void ExtendPlaybackEnd(int byteCount)
    {
        int sampleCount = byteCount / 2; // PCM16
        long chunkTicks = (long)sampleCount * TimeSpan.TicksPerSecond / OutputSampleRate;

        long now = DateTime.UtcNow.Ticks;
        long currentEnd, newEnd;
        do
        {
            currentEnd = Interlocked.Read(ref _playbackEndTicks);
            long playbackStart = currentEnd > now ? currentEnd : now + PlayerLatencyTicks;
            newEnd = playbackStart + chunkTicks;
        } while (Interlocked.CompareExchange(ref _playbackEndTicks, newEnd, currentEnd) != currentEnd);
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task ReceiverLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                } while (!result.EndOfMessage);

                var raw = sb.ToString();
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var typeProp = doc.RootElement.TryGetProperty("type", out var t)
                        ? t.GetString() : "?";
                    // Skip the chatty audio-delta type in the log; everything
                    // else gets a snippet so we can see error details.
                    if (typeProp == "session.output_audio.delta"
                        || typeProp == "session.output_transcript.delta"
                        || typeProp == "session.input_transcript.delta")
                        Log($"recv {typeProp}");
                    else
                        Log($"recv {typeProp}: {Truncate(raw, 2000)}");
                    Dispatch(doc.RootElement);
                }
                catch (JsonException) { Log($"recv malformed: {Truncate(raw, 200)}"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* connection lost */ }
    }

    private void Dispatch(JsonElement evt)
    {
        try { DispatchCore(evt); }
        catch (Exception ex)
        {
            // Event handlers (UI updates, ducker, player.Push) must not be
            // able to kill the receive loop. Surface the error and continue.
            Error?.Invoke($"handler error: {ex.Message}");
        }
    }

    private void DispatchCore(JsonElement evt)
    {
        if (!evt.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString() ?? string.Empty;

        switch (type)
        {
            case "session.output_audio.delta":
                if (evt.TryGetProperty("delta", out var ad) && ad.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(ad.GetString()!);
                        // Extend the projected end-of-playback timestamp by
                        // this chunk's duration (PCM16 mono @ OutputSampleRate).
                        ExtendPlaybackEnd(bytes.Length);
                        AudioOut?.Invoke(bytes);
                    }
                    catch (FormatException) { /* skip */ }
                }
                break;

            case "session.output_transcript.delta":
                if (evt.TryGetProperty("delta", out var td))
                    TranslatedTranscript?.Invoke(td.GetString() ?? string.Empty);
                break;

            case "session.input_transcript.delta":
                if (evt.TryGetProperty("delta", out var sd))
                    SourceTranscript?.Invoke(sd.GetString() ?? string.Empty);
                break;

            case "session.created":
            case "session.updated":
                Status?.Invoke(type);
                break;

            case "error":
                string err = evt.TryGetProperty("error", out var e) ? e.GetRawText() : evt.GetRawText();
                Error?.Invoke(err);
                break;

            // Everything else (rate_limits.updated, session.output_audio.done,
            // session.output_transcript.done, session.input_transcript.completed,
            // …) we ignore for now.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log("==== session end ====");
        try { _log?.Dispose(); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _ws.Dispose(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}

internal static class TaskExtensions
{
    /// <summary>Convert any Task into one that never throws (for clean WhenAll).</summary>
    public static Task AsCompletedTask(this Task t) =>
        t.ContinueWith(_ => { }, TaskScheduler.Default);
}
