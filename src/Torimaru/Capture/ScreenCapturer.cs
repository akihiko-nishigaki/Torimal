using System.Runtime.InteropServices;
using Torimaru.App.Interop;
using Torimaru.Core;

// GDI+ の型は WPF の同名型（Point / Size / Rectangle / PixelFormat）と衝突するため
// すべて別名で取り込み、どちらの型なのかを読み手に明示する。
using Bitmap = System.Drawing.Bitmap;
using CopyPixelOperation = System.Drawing.CopyPixelOperation;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using Graphics = System.Drawing.Graphics;
using Icon = System.Drawing.Icon;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Torimaru.App.Capture;

/// <summary>
/// 画面全体（全モニタ）を 1 枚のビットマップとして取り込む。
///
/// 範囲選択は「まず全画面を凍結してから、その静止画の上で選ぶ」方式にしている。
/// 選択中に裏の画面が動いても結果が変わらず、DPI 混在環境でも座標が 1:1 で一致する。
/// </summary>
public static class ScreenCapturer
{
    /// <summary>仮想画面（全モニタを含む外接矩形）を物理ピクセルで返す。</summary>
    public static (int X, int Y, int Width, int Height) GetVirtualScreenBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        // 取得できない場合はプライマリ画面にフォールバック
        if (width <= 0 || height <= 0)
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                          ?? new DrawingRectangle(0, 0, 1920, 1080);
            return (primary.X, primary.Y, primary.Width, primary.Height);
        }

        return (x, y, width, height);
    }

    /// <summary>全モニタを取り込む。</summary>
    public static CapturedImage CaptureVirtualScreen(bool includeCursor)
    {
        var (vx, vy, vw, vh) = GetVirtualScreenBounds();

        using var bitmap = new Bitmap(vw, vh, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(vx, vy, 0, 0, new DrawingSize(vw, vh), CopyPixelOperation.SourceCopy);

            if (includeCursor)
            {
                DrawCursor(graphics, vx, vy);
            }
        }

        return ToCapturedImage(bitmap, vx, vy);
    }

    /// <summary>
    /// 画面の一部だけを取り込んで <see cref="RawImage"/> で返す。
    /// スクロールキャプチャでは何度も撮り直すため、全画面を撮るより軽い経路を使う。
    /// </summary>
    public static RawImage CaptureRegionRaw(PixelRect virtualRect)
    {
        var (x, y, width, height) = virtualRect.ToIntegerBounds();
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualRect), "取り込む範囲が空です。");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new DrawingSize(width, height), CopyPixelOperation.SourceCopy);
        }

        return ToRawImage(bitmap);
    }

    /// <summary><see cref="RawImage"/> を WPF で表示できる形へ変換する。</summary>
    public static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(RawImage image)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgr32,
            null,
            image.Pixels,
            image.Stride);

        source.Freeze();
        return source;
    }

    private static RawImage ToRawImage(Bitmap bitmap)
    {
        var rect = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bitmap.Height];

            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (data.Stride * y), buffer, stride * y, stride);
            }

            return new RawImage(buffer, bitmap.Width, bitmap.Height, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>カーソル位置があるモニタの作業領域全体を取り込む。</summary>
    public static PixelRect GetMonitorBoundsAt(CapturedImage image, double imageX, double imageY)
    {
        var virtualX = (int)Math.Round(imageX) + image.OriginX;
        var virtualY = (int)Math.Round(imageY) + image.OriginY;

        var screen = System.Windows.Forms.Screen.FromPoint(new DrawingPoint(virtualX, virtualY));
        var bounds = screen.Bounds;

        return new PixelRect(
            bounds.X - image.OriginX,
            bounds.Y - image.OriginY,
            bounds.Width,
            bounds.Height);
    }

    private static void DrawCursor(Graphics graphics, int originX, int originY)
    {
        var info = new NativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>(),
        };

        if (!NativeMethods.GetCursorInfo(ref info)
            || (info.flags & NativeMethods.CURSOR_SHOWING) == 0
            || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        // 元のカーソルハンドルを所有していないので、複製して扱う
        var copy = NativeMethods.CopyIcon(info.hCursor);
        if (copy == IntPtr.Zero)
        {
            return;
        }

        var iconInfoValid = NativeMethods.GetIconInfo(copy, out var iconInfo);
        try
        {
            var hotspotX = iconInfoValid ? iconInfo.xHotspot : 0;
            var hotspotY = iconInfoValid ? iconInfo.yHotspot : 0;

            using var icon = Icon.FromHandle(copy);
            graphics.DrawIcon(
                icon,
                new DrawingRectangle(
                    info.ptScreenPos.X - originX - hotspotX,
                    info.ptScreenPos.Y - originY - hotspotY,
                    icon.Width,
                    icon.Height));
        }
        catch (Exception ex)
        {
            // カーソル描画の失敗でキャプチャ全体を落とさない
            AppLog.Error("カーソルの描画に失敗", ex);
        }
        finally
        {
            if (iconInfoValid)
            {
                if (iconInfo.hbmMask != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(iconInfo.hbmMask);
                }

                if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(iconInfo.hbmColor);
                }
            }

            NativeMethods.DestroyIcon(copy);
        }
    }

    private static CapturedImage ToCapturedImage(Bitmap bitmap, int originX, int originY)
    {
        var rect = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bitmap.Height];

            if (data.Stride > 0)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                // ボトムアップのビットマップは行単位で上下反転しながらコピーする
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var source = data.Scan0 + (data.Stride * y);
                    Marshal.Copy(source, buffer, stride * y, stride);
                }
            }

            return new CapturedImage(buffer, bitmap.Width, bitmap.Height, stride, originX, originY);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
