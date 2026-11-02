namespace Torimaru.Core;

/// <summary>
/// スクロールしながら撮った複数枚を 1 枚の縦長画像へ繋ぐ。
///
/// 仕組み: 各行のハッシュを取り、前フレームと次フレームで行列が何行ずれたかを探す。
/// 画素同士を総当たりで比べるより桁違いに速く、文字や UI のような
/// 「行ごとに模様が違う」内容では十分に正確。
///
/// 空白行は多くのずれ量に一致してしまうため、フレーム内で珍しい行
/// （出現回数の少ない行）だけを得点に数えて誤検出を防いでいる。
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>重なりがこれ未満になるずれ量は信用しない。</summary>
    private const int MinOverlapRows = 24;

    /// <summary>一致と認めるために必要な「珍しい行」の最小数。</summary>
    private const int MinDistinctMatches = 6;

    private readonly List<RawImage> _appended = [];
    private readonly int _hashLeft;
    private readonly int _hashRight;

    private ulong[]? _previousHashes;
    private bool[]? _previousDistinct;

    public ScrollStitcher(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Width = width;
        FrameHeight = height;

        // 右端のスクロールバーは毎フレーム変わるのでハッシュから外す。
        // 左端も枠線の描き換えが起きやすいので少し削る。
        if (width > 48)
        {
            _hashLeft = 2;
            _hashRight = width - 18;
        }
        else
        {
            _hashLeft = 0;
            _hashRight = width;
        }
    }

    public int Width { get; }

    public int FrameHeight { get; }

    /// <summary>取り込んだフレーム数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>現時点で合成される画像の高さ。</summary>
    public int TotalHeight { get; private set; }

    /// <summary>
    /// フレームを追加する。
    /// </summary>
    /// <param name="frame">今回のフレーム。</param>
    /// <param name="advanced">前回から何行進んだか。進まなかった場合は 0。</param>
    /// <returns>画像が伸びた場合は true。末尾に到達した場合は false。</returns>
    public bool AddFrame(RawImage frame, out int advanced)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Width != Width || frame.Height != FrameHeight)
        {
            throw new ArgumentException("フレームの寸法が一致しません。", nameof(frame));
        }

        var hashes = ComputeRowHashes(frame, _hashLeft, _hashRight);
        var distinct = MarkDistinctRows(hashes);

        if (_previousHashes is null)
        {
            _appended.Add(frame.TakeRows(0, FrameHeight));
            TotalHeight = FrameHeight;
            FrameCount = 1;

            _previousHashes = hashes;
            _previousDistinct = distinct;

            advanced = FrameHeight;
            return true;
        }

        var shift = FindVerticalShift(_previousHashes, _previousDistinct!, hashes);
        if (shift <= 0)
        {
            advanced = 0;
            return false;
        }

        // 新しく見えた分（下端 shift 行）だけを足す
        _appended.Add(frame.TakeRows(FrameHeight - shift, shift));
        TotalHeight += shift;
        FrameCount++;

        _previousHashes = hashes;
        _previousDistinct = distinct;

        advanced = shift;
        return true;
    }

    /// <summary>これまでのフレームを 1 枚に繋いで返す。</summary>
    public RawImage Build()
    {
        if (_appended.Count == 0)
        {
            throw new InvalidOperationException("フレームが 1 枚も追加されていません。");
        }

        var result = RawImage.Create(Width, TotalHeight);

        var y = 0;
        foreach (var piece in _appended)
        {
            result.CopyRowsFrom(piece, 0, y, piece.Height);
            y += piece.Height;
        }

        return result;
    }

    /// <summary>
    /// 行ごとのハッシュを計算する（FNV-1a）。
    /// </summary>
    public static ulong[] ComputeRowHashes(RawImage image, int left, int right)
    {
        left = Math.Clamp(left, 0, image.Width);
        right = Math.Clamp(right, left, image.Width);

        var hashes = new ulong[image.Height];
        var from = left * RawImage.BytesPerPixel;
        var to = right * RawImage.BytesPerPixel;

        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(y * image.Stride, image.Width * RawImage.BytesPerPixel);

            var hash = 14695981039346656037UL;
            for (var i = from; i < to; i++)
            {
                hash ^= row[i];
                hash *= 1099511628211UL;
            }

            hashes[y] = hash;
        }

        return hashes;
    }

    /// <summary>
    /// フレーム内で 2 回以下しか現れない行に印を付ける。
    /// 余白のような繰り返し行を得点から外すのが目的。
    /// </summary>
    public static bool[] MarkDistinctRows(ulong[] hashes)
    {
        var counts = new Dictionary<ulong, int>(hashes.Length);
        foreach (var hash in hashes)
        {
            counts[hash] = counts.TryGetValue(hash, out var n) ? n + 1 : 1;
        }

        var distinct = new bool[hashes.Length];
        for (var i = 0; i < hashes.Length; i++)
        {
            distinct[i] = counts[hashes[i]] <= 2;
        }

        return distinct;
    }

    /// <summary>
    /// 前フレームから次フレームへ、内容が何行上へ移動したかを求める。
    /// 判定できない場合は 0 を返す。
    /// </summary>
    public static int FindVerticalShift(ulong[] previous, bool[] previousDistinct, ulong[] next)
    {
        var height = previous.Length;
        if (height != next.Length || height < MinOverlapRows)
        {
            return 0;
        }

        var minOverlap = Math.Max(MinOverlapRows, height / 8);
        var maxShift = height - minOverlap;

        var bestShift = 0;
        var bestDistinct = 0;
        var bestTotal = 0;

        for (var shift = 1; shift <= maxShift; shift++)
        {
            var overlap = height - shift;
            var distinctMatches = 0;
            var totalMatches = 0;

            for (var y = 0; y < overlap; y++)
            {
                if (previous[y + shift] != next[y])
                {
                    continue;
                }

                totalMatches++;
                if (previousDistinct[y + shift])
                {
                    distinctMatches++;
                }
            }

            // 重なりが浅いほど偶然一致しやすいので、同点なら small shift を優先する
            if (distinctMatches > bestDistinct
                || (distinctMatches == bestDistinct && distinctMatches > 0 && totalMatches > bestTotal))
            {
                bestDistinct = distinctMatches;
                bestTotal = totalMatches;
                bestShift = shift;
            }
        }

        if (bestDistinct < MinDistinctMatches)
        {
            return 0;
        }

        // 重なり部分の大半が一致していることも確認する（部分的な偶然一致を弾く）
        var bestOverlap = height - bestShift;
        return bestTotal * 2 >= bestOverlap ? bestShift : 0;
    }
}
