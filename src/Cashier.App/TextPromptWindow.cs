using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cashier.Core;

namespace Cashier.App;

/// <summary>通用文本输入对话框（反审核/作废/解除关联等原因）。</summary>
public class TextPromptWindow : Window
{
    private readonly TextBox _box;
    private string? _result;

    private TextPromptWindow(string title, string prompt, string? preset)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Ui.Br("B.Bg");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Br("B.Ink2"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _box = new TextBox { Height = 62, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Text = preset ?? "" };
        root.Children.Add(_box);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "确定", Style = (Style)Application.Current.Resources["Btn.Primary"], Width = 78, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_box.Text))
            {
                MessageBox.Show("请填写内容。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _result = _box.Text.Trim();
            DialogResult = true;
        };
        var cancel = new Button { Content = "取消", Width = 78, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(string title, string prompt, Window? owner, string? preset = null)
    {
        var w = new TextPromptWindow(title, prompt, preset) { Owner = owner };
        return w.ShowDialog() == true ? w._result : null;
    }
}
