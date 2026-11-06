using System.Windows;
using System.Windows.Input;

namespace Torimaru.App.Editor;

/// <summary>
/// プリセットに付ける名前を 1 行入力するだけの小さなダイアログ。
/// 登録と名前の変更の両方で使う。
/// </summary>
public partial class PresetNameWindow : Window
{
    /// <param name="prompt">何を入力してほしいかの説明。</param>
    /// <param name="detail">その下に薄く出す補足（プリセットの中身など）。</param>
    /// <param name="initialName">最初から入れておく名前。</param>
    public PresetNameWindow(string prompt, string detail, string initialName = "")
    {
        InitializeComponent();

        PromptText.Text = prompt;
        DetailText.Text = detail;
        NameBox.Text = initialName;
    }

    /// <summary>入力された名前（キャンセルされた場合は null）。</summary>
    public string? PresetName { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NameBox.SelectAll();
        NameBox.Focus();
        UpdateOkState();
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateOkState();

    /// <summary>名前が空のまま登録させない（押せるのに何も起きないボタンを作らない）。</summary>
    private void UpdateOkState()
    {
        if (OkButton is not null)
        {
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Accept()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        PresetName = name;
        DialogResult = true;
        Close();
    }
}
