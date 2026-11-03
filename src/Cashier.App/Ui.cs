using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cashier.Core;

namespace Cashier.App;

public static class Ui
{
    public static SolidColorBrush Br(string key) => (SolidColorBrush)Application.Current.Resources[key];

    public static Button Btn(string text, RoutedEventHandler? click = null, string? style = null, double? width = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = (Style)Application.Current.Resources[style];
        if (width.HasValue) b.Width = width.Value;
        if (click != null) b.Click += click;
        return b;
    }

    public static TextBlock Txt(string text, string? style = null, double? size = null)
    {
        var t = new TextBlock { Text = text };
        if (style != null) t.Style = (Style)Application.Current.Resources[style];
        if (size.HasValue) t.FontSize = size.Value;
        return t;
    }

    public static Border Card(UIElement child, double padding = 0)
    {
        var b = new Border
        {
            Style = (Style)Application.Current.Resources["Card"],
            Padding = new Thickness(padding),
            Child = child,
        };
        return b;
    }

    public static StackPanel Field(string label, FrameworkElement input, double width = double.NaN)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        if (!double.IsNaN(width)) sp.Width = width;
        sp.Children.Add(Txt(label, "T.Label"));
        sp.Children.Add(input);
        return sp;
    }

    /// <summary>1.1.3：标题不再与主窗口同名，避免用户与自动化都分不清"弹窗"还是"主界面"。</summary>
    public static void Info(string msg, string title = "出纳管理 · 提示")
        => MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string msg, string title = "请确认")
        => MessageBox.Show(msg, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public static void Error(Exception ex, string title = "操作未完成")
    {
        var msg = ex is AppException ? ex.Message : ex.Message + "\n\n（详细信息已写入日志）";
        MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static DataGridTextColumn Col(string header, string path, double width, bool right = false, string? color = null)
    {
        var col = right
            ? new NumericColumn { RightAligned = true }
            : new DataGridTextColumn();
        col.Header = header;
        col.Binding = new System.Windows.Data.Binding(path);
        col.Width = width <= 0 ? DataGridLength.Auto : new DataGridLength(width);
        // 固定列宽同时锁定最小宽度：窗口变窄或高 DPI 缩放时由表格横向滚动承载，
        // 而不是把每列压缩到无法阅读（原先 10 列各被压缩约 34px，日期/单号/状态全部截断）。
        if (width > 0) col.MinWidth = width;
        if (right)
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            st.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI")));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
            st.Setters.Add(new Setter(TextBlock.ToolTipProperty,new System.Windows.Data.Binding("Text"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self)}));
            if (color != null) st.Setters.Add(new Setter(TextBlock.ForegroundProperty, Br(color)));
            col.ElementStyle = st;
        }
        else if (color != null)
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty, Br(color)));
            col.ElementStyle = st;
        }
        if(!right)
        {
            var st=new Style(typeof(TextBlock),col.ElementStyle);
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
            st.Setters.Add(new Setter(TextBlock.ToolTipProperty,new System.Windows.Data.Binding("Text"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self)}));
            col.ElementStyle=st;
        }
        return col;
    }

    public static DataGridTextColumn Star(string header, string path)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(path),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        };
        col.MinWidth=140;
        var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty,new System.Windows.Data.Binding("Text"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self)}));col.ElementStyle=style;
        return col;
    }
}

public class NumericColumn : DataGridTextColumn
{
    public bool RightAligned { get; set; } = true;
    public NumericColumn()
    {
        var header=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),Application.Current?.TryFindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)) as Style);
        header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Right));HeaderStyle=header;
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        ElementStyle=text;
    }
}

public class ComboItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}
