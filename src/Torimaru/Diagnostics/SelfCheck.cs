using System.Text;
using Torimaru.App.Capture;
using Torimaru.App.Editor;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App.Diagnostics;

/// <summary>
/// `Torimaru.exe --selftest` で走る自己診断。
///
/// GUI を操作しないと試せない部分（実画面の取り込み、全ツールの描画、
/// PNG へのメタデータ埋め込みと再読込）を、ヘッドレスに近い形で通して確認する。
/// 成果物の PNG を残すので、目視でも確認できる。
/// </summary>
public static class SelfCheck
{
    public static async Task<int> RunAsync()
    {
        var report = new StringBuilder();
        var failures = 0;
        var checks = 0;

        void Record(string name, bool ok, string detail)
        {
            checks++;
            if (!ok)
            {
                failures++;
            }

            report.AppendLine($"  {(ok ? "PASS " : "FAIL ")} {name}{detail}");
        }

        void Check(string name, Func<bool> body)
        {
            try
            {
                Record(name, body(), string.Empty);
            }
            catch (Exception ex)
            {
                Record(name, false, $" ({ex.GetType().Name}: {ex.Message})");
            }
        }

        async Task CheckAsync(string name, Func<Task<bool>> body)
        {
            try
            {
                Record(name, await body(), string.Empty);
            }
            catch (Exception ex)
            {
                Record(name, false, $" ({ex.GetType().Name}: {ex.Message})");
            }
        }

        var outputFolder = Path.Combine(Path.GetTempPath(), "TorimaruSelfCheck");
        Directory.CreateDirectory(outputFolder);

        report.AppendLine("Torimaru 自己診断");
        report.AppendLine(new string('=', 60));
        report.AppendLine();
        report.AppendLine("[画面の取り込み]");

        CapturedImage? captured = null;
        Check("仮想画面を取り込める", () =>
        {
            captured = ScreenCapturer.CaptureVirtualScreen(includeCursor: false);
            return captured.Width > 0 && captured.Height > 0;
        });

        Check("取り込み結果の寸法が仮想画面と一致する", () =>
        {
            var (_, _, vw, vh) = ScreenCapturer.GetVirtualScreenBounds();
            return captured is not null && captured.Width == vw && captured.Height == vh;
        });

        Check("ピクセル色を読み出せる", () =>
        {
            if (captured is null)
            {
                return false;
            }

            var color = captured.ColorAt(captured.Width / 2, captured.Height / 2);
            return color.A == 255;
        });

        Check("範囲外のピクセル読み出しが安全", () =>
            captured is not null && captured.ColorAt(-5, -5).A == 0);

        if (captured is null)
        {
            report.AppendLine();
            report.AppendLine("画面を取り込めなかったため、以降の確認は実施できません。");
            Console.Write(report.ToString());
            return 1;
        }

        // 以降は切り出した領域で検証する
        var regionWidth = Math.Min(640, captured.Width);
        var regionHeight = Math.Min(420, captured.Height);
        var region = new PixelRect(0, 0, regionWidth, regionHeight);

        System.Windows.Media.Imaging.BitmapSource? baseImage = null;
        Check("領域を切り出せる", () =>
        {
            baseImage = captured.Crop(region);
            return baseImage.PixelWidth == regionWidth && baseImage.PixelHeight == regionHeight;
        });

        if (baseImage is null)
        {
            Console.Write(report.ToString());
            return 1;
        }

        report.AppendLine();
        report.AppendLine("[注釈の描画と焼き込み]");

        // 全ツールを 1 つずつ置く（描画経路を全部通す）
        var document = new AnnotationDocument
        {
            NextStepNumber = 3,
            Shapes =
            [
                new AnnotationShape
                {
                    Kind = ShapeKind.Highlight,
                    X = 30, Y = 24, Width = 260, Height = 34,
                    Color = "#FFFDD835",
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Rectangle,
                    X = 28, Y = 80, Width = 240, Height = 110,
                    Color = "#FFE53935", Thickness = 4,
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Ellipse,
                    X = 300, Y = 80, Width = 150, Height = 110,
                    Color = "#FF1E88E5", Thickness = 3,
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Arrow,
                    X = 60, Y = 250, Width = 190, Height = -40,
                    Color = "#FF43A047", Thickness = 6,
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Text,
                    X = 30, Y = 290, Width = 200, Height = 30,
                    Text = "日本語のテスト注釈 ABC",
                    FontSize = 22,
                    Color = "#FFE53935",
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Step,
                    X = 300, Y = 230, Width = 44, Height = 44,
                    Number = 1,
                    Color = "#FFE53935",
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Step,
                    X = 360, Y = 230, Width = 44, Height = 44,
                    Number = 2,
                    Color = "#FF8E24AA",
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Blur,
                    X = 430, Y = 290, Width = 170, Height = 60,
                    BlurRadius = 16,
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Callout,
                    X = 420, Y = 120, Width = 150, Height = 46,
                    Text = "ここを見て",
                    FontSize = 20,
                    Color = "#FF1E88E5",
                    TailX = 360, TailY = 210,
                },
                new AnnotationShape
                {
                    Kind = ShapeKind.Callout,
                    X = 40, Y = 300, Width = 190, Height = 70,
                    Text = "楕円＋白地",
                    FontSize = 20,
                    Color = "#FF8E24AA",
                    CalloutShape = CalloutShapes.Oval,
                    CalloutStyle = CalloutStyles.Outline,
                    TailX = 170, TailY = 400,
                },
            ],
        };

        System.Windows.Media.Imaging.BitmapSource? flattened = null;
        Check("全 8 種のツールを焼き込める", () =>
        {
            flattened = CaptureSaver.Flatten(baseImage, document);
            return flattened.PixelWidth == regionWidth && flattened.PixelHeight == regionHeight;
        });

        Check("焼き込み後の画像が元画像と異なる（描画された証拠）", () =>
            flattened is not null && CountDifferingPixels(baseImage, flattened) > 500);

        Check("注釈が無い場合は元画像と一致する", () =>
        {
            var untouched = CaptureSaver.Flatten(baseImage, new AnnotationDocument());
            return CountDifferingPixels(baseImage, untouched) == 0;
        });

        report.AppendLine();
        report.AppendLine("[保存と再編集]");

        var savedPath = Path.Combine(outputFolder, "selfcheck.png");
        Check("注釈付きで保存できる", () =>
        {
            if (File.Exists(savedPath))
            {
                File.Delete(savedPath);
            }

            var original = CaptureNaming.OriginalPathFor(savedPath);
            if (File.Exists(original))
            {
                File.Delete(original);
            }

            CaptureSaver.SaveEdited(savedPath, baseImage, document);
            return File.Exists(savedPath);
        });

        Check("注釈前のオリジナルが退避されている", () =>
            File.Exists(CaptureNaming.OriginalPathFor(savedPath)));

        Check("保存した PNG に注釈データが埋まっている", () =>
            PngTextChunk.Extract(File.ReadAllBytes(savedPath)) is not null);

        LoadedCapture? reloaded = null;
        Check("保存した PNG を再編集用に読み戻せる", () =>
        {
            reloaded = CaptureSaver.Load(savedPath);
            return reloaded.Document.Shapes.Count == document.Shapes.Count;
        });

        Check("下地がオリジナル（注釈前）に戻る", () =>
            reloaded is not null
            && reloaded.BaseImage.PixelWidth == regionWidth
            && reloaded.BaseImage.PixelHeight == regionHeight
            && CountDifferingPixels(baseImage, reloaded.BaseImage) == 0);

        Check("吹き出しが本文・しっぽごと復元される", () =>
            reloaded is not null
            && reloaded.Document.Shapes.Any(s =>
                s.Kind == ShapeKind.Callout
                && s.Text == "ここを見て"
                && Math.Abs(s.TailX - 360) < 0.01
                && Math.Abs(s.TailY - 210) < 0.01));

        Check("日本語テキストが欠けずに復元される", () =>
            reloaded is not null
            && reloaded.Document.Shapes.Any(s =>
                s.Kind == ShapeKind.Text && s.Text == "日本語のテスト注釈 ABC"));

        Check("ステップ番号が保持される", () =>
            reloaded is not null
            && reloaded.Document.Shapes.Count(s => s.Kind == ShapeKind.Step) == 2
            && reloaded.Document.NextStepNumber == 3);

        Check("再読込→再焼き込みで同じ絵になる（再編集しても劣化しない）", () =>
        {
            if (reloaded is null || flattened is null)
            {
                return false;
            }

            var again = CaptureSaver.Flatten(reloaded.BaseImage, reloaded.Document);
            return CountDifferingPixels(flattened, again) == 0;
        });

        Check("2 回保存してもチャンクが増えない", () =>
        {
            CaptureSaver.SaveEdited(savedPath, baseImage, document);
            var bytes = File.ReadAllBytes(savedPath);
            return PngTextChunk.Extract(bytes) is not null && CountTextChunks(bytes) == 1;
        });

        report.AppendLine();
        report.AppendLine("[複数行テキスト]");

        // 測定は描画 (ShapeRenderer) と同じ書体で行う必要がある。
        // ここがずれると選択枠と当たり判定が見た目と合わなくなる。
        var oneLine = ShapeRenderer.MeasureText("あいう", 22, 1.0);
        var twoLines = ShapeRenderer.MeasureText("あいう\nあいう", 22, 1.0);

        Check("改行すると高さが行数ぶんになる", () =>
        {
            report.AppendLine(
                $"       1 行: {oneLine.Width:0.#} x {oneLine.Height:0.#} / "
                + $"2 行: {twoLines.Width:0.#} x {twoLines.Height:0.#}");

            return twoLines.Height > oneLine.Height * 1.8
                && twoLines.Height < oneLine.Height * 2.2;
        });

        Check("CRLF 改行でも行数ぶんの高さになる", () =>
        {
            // WPF の TextBox が返す改行は CRLF なので、実際に入るのはこちら
            var crlf = ShapeRenderer.MeasureText("あいう\r\nあいう", 22, 1.0);

            return Math.Abs(crlf.Height - twoLines.Height) < 0.5
                && Math.Abs(crlf.Width - twoLines.Width) < 0.5;
        });

        Check("改行しても幅は最長行で決まる", () =>
        {
            var ragged = ShapeRenderer.MeasureText("ab\nabcd", 22, 1.0);
            var longest = ShapeRenderer.MeasureText("abcd", 22, 1.0);

            return Math.Abs(ragged.Width - longest.Width) < 0.5
                && Math.Abs(twoLines.Width - oneLine.Width) < 0.5;
        });

        Check("改行つきテキストが 1 行より縦に長く焼き込まれる", () =>
        {
            var singleDoc = new AnnotationDocument();
            singleDoc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 10, Y = 10,
                Text = "あいう",
                FontSize = 22,
                Color = "#FFE53935",
            });

            var multiDoc = new AnnotationDocument();
            multiDoc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 10, Y = 10,
                Text = "あいう\nあいう\nあいう",
                FontSize = 22,
                Color = "#FFE53935",
            });

            var blank = RenderSolid(System.Windows.Media.Colors.White, 240, 180);
            var singleInk = CountDifferingPixels(blank, CaptureSaver.Flatten(blank, singleDoc));
            var multiInk = CountDifferingPixels(blank, CaptureSaver.Flatten(blank, multiDoc));

            report.AppendLine($"       描画ピクセル数 1 行: {singleInk} / 3 行: {multiInk}");

            return singleInk > 0 && multiInk > singleInk * 2;
        });

        Check("改行を含むテキストが保存→再読込で保持される", () =>
        {
            var path = Path.Combine(outputFolder, "multiline.png");
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 12, Y = 12,
                Text = "1 行目\n2 行目\n3 行目",
                FontSize = 20,
                Color = "#FFE53935",
            });

            CaptureSaver.SaveEdited(path, RenderSolid(System.Windows.Media.Colors.White, 320, 200), doc);
            var back = CaptureSaver.Load(path);
            var shape = back.Document.Shapes.FirstOrDefault(x => x.Kind == ShapeKind.Text);

            return shape?.Text == "1 行目\n2 行目\n3 行目"
                && shape.Text.Split('\n').Length == 3;
        });

        report.AppendLine();
        report.AppendLine("[吹き出し]");

        AnnotationShape MakeCallout(double tailX, double tailY) => new()
        {
            Kind = ShapeKind.Callout,
            X = 60, Y = 60, Width = 140, Height = 44,
            Text = "ここを見て",
            FontSize = 20,
            Color = "#FF1E88E5",
            TailX = tailX, TailY = tailY,
        };

        System.Windows.Media.Imaging.BitmapSource DrawOnly(AnnotationShape shape)
        {
            var doc = new AnnotationDocument();
            doc.Shapes.Add(shape);
            return CaptureSaver.Flatten(
                RenderSolid(System.Windows.Media.Colors.White, 320, 260), doc);
        }

        var canvasBlank = RenderSolid(System.Windows.Media.Colors.White, 320, 260);

        Check("本体が描かれる", () =>
        {
            var ink = CountDifferingPixels(canvasBlank, DrawOnly(MakeCallout(100, 170)));
            report.AppendLine($"       描画ピクセル数: {ink}");
            return ink > 1000;
        });

        Check("しっぽの向きで絵が変わる（先端の位置が効いている）", () =>
        {
            var down = DrawOnly(MakeCallout(100, 180));
            var right = DrawOnly(MakeCallout(260, 80));

            var diff = CountDifferingPixels(down, right);
            report.AppendLine($"       下向き vs 右向きの差分: {diff} px");
            return diff > 200;
        });

        Check("しっぽは根元から先へ細くなる（縁の外で太くなる「返し」ができない）", () =>
        {
            // 付け根の両端を本体の外に置いてしまうと、縁のすぐ下でいったん幅が
            // 広がり、釣り針の「返し」のような形になる（＝しっぽが本体から
            // 切り離されて見える）。横長の本体から斜め下へ出すと必ず再現した。
            var shape = MakeCallout(85, 146);
            shape.X = 60;
            shape.Y = 60;
            shape.Width = 116;
            shape.Height = 47;

            // 本体の下端は Y+Height = 107。縁を出た直後から測らないと、
            // 「返し」の一番太いところ（縁の 1〜3 px 下）を跨いでしまう。
            var widths = RowInkWidths(DrawOnly(shape), 108, 140);

            var worst = 0;
            for (var i = 1; i < widths.Count; i++)
            {
                worst = Math.Max(worst, widths[i] - widths[i - 1]);
            }

            report.AppendLine(
                $"       縁の下の幅: {string.Join(" ", widths.Take(10))} … / 最大の増加: {worst} px");

            return widths.Count == 33 && widths[0] > 4 && worst <= 1;
        });

        Check("横向きのしっぽが本体の高さを食いつぶさない", () =>
        {
            // 根元の幅を本体の短辺で決めると、横向きのしっぽが高さいっぱいに
            // 広がって「旗つきの矢印」になる。生える辺の長さで決めること。
            var shape = MakeCallout(20, 82); // 本体の左へ水平に伸ばす
            var rooted = ColumnInkHeight(DrawOnly(shape), (int)shape.X - 4);

            report.AppendLine($"       根元の縦幅: {rooted} px / 本体の高さ: {shape.Height:0} px");
            return rooted > 4 && rooted <= shape.Height * 0.45;
        });

        Check("先端を本体の中に置くとしっぽが出ない", () =>
        {
            var inside = MakeCallout(120, 80);   // 本体の内側
            var outside = MakeCallout(120, 190); // 本体の外側

            var insideInk = CountDifferingPixels(canvasBlank, DrawOnly(inside));
            var outsideInk = CountDifferingPixels(canvasBlank, DrawOnly(outside));

            report.AppendLine($"       内側: {insideInk} px / 外側: {outsideInk} px");
            return outsideInk > insideInk;
        });

        Check("大きさが未確定（入力中）の吹き出しでも描画が落ちない", () =>
        {
            // 文字を入力している最中は Width/Height がまだ 0。
            // ここで例外を出すと、吹き出しツールでクリックした瞬間にエラーになる。
            var typing = MakeCallout(120, 200);
            typing.Width = 0;
            typing.Height = 0;
            typing.Text = null;

            DrawOnly(typing);

            var oneByOne = MakeCallout(120, 200);
            oneByOne.Width = 1;
            oneByOne.Height = 1;
            DrawOnly(oneByOne);

            return true; // 例外を投げないことが要件
        });

        Check("本体サイズが文字に合わせて決まる", () =>
        {
            var shortText = MakeCallout(100, 180);
            shortText.Text = "短い";
            ShapeRenderer.ApplyCalloutSize(shortText, 1.0);

            var longText = MakeCallout(100, 180);
            longText.Text = "とても長い説明文がここに入ります";
            ShapeRenderer.ApplyCalloutSize(longText, 1.0);

            var multi = MakeCallout(100, 180);
            multi.Text = "1 行目\n2 行目";
            ShapeRenderer.ApplyCalloutSize(multi, 1.0);

            report.AppendLine(
                $"       短: {shortText.Width:0}x{shortText.Height:0} / "
                + $"長: {longText.Width:0}x{longText.Height:0} / "
                + $"2 行: {multi.Width:0}x{multi.Height:0}");

            return longText.Width > shortText.Width
                && Math.Abs(longText.Height - shortText.Height) < 1
                && multi.Height > shortText.Height * 1.6;
        });

        report.AppendLine();
        report.AppendLine("[書体と文字の大きさ]");

        System.Windows.Media.Imaging.BitmapSource DrawText(
            string text, string? fontFamily, double fontSize)
        {
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 20, Y = 20,
                Text = text,
                FontSize = fontSize,
                FontFamily = fontFamily,
                Color = "#FF000000",
            });

            return CaptureSaver.Flatten(
                RenderSolid(System.Windows.Media.Colors.White, 420, 140), doc);
        }

        bool FontInstalled(string name) => System.Windows.Media.Fonts.SystemFontFamilies
            .Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase));

        Check("大きさを変えると実寸が比例する", () =>
        {
            var small = ShapeRenderer.MeasureText("あいう", 20, 1.0);
            var large = ShapeRenderer.MeasureText("あいう", 40, 1.0);

            report.AppendLine(
                $"       20px: {small.Width:0.#}x{small.Height:0.#} / 40px: {large.Width:0.#}x{large.Height:0.#}");

            return large.Width > small.Width * 1.9 && large.Width < small.Width * 2.1
                && large.Height > small.Height * 1.9 && large.Height < small.Height * 2.1;
        });

        Check("書体を変えると実寸が変わる（等幅と比例幅）", () =>
        {
            if (!FontInstalled("Arial") || !FontInstalled("Consolas"))
            {
                report.AppendLine("       Arial / Consolas が無いため比較できません");
                return false;
            }

            var proportional = ShapeRenderer.MeasureText("iiiiiiiiii", 24, 1.0, "Arial");
            var monospace = ShapeRenderer.MeasureText("iiiiiiiiii", 24, 1.0, "Consolas");

            report.AppendLine(
                $"       Arial: {proportional.Width:0.#} / Consolas: {monospace.Width:0.#}");

            // 細い i が 10 個。等幅のほうが明確に広くなる
            return monospace.Width > proportional.Width * 1.3;
        });

        Check("測った実寸と実際に描かれる文字の幅が書体ごとに一致する", () =>
        {
            if (!FontInstalled("Arial") || !FontInstalled("Consolas"))
            {
                report.AppendLine("       Arial / Consolas が無いため比較できません");
                return false;
            }

            // 測定 (選択枠・当たり判定) と描画で違う書体を使っていると、この比が崩れる
            var measuredRatio =
                ShapeRenderer.MeasureText("iiiiiiiiii", 24, 1.0, "Consolas").Width
                / ShapeRenderer.MeasureText("iiiiiiiiii", 24, 1.0, "Arial").Width;

            var inkRatio = (double)InkWidth(DrawText("iiiiiiiiii", "Consolas", 24))
                / InkWidth(DrawText("iiiiiiiiii", "Arial", 24));

            report.AppendLine($"       測定比: {measuredRatio:0.###} / 描画比: {inkRatio:0.###}");
            return Math.Abs(measuredRatio - inkRatio) < 0.15;
        });

        Check("書体を指定しない注釈は従来どおりの見た目で描かれる", () =>
        {
            // null = 既定。ここが変わると、書体を選べる前に保存した PNG の見た目が変わる
            var legacy = DrawText("iiiiiiiiii", null, 24);
            var explicitDefault = DrawText("iiiiiiiiii", "Yu Gothic UI, Segoe UI", 24);

            return CountDifferingPixels(legacy, explicitDefault) == 0;
        });

        Check("日本語でも測った実寸どおりに描かれる", () =>
        {
            // 吹き出しの本体サイズも選択枠も、この測定値で決まる。
            // 測定と描画で書体がずれると、枠からはみ出したり余白が空いたりする。
            const string japanese = "あいうえお";
            var measured = ShapeRenderer.MeasureText(japanese, 32, 1.0);
            var ink = InkWidth(DrawText(japanese, null, 32));
            var ratio = ink / measured.Width;

            report.AppendLine($"       測定: {measured.Width:0.#} / 実際の墨: {ink} (比 {ratio:0.##})");

            // 文字の左右には僅かな余白があるので、墨は測定値より少し狭いのが正しい
            return ratio > 0.7 && ratio <= 1.05;
        });

        Check("吹き出しの本体が文字の大きさに追従する", () =>
        {
            var small = MakeCallout(100, 190);
            small.FontSize = 16;
            ShapeRenderer.ApplyCalloutSize(small, 1.0);

            var large = MakeCallout(100, 190);
            large.FontSize = 40;
            ShapeRenderer.ApplyCalloutSize(large, 1.0);

            report.AppendLine(
                $"       16px: {small.Width:0}x{small.Height:0} / 40px: {large.Width:0}x{large.Height:0}");

            return large.Width > small.Width * 1.8 && large.Height > small.Height * 1.8;
        });

        Check("書体と大きさが保存→再読込で保持される", () =>
        {
            var path = Path.Combine(outputFolder, "font.png");
            var doc = new AnnotationDocument();
            doc.Shapes.Add(new AnnotationShape
            {
                Kind = ShapeKind.Text,
                X = 12, Y = 12,
                Text = "書体テスト",
                FontFamily = "MS Gothic",
                FontSize = 36,
                Color = "#FFE53935",
            });

            CaptureSaver.SaveEdited(
                path, RenderSolid(System.Windows.Media.Colors.White, 320, 200), doc);

            var shape = CaptureSaver.Load(path).Document.Shapes.FirstOrDefault();
            return shape?.FontFamily == "MS Gothic" && shape.FontSize == 36;
        });

        report.AppendLine();
        report.AppendLine("[吹き出しの形と地色]");

        AnnotationShape Looked(string bodyShape, string style, double tailX = 100, double tailY = 190)
        {
            var shape = MakeCallout(tailX, tailY);
            shape.CalloutShape = bodyShape;
            shape.CalloutStyle = style;
            return shape;
        }

        Check("形（角丸・四角・楕円）で絵が変わる", () =>
        {
            var round = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Solid));
            var rect = DrawOnly(Looked(CalloutShapes.Rect, CalloutStyles.Solid));
            var oval = DrawOnly(Looked(CalloutShapes.Oval, CalloutStyles.Solid));

            var roundVsRect = CountDifferingPixels(round, rect);
            var roundVsOval = CountDifferingPixels(round, oval);

            report.AppendLine($"       角丸 vs 四角: {roundVsRect} px / 角丸 vs 楕円: {roundVsOval} px");
            return roundVsRect > 50 && roundVsOval > 500;
        });

        Check("楕円は同じ文字でも本体が大きくなる（文字が縁に当たらない）", () =>
        {
            var round = MakeCallout(100, 190);
            round.CalloutShape = CalloutShapes.Round;
            ShapeRenderer.ApplyCalloutSize(round, 1.0);

            var oval = MakeCallout(100, 190);
            oval.CalloutShape = CalloutShapes.Oval;
            ShapeRenderer.ApplyCalloutSize(oval, 1.0);

            report.AppendLine(
                $"       角丸: {round.Width:0}x{round.Height:0} / 楕円: {oval.Width:0}x{oval.Height:0}");

            return oval.Width > round.Width * 1.3 && oval.Height > round.Height * 1.3;
        });

        Check("地色（色で塗り・白地・黒地）で絵が変わる", () =>
        {
            var solid = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Solid));
            var outline = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Outline));
            var dark = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Dark));

            var solidVsOutline = CountDifferingPixels(solid, outline);
            var outlineVsDark = CountDifferingPixels(outline, dark);

            report.AppendLine(
                $"       塗り vs 白地: {solidVsOutline} px / 白地 vs 黒地: {outlineVsDark} px");

            return solidVsOutline > 500 && outlineVsDark > 500;
        });

        Check("白地は枠と文字にパレットの色を使う", () =>
        {
            var look = ShapeRenderer.ResolveCalloutLook(
                Looked(CalloutShapes.Round, CalloutStyles.Outline));

            var color = ShapeRenderer.ParseColor("#FF1E88E5");
            return look.Fill == System.Windows.Media.Colors.White
                && look.Stroke == color
                && look.Text == color;
        });

        Check("白を選んだ白地では地が暗い色へ反転する（見えなくならない）", () =>
        {
            var shape = Looked(CalloutShapes.Round, CalloutStyles.Outline);
            shape.Color = "#FFFFFFFF";

            var look = ShapeRenderer.ResolveCalloutLook(shape);
            report.AppendLine($"       地の色: {look.Fill} / 文字: {look.Text}");

            return look.Fill.R < 0x60 && look.Fill.G < 0x60 && look.Fill.B < 0x60;
        });

        Check("しっぽは楕円の縁から生える（四角なら内側になる位置でも出る）", () =>
        {
            // 本体 (60,60)-(200,104) の左上の角は、四角の内側だが楕円の外側にある。
            // 形ごとに縁の判定を分けていないと、ここでしっぽが出ない／出てしまう。
            var cornerRound = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Solid, 63, 63));
            var centerRound = DrawOnly(Looked(CalloutShapes.Round, CalloutStyles.Solid, 130, 82));
            var cornerOval = DrawOnly(Looked(CalloutShapes.Oval, CalloutStyles.Solid, 63, 63));
            var centerOval = DrawOnly(Looked(CalloutShapes.Oval, CalloutStyles.Solid, 130, 82));

            var roundDiff = CountDifferingPixels(cornerRound, centerRound);
            var ovalDiff = CountDifferingPixels(cornerOval, centerOval);

            report.AppendLine($"       角丸の差分: {roundDiff} px / 楕円の差分: {ovalDiff} px");
            return roundDiff == 0 && ovalDiff > 50;
        });

        Check("楕円・白地でも大きさが未確定（入力中）で落ちない", () =>
        {
            foreach (var bodyShape in CalloutShapes.All)
            {
                foreach (var style in CalloutStyles.All)
                {
                    var typing = Looked(bodyShape, style, 120, 200);
                    typing.Width = 0;
                    typing.Height = 0;
                    typing.Text = null;
                    DrawOnly(typing);
                }
            }

            return true; // 例外を投げないことが要件
        });

        Check("形・地色が保存→再読込で保持される", () =>
        {
            var path = Path.Combine(outputFolder, "callout-look.png");
            var doc = new AnnotationDocument();
            doc.Shapes.Add(Looked(CalloutShapes.Oval, CalloutStyles.Dark));

            CaptureSaver.SaveEdited(
                path, RenderSolid(System.Windows.Media.Colors.White, 320, 260), doc);

            var shape = CaptureSaver.Load(path).Document.Shapes.FirstOrDefault();

            return shape?.CalloutShape == CalloutShapes.Oval
                && shape.CalloutStyle == CalloutStyles.Dark
                && shape.Text == "ここを見て";
        });

        report.AppendLine();
        report.AppendLine("[ウィンドウ列挙]");

        Check("トップレベルウィンドウを列挙できる", () =>
            Interop.WindowFinder.EnumerateVisibleWindows().Count > 0);

        Check("列挙したウィンドウに有効な矩形がある", () =>
            Interop.WindowFinder.EnumerateVisibleWindows()
                .All(w => w.Bounds.Width >= 8 && w.Bounds.Height >= 8));

        // ---------------------------------------------------------- Phase 2

        report.AppendLine();
        report.AppendLine("[部分取り込み / スクロール合成]");

        var probeRegion = new PixelRect(
            captured.OriginX, captured.OriginY,
            Math.Min(320, captured.Width), Math.Min(240, captured.Height));

        Check("画面の一部を RawImage として取り込める", () =>
        {
            var raw = ScreenCapturer.CaptureRegionRaw(probeRegion);
            return raw.Width == (int)probeRegion.Width
                   && raw.Height == (int)probeRegion.Height
                   && raw.Stride >= raw.Width * RawImage.BytesPerPixel;
        });

        Check("RawImage を表示用の画像へ変換できる", () =>
        {
            var raw = ScreenCapturer.CaptureRegionRaw(probeRegion);
            var source = ScreenCapturer.ToBitmapSource(raw);
            return source.PixelWidth == raw.Width && source.PixelHeight == raw.Height;
        });

        Check("合成したフレームが縦長画像になる", () =>
        {
            // 実画面のスクロールは自動化できないため、既知のページを分割して通す
            var page = BuildSyntheticPage(240, 500);
            var stitcher = new ScrollStitcher(240, 160);

            for (var i = 0; i < 4; i++)
            {
                stitcher.AddFrame(page.TakeRows(i * 50, 160), out _);
            }

            var stitched = ScreenCapturer.ToBitmapSource(stitcher.Build());
            return stitched.PixelHeight == 160 + (50 * 3) && stitched.PixelWidth == 240;
        });

        report.AppendLine();
        report.AppendLine("[文字の抽出 (OCR)]");

        Check("OCR エンジンが使える", () => OcrService.IsAvailable);

        report.AppendLine($"       利用可能な言語: {string.Join(", ", OcrService.AvailableLanguages)}");

        var asciiImage = RenderText("ABC 123", 220, 80, 36);
        await CheckAsync("英数字を読み取れる", async () =>
        {
            var outcome = await OcrService.RecognizeAsync(asciiImage, null);
            report.AppendLine($"       読み取り: \"{outcome.Text.Replace("\r\n", " / ").Replace("\n", " / ")}\"");
            return outcome.Success && outcome.Text.Replace(" ", string.Empty).Contains("ABC", StringComparison.OrdinalIgnoreCase);
        });

        var japaneseImage = RenderText("画面キャプチャ", 320, 90, 34);
        await CheckAsync("日本語を読み取れる", async () =>
        {
            var outcome = await OcrService.RecognizeAsync(japaneseImage, "ja");
            report.AppendLine($"       読み取り: \"{outcome.Text.Replace("\r\n", " / ").Replace("\n", " / ")}\"");
            return outcome.Success && outcome.Text.Contains("画面", StringComparison.Ordinal);
        });

        await CheckAsync("文字が無い画像では失敗として返る（例外にしない）", async () =>
        {
            var blank = RenderText(string.Empty, 120, 60, 20);
            var outcome = await OcrService.RecognizeAsync(blank, null);
            return !outcome.Success && outcome.Message.Length > 0;
        });

        report.AppendLine();
        report.AppendLine("[QR / バーコード]");

        const string payload = "https://example.com/?q=テスト";

        Check("生成した QR コードを読み取れる", () =>
        {
            var qr = RenderQrCode(payload, 300);
            var hits = BarcodeService.DecodeAll(qr);

            report.AppendLine($"       QR 画像: {qr.PixelWidth} x {qr.PixelHeight} / 検出 {hits.Count} 件");
            if (hits.Count > 0)
            {
                report.AppendLine($"       読み取り: {hits[0].Format} \"{hits[0].Text}\"");
            }

            return hits.Count == 1 && hits[0].Text == payload && hits[0].Format == "QR_CODE";
        });

        Check("コードが無い画像では空の結果を返す", () =>
            BarcodeService.DecodeAll(RenderText("no code here", 200, 80, 20)).Count == 0);

        // ---------------------------------------------------------- Phase 3

        report.AppendLine();
        report.AppendLine("[動画録画の前提]");

        Check("ffmpeg が見つかる", () => Ffmpeg.IsAvailable);
        report.AppendLine($"       ffmpeg: {Ffmpeg.ResolvePath() ?? "(見つかりません)"}");

        var renderDevices = Audio.AudioDevices.GetRenderDevices();
        var captureDevices = Audio.AudioDevices.GetCaptureDevices();

        Check("再生デバイス（システム音声の取り込み元）を列挙できる", () => renderDevices.Count > 0);
        report.AppendLine($"       再生: {string.Join(" / ", renderDevices.Select(d => d.Name))}");
        report.AppendLine($"       録音: {(captureDevices.Count == 0 ? "(なし)" : string.Join(" / ", captureDevices.Select(d => d.Name)))}");

        await CheckAsync("システム音声を WAV に録れる", async () =>
        {
            var wavPath = Path.Combine(outputFolder, "loopback.wav");
            using var recorder = new Audio.WasapiRecorder(loopback: true, deviceId: null, wavPath);

            if (!recorder.TryStart(out var error))
            {
                report.AppendLine($"       {error}");
                return false;
            }

            await Task.Delay(1200);
            recorder.Stop();

            var info = new FileInfo(wavPath);
            report.AppendLine($"       {info.Length:N0} バイト / {recorder.Duration.TotalSeconds:0.00} 秒");

            // 無音でも時間ぶんは埋まっている必要がある（映像とのずれ防止）
            return info.Exists && info.Length > 1024
                   && recorder.Duration > TimeSpan.FromSeconds(0.9)
                   && recorder.Duration < TimeSpan.FromSeconds(2.0);
        });

        if (Ffmpeg.IsAvailable)
        {
            report.AppendLine();
            report.AppendLine("[録画 → 書き出し → 加工]");

            // 診断は短時間で終わらせたいので、軽い設定で録る
            var settingsForRecording = new AppSettings
            {
                VideoFps = 15,
                VideoCrf = 30,
                RecordCursor = true,
                RecordSystemAudio = true,
                RecordMicrophone = false,
            };

            var recordedPath = Path.Combine(outputFolder, "recorded.mp4");
            var recorder = new VideoRecorder(settingsForRecording);

            await CheckAsync("一時停止をはさんで録画できる", async () =>
            {
                var region = new PixelRect(
                    captured.OriginX, captured.OriginY,
                    Math.Min(320, captured.Width), Math.Min(240, captured.Height));

                var started = await recorder.StartAsync(region);
                if (!started.Success)
                {
                    report.AppendLine($"       {started.Message}");
                    return false;
                }

                await Task.Delay(1500);
                await recorder.PauseAsync();
                await Task.Delay(400);

                var resumed = await recorder.ResumeAsync();
                if (!resumed.Success)
                {
                    report.AppendLine($"       {resumed.Message}");
                    await recorder.CancelAsync();
                    return false;
                }

                await Task.Delay(1500);

                var stopped = await recorder.StopAsync(recordedPath);
                report.AppendLine($"       {stopped.Message}");
                return stopped.Success && File.Exists(recordedPath);
            });

            recorder.Dispose();

            await CheckAsync("録画結果に映像と音声が入っている", async () =>
            {
                var probe = await Ffmpeg.RunAsync($"-hide_banner -i {Ffmpeg.Quote(recordedPath)}");
                var hasVideo = probe.Output.Contains("Video:", StringComparison.Ordinal);
                var hasAudio = probe.Output.Contains("Audio:", StringComparison.Ordinal);

                report.AppendLine($"       映像={hasVideo} 音声={hasAudio}");
                return hasVideo && hasAudio;
            });

            await CheckAsync("2 区間が連結され、約 3 秒になっている", async () =>
            {
                var duration = await Ffmpeg.TryGetDurationAsync(recordedPath);
                report.AppendLine($"       長さ: {duration?.TotalSeconds:0.00} 秒");

                // 1 区間だけなら約 1.5 秒にしかならない
                return duration is not null
                       && duration.Value > TimeSpan.FromSeconds(2.2)
                       && duration.Value < TimeSpan.FromSeconds(4.5);
            });

            var framePath = Path.Combine(outputFolder, "frame.png");
            await CheckAsync("動画からフレームを書き出せる", async () =>
            {
                var result = await VideoTools.ExtractFrameAsync(
                    recordedPath, TimeSpan.FromSeconds(1), framePath);

                if (!result.Success)
                {
                    report.AppendLine($"       {result.Message}");
                    return false;
                }

                var image = CaptureSaver.DecodeImage(await File.ReadAllBytesAsync(framePath));
                report.AppendLine($"       {image.PixelWidth} x {image.PixelHeight}");
                return image.PixelWidth > 0 && image.PixelHeight > 0;
            });

            var trimmedPath = Path.Combine(outputFolder, "trimmed.mp4");
            await CheckAsync("動画をトリミングできる", async () =>
            {
                var result = await VideoTools.TrimAsync(
                    recordedPath, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5),
                    trimmedPath, reencode: true);

                if (!result.Success)
                {
                    report.AppendLine($"       {result.Message}");
                    return false;
                }

                var duration = await Ffmpeg.TryGetDurationAsync(trimmedPath);
                report.AppendLine($"       長さ: {duration?.TotalSeconds:0.00} 秒");
                return duration is not null && duration.Value < TimeSpan.FromSeconds(1.6);
            });

            await CheckAsync("動画を結合できる", async () =>
            {
                var copy = Path.Combine(outputFolder, "trimmed-copy.mp4");
                File.Copy(trimmedPath, copy, overwrite: true);

                var joined = Path.Combine(outputFolder, "joined.mp4");
                var result = await VideoTools.ConcatAsync([trimmedPath, copy], joined);

                if (!result.Success)
                {
                    report.AppendLine($"       {result.Message}");
                    return false;
                }

                var duration = await Ffmpeg.TryGetDurationAsync(joined);
                report.AppendLine($"       長さ: {duration?.TotalSeconds:0.00} 秒");
                return duration is not null && duration.Value > TimeSpan.FromSeconds(1.5);
            });
        }

        report.AppendLine();
        report.AppendLine("[色の採取]");

        Check("既知の色を正しく読み出せる", () =>
        {
            var swatch = RenderSolid(System.Windows.Media.Color.FromRgb(18, 52, 86), 40, 30);
            var raw = ToRaw(swatch);
            var image = new CapturedImage(raw.Pixels, raw.Width, raw.Height, raw.Stride, 0, 0);
            var color = image.ColorAt(20, 15);

            return color.R == 18 && color.G == 52 && color.B == 86;
        });

        report.AppendLine();
        report.AppendLine(new string('-', 60));
        report.AppendLine($"合格: {checks - failures} / 失敗: {failures}");
        report.AppendLine();
        report.AppendLine($"確認用の出力: {savedPath}");
        report.AppendLine($"（注釈前のオリジナル: {CaptureNaming.OriginalPathFor(savedPath)}）");

        Emit(report.ToString(), outputFolder);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 結果を標準出力とファイルの両方へ出す。
    /// WinExe はコンソールを持たないことがあるため、ファイルを正本として扱う。
    /// </summary>
    private static void Emit(string report, string outputFolder)
    {
        try
        {
            Console.Write(report);
        }
        catch (IOException)
        {
            // コンソールが無い環境では無視してファイルに任せる
        }

        try
        {
            File.WriteAllText(Path.Combine(outputFolder, "report.txt"), report, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("自己診断レポートの書き出しに失敗", ex);
        }
    }

    /// <summary>2 枚の画像で色が違うピクセルの数を数える。</summary>
    /// <summary>白地に描かれた絵の、墨（白でない画素）の横幅。</summary>
    private static int InkWidth(System.Windows.Media.Imaging.BitmapSource image)
    {
        var pixels = ToBgra(image);
        var minX = int.MaxValue;
        var maxX = int.MinValue;

        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var i = ((y * image.PixelWidth) + x) * 4;

                // 影がうっすら乗るので、はっきり暗い画素だけを数える
                if (pixels[i] < 160 && pixels[i + 1] < 160 && pixels[i + 2] < 160)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        return maxX < minX ? 0 : maxX - minX + 1;
    }

    /// <summary>行ごとの「色のついた画素」の横幅（吹き出しのしっぽの太さを測る）。</summary>
    private static List<int> RowInkWidths(
        System.Windows.Media.Imaging.BitmapSource image, int fromY, int toY)
    {
        var pixels = ToBgra(image);
        var widths = new List<int>();

        for (var y = fromY; y <= toY && y < image.PixelHeight; y++)
        {
            var minX = int.MaxValue;
            var maxX = int.MinValue;

            for (var x = 0; x < image.PixelWidth; x++)
            {
                if (IsColoured(pixels, ((y * image.PixelWidth) + x) * 4))
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }

            widths.Add(maxX < minX ? 0 : maxX - minX + 1);
        }

        return widths;
    }

    /// <summary>ある列の「色のついた画素」の縦幅。</summary>
    private static int ColumnInkHeight(
        System.Windows.Media.Imaging.BitmapSource image, int x)
    {
        var pixels = ToBgra(image);
        var minY = int.MaxValue;
        var maxY = int.MinValue;

        for (var y = 0; y < image.PixelHeight; y++)
        {
            if (IsColoured(pixels, ((y * image.PixelWidth) + x) * 4))
            {
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxY < minY ? 0 : maxY - minY + 1;
    }

    /// <summary>
    /// 白い下地・白い縁取り・灰色の影と区別するため、
    /// 「彩度のある画素」だけを図形とみなす。
    /// </summary>
    private static bool IsColoured(byte[] pixels, int index)
    {
        var b = pixels[index];
        var g = pixels[index + 1];
        var r = pixels[index + 2];

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max - min > 60;
    }

    private static int CountDifferingPixels(
        System.Windows.Media.Imaging.BitmapSource left,
        System.Windows.Media.Imaging.BitmapSource right)
    {
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
        {
            return int.MaxValue;
        }

        var leftPixels = ToBgra(left);
        var rightPixels = ToBgra(right);

        var differing = 0;
        for (var i = 0; i < leftPixels.Length; i += 4)
        {
            // アルファは比較しない（元画像は不透明、焼き込み後は Pbgra32 になる）
            if (leftPixels[i] != rightPixels[i]
                || leftPixels[i + 1] != rightPixels[i + 1]
                || leftPixels[i + 2] != rightPixels[i + 2])
            {
                differing++;
            }
        }

        return differing;
    }

    /// <summary>行ごとに模様が違う縦長のページを作る（スクロール合成の検証用）。</summary>
    private static RawImage BuildSyntheticPage(int width, int height)
    {
        var page = RawImage.Create(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * page.Stride) + (x * RawImage.BytesPerPixel);
                var value = (byte)(((y * 131) + (x * 7)) % 251);

                page.Pixels[offset] = value;
                page.Pixels[offset + 1] = value;
                page.Pixels[offset + 2] = value;
                page.Pixels[offset + 3] = 255;
            }
        }

        return page;
    }

    /// <summary>白地に黒文字を描いた画像を作る（OCR の検証用）。</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderText(
        string text, int width, int height, double fontSize)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                System.Windows.Media.Brushes.White,
                null,
                new System.Windows.Rect(0, 0, width, height));

            if (!string.IsNullOrEmpty(text))
            {
                var formatted = new System.Windows.Media.FormattedText(
                    text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface(
                        new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI"),
                        System.Windows.FontStyles.Normal,
                        System.Windows.FontWeights.Normal,
                        System.Windows.FontStretches.Normal),
                    fontSize,
                    System.Windows.Media.Brushes.Black,
                    1.0);

                context.DrawText(formatted, new System.Windows.Point(12, (height - formatted.Height) / 2));
            }
        }

        return RenderToBitmap(visual, width, height);
    }

    /// <summary>単色で塗った画像を作る（カラーピッカーの検証用）。</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderSolid(
        System.Windows.Media.Color color, int width, int height)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new System.Windows.Media.SolidColorBrush(color),
                null,
                new System.Windows.Rect(0, 0, width, height));
        }

        return RenderToBitmap(visual, width, height);
    }

    /// <summary>QR コードを描いた画像を作る（読み取りの往復検証用）。</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderQrCode(string payload, int size)
    {
        var writer = new ZXing.BarcodeWriterGeneric
        {
            Format = ZXing.BarcodeFormat.QR_CODE,

            // 日本語を含むため UTF-8 を明示する（既定は ISO-8859-1）
            Options = new ZXing.QrCode.QrCodeEncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 2,
                CharacterSet = "UTF-8",
            },
        };

        var matrix = writer.Encode(payload);
        var image = RawImage.Create(matrix.Width, matrix.Height);

        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var offset = (y * image.Stride) + (x * RawImage.BytesPerPixel);
                var value = matrix[x, y] ? (byte)0 : (byte)255;

                image.Pixels[offset] = value;
                image.Pixels[offset + 1] = value;
                image.Pixels[offset + 2] = value;
                image.Pixels[offset + 3] = 255;
            }
        }

        return ScreenCapturer.ToBitmapSource(image);
    }

    private static System.Windows.Media.Imaging.BitmapSource RenderToBitmap(
        System.Windows.Media.Visual visual, int width, int height)
    {
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static RawImage ToRaw(System.Windows.Media.Imaging.BitmapSource source)
    {
        var pixels = ToBgra(source);
        return new RawImage(pixels, source.PixelWidth, source.PixelHeight, source.PixelWidth * 4);
    }

    private static byte[] ToBgra(System.Windows.Media.Imaging.BitmapSource source)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static int CountTextChunks(byte[] png)
    {
        var count = 0;
        var offset = 8;

        while (offset + 12 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16)
                         | (png[offset + 2] << 8) | png[offset + 3];
            if (length < 0 || offset + 12 + length > png.Length)
            {
                break;
            }

            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "tEXt")
            {
                count++;
            }

            offset += 12 + length;
        }

        return count;
    }
}
