using System.Text.Json;

namespace Torimaru.Core;

/// <summary>アプリ設定。%AppData%\Torimaru\settings.json に保存される。</summary>
public sealed class AppSettings
{
    /// <summary>キャプチャの保存先フォルダ。</summary>
    public string SaveFolder { get; set; } = DefaultSaveFolder();

    /// <summary>ファイル名に使う日時書式。</summary>
    public string FileNamePattern { get; set; } = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>マウスカーソルを一緒に写すか。</summary>
    public bool CaptureCursor { get; set; }

    /// <summary>キャプチャ直後にクリップボードへコピーするか。</summary>
    public bool CopyToClipboardOnCapture { get; set; } = true;

    /// <summary>キャプチャ直後にエディタを開くか。</summary>
    public bool OpenEditorAfterCapture { get; set; } = true;

    /// <summary>注釈のデフォルト色（#AARRGGBB）。</summary>
    public string DefaultColor { get; set; } = "#FFE53935";

    public double DefaultThickness { get; set; } = 3;

    /// <summary>文字・吹き出しの既定の書体。</summary>
    public string DefaultFontFamily { get; set; } = "Yu Gothic UI";

    /// <summary>文字・吹き出しの既定の大きさ（画像ピクセル）。</summary>
    public double DefaultFontSize { get; set; } = 24;

    /// <summary>
    /// 書体一覧の先頭に出すお気に入り。
    /// null は「まだ決めていない」で、この場合だけ定番の書体を自動で並べる。
    /// 空リストは「ひとつも要らない」という意思表示なので、勝手に埋め直さない。
    /// </summary>
    public List<string>? FavoriteFonts { get; set; }

    /// <summary>範囲選択中のルーペ倍率。</summary>
    public int MagnifierZoom { get; set; } = 8;

    /// <summary>遅延キャプチャの待ち時間（秒）。</summary>
    public int DelaySeconds { get; set; } = 5;

    /// <summary>スクロールキャプチャで 1 回に送るホイールの刻み数。</summary>
    public int ScrollNotches { get; set; } = 3;

    /// <summary>スクロール後、描画が落ち着くまで待つ時間（ミリ秒）。</summary>
    public int ScrollSettleMs { get; set; } = 260;

    /// <summary>スクロールキャプチャで撮る最大フレーム数（暴走防止）。</summary>
    public int ScrollMaxFrames { get; set; } = 60;

    /// <summary>OCR に使う言語タグ。空なら OS の既定を使う。</summary>
    public string OcrLanguage { get; set; } = "ja";

    /// <summary>録画のフレームレート。</summary>
    public int VideoFps { get; set; } = 30;

    /// <summary>H.264 の画質（小さいほど高画質・大きいほど軽い）。</summary>
    public int VideoCrf { get; set; } = 23;

    /// <summary>録画にマウスカーソルを含めるか。</summary>
    public bool RecordCursor { get; set; } = true;

    /// <summary>スピーカーに出ている音を録るか。</summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>マイクを録るか。</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>使用するマイクのデバイス ID。空なら既定のマイク。</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>ffmpeg.exe のフルパス。空なら PATH から探す。</summary>
    public string? FfmpegPath { get; set; }

    public static string DefaultSaveFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Torimaru");

    public static string SettingsPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Torimaru",
            "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>設定を読み込む。無い/壊れている場合は既定値を返す（例外は投げない）。</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath();
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions);
            return loaded is null ? new AppSettings() : loaded.Sanitized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= SettingsPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>不正な値を既定値へ戻す。</summary>
    public AppSettings Sanitized()
    {
        if (string.IsNullOrWhiteSpace(SaveFolder))
        {
            SaveFolder = DefaultSaveFolder();
        }

        if (string.IsNullOrWhiteSpace(FileNamePattern))
        {
            FileNamePattern = "yyyy-MM-dd_HH-mm-ss";
        }

        if (string.IsNullOrWhiteSpace(DefaultColor))
        {
            DefaultColor = "#FFE53935";
        }

        if (string.IsNullOrWhiteSpace(DefaultFontFamily))
        {
            DefaultFontFamily = "Yu Gothic UI";
        }

        DefaultThickness = Math.Clamp(DefaultThickness, 1, 40);
        DefaultFontSize = Math.Clamp(DefaultFontSize, 8, 200);

        if (FavoriteFonts is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FavoriteFonts = FavoriteFonts
                .Where(name => !string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
                .Select(name => name.Trim())
                .Take(40)
                .ToList();
        }
        MagnifierZoom = Math.Clamp(MagnifierZoom, 2, 20);
        DelaySeconds = Math.Clamp(DelaySeconds, 1, 60);
        ScrollNotches = Math.Clamp(ScrollNotches, 1, 10);
        ScrollSettleMs = Math.Clamp(ScrollSettleMs, 60, 3000);
        ScrollMaxFrames = Math.Clamp(ScrollMaxFrames, 2, 400);
        VideoFps = Math.Clamp(VideoFps, 5, 60);
        VideoCrf = Math.Clamp(VideoCrf, 0, 51);
        return this;
    }
}
