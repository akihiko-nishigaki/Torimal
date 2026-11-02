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

            t.Case("設定ファイルが無ければ既定値を返す", () =>
            {
                var loaded = AppSettings.Load(Path.Combine(temp, "missing.json"));
                TestRunner.Equal(AppSettings.DefaultSaveFolder(), loaded.SaveFolder, "既定の保存先");
                TestRunner.True(loaded.CopyToClipboardOnCapture, "既定でクリップボードにコピー");
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
