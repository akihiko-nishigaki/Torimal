using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Torimaru.App.Audio;

/// <summary>
/// WASAPI で音声を WAV ファイルへ録る。
///
/// <para>
/// ループバック（スピーカーに出ている音）と録音デバイス（マイク）の両方に対応する。
/// COM の呼び出しはすべて専用スレッド（MTA）に閉じ込めている。
/// </para>
/// <para>
/// ループバックには「無音のあいだパケットが 1 つも届かない」という性質がある。
/// そのまま書くと音声が映像より短くなり、ずれていくため、
/// 経過時間に対して書き込み量が足りない分を無音で埋めている。
/// </para>
/// </summary>
public sealed class WasapiRecorder : IDisposable
{
    private readonly bool _loopback;
    private readonly string? _deviceId;
    private readonly string _outputPath;

    private readonly ManualResetEventSlim _started = new(false);
    private readonly Stopwatch _clock = new();

    private Thread? _thread;
    private volatile bool _stopping;
    private Exception? _failure;

    private long _framesWritten;
    private int _sampleRate;
    private int _blockAlign;

    public WasapiRecorder(bool loopback, string? deviceId, string outputPath)
    {
        _loopback = loopback;
        _deviceId = deviceId;
        _outputPath = outputPath;
    }

    public string OutputPath => _outputPath;

