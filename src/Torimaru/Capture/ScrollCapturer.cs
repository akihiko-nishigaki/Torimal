using Torimaru.App.Interop;
using Torimaru.Core;

namespace Torimaru.App.Capture;

/// <summary>スクロールキャプチャの結果。</summary>
public sealed record ScrollCaptureResult(
    System.Windows.Media.Imaging.BitmapSource? Image,
    int Frames,
    int Height,
    string Message,
    bool Cancelled);

/// <summary>
/// 画面に収まらない内容を、スクロールしながら撮って 1 枚に繋ぐ。
///
/// 対象ウィンドウの中央へカーソルを移してホイールを送り、
/// 1 回ごとに取り込んで <see cref="ScrollStitcher"/> へ渡す。
/// 内容が進まなくなったら末尾に到達したとみなして終了する。
/// </summary>
public static class ScrollCapturer
{
    /// <summary>合成結果の高さの上限（メモリ暴走を防ぐ）。</summary>
    private const int MaxStitchedHeight = 32000;

    /// <summary>これより小さい範囲はスクロール検出が不安定なので受け付けない。</summary>
    public const int MinimumSize = 80;

    public static async Task<ScrollCaptureResult> CaptureAsync(
        PixelRect virtualRegion,
        AppSettings settings,
        IProgress<string>? progress = null)
    {
        var (rx, ry, width, height) = virtualRegion.ToIntegerBounds();

        if (width < MinimumSize || height < MinimumSize)
        {
            return new ScrollCaptureResult(
                null, 0, 0,
                $"範囲が小さすぎます（{MinimumSize} x {MinimumSize} ピクセル以上を選んでください）。",
                false);
        }

        var region = new PixelRect(rx, ry, width, height);
        var stitcher = new ScrollStitcher(width, height);

        NativeMethods.GetCursorPos(out var originalCursor);

        try
        {
            // ホイールは「カーソルの下のウィンドウ」へ届くので、まず中央へ移す
            NativeMethods.SetCursorPos(rx + (width / 2), ry + (height / 2));
            await Task.Delay(120);

            stitcher.AddFrame(ScreenCapturer.CaptureRegionRaw(region), out _);
            progress?.Report("1 枚目を取り込みました");

            var stalled = 0;

            for (var i = 1; i < settings.ScrollMaxFrames; i++)
            {
                if (IsEscapePressed())
                {
                    return Finish(stitcher, "Esc で中止しました", cancelled: true);
                }

                NativeMethods.SendWheel(-settings.ScrollNotches);
                await Task.Delay(settings.ScrollSettleMs);

                var frame = ScreenCapturer.CaptureRegionRaw(region);
                if (stitcher.AddFrame(frame, out var advanced))
                {
                    stalled = 0;
                    progress?.Report($"{stitcher.FrameCount} 枚目 / 高さ {stitcher.TotalHeight} px");
                }
                else
                {
                    // アニメーション中で間に合わなかった可能性があるので、
                    // 一度だけ長めに待って取り直す
                    stalled++;
                    if (stalled >= 2)
                    {
                        return Finish(stitcher, "最後まで到達しました", cancelled: false);
                    }

                    await Task.Delay(settings.ScrollSettleMs * 2);
                    if (!stitcher.AddFrame(ScreenCapturer.CaptureRegionRaw(region), out _))
                    {
                        continue;
                    }

                    stalled = 0;
                }

                if (stitcher.TotalHeight >= MaxStitchedHeight)
                {
                    return Finish(stitcher, $"上限の高さ {MaxStitchedHeight} px に達しました", cancelled: false);
                }
            }

            return Finish(stitcher, $"上限の {settings.ScrollMaxFrames} 枚に達しました", cancelled: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("スクロールキャプチャに失敗", ex);
            return new ScrollCaptureResult(null, stitcher.FrameCount, stitcher.TotalHeight, ex.Message, false);
        }
        finally
        {
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static ScrollCaptureResult Finish(ScrollStitcher stitcher, string message, bool cancelled)
    {
        if (stitcher.FrameCount == 0)
        {
            return new ScrollCaptureResult(null, 0, 0, "1 枚も取り込めませんでした", cancelled);
        }

        var stitched = stitcher.Build();
        var image = ScreenCapturer.ToBitmapSource(stitched);

        var summary = stitcher.FrameCount == 1
            ? "スクロールを検出できませんでした（1 画面分のみ取り込みました）"
            : $"{stitcher.FrameCount} 枚を合成しました（高さ {stitched.Height} px）— {message}";

        return new ScrollCaptureResult(image, stitcher.FrameCount, stitched.Height, summary, cancelled);
    }

    private static bool IsEscapePressed()
        => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE) & 0x8000) != 0;
}
