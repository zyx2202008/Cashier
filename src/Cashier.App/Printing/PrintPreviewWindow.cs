using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cashier.App.Printing;

public class PrintPreviewWindow : Window
{
    private readonly FixedDocument _doc;

    public PrintPreviewWindow(FixedDocument doc, string title)
    {
        _doc = doc;
        Title = "打印预览 · " + title;
        Width = 1080;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Br("B.Bg");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        var root = new DockPanel();

        var bar = new Border
        {
            Background = Brushes.White,
            BorderBrush = Ui.Br("B.Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8),
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var print = new Button { Content = "打印…", Width = 90, Style = (Style)Application.Current.Resources["Btn.Primary"] };
        print.Click += (_, _) => DoPrint();
        var close = new Button { Content = "关闭", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        btns.Children.Add(print);
        btns.Children.Add(close);
        bar.Child = btns;
        DockPanel.SetDock(bar, Dock.Top);

        var viewer = new DocumentViewer { Document = _doc, Background = Ui.Br("B.Bg") };
        root.Children.Add(bar);
        root.Children.Add(viewer);
        Content = root;
    }

    private void DoPrint()
    {
        try
        {
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            var doc = _doc;
            dlg.PrintDocument(doc.DocumentPaginator, Title);
            Ui.Info("打印任务已发送到打印机。", "打印");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打印失败：" + ex.Message, "打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
