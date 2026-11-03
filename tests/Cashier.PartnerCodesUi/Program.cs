using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cashier.App;
using Cashier.App.Views;
using Cashier.Core;
using Cashier.Core.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/Cashier;component/Theme/Styles.xaml",UriKind.Absolute)});
        AppSession.Db=new Database(args[0]+".data");AppSession.Db.EnsureCreated();
        new AuthService().Login("admin","admin888");
        int checks = 0, failures = 0;
        void Check(string name, bool ok) { checks++; if (!ok) failures++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | {name}"); }
        var ctor = typeof(MasterDataDialog).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(object) }, null)!;
        var fieldsField = typeof(MasterDataDialog).GetField("_fields", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MasterDataDialog Dialog(Partner? p) => (MasterDataDialog)ctor.Invoke(new object?[] { "partner", p });
        Dictionary<string, Control> Fields(MasterDataDialog dialog) => (Dictionary<string, Control>)fieldsField.GetValue(dialog)!;
        var add = Dialog(null);
        var fields = Fields(add);
        var code = (TextBox)fields["code"];
        var kind = (ComboBox)fields["kind"];
        Check("new form code is read-only and generated on save", code.IsReadOnly && code.Text == "保存时自动生成");
        kind.SelectedIndex = 1;
        Check("supplier selection remains read-only and generated on save", code.IsReadOnly && code.Text == "保存时自动生成");
        add.Show(); add.UpdateLayout();
        var image = new RenderTargetBitmap((int)add.ActualWidth, (int)add.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(add);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(args[0])) encoder.Save(stream);
        add.Close();
        var entity = new Partner { Id = 1, Code = "KH-0001", Name = "测试客户", Kind = "customer" };
        var edit = Dialog(entity);
        fields = Fields(edit); code = (TextBox)fields["code"]; kind = (ComboBox)fields["kind"];
        Check("edit form displays existing read-only code", code.IsReadOnly && code.Text == "KH-0001");
        kind.SelectedIndex = 1;
        Check("type change announces a new automatic code", code.IsReadOnly && code.Text == "保存时自动生成新编码");
        entity.Kind = "supplier"; entity.Code = "GYS-0001"; // simulate a failed save mutating the form object
        kind.SelectedIndex = 2;
        Check("failed save does not change the original code displayed by the form", code.Text == "KH-0001");
        edit.Close();
        Console.WriteLine($"RESULT checks={checks} failures={failures}");
        return failures == 0 ? 0 : 1;
    }
}
