using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Torimaru.Core;

namespace Torimaru.App.Settings;

/// <summary>
/// 端末ごとにホットキーを割り当てる画面。
///
/// 押したキーをその場で受け取り、(1) 組み合わせとして成立するか、
/// (2) 他の操作とぶつからないか、(3) 他のアプリに取られていないか を順に確かめる。
/// 3 つ目は実際に登録して試すしかないので、呼び出し側から判定を渡してもらう。
///
/// この画面を開いている間、呼び出し側は自分のホットキーを全部外しておくこと
/// （付けたままだと、キーを選ぶ操作でキャプチャが始まってしまう）。
/// </summary>
public partial class HotKeySettingsWindow : Window
{
    private sealed record Row(HotKeyAction Action, TextBlock KeyText, Button ResetButton);

    private readonly AppSettings _settings;
    private readonly Func<HotKeySpec, bool> _isAvailable;
    private readonly List<Row> _rows = [];

    private HotKeyAction? _recording;

    /// <param name="settings">書き換える設定（この画面が直接更新する）。</param>
    /// <param name="isAvailable">そのキーが OS 上で空いているかを調べる処理。</param>
    public HotKeySettingsWindow(AppSettings settings, Func<HotKeySpec, bool> isAvailable)
    {
        _settings = settings;
        _isAvailable = isAvailable;

        InitializeComponent();
        BuildRows();
        ShowMessage("変更したい操作の「変更」を押してください。");
    }

    /// <summary>この画面で割り当てを変えたか（呼び出し側が保存の判断に使う）。</summary>
    public bool Changed { get; private set; }

    private void BuildRows()
    {
        foreach (var action in HotKeyActions.All)
        {
            var keyText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var keyBox = new Border
            {
                Width = 230,
                Height = 26,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xD5)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = keyText,
            };

            var changeButton = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Content = "変更",
                Tag = action,
            };
            changeButton.Click += OnChangeClick;

            var resetButton = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Content = "既定に戻す",
                MinWidth = 86,
                Tag = action,
            };
            resetButton.Click += OnResetClick;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
            };

            panel.Children.Add(new TextBlock
            {
                Text = action.DisplayName,
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center,
            });

            panel.Children.Add(keyBox);
            panel.Children.Add(changeButton);
            panel.Children.Add(resetButton);

            Rows.Children.Add(panel);
            _rows.Add(new Row(action, keyText, resetButton));
        }

        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (var row in _rows)
        {
            if (ReferenceEquals(row.Action, _recording))
            {
                row.KeyText.Text = "キーを押してください（Esc で中止）";
                row.KeyText.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));
                row.KeyText.FontWeight = FontWeights.Bold;
                row.ResetButton.IsEnabled = false;
                continue;
            }

            row.KeyText.FontWeight = FontWeights.Normal;

            var assigned = _settings.FindHotKey(row.Action.Id);
            if (assigned is { } spec)
            {
                row.KeyText.Text = spec.ToString();
                row.KeyText.Foreground = Brushes.Black;
                row.ResetButton.IsEnabled = true;
            }
            else
            {
                // 未設定のときは、実際に試される既定の候補を見せる
                row.KeyText.Text = "既定: " + string.Join(" → ", row.Action.DefaultKeys);
                row.KeyText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                row.ResetButton.IsEnabled = false;
            }
        }
    }

    private void OnChangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotKeyAction action })
        {
            return;
        }

        _recording = action;
        RefreshRows();
        ShowMessage($"{action.DisplayName}: 使いたいキーを押してください。");

        // ボタンにフォーカスが残っていると Space / Enter がボタンに食われる
        Keyboard.Focus(this);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotKeyAction action })
        {
            return;
        }

        _settings.SetHotKey(action.Id, null);
        Changed = true;
        _recording = null;
        RefreshRows();
        ShowMessage($"{action.DisplayName}: 既定のキーに戻しました。");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        // 記録中はどのキーも画面の操作に渡さない（Space でボタンが押されるのを防ぐ）
        e.Handled = true;

        var key = ResolveKey(e);

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            var cancelled = _recording;
            _recording = null;
            RefreshRows();
            ShowMessage($"{cancelled.DisplayName}: 変更を中止しました。");
            return;
        }

        TryAssign(key);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        e.Handled = true;

        // PrintScreen は押した瞬間の WM_KEYDOWN が来ないため、離したときに拾う
        if (ResolveKey(e) == Key.Snapshot)
        {
            TryAssign(Key.Snapshot);
        }
    }

    /// <summary>
    /// 実際に押された物理キーを取り出す。
    /// Alt との組み合わせは Key.System に、IME が有効だと Key.ImeProcessed に化ける。
    /// </summary>
    private static Key ResolveKey(KeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            return e.SystemKey;
        }

        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return e.ImeProcessedKey;
        }

        return e.Key;
    }

    private void TryAssign(Key key)
    {
        if (_recording is not { } action)
        {
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || HotKeySpec.IsModifierKey(vk))
        {
            // 修飾キーを押している途中。まだ確定しない。
            return;
        }

        var spec = new HotKeySpec(CurrentModifiers(), vk);

        if (spec.Validate() is { } problem)
        {
            ShowMessage($"{spec} は使えません。{problem}");
            return;
        }

        if (FindConflict(action, spec) is { } conflict)
        {
            ShowMessage($"{spec} は「{conflict.DisplayName}」と重なっています。別のキーにしてください。");
            return;
        }

        if (!_isAvailable(spec))
        {
            ShowMessage($"{spec} は他のアプリが使っているため登録できません。別のキーにしてください。");
            return;
        }

        _settings.SetHotKey(action.Id, spec);
        Changed = true;
        _recording = null;
        RefreshRows();
        ShowMessage($"{action.DisplayName} を {spec} に割り当てました。");
    }

    private static HotKeyModifiers CurrentModifiers()
    {
        var modifiers = HotKeyModifiers.None;
        var pressed = Keyboard.Modifiers;

        if ((pressed & ModifierKeys.Control) != 0)
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if ((pressed & ModifierKeys.Shift) != 0)
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if ((pressed & ModifierKeys.Alt) != 0)
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if ((pressed & ModifierKeys.Windows) != 0)
        {
            modifiers |= HotKeyModifiers.Win;
        }

        return modifiers;
    }

    /// <summary>
    /// 他の操作と同じキーになっていないか調べる。
    /// 未設定の操作は、実際に最初に試される既定のキーと比べる。
    /// </summary>
    private HotKeyAction? FindConflict(HotKeyAction target, HotKeySpec spec)
    {
        foreach (var action in HotKeyActions.All)
        {
            if (ReferenceEquals(action, target))
            {
                continue;
            }

            var effective = _settings.FindHotKey(action.Id) ?? action.DefaultSpecs().FirstOrDefault();
            if (effective.IsAssigned && effective == spec)
            {
                return action;
            }
        }

        return null;
    }

    private void ShowMessage(string message) => MessageText.Text = message;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
