using System.Windows;
namespace Bonfire.App;
public partial class App : Application
{
    private System.Threading.Mutex? singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new System.Threading.Mutex(true, "Local\\BonfireSave-" + Environment.UserName, out var created);
        if (!created) { MessageBox.Show("篝火已在运行，请使用已有窗口。", "篝火"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { singleInstance?.Dispose(); base.OnExit(e); }
}
