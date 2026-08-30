using System.Windows;
using System.Windows.Input;
using focus_desktop.Services;

namespace focus_desktop;

/// <summary>
/// 退出验证（Step 6）。独立置顶窗口：
/// 1) 不受主窗口 Grid 布局影响（旧版弹窗被压进顶栏行导致不可见——2026-08-30 事故根因）
/// 2) 不受 WindowsFormsHost airspace 影响（永远画在 WebView2 之上）
/// 用法：var w = new ExitWindow(phrase); if (w.ShowDialog() == true) → 允许退出
/// </summary>
public partial class ExitWindow : Window
{
    private readonly string _expected;

    public ExitWindow()
    {
        InitializeComponent();
        _expected = "";
    }

    public ExitWindow(string phrase) : this()
    {
        _expected = phrase.Trim();
        PhraseText.Text = phrase;
        Loaded += (_, _) => InputBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void TryConfirm()
    {
        if (InputBox.Text.Trim() == _expected)
        {
            DialogResult = true;
        }
        else
        {
            ErrorText.Text = "输入不一致，请核对后重试";
            InputBox.SelectAll();
            InputBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter) { TryConfirm(); e.Handled = true; }
    }
}
