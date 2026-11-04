namespace Torimaru.Core;

/// <summary>
/// 画像を切ったときに、注釈の座標を一緒に付け替える。
///
/// Torimaru は注釈をベクタのまま持ち続けるので、画像だけ切って注釈を放置すると
/// 注釈が元の位置に残って中身とずれる。画像を切る操作と必ず対で呼ぶこと。
/// </summary>
public static class AnnotationTransform
{
    /// <summary>
    /// 2 点で形が決まる図形か（＝両端をそれぞれ写せばよい図形）。
    /// 文字・番号・吹き出しは「置いた点」と「文字から決まる大きさ」なので別扱いにする。
    /// </summary>
    private static bool IsAnchored(string kind)
        => kind is ShapeKind.Text or ShapeKind.Step or ShapeKind.Callout;

    /// <summary>
    /// トリミング後の注釈を返す。残す範囲に掛かっていない図形は落とす。
    /// </summary>
    public static AnnotationDocument Crop(AnnotationDocument source, PixelRect crop)
    {
        ArgumentNullException.ThrowIfNull(source);

        var area = crop.Normalized();
        var result = source.Clone();

        // 範囲に少しでも掛かっている図形は残す（はみ出た分は描画時に切られる）
        result.Shapes = result.Shapes
            .Where(shape => shape.HitBounds.IntersectsWith(area))
            .ToList();

        foreach (var shape in result.Shapes)
        {
            shape.Offset(-area.X, -area.Y);
        }

        return result;
    }

    /// <summary>
    /// 横帯（<paramref name="count"/> 行）を抜いて縦に詰めた注釈を返す。
    /// <see cref="ImageSurgery.RemoveRows"/> と同じ値を渡すこと。
    /// </summary>
    public static AnnotationDocument RemoveRows(AnnotationDocument source, double start, double count)
        => RemoveBand(source, start, count, vertical: true);

    /// <summary>
    /// 縦帯（<paramref name="count"/> 列）を抜いて横に詰めた注釈を返す。
    /// <see cref="ImageSurgery.RemoveColumns"/> と同じ値を渡すこと。
    /// </summary>
    public static AnnotationDocument RemoveColumns(AnnotationDocument source, double start, double count)
        => RemoveBand(source, start, count, vertical: false);

    private static AnnotationDocument RemoveBand(
        AnnotationDocument source, double start, double count, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (count <= 0)
        {
            return source.Clone();
        }

        var end = start + count;
        var result = source.Clone();
        var kept = new List<AnnotationShape>(result.Shapes.Count);

        foreach (var shape in result.Shapes)
        {
            if (IsAnchored(shape.Kind))
            {
                // 置いた点が抜く帯の中なら、注釈していた対象ごと消えるので図形も消す。
                // （帯の外に出して残すと、何を指していたのか分からない注釈になる）
                var anchor = vertical ? shape.Y : shape.X;
                if (anchor > start && anchor < end)
                {
                    continue;
                }

                // 大きさは文字から決まるので変えない。位置だけ詰める。
                var shift = Collapse(anchor, start, count) - anchor;
                if (vertical)
                {
                    shape.Y += shift;
                    shape.TailY = Collapse(shape.TailY, start, count);
                }
                else
                {
                    shape.X += shift;
                    shape.TailX = Collapse(shape.TailX, start, count);
                }

                kept.Add(shape);
                continue;
            }

            // 枠・楕円・矢印などは両端をそれぞれ写す。
            // 帯をまたぐ図形は、消えた内容の分だけ継ぎ目で縮む。
            if (vertical)
            {
                var top = Collapse(shape.Y, start, count);
                var bottom = Collapse(shape.Y + shape.Height, start, count);
                shape.Y = top;
                shape.Height = bottom - top;
            }
            else
            {
                var left = Collapse(shape.X, start, count);
                var right = Collapse(shape.X + shape.Width, start, count);
                shape.X = left;
                shape.Width = right - left;
            }

            if (!IsDegenerate(shape))
            {
                kept.Add(shape);
            }
        }

        result.Shapes = kept;
        return result;
    }

    /// <summary>
    /// 抜いた帯の分だけ座標を詰める。帯の中にあった座標は継ぎ目へ寄せる。
    /// </summary>
    private static double Collapse(double value, double start, double count)
    {
        if (value <= start)
        {
            return value;
        }

        return value >= start + count ? value - count : start;
    }

    /// <summary>詰めた結果、線や点にまで潰れてしまった図形か。</summary>
    private static bool IsDegenerate(AnnotationShape shape)
    {
        if (shape.Kind == ShapeKind.Arrow)
        {
            var length = Math.Sqrt((shape.Width * shape.Width) + (shape.Height * shape.Height));
            return length < 2;
        }

        var bounds = shape.Bounds.Normalized();
        return bounds.Width < 2 || bounds.Height < 2;
    }
}
