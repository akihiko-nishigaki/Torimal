using System.Windows;

namespace Torimaru.App.Editor;

/// <summary>OCR やコード読み取りの結果を見せて、コピー・保存できるようにする。</summary>
public partial class TextResultWindow : Window
{
    public TextResultWindow(string title, string summary, string body)
    {
        InitializeComponent();

        Title = title;
        Summary.Text = summary;
        Body.Text = body;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(Body.Text);
                Summary.Text = "クリップボードへコピーしました";
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(60);
            }
        }

        Summary.Text = "クリップボードへのコピーに失敗しました";
    }

    private void OnSaveText(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "テキスト ファイル|*.txt",
            DefaultExt = ".txt",
            FileName = "ocr.txt",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, Body.Text, System.Text.Encoding.UTF8);
            Summary.Text = $"保存しました: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("テキストの保存に失敗", ex);
            MessageBox.Show(
                this,
                $"保存できませんでした。\n\n{ex.Message}",
                "Torimaru",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
