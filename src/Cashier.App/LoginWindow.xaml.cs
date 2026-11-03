using System.Windows;
using System.Windows.Input;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        BookName.Text = "本地单机版 · " + SettingsService.BookName;
        DataHint.Text = "数据目录：" + App.Db.DataDir;
        Foot.Text = "首次使用请用管理员账号 admin / admin888 登录，并在“系统工具 → 用户管理”中修改密码。";
        Loaded += (_, _) =>
        {
            TxtPwd.Focus();
            if (App.QaAutoLogin is { } cred)
            {
                TxtUser.Text = cred.user;
                TxtPwd.Password = cred.pwd;
                Dispatcher.BeginInvoke(new Action(DoLogin), System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void TxtPwd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoLogin();
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e) => DoLogin();

    private void DoLogin()
    {
        ErrText.Visibility = Visibility.Collapsed;
        try
        {
            var pwd = TxtPwd.Password;
            if (string.IsNullOrWhiteSpace(TxtUser.Text)) throw new AppException("请输入登录名。");
            if (string.IsNullOrEmpty(pwd)) throw new AppException("请输入密码。");
            new AuthService().Login(TxtUser.Text, pwd);
            DialogResult = true;
            Close();
        }
        catch (AppException ex)
        {
            ErrText.Text = ex.Message;
            ErrText.Visibility = Visibility.Visible;
            TxtPwd.SelectAll();
            TxtPwd.Focus();
        }
        catch (Exception ex)
        {
            ErrText.Text = "登录失败：" + ex.Message;
            ErrText.Visibility = Visibility.Visible;
        }
    }
}
