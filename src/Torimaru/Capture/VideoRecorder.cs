using System.Diagnostics;
using System.Globalization;
using Torimaru.App.Audio;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App.Capture;

/// <summary>録画開始の結果。</summary>
public sealed record RecordingStartResult(bool Success, string Message);

/// <summary>録画終了の結果。</summary>
public sealed record RecordingStopResult(bool Success, string Message, string? OutputPath);

/// <summary>
/// 画面を MP4 に録画する。
///
/// <para>映像は ffmpeg の gdigrab、音声は WASAPI で別々に録り、終了時にまとめる。
/// 音声を ffmpeg 任せにしないのは、この PC に Stereo Mix が無く
/// DirectShow ではスピーカー出力を拾えないため。</para>
///
/// <para>一時停止は「区間を切って録り直し、最後に連結する」方式。
/// ffmpeg 自体に一時停止が無いので、既にある結合処理を使い回している。</para>
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    private readonly AppSettings _settings;
    private readonly List<string> _segments = [];
    private readonly Stopwatch _segmentClock = new();

    private string _workFolder = string.Empty;
    private PixelRect _region;

    private Process? _ffmpeg;
    private WasapiRecorder? _systemAudio;
    private WasapiRecorder? _microphone;
    private string _segmentVideoPath = string.Empty;
    private string _systemAudioPath = string.Empty;
    private string _microphonePath = string.Empty;

    private TimeSpan _accumulated;

    public VideoRecorder(AppSettings settings) => _settings = settings;

    public bool IsRecording { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>録画済みの長さ（一時停止中は進まない）。</summary>
    public TimeSpan Elapsed => _accumulated + _segmentClock.Elapsed;

    /// <summary>音声が実際に録れているかどうか（UI 表示用）。</summary>
    public bool SystemAudioActive { get; private set; }

    public bool MicrophoneActive { get; private set; }

    public async Task<RecordingStartResult> StartAsync(PixelRect virtualRegion)
    {
        if (IsRecording)
        {
            return new RecordingStartResult(false, "すでに録画中です");
        }

        if (!Ffmpeg.IsAvailable)
        {
            return new RecordingStartResult(false, Ffmpeg.MissingMessage);
        }

        var (x, y, width, height) = virtualRegion.ToIntegerBounds();

        // yuv420p は縦横が偶数である必要がある
        width -= width % 2;
        height -= height % 2;

        if (width < 32 || height < 32)
        {
            return new RecordingStartResult(false, "録画範囲が小さすぎます（32 x 32 ピクセル以上）");
        }

        _region = new PixelRect(x, y, width, height);
        _workFolder = Path.Combine(Path.GetTempPath(), "TorimaruRecording", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workFolder);

        _segments.Clear();
        _accumulated = TimeSpan.Zero;

        var started = await StartSegmentAsync();
        if (!started.Success)
        {
            Cleanup();
            return started;
        }

        IsRecording = true;
        IsPaused = false;
        return started;
    }

    public async Task PauseAsync()
    {
        if (!IsRecording || IsPaused)
        {
            return;
        }

        await StopSegmentAsync();
        _accumulated += _segmentClock.Elapsed;
        _segmentClock.Reset();
        IsPaused = true;
    }

    public async Task<RecordingStartResult> ResumeAsync()
    {
        if (!IsRecording || !IsPaused)
        {
            return new RecordingStartResult(false, "一時停止中ではありません");
        }

        var result = await StartSegmentAsync();
        if (result.Success)
        {
            IsPaused = false;
        }

        return result;
    }

    /// <summary>録画を終えて 1 本の MP4 にまとめる。</summary>
    public async Task<RecordingStopResult> StopAsync(string outputPath)
    {
        if (!IsRecording)
        {
            return new RecordingStopResult(false, "録画していません", null);
        }

        try
        {
            if (!IsPaused)
            {
                await StopSegmentAsync();
                _accumulated += _segmentClock.Elapsed;
            }

            _segmentClock.Reset();

            if (_segments.Count == 0)
            {
                return new RecordingStopResult(false, "録画データが作られませんでした", null);
            }

            var concat = await VideoTools.ConcatAsync(_segments, outputPath);
            if (!concat.Success)
            {
                return new RecordingStopResult(false, concat.Message, null);
            }

            var length = Elapsed;
            return new RecordingStopResult(
                true,
                $"{length:mm\\:ss} の録画を保存しました（{_segments.Count} 区間）",
                outputPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("録画の終了処理に失敗", ex);
            return new RecordingStopResult(false, ex.Message, null);
        }
        finally
        {
            IsRecording = false;
            IsPaused = false;
            Cleanup();
        }
    }

    /// <summary>保存せずに中止する。</summary>
    public async Task CancelAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        try
        {
            if (!IsPaused)
            {
                await StopSegmentAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("録画の中止処理に失敗", ex);
        }
        finally
        {
            IsRecording = false;
            IsPaused = false;
            Cleanup();
        }
    }

    // ------------------------------------------------------------ 区間の開始 / 終了

    private async Task<RecordingStartResult> StartSegmentAsync()
    {
        var index = _segments.Count;
        _segmentVideoPath = Path.Combine(_workFolder, $"seg{index}-video.mp4");
        _systemAudioPath = Path.Combine(_workFolder, $"seg{index}-system.wav");
        _microphonePath = Path.Combine(_workFolder, $"seg{index}-mic.wav");

        var executable = Ffmpeg.ResolvePath();
        if (executable is null)
        {
            return new RecordingStartResult(false, Ffmpeg.MissingMessage);
        }

        var arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"-y -hide_banner -loglevel error -stats -stats_period 0.2 "
            + $"-f gdigrab -framerate {_settings.VideoFps} "
            + $"-offset_x {(int)_region.X} -offset_y {(int)_region.Y} "
            + $"-video_size {(int)_region.Width}x{(int)_region.Height} "
            + $"-draw_mouse {(_settings.RecordCursor ? 1 : 0)} -i desktop "
            + $"-c:v libx264 -preset veryfast -crf {_settings.VideoCrf} -pix_fmt yuv420p "
            + $"{Ffmpeg.Quote(_segmentVideoPath)}");

        var firstFrame = new TaskCompletionSource();

        _ffmpeg = new Process
        {
            StartInfo = Ffmpeg.CreateStartInfo(executable, arguments),
            EnableRaisingEvents = true,
        };

        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // 最初のフレームが出たら、そこから音声を録り始めて頭ずれを減らす
            if (e.Data.Contains("frame=", StringComparison.Ordinal))
            {
                firstFrame.TrySetResult();
            }
            else if (e.Data.Length > 0)
            {
                AppLog.Info($"ffmpeg: {e.Data}");
            }
        };

        try
        {
            _ffmpeg.Start();
            _ffmpeg.BeginErrorReadLine();
            _ffmpeg.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            AppLog.Error("ffmpeg を起動できませんでした", ex);
            return new RecordingStartResult(false, $"ffmpeg を起動できませんでした: {ex.Message}");
        }

        // エンコードが始まるまで待つ（届かない場合もあるので上限付き）
        await Task.WhenAny(firstFrame.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        if (_ffmpeg.HasExited)
        {
            return new RecordingStartResult(
                false, "ffmpeg が起動直後に終了しました。ログを確認してください。");
        }

        StartAudio();

        _segmentClock.Restart();
        return new RecordingStartResult(true, "録画を開始しました");
    }

    private void StartAudio()
    {
        SystemAudioActive = false;
        MicrophoneActive = false;

        if (_settings.RecordSystemAudio)
        {
            _systemAudio = new WasapiRecorder(loopback: true, deviceId: null, _systemAudioPath);
            if (_systemAudio.TryStart(out var error))
            {
                SystemAudioActive = true;
            }
            else
            {
                AppLog.Error($"システム音声を録音できません: {error}");
                _systemAudio.Dispose();
                _systemAudio = null;
            }
        }

        if (_settings.RecordMicrophone)
        {
            _microphone = new WasapiRecorder(
                loopback: false,
                deviceId: string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId) ? null : _settings.MicrophoneDeviceId,
                _microphonePath);

            if (_microphone.TryStart(out var error))
            {
                MicrophoneActive = true;
            }
            else
            {
                AppLog.Error($"マイクを録音できません: {error}");
                _microphone.Dispose();
                _microphone = null;
            }
        }
    }

    private async Task StopSegmentAsync()
    {
        // 音声を先に止める（映像より長くなっても -shortest で揃う）
        _systemAudio?.Stop();
        _microphone?.Stop();

        await StopFfmpegAsync();

        var audioPaths = new List<string>();
        if (SystemAudioActive)
        {
            audioPaths.Add(_systemAudioPath);
        }

        if (MicrophoneActive)
        {
            audioPaths.Add(_microphonePath);
        }

        _systemAudio?.Dispose();
        _microphone?.Dispose();
        _systemAudio = null;
        _microphone = null;

        if (!File.Exists(_segmentVideoPath))
        {
            AppLog.Error("区間の映像ファイルが作られませんでした");
            return;
        }

        var segmentPath = Path.Combine(_workFolder, $"segment{_segments.Count}.mp4");
        var muxed = await VideoTools.MuxAsync(_segmentVideoPath, audioPaths, segmentPath);

        if (muxed.Success && File.Exists(segmentPath))
        {
            _segments.Add(segmentPath);
        }

        foreach (var path in audioPaths)
        {
            VideoTools.TryDelete(path);
        }

        VideoTools.TryDelete(_segmentVideoPath);
    }

    /// <summary>
    /// ffmpeg へ "q" を送って正常終了させる。強制終了だと MP4 が壊れる。
    /// </summary>
    private async Task StopFfmpegAsync()
    {
        var process = _ffmpeg;
        _ffmpeg = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteAsync('q');
                await process.StandardInput.FlushAsync();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            AppLog.Error("ffmpeg が終了しないため強制終了します");
            TryKill(process);
        }
        catch (Exception ex)
        {
            AppLog.Error("ffmpeg の終了処理で例外", ex);
            TryKill(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("ffmpeg の強制終了に失敗", ex);
        }
    }

    private void Cleanup()
    {
        _systemAudio?.Dispose();
        _microphone?.Dispose();
        _systemAudio = null;
        _microphone = null;

        if (_ffmpeg is not null)
        {
            TryKill(_ffmpeg);
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }

        if (string.IsNullOrEmpty(_workFolder) || !Directory.Exists(_workFolder))
        {
            return;
        }

        try
        {
            Directory.Delete(_workFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"作業フォルダを削除できませんでした: {_workFolder}", ex);
        }

        _segments.Clear();
    }

    public void Dispose() => Cleanup();
}