    /// <summary>書き込んだ音声の長さ。</summary>
    public TimeSpan Duration => _sampleRate <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)_framesWritten / _sampleRate);

    /// <summary>
    /// 録音を開始する。初期化に失敗した場合は false を返し、<paramref name="error"/> に理由が入る。
    /// </summary>
    public bool TryStart(out string? error)
    {
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = _loopback ? "Torimaru.SystemAudio" : "Torimaru.Microphone",
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        // 初期化が終わる（あるいは失敗する）まで待つ
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            _stopping = true;
            error = "音声デバイスの初期化がタイムアウトしました。";
            return false;
        }

        if (_failure is not null)
        {
            error = _failure.Message;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>録音を止め、WAV を閉じる。</summary>
    public void Stop()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    private void CaptureLoop()
    {
        WasapiInterop.IMMDeviceEnumerator? enumerator = null;
        WasapiInterop.IMMDevice? device = null;
        WasapiInterop.IAudioClient? client = null;
        WasapiInterop.IAudioCaptureClient? capture = null;
        IntPtr formatPtr = IntPtr.Zero;
        WavWriter? writer = null;

        try
        {
            enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumerator();

            if (string.IsNullOrEmpty(_deviceId))
            {
                var flow = _loopback ? WasapiInterop.DataFlow.Render : WasapiInterop.DataFlow.Capture;
                WasapiInterop.ThrowIfFailed(
                    enumerator.GetDefaultAudioEndpoint(flow, WasapiInterop.Role.Multimedia, out device),
                    "既定の音声デバイスの取得");
            }
            else
            {
                WasapiInterop.ThrowIfFailed(
                    enumerator.GetDevice(_deviceId, out device), "音声デバイスの取得");
            }

            var iid = WasapiInterop.IID_IAudioClient;
            WasapiInterop.ThrowIfFailed(
                device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var clientObject),
                "オーディオクライアントの作成");

            client = (WasapiInterop.IAudioClient)clientObject;

            WasapiInterop.ThrowIfFailed(client.GetMixFormat(out formatPtr), "音声フォーマットの取得");
            var format = Marshal.PtrToStructure<WasapiInterop.WaveFormatEx>(formatPtr);

            _sampleRate = (int)format.SamplesPerSecond;
            _blockAlign = format.BlockAlign;

            if (_blockAlign <= 0 || _sampleRate <= 0)
            {
                throw new InvalidOperationException("音声フォーマットを解釈できませんでした。");
            }

            var streamFlags = _loopback ? WasapiInterop.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;

            WasapiInterop.ThrowIfFailed(
                client.Initialize(
                    WasapiInterop.AUDCLNT_SHAREMODE_SHARED,
                    streamFlags,
                    WasapiInterop.ReferenceTimesPerSecond, // 1 秒分のバッファ
                    0,
                    formatPtr,
                    IntPtr.Zero),
                "オーディオクライアントの初期化");

            var captureIid = WasapiInterop.IID_IAudioCaptureClient;
            WasapiInterop.ThrowIfFailed(
                client.GetService(ref captureIid, out var captureObject), "取り込みクライアントの作成");

            capture = (WasapiInterop.IAudioCaptureClient)captureObject;

            writer = new WavWriter(_outputPath, formatPtr, format);

            WasapiInterop.ThrowIfFailed(client.Start(), "録音の開始");
            _clock.Restart();
            _started.Set();

            var silence = new byte[_blockAlign * 4096];

            while (!_stopping)
            {
                Thread.Sleep(10);

                while (true)
                {
                    if (capture.GetNextPacketSize(out var packetFrames) < 0 || packetFrames == 0)
                    {
                        break;
                    }

                    if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0)
                    {
                        break;
                    }

                    try
                    {
                        var byteCount = frames * _blockAlign;

                        if ((flags & WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                        {
                            writer.WriteSilence(byteCount);
                        }
                        else
                        {
                            writer.Write(data, byteCount);
                        }

                        _framesWritten += frames;
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }

                PadSilenceIfBehind(writer, silence);
            }

            // 止める直前までの長さに合わせて埋める
            PadSilenceIfBehind(writer, silence, toleranceFrames: 0);
            client.Stop();
        }
        catch (Exception ex)
        {
            _failure = ex;
            AppLog.Error($"音声の録音に失敗 (loopback={_loopback})", ex);
            _started.Set();
        }
        finally
        {
            writer?.Dispose();

            if (formatPtr != IntPtr.Zero)
            {
                WasapiInterop.CoTaskMemFree(formatPtr);
            }

            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>
    /// 経過時間に対して書き込みが足りていなければ無音で埋める。
    /// ループバックは「音が鳴っていない間は何も届かない」ので、これが無いと映像とずれる。
    /// </summary>
    private void PadSilenceIfBehind(WavWriter writer, byte[] silence, int toleranceFrames = -1)
    {
        if (_sampleRate <= 0)
        {
            return;
        }

        // 既定の許容は 50 ミリ秒（毎回ぴったり合わせると細かく刻みすぎるため）
        var tolerance = toleranceFrames >= 0 ? toleranceFrames : _sampleRate / 20;
        var expected = (long)(_clock.Elapsed.TotalSeconds * _sampleRate);
        var missing = expected - _framesWritten - tolerance;

        if (missing <= 0)
        {
            return;
        }

        var framesPerChunk = silence.Length / _blockAlign;
        while (missing > 0)
        {
            var frames = (int)Math.Min(missing, framesPerChunk);
            writer.WriteSilence(frames * _blockAlign);
            _framesWritten += frames;
            missing -= frames;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    public void Dispose()
    {
        Stop();
        _started.Dispose();
    }

    /// <summary>
    /// WAV を書き出す。fmt チャンクにはデバイスの形式をそのまま書くので、
    /// 32bit 浮動小数の多チャンネルでも変換なしで保存できる。
    /// </summary>
    private sealed class WavWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly byte[] _silenceBuffer = new byte[16384];
        private byte[] _scratch = new byte[16384];
        private long _dataSizeOffset;
        private long _dataLength;

        public WavWriter(string path, IntPtr formatPtr, WasapiInterop.WaveFormatEx format)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            _writer = new BinaryWriter(_stream);

            // PCM だけの場合は 16 バイト、拡張形式なら 18 + cbSize バイト
            var formatSize = format.ExtraSize == 0 ? 16 : 18 + format.ExtraSize;
            var formatBytes = new byte[formatSize];
            Marshal.Copy(formatPtr, formatBytes, 0, formatSize);

            _writer.Write("RIFF"u8.ToArray());
            _writer.Write(0); // 後で埋める
            _writer.Write("WAVE"u8.ToArray());

            _writer.Write("fmt "u8.ToArray());
            _writer.Write(formatSize);
            _writer.Write(formatBytes);

            _writer.Write("data"u8.ToArray());
            _dataSizeOffset = _stream.Position;
            _writer.Write(0); // 後で埋める
        }

        public void Write(IntPtr source, int byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }

            if (_scratch.Length < byteCount)
            {
                _scratch = new byte[byteCount];
            }

            Marshal.Copy(source, _scratch, 0, byteCount);
            _stream.Write(_scratch, 0, byteCount);
            _dataLength += byteCount;
        }

        public void WriteSilence(int byteCount)
        {
            while (byteCount > 0)
            {
                var chunk = Math.Min(byteCount, _silenceBuffer.Length);
                _stream.Write(_silenceBuffer, 0, chunk);
                _dataLength += chunk;
                byteCount -= chunk;
            }
        }

        public void Dispose()
        {
            try
            {
                _stream.Flush();

                // RIFF と data のサイズを後から埋める
                var totalLength = _stream.Length;

                _stream.Seek(4, SeekOrigin.Begin);
                _writer.Write((uint)(totalLength - 8));

                _stream.Seek(_dataSizeOffset, SeekOrigin.Begin);
                _writer.Write((uint)Math.Min(_dataLength, uint.MaxValue));
            }
            catch (IOException ex)
            {
                AppLog.Error("WAV ヘッダーの書き戻しに失敗", ex);
            }
            finally
            {
                _writer.Dispose();
            }
        }
    }
}
