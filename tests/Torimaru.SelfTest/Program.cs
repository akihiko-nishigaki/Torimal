using System.Text;
using Torimaru.Core;

namespace Torimaru.SelfTest;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Torimaru Core セルフテスト");
        Console.WriteLine(new string('=', 60));

        var t = new TestRunner();

        PixelRectTests(t);
        PngTextChunkTests(t);
        AnnotationTests(t);
        NamingTests(t);
        SettingsTests(t);
        RawImageTests(t);
        ScrollStitcherTests(t);
        HotKeyTests(t);
        ImageSurgeryTests(t);
        AnnotationTransformTests(t);
        BandSelectionTests(t);
        SavedRegionTests(t);
        StylePresetTests(t);

        return t.Report();
    }

    // ================================================================ RawImage

    private static void RawImageTests(TestRunner t)
    {
        t.Section("RawImage");

        t.Case("Create は幅 x 高さ x 4 バイトを確保する", () =>
        {
            var image = RawImage.Create(10, 5);
            TestRunner.Equal(40, image.Stride, "ストライド");
            TestRunner.Equal(200, image.Pixels.Length, "バイト数");
        });

        t.Case("不正な寸法は例外になる", () =>
        {
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => RawImage.Create(0, 5), "幅 0");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => new RawImage(new byte[100], 10, 5, 8), "ストライド不足");
            TestRunner.Throws<ArgumentException>(
                () => new RawImage(new byte[10], 10, 5, 40), "配列が小さい");
        });

        t.Case("CopyRowsFrom は行単位でコピーする", () =>
        {
            var source = RawImage.Create(4, 4);
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    source.Pixels[(y * source.Stride) + x] = (byte)(y + 1);
                }
            }

            var destination = RawImage.Create(4, 2);
            destination.CopyRowsFrom(source, 2, 0, 2);

            TestRunner.Equal((byte)3, destination.Pixels[0], "1 行目は元の 3 行目");
            TestRunner.Equal((byte)4, destination.Pixels[destination.Stride], "2 行目は元の 4 行目");
        });

        t.Case("範囲外のコピーは例外になる", () =>
        {
            var source = RawImage.Create(4, 4);
            var destination = RawImage.Create(4, 2);

            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => destination.CopyRowsFrom(source, 3, 0, 3), "元がはみ出す");
            TestRunner.Throws<ArgumentException>(
                () => RawImage.Create(8, 2).CopyRowsFrom(source, 0, 0, 2), "幅が違う");
        });

        t.Case("TakeRows は指定範囲だけを切り出す", () =>
        {
            var page = BuildPage(64, 20, blankEvery: 0);
            var slice = page.TakeRows(5, 4);

            TestRunner.Equal(4, slice.Height, "高さ");
            TestRunner.True(RowsEqual(page, 5, slice, 0, 4), "内容が一致する");
        });
    }

    // ================================================================ ScrollStitcher

    private static void ScrollStitcherTests(TestRunner t)
    {
        t.Section("ScrollStitcher");

        const int width = 200;
        const int viewport = 200;

        t.Case("既知のスクロール量を検出できる", () =>
        {
            var page = BuildPage(width, 600);
            TestRunner.Equal(37, DetectShift(page, 0, 37, viewport), "ずれ量");
        });

        t.Case("スクロールしていない場合は 0 を返す", () =>
        {
            var page = BuildPage(width, 600);
            TestRunner.Equal(0, DetectShift(page, 80, 0, viewport), "ずれ量");
        });

        t.Case("重なりが足りないほど大きいずれは検出しない", () =>
        {
            var page = BuildPage(width, 900);
            TestRunner.Equal(0, DetectShift(page, 0, 195, viewport), "ずれ量");
        });

        t.Case("余白が多いページでも検出できる", () =>
        {
            // 20 行に 1 行だけ内容がある、余白だらけのページ
            var page = BuildPage(width, 600, blankEvery: 1, contentEvery: 20);
            TestRunner.Equal(40, DetectShift(page, 0, 40, viewport), "ずれ量");
        });

        t.Case("右端のスクロールバーが変化しても検出できる", () =>
        {
            var page = BuildPage(width, 600);
            var first = page.TakeRows(0, viewport);
            var second = page.TakeRows(45, viewport);

            // 右端 12px を毎フレーム違う色で塗る（スクロールバーの動きを模す）
            PaintRightEdge(first, 12, 10);
            PaintRightEdge(second, 12, 200);

            var hashes = ScrollStitcher.ComputeRowHashes(first, 2, width - 18);
            var distinct = ScrollStitcher.MarkDistinctRows(hashes);
            var nextHashes = ScrollStitcher.ComputeRowHashes(second, 2, width - 18);

            TestRunner.Equal(45, ScrollStitcher.FindVerticalShift(hashes, distinct, nextHashes), "ずれ量");
        });

        t.Case("複数フレームを繋ぐと元のページが再現される", () =>
        {
            var page = BuildPage(width, 600);
            var stitcher = new ScrollStitcher(width, viewport);

            const int step = 60;
            var frames = 4;
            for (var i = 0; i < frames; i++)
            {
                var ok = stitcher.AddFrame(page.TakeRows(i * step, viewport), out var advanced);
                TestRunner.True(ok, $"{i + 1} 枚目を追加できる");
                TestRunner.Equal(i == 0 ? viewport : step, advanced, $"{i + 1} 枚目の進み量");
            }

            var stitched = stitcher.Build();
            var expectedHeight = viewport + (step * (frames - 1));

            TestRunner.Equal(expectedHeight, stitched.Height, "合成後の高さ");
            TestRunner.Equal(expectedHeight, stitcher.TotalHeight, "TotalHeight");
            TestRunner.Equal(frames, stitcher.FrameCount, "フレーム数");
            TestRunner.True(RowsEqual(page, 0, stitched, 0, expectedHeight), "元ページと一致する");
        });

        t.Case("同じフレームを足すと末尾として扱われる", () =>
        {
            var page = BuildPage(width, 600);
            var stitcher = new ScrollStitcher(width, viewport);

            stitcher.AddFrame(page.TakeRows(0, viewport), out _);
            var advancedAgain = stitcher.AddFrame(page.TakeRows(0, viewport), out var advanced);

            TestRunner.True(!advancedAgain, "進まない");
            TestRunner.Equal(0, advanced, "進み量 0");
            TestRunner.Equal(viewport, stitcher.TotalHeight, "高さは変わらない");
        });

        t.Case("寸法が違うフレームは例外になる", () =>
        {
            var stitcher = new ScrollStitcher(width, viewport);
            stitcher.AddFrame(BuildPage(width, viewport), out _);

            TestRunner.Throws<ArgumentException>(
                () => stitcher.AddFrame(RawImage.Create(width + 10, viewport), out _),
                "幅違い");
        });

        t.Case("1 枚も追加せずに Build すると例外になる", () =>
            TestRunner.Throws<InvalidOperationException>(
                () => new ScrollStitcher(width, viewport).Build(),
                "空の合成"));

        t.Case("MarkDistinctRows は繰り返し行を除外する", () =>
        {
            ulong[] hashes = [1, 1, 1, 2, 3];
            var distinct = ScrollStitcher.MarkDistinctRows(hashes);

            TestRunner.True(!distinct[0], "3 回出る行は珍しくない");
            TestRunner.True(distinct[3], "1 回だけの行は珍しい");
            TestRunner.True(distinct[4], "1 回だけの行は珍しい");
        });
    }

    private static int DetectShift(RawImage page, int startY, int shift, int viewport)
    {
        var first = page.TakeRows(startY, viewport);
        var second = page.TakeRows(startY + shift, viewport);

        var hashes = ScrollStitcher.ComputeRowHashes(first, 2, page.Width - 18);
        var distinct = ScrollStitcher.MarkDistinctRows(hashes);
        var nextHashes = ScrollStitcher.ComputeRowHashes(second, 2, page.Width - 18);

        return ScrollStitcher.FindVerticalShift(hashes, distinct, nextHashes);
    }

    /// <summary>
    /// 行ごとに模様が違う合成ページを作る（実際の文書やウェブページの代わり）。
    /// </summary>
    private static RawImage BuildPage(int width, int height, int blankEvery = 7, int contentEvery = 0)
    {
        var page = RawImage.Create(width, height);

        for (var y = 0; y < height; y++)
        {
            var blank = contentEvery > 0
                ? y % contentEvery != 0
                : blankEvery > 0 && y % blankEvery == 0;

            for (var x = 0; x < width; x++)
            {
                var offset = (y * page.Stride) + (x * RawImage.BytesPerPixel);
                var value = blank ? (byte)255 : (byte)(((y * 131) + (x * 7)) % 251);

                page.Pixels[offset] = value;
                page.Pixels[offset + 1] = value;
                page.Pixels[offset + 2] = value;
                page.Pixels[offset + 3] = 255;
            }
        }

        return page;
    }

    private static void PaintRightEdge(RawImage image, int bandWidth, byte value)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = image.Width - bandWidth; x < image.Width; x++)
            {
                var offset = (y * image.Stride) + (x * RawImage.BytesPerPixel);
                image.Pixels[offset] = value;
                image.Pixels[offset + 1] = value;
                image.Pixels[offset + 2] = value;
            }
        }
    }

    private static bool RowsEqual(RawImage left, int leftY, RawImage right, int rightY, int rowCount)
    {
        if (left.Width != right.Width)
        {
            return false;
        }

        var lineBytes = left.Width * RawImage.BytesPerPixel;
        for (var i = 0; i < rowCount; i++)
        {
            var a = left.Pixels.AsSpan(((leftY + i) * left.Stride), lineBytes);
            var b = right.Pixels.AsSpan(((rightY + i) * right.Stride), lineBytes);
            if (!a.SequenceEqual(b))
            {
                return false;
            }
        }

        return true;
    }

    private static void PixelRectTests(TestRunner t)
    {
        t.Section("PixelRect");

        t.Case("FromPoints はドラッグ方向に依らず正の幅・高さになる", () =>
        {
            var a = PixelRect.FromPoints(100, 200, 40, 60);
            TestRunner.Equal(40d, a.X, "X");
            TestRunner.Equal(60d, a.Y, "Y");
            TestRunner.Equal(60d, a.Width, "Width");
            TestRunner.Equal(140d, a.Height, "Height");
        });

        t.Case("Normalized は負の幅・高さを吸収する", () =>
        {
            var n = new PixelRect(100, 100, -30, -40).Normalized();
            TestRunner.Equal(70d, n.X, "X");
            TestRunner.Equal(60d, n.Y, "Y");
            TestRunner.Equal(30d, n.Width, "Width");
            TestRunner.Equal(40d, n.Height, "Height");
        });

        t.Case("Clip は画面外へのはみ出しを切り落とす", () =>
        {
            var c = new PixelRect(-10, -10, 50, 50).Clip(30, 30);
            TestRunner.Equal(0d, c.X, "X");
            TestRunner.Equal(0d, c.Y, "Y");
            TestRunner.Equal(30d, c.Width, "Width");
            TestRunner.Equal(30d, c.Height, "Height");
        });

        t.Case("Clip は完全に範囲外なら空になる", () =>
        {
            var c = new PixelRect(500, 500, 100, 100).Clip(200, 200);
            TestRunner.True(c.IsEmpty, "IsEmpty");
        });

        t.Case("ToIntegerBounds は小数を外側へ丸めて切り出し範囲を失わない", () =>
        {
            var (x, y, w, h) = new PixelRect(10.2, 20.8, 30.5, 40.1).ToIntegerBounds();
            TestRunner.Equal(10, x, "X");
            TestRunner.Equal(20, y, "Y");
            TestRunner.Equal(31, w, "Width");
            TestRunner.Equal(41, h, "Height");
        });

        t.Case("Inflate は全方向に広がる", () =>
        {
            var r = new PixelRect(10, 10, 20, 20).Inflate(5);
            TestRunner.Equal(5d, r.X, "X");
            TestRunner.Equal(30d, r.Width, "Width");
        });

        t.Case("Contains は境界を含む", () =>
        {
            var r = new PixelRect(0, 0, 10, 10);
            TestRunner.True(r.Contains(10, 10), "右下の角");
            TestRunner.True(!r.Contains(10.1, 5), "わずかに外側");
        });
    }

    private static void PngTextChunkTests(TestRunner t)
    {
        t.Section("PngTextChunk");

        t.Case("埋め込んだ文字列をそのまま取り出せる", () =>
        {
            var png = BuildMinimalPng();
            var embedded = PngTextChunk.Embed(png, "{\"hello\":1}");
            TestRunner.Equal("{\"hello\":1}", PngTextChunk.Extract(embedded), "往復");
        });

        t.Case("日本語を含む文字列が壊れずに往復する", () =>
        {
            const string text = "テスト：矢印・吹き出し＋絵文字🙂";
            var embedded = PngTextChunk.Embed(BuildMinimalPng(), text);
            TestRunner.Equal(text, PngTextChunk.Extract(embedded), "往復");
        });

        t.Case("生成された PNG の全チャンク CRC が正しい", () =>
        {
            var embedded = PngTextChunk.Embed(BuildMinimalPng(), "payload");
            var types = VerifyChunks(embedded);
            TestRunner.True(types.Contains("tEXt"), "tEXt が存在する");
        });

        t.Case("IEND が最後のチャンクである", () =>
        {
            var embedded = PngTextChunk.Embed(BuildMinimalPng(), "payload");
            var types = VerifyChunks(embedded);
            TestRunner.Equal("IEND", types[^1], "最終チャンク");
        });

        t.Case("再埋め込みでチャンクが重複しない", () =>
        {
            var once = PngTextChunk.Embed(BuildMinimalPng(), "first");
            var twice = PngTextChunk.Embed(once, "second");

            var count = VerifyChunks(twice).Count(type => type == "tEXt");
            TestRunner.Equal(1, count, "tEXt の個数");
            TestRunner.Equal("second", PngTextChunk.Extract(twice), "上書きされた値");
        });

        t.Case("他のキーワードの tEXt は保持される", () =>
        {
            var withOther = PngTextChunk.Embed(BuildMinimalPng(), "other-value", "OtherKeyword");
            var withBoth = PngTextChunk.Embed(withOther, "mine");

            TestRunner.Equal("other-value", PngTextChunk.Extract(withBoth, "OtherKeyword"), "他キーワード");
            TestRunner.Equal("mine", PngTextChunk.Extract(withBoth), "自キーワード");
        });

        t.Case("旧名時代のキーワードも読める", () =>
        {
            // 改名前 (ScreenCaptureData) に保存した PNG を再編集できることが要件
            var legacy = PngTextChunk.Embed(
                BuildMinimalPng(), "{\"legacy\":1}", PngTextChunk.LegacyKeyword);

            TestRunner.Null(PngTextChunk.Extract(legacy), "現行キーワードでは見つからない");
            TestRunner.Equal("{\"legacy\":1}", PngTextChunk.ExtractWithLegacy(legacy), "旧キーワード経由");
        });

        t.Case("現行キーワードがあれば旧キーワードより優先される", () =>
        {
            var legacy = PngTextChunk.Embed(
                BuildMinimalPng(), "old", PngTextChunk.LegacyKeyword);
            var both = PngTextChunk.Embed(legacy, "new");

            TestRunner.Equal("new", PngTextChunk.ExtractWithLegacy(both), "現行を優先");
        });

        t.Case("空文字列も往復する", () =>
        {
            var embedded = PngTextChunk.Embed(BuildMinimalPng(), string.Empty);
            TestRunner.Equal(string.Empty, PngTextChunk.Extract(embedded), "空文字列");
        });

        t.Case("注釈データが無い PNG では null を返す", () =>
            TestRunner.Null(PngTextChunk.Extract(BuildMinimalPng()), "未埋め込み"));

        t.Case("PNG でないデータには null を返す", () =>
        {
            TestRunner.Null(PngTextChunk.Extract([1, 2, 3]), "短すぎるデータ");
            TestRunner.Null(PngTextChunk.Extract(Encoding.ASCII.GetBytes("GIF89a-not-a-png!")), "別形式");
            TestRunner.Null(PngTextChunk.Extract(null), "null");
        });

        t.Case("IsPng が署名を正しく判定する", () =>
        {
            TestRunner.True(PngTextChunk.IsPng(BuildMinimalPng()), "正しい PNG");
            TestRunner.True(!PngTextChunk.IsPng([0x89, 0x50]), "途中で切れたデータ");
        });

        t.Case("PNG でないデータへの埋め込みは例外になる", () =>
            TestRunner.Throws<ArgumentException>(
                () => PngTextChunk.Embed([0, 1, 2], "x"),
                "署名チェック"));

        t.Case("不正なキーワードは例外になる", () =>
        {
            TestRunner.Throws<ArgumentException>(
                () => PngTextChunk.Embed(BuildMinimalPng(), "x", string.Empty),
                "空キーワード");
            TestRunner.Throws<ArgumentException>(
                () => PngTextChunk.Embed(BuildMinimalPng(), "x", new string('a', 80)),
                "80 文字のキーワード");
            TestRunner.Throws<ArgumentException>(
                () => PngTextChunk.Embed(BuildMinimalPng(), "x", "日本語キー"),
                "非 ASCII キーワード");
        });

        t.Case("切り詰められた PNG でも例外を投げず null を返す", () =>
        {
            var embedded = PngTextChunk.Embed(BuildMinimalPng(), "payload");
            var truncated = embedded[..(embedded.Length - 20)];
            PngTextChunk.Extract(truncated); // 例外を投げないことが要件
        });
    }

    private static void AnnotationTests(TestRunner t)
    {
        t.Section("AnnotationDocument");

        t.Case("図形を含むドキュメントが JSON で往復する", () =>
        {
            var doc = new AnnotationDocument
            {
                OriginalRelativePath = ".originals/a.png",
                NextStepNumber = 4,
                Shapes =
                [
                    new AnnotationShape
                    {
                        Kind = ShapeKind.Arrow, X = 10, Y = 20, Width = 30, Height = -40,
                        Color = "#FF112233", Thickness = 5,
                    },
                    new AnnotationShape { Kind = ShapeKind.Text, Text = "日本語のメモ", FontSize = 18 },
                    new AnnotationShape { Kind = ShapeKind.Step, Number = 3 },
                ],
            };

            var restored = AnnotationDocument.FromJson(doc.ToJson());
            TestRunner.NotNull(restored, "復元結果");
            TestRunner.Equal(3, restored!.Shapes.Count, "図形数");
            TestRunner.Equal(4, restored.NextStepNumber, "次の連番");
            TestRunner.Equal(".originals/a.png", restored.OriginalRelativePath, "オリジナルパス");
            TestRunner.Equal(-40d, restored.Shapes[0].Height, "負の高さが保持される");
            TestRunner.Equal("#FF112233", restored.Shapes[0].Color, "色");
            TestRunner.Equal("日本語のメモ", restored.Shapes[1].Text, "日本語テキスト");
            TestRunner.Equal(3, restored.Shapes[2].Number, "ステップ番号");
        });

        t.Case("未知の図形種類は読み飛ばされる", () =>
        {
            const string json = """
            {"Version":1,"Shapes":[{"Kind":"Rectangle"},{"Kind":"FutureTool"},{"Kind":"Arrow"}]}
            """;
            var restored = AnnotationDocument.FromJson(json);
            TestRunner.NotNull(restored, "復元結果");
            TestRunner.Equal(2, restored!.Shapes.Count, "既知の図形のみ残る");
        });

        t.Case("壊れた JSON は null を返す（例外を投げない）", () =>
        {
            TestRunner.Null(AnnotationDocument.FromJson("{ this is not json"), "壊れた JSON");
            TestRunner.Null(AnnotationDocument.FromJson(string.Empty), "空文字列");
            TestRunner.Null(AnnotationDocument.FromJson(null), "null");
        });

        t.Case("NextStepNumber は 1 未満なら 1 に補正される", () =>
        {
            var restored = AnnotationDocument.FromJson("{\"NextStepNumber\":0,\"Shapes\":[]}");
            TestRunner.Equal(1, restored!.NextStepNumber, "補正後");
        });

        t.Case("Clone は深いコピーになる", () =>
        {
            var doc = new AnnotationDocument
            {
                Shapes = [new AnnotationShape { X = 1, Kind = ShapeKind.Rectangle }],
            };
            var clone = doc.Clone();
            clone.Shapes[0].X = 999;

            TestRunner.Equal(1d, doc.Shapes[0].X, "元は変化しない");
        });

        t.Case("Offset は図形を平行移動する", () =>
        {
            var shape = new AnnotationShape { X = 10, Y = 20, Width = 5, Height = 5 };
            shape.Offset(-3, 7);
            TestRunner.Equal(7d, shape.X, "X");
            TestRunner.Equal(27d, shape.Y, "Y");
            TestRunner.Equal(5d, shape.Width, "幅は変わらない");
        });

        t.Case("Offset は吹き出しのしっぽも一緒に動かす", () =>
        {
            var shape = new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 100, Y = 100, Width = 80, Height = 30,
                TailX = 110, TailY = 160,
            };

            shape.Offset(20, -5);

            TestRunner.Equal(120d, shape.X, "本体 X");
            TestRunner.Equal(130d, shape.TailX, "しっぽ X");
            TestRunner.Equal(155d, shape.TailY, "しっぽ Y");
        });

        t.Case("ResetTail は本体の下側に先端を置く", () =>
        {
            var shape = new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 200, Y = 50, Width = 100, Height = 40,
                FontSize = 20,
            };

            shape.ResetTail();

            TestRunner.True(shape.TailY > shape.Y + shape.Height, "本体より下にある");
            TestRunner.True(
                shape.TailX > shape.X && shape.TailX < shape.X + shape.Width,
                "本体の横幅の内側から生える");
        });

        t.Case("吹き出しは既知の種類として扱われる", () =>
        {
            TestRunner.True(ShapeKind.IsKnown(ShapeKind.Callout), "IsKnown");
            TestRunner.True(
                Array.IndexOf(ShapeKind.All, ShapeKind.Callout) >= 0, "All に含まれる");
        });

        t.Case("吹き出しの形は未設定・未知なら角丸になる", () =>
        {
            TestRunner.Equal(CalloutShapes.Round, CalloutShapes.Normalize(null), "未設定");
            TestRunner.Equal(CalloutShapes.Round, CalloutShapes.Normalize("Nope"), "未知の値");
            TestRunner.Equal(CalloutShapes.Oval, CalloutShapes.Normalize(CalloutShapes.Oval), "楕円");
            TestRunner.Equal(CalloutShapes.Rect, CalloutShapes.Normalize(CalloutShapes.Rect), "四角");
        });

        t.Case("吹き出しの地色は未設定・未知なら色で塗りになる", () =>
        {
            TestRunner.Equal(CalloutStyles.Solid, CalloutStyles.Normalize(null), "未設定");
            TestRunner.Equal(CalloutStyles.Solid, CalloutStyles.Normalize("Nope"), "未知の値");
            TestRunner.Equal(CalloutStyles.Outline, CalloutStyles.Normalize(CalloutStyles.Outline), "白地");
            TestRunner.Equal(CalloutStyles.Dark, CalloutStyles.Normalize(CalloutStyles.Dark), "黒地");
        });

        t.Case("Clone は吹き出しの形・地色も引き継ぐ", () =>
        {
            var shape = new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                CalloutShape = CalloutShapes.Oval,
                CalloutStyle = CalloutStyles.Dark,
            };

            var copy = shape.Clone();
            TestRunner.Equal(CalloutShapes.Oval, copy.CalloutShape, "形");
            TestRunner.Equal(CalloutStyles.Dark, copy.CalloutStyle, "地色");
        });

        t.Case("HitBounds は細い図形でも掴める大きさを持つ", () =>
        {
            var line = new AnnotationShape { X = 50, Y = 50, Width = 100, Height = 0 };
            var hit = line.HitBounds;
            TestRunner.True(hit.Height >= 10, "最低の高さが確保される");
            TestRunner.True(hit.Contains(100, 50), "線上の点を含む");
        });

        t.Case("ShapeKind.IsKnown が全ツールを認識する", () =>
        {
            foreach (var kind in ShapeKind.All)
            {
                TestRunner.True(ShapeKind.IsKnown(kind), kind);
            }

            TestRunner.True(!ShapeKind.IsKnown("Nope"), "未知の種類");
            TestRunner.True(!ShapeKind.IsKnown(null), "null");
        });
    }

    private static void NamingTests(TestRunner t)
    {
        t.Section("Callout の JSON 往復");

        t.Case("しっぽの座標が JSON 往復で保持される", () =>
        {
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 12, Y = 34, Width = 120, Height = 44,
                Text = "ここを見て",
                TailX = 40.5, TailY = 110.25,
            });

            var back = AnnotationDocument.FromJson(doc.ToJson());
            var shape = back?.Shapes.FirstOrDefault();

            TestRunner.Equal(ShapeKind.Callout, shape?.Kind, "種類");
            TestRunner.Equal("ここを見て", shape?.Text, "本文");
            TestRunner.Equal(40.5, shape?.TailX, "しっぽ X");
            TestRunner.Equal(110.25, shape?.TailY, "しっぽ Y");
        });

        t.Case("吹き出しの形・地色が JSON 往復で保持される", () =>
        {
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 5, Y = 6, Width = 100, Height = 40,
                Text = "楕円の白地",
                CalloutShape = CalloutShapes.Oval,
                CalloutStyle = CalloutStyles.Outline,
            });

            var shape = AnnotationDocument.FromJson(doc.ToJson())?.Shapes.FirstOrDefault();

            TestRunner.Equal(CalloutShapes.Oval, shape?.CalloutShape, "形");
            TestRunner.Equal(CalloutStyles.Outline, shape?.CalloutStyle, "地色");
        });

        t.Case("書体が JSON 往復で保持される", () =>
        {
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 1, Y = 2,
                Text = "書体テスト",
                FontFamily = "MS Gothic",
                FontSize = 32,
            });

            var shape = AnnotationDocument.FromJson(doc.ToJson())?.Shapes.FirstOrDefault();

            TestRunner.Equal("MS Gothic", shape?.FontFamily, "書体");
            TestRunner.Equal(32d, shape?.FontSize, "大きさ");
        });

        t.Case("書体を持たない旧バージョンの注釈は null のまま読める", () =>
        {
            // null = 既定の書体。ここで既定名を埋めてしまうと、
            // 書体を選べる前に保存した PNG の見た目が変わってしまう。
            const string legacy =
                "{\"Version\":1,\"NextStepNumber\":1,\"Shapes\":[{\"Kind\":\"Text\","
                + "\"X\":5,\"Y\":5,\"Text\":\"旧\",\"FontSize\":24}]}";

            var shape = AnnotationDocument.FromJson(legacy)?.Shapes.FirstOrDefault();

            TestRunner.Equal("旧", shape?.Text, "本文");
            TestRunner.Null(shape?.FontFamily, "書体は未設定");
        });

        t.Case("形・地色を持たない旧バージョンの JSON も既定として読める", () =>
        {
            // 形・地色を追加する前に保存した PNG を開いても壊れないこと
            const string legacy =
                "{\"Version\":1,\"NextStepNumber\":1,\"Shapes\":[{\"Kind\":\"Callout\","
                + "\"X\":10,\"Y\":20,\"Width\":120,\"Height\":44,\"Text\":\"旧ファイル\","
                + "\"TailX\":30,\"TailY\":90}]}";

            var shape = AnnotationDocument.FromJson(legacy)?.Shapes.FirstOrDefault();

            TestRunner.Equal("旧ファイル", shape?.Text, "本文");
            TestRunner.Equal(30d, shape?.TailX, "しっぽ X");
            TestRunner.Null(shape?.CalloutShape, "形は未設定");
            TestRunner.Equal(CalloutShapes.Round, CalloutShapes.Normalize(shape?.CalloutShape), "既定は角丸");
            TestRunner.Equal(CalloutStyles.Solid, CalloutStyles.Normalize(shape?.CalloutStyle), "既定は色で塗り");
        });

        t.Section("CaptureNaming");

        var stamp = new DateTime(2026, 10, 31, 18, 24, 39, DateTimeKind.Local);

        t.Case("日時からファイル名を作る", () =>
        {
            var path = CaptureNaming.BuildUniquePath(@"C:\shots", stamp, exists: _ => false);
            TestRunner.Equal("2026-10-31_18-24-39.png", Path.GetFileName(path), "ファイル名");
        });

        t.Case("既存ファイルがあれば連番を付ける", () =>
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(@"C:\shots", "2026-10-31_18-24-39.png"),
                Path.Combine(@"C:\shots", "2026-10-31_18-24-39-2.png"),
            };

            var path = CaptureNaming.BuildUniquePath(@"C:\shots", stamp, exists: taken.Contains);
            TestRunner.Equal("2026-10-31_18-24-39-3.png", Path.GetFileName(path), "3 番目");
        });

        t.Case("空のフォルダ名は例外になる", () =>
            TestRunner.Throws<ArgumentException>(
                () => CaptureNaming.BuildUniquePath("  ", stamp),
                "フォルダ検証"));

        t.Case("オリジナル画像は .originals サブフォルダに置かれる", () =>
        {
            var flattened = Path.Combine(@"C:\shots", "a.png");
            TestRunner.Equal(
                Path.Combine(@"C:\shots", ".originals", "a.png"),
                CaptureNaming.OriginalPathFor(flattened),
                "オリジナルパス");
            TestRunner.Equal(
                ".originals/a.png",
                CaptureNaming.OriginalRelativePathFor(flattened),
                "埋め込み用の相対パス");
        });

        t.Case("相対パスは元 PNG の場所を基準に解決される", () =>
        {
            var resolved = CaptureNaming.ResolveOriginalPath(
                Path.Combine(@"C:\shots", "a.png"), ".originals/a.png");
            TestRunner.Equal(Path.Combine(@"C:\shots", ".originals", "a.png"), resolved, "解決結果");
        });

        t.Case("相対パスが無ければ null を返す", () =>
            TestRunner.Null(
                CaptureNaming.ResolveOriginalPath(Path.Combine(@"C:\shots", "a.png"), null),
                "未設定"));

        t.Case("ファイル名に使えない文字を置き換える", () =>
        {
            TestRunner.Equal("a-b-c", CaptureNaming.Sanitize("a:b*c"), "記号の置換");
            TestRunner.Equal("日本語", CaptureNaming.Sanitize("日本語"), "日本語は保持");
            TestRunner.Equal(string.Empty, CaptureNaming.Sanitize(string.Empty), "空文字列");
        });
    }

    private static void SettingsTests(TestRunner t)
    {
        t.Section("AppSettings");

        var temp = Path.Combine(Path.GetTempPath(), "TorimaruSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "settings.json");

        try
        {
            t.Case("保存した設定を読み戻せる", () =>
            {
                new AppSettings
                {
                    SaveFolder = @"D:\caps",
                    CaptureCursor = true,
                    MagnifierZoom = 6,
                    DefaultColor = "#FF00FF00",
                }.Save(path);

                var loaded = AppSettings.Load(path);
                TestRunner.Equal(@"D:\caps", loaded.SaveFolder, "保存先");
                TestRunner.True(loaded.CaptureCursor, "カーソル設定");
                TestRunner.Equal(6, loaded.MagnifierZoom, "ルーペ倍率");
                TestRunner.Equal("#FF00FF00", loaded.DefaultColor, "既定色");
            });

            t.Case("前回の範囲とプリセットを保存して読み戻せる", () =>
            {
                var file = Path.Combine(temp, "memory.json");
                var saved = new AppSettings
                {
                    LastRegion = new SavedRegion { X = -120, Y = 40, Width = 800, Height = 600 },
                    StylePresets =
                    [
                        new StylePreset
                        {
                            Name = "太い赤枠",
                            Tool = ShapeKind.Rectangle,
                            Color = "#FFE53935",
                            Thickness = 8,
                        },
                    ],
                };

                saved.Save(file);
                var loaded = AppSettings.Load(file);

                TestRunner.NotNull(loaded.LastRegion, "範囲が残る");
                TestRunner.Equal(-120, loaded.LastRegion!.X, "左（負の座標も保つ）");
                TestRunner.Equal(600, loaded.LastRegion.Height, "高さ");

                TestRunner.NotNull(loaded.StylePresets, "プリセットが残る");
                TestRunner.Equal(1, loaded.StylePresets!.Count, "件数");
                TestRunner.Equal("太い赤枠", loaded.StylePresets[0].Name, "名前");
                TestRunner.Near(8, loaded.StylePresets[0].Thickness, 0.001, "太さ");
            });

            t.Case("0 ピクセルの範囲は覚えていないことにする", () =>
            {
                var file = Path.Combine(temp, "emptyregion.json");
                new AppSettings
                {
                    LastRegion = new SavedRegion { X = 10, Y = 10, Width = 0, Height = 0 },
                }.Save(file);

                TestRunner.Null(AppSettings.Load(file).LastRegion, "読み戻すと消えている");
            });

            t.Case("プリセットを作っていない状態と、全部消した状態を区別する", () =>
            {
                // null は「初期セットを入れてよい」、空リストは「要らない」という意思表示
                var untouched = Path.Combine(temp, "nopresets.json");
                new AppSettings().Save(untouched);
                TestRunner.Null(AppSettings.Load(untouched).StylePresets, "未設定は null のまま");

                var cleared = Path.Combine(temp, "clearedpresets.json");
                new AppSettings { StylePresets = [] }.Save(cleared);

                var loaded = AppSettings.Load(cleared);
                TestRunner.NotNull(loaded.StylePresets, "空リストは残る");
                TestRunner.Equal(0, loaded.StylePresets!.Count, "空のまま");
            });

            t.Case("一度も作っていなければ初期セットが入る", () =>
            {
                var settings = new AppSettings();
                TestRunner.Null(settings.StylePresets, "最初は未設定");

                var presets = settings.EnsureStylePresets();
                TestRunner.True(presets.Count > 0, "初期セットが入る");
                TestRunner.NotNull(settings.StylePresets, "設定にも入る");

                // 全部消した状態を呼び直しても埋め戻さない
                settings.StylePresets = [];
                TestRunner.Equal(0, settings.EnsureStylePresets().Count, "空のまま");
            });

            t.Case("壊れたプリセットは読み込み時に落ちる", () =>
            {
                var file = Path.Combine(temp, "brokenpresets.json");
                new AppSettings
                {
                    StylePresets =
                    [
                        new StylePreset { Name = "使える", Tool = ShapeKind.Arrow },
                        new StylePreset { Name = string.Empty, Tool = ShapeKind.Arrow },
                        new StylePreset { Name = "謎ツール", Tool = "Nope" },
                    ],
                }.Save(file);

                var loaded = AppSettings.Load(file).StylePresets;
                TestRunner.NotNull(loaded, "一覧がある");
                TestRunner.Equal(2, loaded!.Count, "名前の無いものだけ落ちる");
                TestRunner.Equal(ShapeKind.Rectangle, loaded[1].Tool, "知らないツールは枠へ寄る");
            });

            t.Case("設定ファイルが無ければ既定値を返す", () =>
            {
                var loaded = AppSettings.Load(Path.Combine(temp, "missing.json"));
                TestRunner.Equal(AppSettings.DefaultSaveFolder(), loaded.SaveFolder, "既定の保存先");
                TestRunner.True(loaded.CopyToClipboardOnCapture, "既定でクリップボードにコピー");
                TestRunner.Equal(AfterCaptureModes.Workspace, loaded.AfterCapture, "既定はワークスペースを出す");
                TestRunner.Equal(3, loaded.WorkspaceAutoHideSeconds, "3 秒で消える");
            });

            t.Case("撮影後の動作は知らない値なら既定へ戻る", () =>
            {
                var file = Path.Combine(temp, "aftercapture.json");
                File.WriteAllText(
                    file,
                    """{ "AfterCapture": "Nope", "WorkspaceAutoHideSeconds": 999, "OpenEditorAfterCapture": true }""");

                var loaded = AppSettings.Load(file);
                TestRunner.Equal(AfterCaptureModes.Workspace, loaded.AfterCapture, "知らない値は既定");
                TestRunner.Equal(60, loaded.WorkspaceAutoHideSeconds, "秒数の上限");

                var editor = new AppSettings { AfterCapture = AfterCaptureModes.Editor, WorkspaceAutoHideSeconds = -1 }
                    .Sanitized();
                TestRunner.Equal(AfterCaptureModes.Editor, editor.AfterCapture, "正しい値はそのまま");
                TestRunner.Equal(0, editor.WorkspaceAutoHideSeconds, "負の秒数は 0（消さない）");
            });

            t.Case("壊れた設定ファイルでも落ちずに既定値を返す", () =>
            {
                var broken = Path.Combine(temp, "broken.json");
                File.WriteAllText(broken, "{ not json at all");
                TestRunner.Equal(
                    AppSettings.DefaultSaveFolder(),
                    AppSettings.Load(broken).SaveFolder,
                    "既定の保存先へフォールバック");
            });

            t.Case("範囲外の値は補正される", () =>
            {
                var settings = new AppSettings
                {
                    SaveFolder = "   ",
                    FileNamePattern = string.Empty,
                    DefaultThickness = 500,
                    MagnifierZoom = 99,
                    DefaultColor = string.Empty,
                }.Sanitized();

                TestRunner.Equal(AppSettings.DefaultSaveFolder(), settings.SaveFolder, "保存先");
                TestRunner.Equal("yyyy-MM-dd_HH-mm-ss", settings.FileNamePattern, "日時書式");
                TestRunner.Equal(40d, settings.DefaultThickness, "線の太さ上限");
                TestRunner.Equal(20, settings.MagnifierZoom, "ルーペ倍率上限");
                TestRunner.Equal("#FFE53935", settings.DefaultColor, "既定色");
            });

            t.Case("書体の既定値と補正", () =>
            {
                var fresh = new AppSettings();
                TestRunner.Equal("Yu Gothic UI", fresh.DefaultFontFamily, "既定の書体");
                TestRunner.Equal(24d, fresh.DefaultFontSize, "既定の大きさ");

                var fixedUp = new AppSettings
                {
                    DefaultFontFamily = "   ",
                    DefaultFontSize = 999,
                }.Sanitized();

                TestRunner.Equal("Yu Gothic UI", fixedUp.DefaultFontFamily, "空なら既定へ");
                TestRunner.Equal(200d, fixedUp.DefaultFontSize, "大きさの上限");
            });

            t.Case("お気に入りの書体は null と空リストを区別して保存される", () =>
            {
                // null = まだ決めていない（定番を並べる） / 空 = ひとつも要らない。
                // ここを同一視すると、全部外したのに次回また定番が並んでしまう。
                var untouched = Path.Combine(temp, "fav-null.json");
                new AppSettings().Save(untouched);
                TestRunner.Null(AppSettings.Load(untouched).FavoriteFonts, "既定は null");

                var emptied = Path.Combine(temp, "fav-empty.json");
                new AppSettings { FavoriteFonts = [] }.Save(emptied);

                var loaded = AppSettings.Load(emptied).FavoriteFonts;
                TestRunner.NotNull(loaded, "空リストは残る");
                TestRunner.Equal(0, loaded!.Count, "件数");
            });

            t.Case("お気に入りの書体が設定ファイルに残る", () =>
            {
                var favPath = Path.Combine(temp, "fav.json");
                new AppSettings
                {
                    FavoriteFonts = ["Meiryo UI", "MS Gothic"],
                }.Save(favPath);

                var loaded = AppSettings.Load(favPath).FavoriteFonts;
                TestRunner.Equal(2, loaded?.Count, "件数");
                TestRunner.Equal("Meiryo UI", loaded?[0], "1 件目（並び順も保つ）");
                TestRunner.Equal("MS Gothic", loaded?[1], "2 件目");
            });

            t.Case("お気に入りの重複・空白・入れすぎは補正される", () =>
            {
                var many = Enumerable.Range(0, 60).Select(i => "Font" + i).ToList();
                many.Insert(0, "  Meiryo  ");
                many.Insert(1, "meiryo");
                many.Insert(2, "   ");

                var settings = new AppSettings { FavoriteFonts = many }.Sanitized();
                var favorites = settings.FavoriteFonts!;

                TestRunner.Equal("Meiryo", favorites[0], "前後の空白を落とす");
                TestRunner.True(
                    !favorites.Skip(1).Any(f => string.Equals(f, "meiryo", StringComparison.OrdinalIgnoreCase)),
                    "大文字小文字違いの重複を除く");
                TestRunner.True(!favorites.Contains("   "), "空白だけの項目を除く");
                TestRunner.Equal(40, favorites.Count, "上限 40 件");
            });

            t.Case("選んだ書体と大きさが設定ファイルに残る", () =>
            {
                var fontPath = Path.Combine(temp, "font.json");
                new AppSettings
                {
                    DefaultFontFamily = "Meiryo UI",
                    DefaultFontSize = 40,
                }.Save(fontPath);

                var loaded = AppSettings.Load(fontPath);
                TestRunner.Equal("Meiryo UI", loaded.DefaultFontFamily, "書体");
                TestRunner.Equal(40d, loaded.DefaultFontSize, "大きさ");
            });

            t.Case("スクロールと録画の設定も範囲内へ補正される", () =>
            {
                var settings = new AppSettings
                {
                    DelaySeconds = 0,
                    ScrollNotches = 99,
                    ScrollSettleMs = 1,
                    ScrollMaxFrames = 9999,
                    VideoFps = 240,
                    VideoCrf = -5,
                }.Sanitized();

                TestRunner.Equal(1, settings.DelaySeconds, "遅延の下限");
                TestRunner.Equal(10, settings.ScrollNotches, "ホイール刻みの上限");
                TestRunner.Equal(60, settings.ScrollSettleMs, "待ち時間の下限");
                TestRunner.Equal(400, settings.ScrollMaxFrames, "最大枚数の上限");
                TestRunner.Equal(60, settings.VideoFps, "フレームレートの上限");
                TestRunner.Equal(0, settings.VideoCrf, "画質の下限");
            });

            t.Case("動画の設定も保存して読み戻せる", () =>
            {
                var videoPath = Path.Combine(temp, "video.json");
                new AppSettings
                {
                    VideoFps = 24,
                    VideoCrf = 18,
                    RecordSystemAudio = false,
                    RecordMicrophone = true,
                    MicrophoneDeviceId = "{device-id}",
                    FfmpegPath = @"D:\tools\ffmpeg.exe",
                }.Save(videoPath);

                var loaded = AppSettings.Load(videoPath);
                TestRunner.Equal(24, loaded.VideoFps, "フレームレート");
                TestRunner.Equal(18, loaded.VideoCrf, "画質");
                TestRunner.True(!loaded.RecordSystemAudio, "システム音声");
                TestRunner.True(loaded.RecordMicrophone, "マイク");
                TestRunner.Equal("{device-id}", loaded.MicrophoneDeviceId, "マイクのデバイス ID");
                TestRunner.Equal(@"D:\tools\ffmpeg.exe", loaded.FfmpegPath, "ffmpeg のパス");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗はテスト結果に影響させない
            }
        }
    }

    /// <summary>
    /// 構造的に正しい最小の PNG を組み立てる（1x1・CRC も正しい）。
    /// CRC は本体コードとは独立に計算しているので、相互チェックになる。
    /// </summary>
    private static byte[] BuildMinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: 1x1, 8bit, truecolor
        byte[] ihdr = [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0];
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT: zlib ストリーム（非圧縮ブロック）
        byte[] idat = [0x78, 0x01, 0x01, 0x04, 0x00, 0xFB, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x02, 0x00, 0x01];
        WriteChunk(ms, "IDAT", idat);

        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
        stream.Write(length);

        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, 0, 4, typeAndData, 0);
        Buffer.BlockCopy(data, 0, typeAndData, 4, data.Length);
        stream.Write(typeAndData);

        var crc = Crc32(typeAndData);
        Span<byte> crcBytes = [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
        stream.Write(crcBytes);
    }

    /// <summary>全チャンクを歩いて CRC を検証し、型名の並びを返す。</summary>
    private static List<string> VerifyChunks(byte[] png)
    {
        var types = new List<string>();
        var offset = 8;

        while (offset + 12 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            if (length < 0 || offset + 12 + length > png.Length)
            {
                throw new TestRunner.AssertionException($"チャンク長が不正です (offset={offset}, length={length})");
            }

            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var typeAndData = new byte[4 + length];
            Buffer.BlockCopy(png, offset + 4, typeAndData, 0, 4 + length);

            var expected = Crc32(typeAndData);
            var actual = ((uint)png[offset + 8 + length] << 24)
                         | ((uint)png[offset + 9 + length] << 16)
                         | ((uint)png[offset + 10 + length] << 8)
                         | png[offset + 11 + length];

            if (expected != actual)
            {
                throw new TestRunner.AssertionException($"{type} チャンクの CRC が一致しません");
            }

            types.Add(type);
            offset += 12 + length;
        }

        if (offset != png.Length)
        {
            throw new TestRunner.AssertionException("末尾に余分なバイトがあります");
        }

        return types;
    }

    // ================================================================ ホットキー

    private static void HotKeyTests(TestRunner t)
    {
        t.Section("ホットキーの割り当て");

        t.Case("キー文字列を解釈して同じ文字列に戻せる", () =>
        {
            foreach (var text in new[]
                     {
                         "Ctrl+Shift+S", "PrintScreen", "Ctrl+Alt+F12",
                         "Ctrl+Shift+Alt+Win+Num7", "Shift+Delete", "Alt+Minus",
                     })
            {
                TestRunner.True(HotKeySpec.TryParse(text, out var spec), $"解釈: {text}");
                TestRunner.Equal(text, spec.ToString(), $"往復: {text}");
            }
        });

        t.Case("書き方のゆらぎを吸収して正規化する", () =>
        {
            TestRunner.True(HotKeySpec.TryParse("ctrl + shift + s", out var lower), "小文字と空白");
            TestRunner.Equal("Ctrl+Shift+S", lower.ToString(), "正規化");

            TestRunner.True(HotKeySpec.TryParse("CONTROL+Esc", out var alias), "別名 Control / Esc");
            TestRunner.Equal("Ctrl+Escape", alias.ToString(), "正規化");

            TestRunner.True(HotKeySpec.TryParse("Windows+Shift+PrtScn", out var win), "別名 Windows / PrtScn");
            TestRunner.Equal("Shift+Win+PrintScreen", win.ToString(), "修飾キーの並びも揃う");
        });

        t.Case("解釈できない指定は拒否する（例外は投げない）", () =>
        {
            foreach (var text in new string?[] { null, "", "   ", "Ctrl", "Ctrl+Shift", "Ctrl+A+B", "Ctrl+Nope" })
            {
                TestRunner.True(!HotKeySpec.TryParse(text, out _), $"拒否: <{text}>");
            }
        });

        t.Case("修飾キー無しの文字キーは使わせない", () =>
        {
            // 単独で奪うと、どのアプリでもその文字が打てなくなる
            TestRunner.NotNull(
                new HotKeySpec(HotKeyModifiers.None, 'S').Validate(), "S 単独は理由付きで拒否");
            TestRunner.NotNull(
                new HotKeySpec(HotKeyModifiers.None, HotKeySpec.VkF1).Validate(), "F1 単独も拒否");

            // 文字入力に使わないキーは単独でも許す（既定の PrintScreen がこれ）
            TestRunner.Null(
                new HotKeySpec(HotKeyModifiers.None, HotKeySpec.VkSnapshot).Validate(), "PrintScreen 単独");
            TestRunner.Null(
                new HotKeySpec(HotKeyModifiers.None, HotKeySpec.VkF13).Validate(), "F13 単独");

            TestRunner.Null(
                new HotKeySpec(HotKeyModifiers.Control | HotKeyModifiers.Shift, 'S').Validate(),
                "Ctrl+Shift+S は使える");
        });

        t.Case("修飾キーそのものは割り当てられない", () =>
        {
            foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0xA2, 0xA3 })
            {
                TestRunner.True(HotKeySpec.IsModifierKey(vk), $"修飾キー判定: 0x{vk:X2}");
                TestRunner.NotNull(
                    new HotKeySpec(HotKeyModifiers.Control, vk).Validate(), $"拒否: 0x{vk:X2}");
            }
        });

        t.Case("既定のキー候補はすべて解釈でき、組み合わせとして成立する", () =>
        {
            foreach (var action in HotKeyActions.All)
            {
                var specs = action.DefaultSpecs();
                TestRunner.Equal(
                    action.DefaultKeys.Length, specs.Count, $"{action.DisplayName}: 候補が全部読めた");

                foreach (var spec in specs)
                {
                    TestRunner.Null(spec.Validate(), $"{action.DisplayName}: {spec} は成立する");
                }
            }
        });

        t.Case("操作の Id と表示名は重複していない", () =>
        {
            var ids = HotKeyActions.All.Select(a => a.Id).Distinct().Count();
            var names = HotKeyActions.All.Select(a => a.DisplayName).Distinct().Count();

            TestRunner.Equal(HotKeyActions.All.Length, ids, "Id");
            TestRunner.Equal(HotKeyActions.All.Length, names, "表示名（トレイメニューの照合に使う）");
        });

        t.Case("Id から操作を引ける", () =>
        {
            TestRunner.NotNull(HotKeyActions.Find("CaptureRegion"), "既知の Id");
            TestRunner.Null(HotKeyActions.Find("NoSuchAction"), "知らない Id");
            TestRunner.Null(HotKeyActions.Find(null), "null");
        });

        var temp = Path.Combine(Path.GetTempPath(), "TorimaruSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            t.Case("割り当てを保存して読み戻せる", () =>
            {
                var path = Path.Combine(temp, "hotkeys.json");
                var settings = new AppSettings();
                settings.SetHotKey(
                    HotKeyActions.CaptureRegion.Id,
                    new HotKeySpec(HotKeyModifiers.Control | HotKeyModifiers.Alt, 'Q'));
                settings.Save(path);

                var loaded = AppSettings.Load(path);
                TestRunner.Equal(
                    "Ctrl+Alt+Q",
                    loaded.FindHotKey(HotKeyActions.CaptureRegion.Id)?.ToString(),
                    "読み戻した割り当て");

                TestRunner.Null(
                    loaded.FindHotKey(HotKeyActions.PickColor.Id), "設定していない操作は未設定のまま");
            });

            t.Case("既定に戻すと割り当てが消える", () =>
            {
                var settings = new AppSettings();
                settings.SetHotKey(HotKeyActions.ShowWorkspace.Id, new HotKeySpec(HotKeyModifiers.Win, 'J'));
                TestRunner.NotNull(settings.FindHotKey(HotKeyActions.ShowWorkspace.Id), "割り当て済み");

                settings.SetHotKey(HotKeyActions.ShowWorkspace.Id, null);
                TestRunner.Null(settings.FindHotKey(HotKeyActions.ShowWorkspace.Id), "消えている");
                TestRunner.Equal(0, settings.HotKeys.Count, "設定ファイルにも残らない");
            });

            t.Case("成立しない割り当ては読み込み時に捨てる", () =>
            {
                // 手で設定ファイルを書き換えても、効かない指定が黙って残らないこと
                var path = Path.Combine(temp, "broken-hotkeys.json");
                File.WriteAllText(
                    path,
                    """
                    {
                      "HotKeys": {
                        "CaptureRegion": "S",
                        "NoSuchAction": "Ctrl+Shift+J",
                        "PickColor": "Ctrl+Nope",
                        "ShowWorkspace": "ctrl+alt+k"
                      }
                    }
                    """);

                var loaded = AppSettings.Load(path);

                TestRunner.Null(loaded.FindHotKey("CaptureRegion"), "修飾キー無しの S は捨てる");
                TestRunner.Null(loaded.FindHotKey("PickColor"), "読めないキー名は捨てる");
                TestRunner.True(!loaded.HotKeys.ContainsKey("NoSuchAction"), "知らない操作名は捨てる");
                TestRunner.Equal(
                    "Ctrl+Alt+K",
                    loaded.FindHotKey("ShowWorkspace")?.ToString(),
                    "正しい指定は正規化して残す");
            });

            t.Case("HotKeys が null でも落ちない", () =>
            {
                var path = Path.Combine(temp, "null-hotkeys.json");
                File.WriteAllText(path, """{ "HotKeys": null }""");

                var loaded = AppSettings.Load(path);
                TestRunner.NotNull(loaded.HotKeys, "空の辞書になる");
                TestRunner.Equal(0, loaded.HotKeys.Count, "中身は空");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗でテストを落とさない
            }
        }
    }

    // ================================================================ 前回の範囲

    private static void SavedRegionTests(TestRunner t)
    {
        t.Section("前回の範囲を覚える");

        t.Case("撮った範囲を整数のピクセルで覚える", () =>
        {
            var region = SavedRegion.From(new PixelRect(100.4, 200.6, 300.2, 150.9));

            TestRunner.NotNull(region, "覚えられる");
            TestRunner.Equal(100, region!.X, "左");
            TestRunner.Equal(200, region.Y, "上");

            // 端は外側へ丸める（1 ピクセル欠けるより、含めるほうが実害が無い）
            TestRunner.Equal(301, region.Width, "幅");
            TestRunner.Equal(152, region.Height, "高さ");
        });

        t.Case("幅や高さが無い範囲は覚えない", () =>
        {
            TestRunner.Null(SavedRegion.From(new PixelRect(10, 10, 0, 50)), "幅 0");
            TestRunner.Null(SavedRegion.From(new PixelRect(10, 10, 50, 0)), "高さ 0");
        });

        t.Case("そのまま収まる範囲はそのまま返る", () =>
        {
            var saved = new SavedRegion { X = 100, Y = 50, Width = 400, Height = 300 };
            var resolved = RegionMemory.Resolve(saved, 0, 0, 1920, 1080);

            TestRunner.NotNull(resolved, "使える");
            TestRunner.Near(100, resolved!.Value.X, 0.001, "左");
            TestRunner.Near(400, resolved.Value.Width, 0.001, "幅");
            TestRunner.Null(
                RegionMemory.ExplainMismatch(saved, 0, 0, 1920, 1080), "説明は要らない");
        });

        t.Case("画面の端にぴったり接していても使える", () =>
        {
            var saved = new SavedRegion { X = 0, Y = 0, Width = 1920, Height = 1080 };
            TestRunner.NotNull(RegionMemory.Resolve(saved, 0, 0, 1920, 1080), "全画面ぴったり");
        });

        t.Case("はみ出す範囲は切らずに断る", () =>
        {
            // 黙って小さく切って返すと「前回と同じ」と言いながら別の絵が出てくる
            var saved = new SavedRegion { X = 1800, Y = 50, Width = 400, Height = 300 };

            TestRunner.Null(RegionMemory.Resolve(saved, 0, 0, 1920, 1080), "右にはみ出す");
            TestRunner.NotNull(
                RegionMemory.ExplainMismatch(saved, 0, 0, 1920, 1080), "理由が付く");
        });

        t.Case("左のモニタ（負の座標）でも覚えたまま使える", () =>
        {
            var saved = new SavedRegion { X = -1700, Y = 100, Width = 800, Height = 600 };
            var resolved = RegionMemory.Resolve(saved, -1920, 0, 3840, 1080);

            TestRunner.NotNull(resolved, "使える");
            TestRunner.Near(-1700, resolved!.Value.X, 0.001, "左");
        });

        t.Case("モニタを外して範囲が画面の外になったら断る", () =>
        {
            var saved = new SavedRegion { X = -1700, Y = 100, Width = 800, Height = 600 };

            // 左のモニタを外した後。仮想画面は 0 から始まる
            TestRunner.Null(RegionMemory.Resolve(saved, 0, 0, 1920, 1080), "もう無い場所");
        });

        t.Case("まだ覚えていなければ断る", () =>
        {
            TestRunner.Null(RegionMemory.Resolve(null, 0, 0, 1920, 1080), "未記録");
            TestRunner.NotNull(
                RegionMemory.ExplainMismatch(null, 0, 0, 1920, 1080), "未記録の説明");
        });

        t.Case("画面が取れていないときは断る", () =>
        {
            var saved = new SavedRegion { X = 0, Y = 0, Width = 10, Height = 10 };
            TestRunner.Null(RegionMemory.Resolve(saved, 0, 0, 0, 0), "画面の寸法が無い");
        });

        t.Case("設定に覚えさせると読み戻せる", () =>
        {
            var settings = new AppSettings();
            settings.RememberRegion(new PixelRect(30, 40, 500, 400));

            TestRunner.NotNull(settings.LastRegion, "覚えた");
            TestRunner.Equal(500, settings.LastRegion!.Width, "幅");

            // 成立しない範囲で上書きされない（直前に覚えた範囲を失わない）
            settings.RememberRegion(new PixelRect(30, 40, 0, 0));
            TestRunner.Equal(500, settings.LastRegion.Width, "空の範囲では消えない");
        });
    }

    // ================================================================ プリセット

    private static void StylePresetTests(TestRunner t)
    {
        t.Section("描き方のプリセット");

        t.Case("名前の前後の空白を落とし、太さを使える範囲に収める", () =>
        {
            var preset = new StylePreset
            {
                Name = "  赤枠  ",
                Tool = ShapeKind.Rectangle,
                Thickness = 999,
                FontSize = 1,
            }.Sanitized();

            TestRunner.Equal("赤枠", preset.Name, "名前");
            TestRunner.Near(20, preset.Thickness, 0.001, "太さはスライダーの上限まで");
            TestRunner.Near(8, preset.FontSize, 0.001, "文字の大きさの下限");
        });

        t.Case("知らないツールは枠として扱う", () =>
        {
            var preset = new StylePreset { Name = "謎", Tool = "NotATool" }.Sanitized();
            TestRunner.Equal(ShapeKind.Rectangle, preset.Tool, "既定のツール");
        });

        t.Case("名前が無いものはプリセットとして使えない", () =>
        {
            TestRunner.True(
                !new StylePreset { Name = "   ", Tool = ShapeKind.Arrow }.Sanitized().IsUsable,
                "空の名前");

            TestRunner.True(
                new StylePreset { Name = "矢印", Tool = ShapeKind.Arrow }.Sanitized().IsUsable,
                "名前があれば使える");
        });

        t.Case("吹き出し以外では吹き出しの指定を持たない", () =>
        {
            var preset = new StylePreset
            {
                Name = "枠",
                Tool = ShapeKind.Rectangle,
                CalloutShape = CalloutShapes.Oval,
                CalloutStyle = CalloutStyles.Dark,
            }.Sanitized();

            TestRunner.Null(preset.CalloutShape, "形");
            TestRunner.Null(preset.CalloutStyle, "地色");
        });

        t.Case("吹き出しの知らない指定は既定へ寄せる", () =>
        {
            var preset = new StylePreset
            {
                Name = "吹き出し",
                Tool = ShapeKind.Callout,
                CalloutShape = "Hexagon",
                CalloutStyle = "Neon",
            }.Sanitized();

            TestRunner.Equal(CalloutShapes.Round, preset.CalloutShape, "形");
            TestRunner.Equal(CalloutStyles.Solid, preset.CalloutStyle, "地色");
        });

        t.Case("色が違えば別の描き方", () =>
        {
            var red = new StylePreset { Name = "a", Tool = ShapeKind.Rectangle, Color = "#FFE53935" };
            var blue = new StylePreset { Name = "b", Tool = ShapeKind.Rectangle, Color = "#FF1E88E5" };

            TestRunner.True(!red.SameStyleAs(blue), "色違いは別物");
            TestRunner.True(red.SameStyleAs(red.Clone()), "写しは同じ");
        });

        t.Case("効かない項目の違いでは別物にしない", () =>
        {
            // 文字ツールに線の太さは効かない。太さ違いで二重登録させない
            var a = new StylePreset { Name = "a", Tool = ShapeKind.Text, Thickness = 3, FontSize = 24 };
            var b = new StylePreset { Name = "b", Tool = ShapeKind.Text, Thickness = 9, FontSize = 24 };
            TestRunner.True(a.SameStyleAs(b), "文字では太さを見ない");

            // 逆に、枠に書体は効かない
            var c = new StylePreset { Name = "c", Tool = ShapeKind.Rectangle, FontFamily = "Meiryo" };
            var d = new StylePreset { Name = "d", Tool = ShapeKind.Rectangle, FontFamily = "Arial" };
            TestRunner.True(c.SameStyleAs(d), "枠では書体を見ない");
        });

        t.Case("文字では書体と大きさの違いを見る", () =>
        {
            var a = new StylePreset { Name = "a", Tool = ShapeKind.Text, FontFamily = "Meiryo", FontSize = 24 };
            var b = new StylePreset { Name = "b", Tool = ShapeKind.Text, FontFamily = "Meiryo", FontSize = 40 };

            TestRunner.True(!a.SameStyleAs(b), "大きさ違い");
        });

        t.Case("使えない項目は一覧から落ちる", () =>
        {
            var list = StylePreset.SanitizeList(
            [
                new StylePreset { Name = "枠", Tool = ShapeKind.Rectangle },
                null,
                new StylePreset { Name = "   ", Tool = ShapeKind.Arrow },
            ]);

            TestRunner.Equal(1, list.Count, "残る件数");
            TestRunner.Equal("枠", list[0].Name, "残ったもの");
        });

        t.Case("同じ名前は後から来たほうに番号が付く", () =>
        {
            var list = StylePreset.SanitizeList(
            [
                new StylePreset { Name = "枠", Tool = ShapeKind.Rectangle },
                new StylePreset { Name = "枠", Tool = ShapeKind.Ellipse },
                new StylePreset { Name = "枠", Tool = ShapeKind.Arrow },
            ]);

            TestRunner.Equal(3, list.Count, "件数");
            TestRunner.Equal("枠", list[0].Name, "1 つ目");
            TestRunner.Equal("枠(2)", list[1].Name, "2 つ目");
            TestRunner.Equal("枠(3)", list[2].Name, "3 つ目");
        });

        t.Case("上限を超える分は捨てる", () =>
        {
            var many = Enumerable.Range(0, StylePreset.MaxCount + 10)
                .Select(i => new StylePreset { Name = $"p{i}", Tool = ShapeKind.Rectangle });

            TestRunner.Equal(StylePreset.MaxCount, StylePreset.SanitizeList(many).Count, "件数");
        });

        t.Case("初期セットはそのまま使える", () =>
        {
            var defaults = StylePreset.Defaults();

            TestRunner.True(defaults.Count > 0, "空ではない");
            TestRunner.True(defaults.All(p => p.Sanitized().IsUsable), "全部使える");
            TestRunner.Equal(
                defaults.Count,
                StylePreset.SanitizeList(defaults).Count,
                "整えても減らない（名前が重複していない）");
        });

        t.Case("中身の説明に効かない項目を出さない", () =>
        {
            var box = new StylePreset { Name = "枠", Tool = ShapeKind.Rectangle, Thickness = 4 }.Sanitized();
            TestRunner.True(box.Describe().Contains("太さ"), "枠には太さを出す");

            var text = new StylePreset { Name = "文字", Tool = ShapeKind.Text }.Sanitized();
            TestRunner.True(!text.Describe().Contains("太さ"), "文字に太さは出さない");

            var blur = new StylePreset { Name = "ぼかし", Tool = ShapeKind.Blur }.Sanitized();
            TestRunner.True(!blur.Describe().Contains("#"), "ぼかしに色は出さない");
        });
    }

    // ================================================================ 中抜きの確定待ち

    private static void BandSelectionTests(TestRunner t)
    {
        t.Section("中抜きの確定待ち（帯の微調整）");

        t.Case("ドラッグの向きが逆でも同じ帯になる", () =>
        {
            var down = BandSelection.FromDrag(90, 150, 300);
            var up = BandSelection.FromDrag(150, 90, 300);

            TestRunner.Equal(down, up, "向きで変わらない");
            TestRunner.Near(60, down.Thickness, 0.001, "太さ");
        });

        t.Case("画像の外へドラッグしても中へ収める", () =>
        {
            var band = BandSelection.FromDrag(-40, 500, 300);

            TestRunner.Near(0, band.Start, 0.001, "始まり");
            TestRunner.Near(300, band.End, 0.001, "終わり");
        });

        t.Case("辺の近くをつまむと、その辺を掴む", () =>
        {
            var band = new BandSelection(100, 200);

            TestRunner.Equal(BandGrip.Start, band.HitTest(102, 5), "上端の近く");
            TestRunner.Equal(BandGrip.End, band.HitTest(197, 5), "下端の近く");
        });

        t.Case("帯の内側は帯ごと、外側は掴まない", () =>
        {
            var band = new BandSelection(100, 200);

            TestRunner.Equal(BandGrip.Whole, band.HitTest(150, 5), "内側");
            TestRunner.Equal(BandGrip.None, band.HitTest(80, 5), "外側");
        });

        t.Case("細い帯でも近いほうの辺を掴める", () =>
        {
            // 両方の辺が許容範囲に入る。内側扱いになると細い帯を広げられなくなる
            var band = new BandSelection(100, 104);

            TestRunner.Equal(BandGrip.Start, band.HitTest(101, 5), "上端寄り");
            TestRunner.Equal(BandGrip.End, band.HitTest(103, 5), "下端寄り");
        });

        t.Case("辺を反対側へ追い越すと前後が入れ替わる", () =>
        {
            var band = new BandSelection(100, 200).MoveEdge(BandGrip.Start, 250, 300);

            TestRunner.Near(200, band.Start, 0.001, "始まり");
            TestRunner.Near(250, band.End, 0.001, "終わり");
        });

        t.Case("辺を動かしても画像の外へは出ない", () =>
        {
            var band = new BandSelection(100, 200).MoveEdge(BandGrip.End, 900, 300);

            TestRunner.Near(300, band.End, 0.001, "画像の端で止まる");
        });

        t.Case("帯ごと動かしても太さは変わらない", () =>
        {
            var band = new BandSelection(100, 200).MoveTo(40, 300);

            TestRunner.Near(40, band.Start, 0.001, "始まり");
            TestRunner.Near(100, band.Thickness, 0.001, "太さ");
        });

        t.Case("帯ごと動かすと画像の中で止まる", () =>
        {
            var band = new BandSelection(100, 200).MoveTo(280, 300);

            TestRunner.Near(200, band.Start, 0.001, "始まり");
            TestRunner.Near(300, band.End, 0.001, "終わり");
            TestRunner.Near(100, band.Thickness, 0.001, "太さは保たれる");
        });

        t.Case("キーで 1 px ずつずらせる", () =>
        {
            var band = new BandSelection(100, 160).Shift(-1, 300).Shift(-1, 300);

            TestRunner.Near(98, band.Start, 0.001, "始まり");
            TestRunner.Near(158, band.End, 0.001, "終わり");
        });

        t.Case("キーで太さを変えると、始まりは動かない", () =>
        {
            var band = new BandSelection(100, 160).Resize(10, 300);

            TestRunner.Near(100, band.Start, 0.001, "始まりは据え置き");
            TestRunner.Near(170, band.End, 0.001, "終わりだけ動く");
        });

        t.Case("太さを縮め続けても始まりより手前には来ない", () =>
        {
            var band = new BandSelection(100, 160).Resize(-500, 300);

            TestRunner.Near(100, band.Start, 0.001, "始まり");
            TestRunner.Near(0, band.Thickness, 0.001, "太さ 0 で止まる");
        });

        t.Case("微調整した帯はそのまま切る指示に渡せる", () =>
        {
            // 画面側で丸めず、確定時に ImageCutPlan へそのまま渡している。
            // 帯の持ち方と受付条件がずれていないかを合わせて見る。
            var band = new BandSelection(100, 160).Shift(-10, 300).Resize(4, 300);

            var planned = ImageCutPlan.TryPlanBand(band.Start, band.End, 300, out var start, out var count, out _);

            TestRunner.True(planned, "受け付けられる");
            TestRunner.Equal(90, start, "抜き始める位置");
            TestRunner.Equal(64, count, "抜く太さ");
        });

        t.Case("太さ 0 まで縮めた帯は受け付けない", () =>
        {
            var band = new BandSelection(100, 160).Resize(-60, 300);

            var planned = ImageCutPlan.TryPlanBand(
                band.Start, band.End, 300, out _, out _, out var problem);

            TestRunner.True(!planned, "切らない");
            TestRunner.NotNull(problem, "理由が付く");
        });
    }

    // ================================================================ 画像を切る

    private static void ImageSurgeryTests(TestRunner t)
    {
        t.Section("トリミングと中抜き（画像）");

        t.Case("Crop は指定範囲だけを残す", () =>
        {
            var cropped = ImageSurgery.Crop(BuildGrid(40, 30), 10, 5, 8, 6);

            TestRunner.Equal(8, cropped.Width, "幅");
            TestRunner.Equal(6, cropped.Height, "高さ");
            TestRunner.Equal((10, 5), PixelAt(cropped, 0, 0), "左上は元の (10,5)");
            TestRunner.Equal((17, 10), PixelAt(cropped, 7, 5), "右下は元の (17,10)");
        });

        t.Case("RemoveRows は帯を抜いて上下をそのまま繋ぐ", () =>
        {
            var cut = ImageSurgery.RemoveRows(BuildGrid(40, 30), 10, 5);

            TestRunner.Equal(40, cut.Width, "幅は変わらない");
            TestRunner.Equal(25, cut.Height, "5 行減る");
            TestRunner.Equal((3, 9), PixelAt(cut, 3, 9), "継ぎ目の上は元のまま");
            TestRunner.Equal((3, 15), PixelAt(cut, 3, 10), "継ぎ目の下は元の 15 行目");
            TestRunner.Equal((3, 29), PixelAt(cut, 3, 24), "最後は元の最終行");
        });

        t.Case("RemoveColumns は帯を抜いて左右をそのまま繋ぐ", () =>
        {
            var cut = ImageSurgery.RemoveColumns(BuildGrid(40, 30), 10, 5);

            TestRunner.Equal(35, cut.Width, "5 列減る");
            TestRunner.Equal(30, cut.Height, "高さは変わらない");
            TestRunner.Equal((9, 7), PixelAt(cut, 9, 7), "継ぎ目の左は元のまま");
            TestRunner.Equal((15, 7), PixelAt(cut, 10, 7), "継ぎ目の右は元の 15 列目");
            TestRunner.Equal((39, 7), PixelAt(cut, 34, 7), "最後は元の最終列");
        });

        t.Case("端の帯も抜ける", () =>
        {
            var top = ImageSurgery.RemoveRows(BuildGrid(40, 30), 0, 4);
            TestRunner.Equal(26, top.Height, "先頭 4 行を抜いた高さ");
            TestRunner.Equal((0, 4), PixelAt(top, 0, 0), "先頭は元の 4 行目");

            var bottom = ImageSurgery.RemoveRows(BuildGrid(40, 30), 26, 4);
            TestRunner.Equal(26, bottom.Height, "末尾 4 行を抜いた高さ");
            TestRunner.Equal((0, 25), PixelAt(bottom, 0, 25), "末尾は元の 25 行目");
        });

        t.Case("ドラッグした範囲は端のピクセルまで含めて切る", () =>
        {
            // 10.4 〜 25.6 をなぞったら 10〜26 を切る（内側に丸めると 1px 残る）
            TestRunner.True(
                ImageCutPlan.TryPlanBand(25.6, 10.4, 300, out var start, out var count, out var problem),
                "受け付けられる");

            TestRunner.Null(problem, "理由は出ない");
            TestRunner.Equal(10, start, "開始");
            TestRunner.Equal(16, count, "太さ");
        });

        t.Case("細すぎる帯・残りが無くなる帯は受け付けない", () =>
        {
            TestRunner.True(
                !ImageCutPlan.TryPlanBand(100, 101, 300, out _, out _, out var thin), "1 px の帯");
            TestRunner.NotNull(thin, "細すぎる理由を返す");

            TestRunner.True(
                !ImageCutPlan.TryPlanBand(0, 300, 300, out _, out _, out var everything), "全部抜く");
            TestRunner.NotNull(everything, "残らない理由を返す");

            TestRunner.True(
                !ImageCutPlan.TryPlanBand(5, 299, 300, out _, out _, out var almost), "残り 6 px");
            TestRunner.NotNull(almost, "残りが足りない理由を返す");
        });

        t.Case("画像の外へはみ出すドラッグは画像の中へ収める", () =>
        {
            TestRunner.True(
                ImageCutPlan.TryPlanBand(-50, 40, 300, out var start, out var count, out _), "左へはみ出す");
            TestRunner.Equal(0, start, "0 から");
            TestRunner.Equal(40, count, "はみ出した分は数えない");

            // 左右へはみ出したドラッグ。幅は画像いっぱいになり、高さだけ切られる
            TestRunner.True(
                ImageCutPlan.TryPlanCrop(
                    new PixelRect(-20, 50, 500, 100), 300, 200, out var crop, out _),
                "左右へはみ出す");

            TestRunner.Equal((0, 50, 300, 100), crop, "はみ出した分は画像の縁で止まる");

            // 四方へはみ出すと結果が画像全体になるので、切るものが無い
            TestRunner.True(
                !ImageCutPlan.TryPlanCrop(
                    new PixelRect(-20, -20, 500, 500), 300, 200, out _, out var whole),
                "全方向へはみ出すと画像全体になる");

            TestRunner.NotNull(whole, "何も切らない理由を返す");
        });

        t.Case("トリミングの範囲は整数へ外側に丸める", () =>
        {
            TestRunner.True(
                ImageCutPlan.TryPlanCrop(
                    new PixelRect(10.7, 20.2, 100.1, 50.9), 300, 200, out var crop, out _),
                "受け付けられる");

            // 10.7〜110.8 → 10〜111（幅 101）、20.2〜71.1 → 20〜72（高さ 52）
            TestRunner.Equal((10, 20, 101, 52), crop, "外側に丸める");
        });

        t.Case("小さすぎるトリミングと画像全体のトリミングは受け付けない", () =>
        {
            TestRunner.True(
                !ImageCutPlan.TryPlanCrop(new PixelRect(10, 10, 4, 100), 300, 200, out _, out var small),
                "幅 4 px");
            TestRunner.NotNull(small, "小さすぎる理由を返す");

            TestRunner.True(
                !ImageCutPlan.TryPlanCrop(new PixelRect(0, 0, 300, 200), 300, 200, out _, out var whole),
                "画像全体");
            TestRunner.NotNull(whole, "何も切らない理由を返す");
        });

        t.Case("受け付けた帯はそのまま画像を切れる値になっている", () =>
        {
            // 計画と実行で条件がずれていると、受け付けた直後に例外で落ちる
            var image = BuildGrid(40, 30);

            foreach (var (a, b) in new[] { (0.0, 5.0), (3.4, 9.6), (25.0, 29.9), (0.0, 21.0) })
            {
                if (ImageCutPlan.TryPlanBand(a, b, image.Height, out var start, out var count, out _))
                {
                    var cut = ImageSurgery.RemoveRows(image, start, count);
                    TestRunner.Equal(image.Height - count, cut.Height, $"({a}, {b}) の高さ");
                }
            }

            foreach (var rect in new[]
                     {
                         new PixelRect(0, 0, 20, 20),
                         new PixelRect(10.5, 5.5, 25.2, 20.1),
                         new PixelRect(-5, -5, 60, 60),
                     })
            {
                if (ImageCutPlan.TryPlanCrop(rect, image.Width, image.Height, out var crop, out _))
                {
                    var cropped = ImageSurgery.Crop(image, crop.X, crop.Y, crop.Width, crop.Height);
                    TestRunner.Equal(crop.Width, cropped.Width, $"{rect} の幅");
                }
            }
        });

        t.Case("無理な指定は例外になる（画像を壊さない）", () =>
        {
            var image = BuildGrid(40, 30);

            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.Crop(image, 35, 0, 10, 10), "はみ出すトリミング");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.Crop(image, 0, 0, 0, 10), "幅 0 のトリミング");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.RemoveRows(image, 0, 30), "全部の行を抜く");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.RemoveRows(image, 28, 5), "はみ出す帯");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.RemoveRows(image, 5, 0), "太さ 0 の帯");
            TestRunner.Throws<ArgumentOutOfRangeException>(
                () => ImageSurgery.RemoveColumns(image, 0, 40), "全部の列を抜く");
        });
    }

    /// <summary>行と列が色から分かるテスト用の画像（寸法は 256 未満にすること）。</summary>
    private static RawImage BuildGrid(int width, int height)
    {
        var image = RawImage.Create(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * image.Stride) + (x * RawImage.BytesPerPixel);
                image.Pixels[i] = (byte)x;
                image.Pixels[i + 1] = (byte)y;
                image.Pixels[i + 2] = 0;
                image.Pixels[i + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>その画素が元画像のどの (列, 行) から来たかを読み取る。</summary>
    private static (int X, int Y) PixelAt(RawImage image, int x, int y)
    {
        var i = (y * image.Stride) + (x * RawImage.BytesPerPixel);
        return (image.Pixels[i], image.Pixels[i + 1]);
    }

    // ================================================================ 注釈の付け替え

    private static void AnnotationTransformTests(TestRunner t)
    {
        t.Section("トリミングと中抜き（注釈の付け替え）");

        static AnnotationDocument DocumentWith(params AnnotationShape[] shapes)
            => new() { Shapes = [.. shapes] };

        static AnnotationShape Box(double x, double y, double w, double h)
            => new() { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h };

        t.Case("トリミングすると図形が切った分だけ寄る", () =>
        {
            var result = AnnotationTransform.Crop(
                DocumentWith(Box(100, 50, 40, 20)), new PixelRect(80, 30, 200, 100));

            TestRunner.Equal(1, result.Shapes.Count, "残る図形");
            TestRunner.Near(20, result.Shapes[0].X, 0.001, "X");
            TestRunner.Near(20, result.Shapes[0].Y, 0.001, "Y");
            TestRunner.Near(40, result.Shapes[0].Width, 0.001, "幅は変わらない");
        });

        t.Case("トリミング範囲に掛からない図形は落とす", () =>
        {
            var result = AnnotationTransform.Crop(
                DocumentWith(Box(10, 10, 20, 20)), new PixelRect(100, 100, 50, 50));

            TestRunner.Equal(0, result.Shapes.Count, "範囲外は消える");
        });

        t.Case("トリミング範囲に半分掛かる図形は残す（描画で切られる）", () =>
        {
            var result = AnnotationTransform.Crop(
                DocumentWith(Box(90, 90, 40, 40)), new PixelRect(100, 100, 50, 50));

            TestRunner.Equal(1, result.Shapes.Count, "残る");
            TestRunner.Near(-10, result.Shapes[0].X, 0.001, "範囲の外へ出る座標になる");
        });

        t.Case("吹き出しはしっぽも一緒に寄る", () =>
        {
            var callout = new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 200, Y = 100, Width = 120, Height = 40,
                TailX = 240, TailY = 180,
            };

            var result = AnnotationTransform.Crop(DocumentWith(callout), new PixelRect(50, 20, 400, 300));

            TestRunner.Near(150, result.Shapes[0].X, 0.001, "本体 X");
            TestRunner.Near(80, result.Shapes[0].Y, 0.001, "本体 Y");
            TestRunner.Near(190, result.Shapes[0].TailX, 0.001, "しっぽ X");
            TestRunner.Near(160, result.Shapes[0].TailY, 0.001, "しっぽ Y");
        });

        t.Case("元の注釈は書き換えない（元に戻せるようにする）", () =>
        {
            var source = DocumentWith(Box(100, 50, 40, 20));
            AnnotationTransform.Crop(source, new PixelRect(80, 30, 200, 100));

            TestRunner.Near(100, source.Shapes[0].X, 0.001, "呼び出し元の図形はそのまま");
        });

        // 以降は 60〜100 px の帯（太さ 40）を抜く
        t.Case("帯より下の図形は帯の分だけ上へ詰まる", () =>
        {
            var result = AnnotationTransform.RemoveRows(DocumentWith(Box(10, 200, 30, 20)), 60, 40);

            TestRunner.Equal(1, result.Shapes.Count, "残る");
            TestRunner.Near(160, result.Shapes[0].Y, 0.001, "200 - 40");
            TestRunner.Near(20, result.Shapes[0].Height, 0.001, "高さは変わらない");
        });

        t.Case("帯より上の図形は動かない", () =>
        {
            var result = AnnotationTransform.RemoveRows(DocumentWith(Box(10, 10, 30, 20)), 60, 40);
            TestRunner.Near(10, result.Shapes[0].Y, 0.001, "Y");
        });

        t.Case("帯をまたぐ枠は消えた分だけ縮む", () =>
        {
            // 40〜140 の枠から 60〜100 が消えるので 40〜100（高さ 60）になる
            var result = AnnotationTransform.RemoveRows(DocumentWith(Box(10, 40, 30, 100)), 60, 40);

            TestRunner.Equal(1, result.Shapes.Count, "残る");
            TestRunner.Near(40, result.Shapes[0].Y, 0.001, "上端は動かない");
            TestRunner.Near(60, result.Shapes[0].Height, 0.001, "高さが 40 px 縮む");
        });

        t.Case("帯の中にしか無い枠は消える", () =>
        {
            var result = AnnotationTransform.RemoveRows(DocumentWith(Box(10, 65, 30, 20)), 60, 40);
            TestRunner.Equal(0, result.Shapes.Count, "潰れた枠は残さない");
        });

        t.Case("矢印は向きを保ったまま詰まる", () =>
        {
            // (10,200) から (40,50) への右上向き矢印。100〜130 の帯を抜く。
            var arrow = new AnnotationShape
            {
                Kind = ShapeKind.Arrow, X = 10, Y = 200, Width = 30, Height = -150,
            };

            var result = AnnotationTransform.RemoveRows(DocumentWith(arrow), 100, 30);

            TestRunner.Equal(1, result.Shapes.Count, "残る");
            TestRunner.Near(170, result.Shapes[0].Y, 0.001, "始点 200 - 30");
            TestRunner.Near(-120, result.Shapes[0].Height, 0.001, "終点 50 は動かないので差分が縮む");
        });

        t.Case("文字は置いた点が消えると一緒に消える", () =>
        {
            var inside = new AnnotationShape
            {
                Kind = ShapeKind.Text, X = 10, Y = 70, Width = 100, Height = 30, Text = "消える",
            };

            var below = new AnnotationShape
            {
                Kind = ShapeKind.Text, X = 10, Y = 150, Width = 100, Height = 30, Text = "残る",
            };

            var result = AnnotationTransform.RemoveRows(DocumentWith(inside, below), 60, 40);

            TestRunner.Equal(1, result.Shapes.Count, "帯の中の文字だけ消える");
            TestRunner.Equal("残る", result.Shapes[0].Text, "残ったのは下の文字");
            TestRunner.Near(110, result.Shapes[0].Y, 0.001, "150 - 40");
            TestRunner.Near(30, result.Shapes[0].Height, 0.001, "文字の高さは縮めない");
        });

        t.Case("吹き出しのしっぽが帯の中なら継ぎ目に寄る", () =>
        {
            var callout = new AnnotationShape
            {
                Kind = ShapeKind.Callout,
                X = 10, Y = 20, Width = 120, Height = 40,
                TailX = 50, TailY = 80,
            };

            var result = AnnotationTransform.RemoveRows(DocumentWith(callout), 60, 40);

            TestRunner.Near(20, result.Shapes[0].Y, 0.001, "本体は動かない");
            TestRunner.Near(60, result.Shapes[0].TailY, 0.001, "しっぽは継ぎ目へ");
        });

        t.Case("横の中抜きも同じように詰まる", () =>
        {
            var result = AnnotationTransform.RemoveColumns(DocumentWith(Box(200, 10, 30, 20)), 50, 30);

            TestRunner.Near(170, result.Shapes[0].X, 0.001, "200 - 30");
            TestRunner.Near(10, result.Shapes[0].Y, 0.001, "Y は変わらない");
        });

        t.Case("太さ 0 の帯では何も変わらない", () =>
        {
            var result = AnnotationTransform.RemoveRows(DocumentWith(Box(10, 200, 30, 20)), 60, 0);
            TestRunner.Near(200, result.Shapes[0].Y, 0.001, "Y");
        });
    }

    private static uint Crc32(byte[] buffer)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in buffer)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFFu;
    }
}
