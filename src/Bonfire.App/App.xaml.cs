using System.Windows;
namespace Bonfire.App;
public partial class App : Application
{
    private System.Threading.Mutex? singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        try { _ = Bonfire.Core.DataPaths.Resolve(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("SOULSVAULT_DATA_HOME") ?? Environment.GetEnvironmentVariable("BONFIRE_DATA_HOME")); }
        catch (Exception ex) { MessageBox.Show("无法读取数据目录配置：" + ex.Message, "魂匣"); Shutdown(); return; }
        singleInstance = new System.Threading.Mutex(true, "Local\\BonfireSave-" + Environment.UserName, out var created);
        if (!created) { MessageBox.Show("魂匣已在运行，请使用已有窗口。", "魂匣"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { singleInstance?.Dispose(); base.OnExit(e); }
}
