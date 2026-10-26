using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Bonfire.Core;
using Microsoft.Win32;

namespace Bonfire.App;
public partial class ModsWindow : Window
{
    private readonly List<GameInstall> manual = [];
    private bool busy;
    public ModsWindow()
    {
        InitializeComponent();
        try { Scan(); } catch (Exception ex) { StatusText.Text = ex.Message; }
        Closing += (_, e) => { if (busy) { e.Cancel = true; StatusText.Text = "正在处理文件，请等待操作完成。"; } };
    }
    private GameInstall Target() => Installations.SelectedItem as GameInstall ?? throw new IOException("请先选择游戏目录。");
    private void Scan()
    {
        var previous = (Installations.SelectedItem as GameInstall)?.Directory;
        var items = SteamGames.Scan().Concat(manual).DistinctBy(g => g.Directory, StringComparer.OrdinalIgnoreCase).ToList();
        Installations.ItemsSource = items; Installations.SelectedItem = items.FirstOrDefault(g => g.Directory == previous) ?? items.FirstOrDefault();
        if (items.Count == 0) StatusText.Text = "没有找到 Steam 游戏，请手动选择游戏目录。";
    }
    private void ShowInventory()
    {
        if (Installations.SelectedItem is not GameInstall target) return;
        DirectoryText.Text = target.Directory; var items = new ModManager(target).Inventory(); ModList.ItemsSource = items;
        StatusText.Text = $"识别到 {items.Count} 项。未知 DLL 仅作为候选，不能据此确认具体 Mod。";
    }
    private async Task Run(string label, Func<Task> action)
    {
        if (busy) return; busy = true; Body.IsEnabled = false; StatusText.Text = label + "…";
        try { await action(); } catch (Exception ex) { StatusText.Text = "操作未完成：" + ex.Message; MessageBox.Show(this, ex.Message, "Mod 管理", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; Body.IsEnabled = true; }
    }
    private void Installation_Changed(object sender, SelectionChangedEventArgs e) { try { ShowInventory(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run("扫描", () => { Scan(); ShowInventory(); return Task.CompletedTask; });
    private async void Locate_Click(object sender, RoutedEventArgs e) => await Run("选择目录", () =>
    {
        var dialog = new OpenFileDialog { Title = "选择游戏主程序，例如 DarkSoulsIII.exe / eldenring.exe", Filter = "游戏主程序|*.exe" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消选择。"; return Task.CompletedTask; }
        var candidates = Game.All.Where(g => string.Equals(g.ProcessName, Path.GetFileNameWithoutExtension(dialog.FileName), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) throw new IOException("这不是支持的游戏主程序。");
        var game = candidates[0];
        if (candidates.Length > 1)
        {
            var choice = MessageBox.Show(this, "这是 Dark Souls II 原罪学者版吗？选择“否”表示 2014 原版。", "选择版本", MessageBoxButton.YesNoCancel);
            if (choice == MessageBoxResult.Cancel) return Task.CompletedTask;
            game = Game.ById(choice == MessageBoxResult.Yes ? "ds2s" : "ds2");
        }
        var install = new GameInstall(game, Path.GetDirectoryName(dialog.FileName)!); manual.Add(install); Scan();
        Installations.SelectedItem = ((IEnumerable<GameInstall>)Installations.ItemsSource).First(g => g.Directory == install.Directory); return Task.CompletedTask;
    });
    private async void Install_Click(object sender, RoutedEventArgs e) => await Run("准备安装", async () =>
    {
        var target = Target(); var manager = new ModManager(target);
        var dialog = new OpenFileDialog { Filter = "Mod 安装包 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消安装。"; return; }
        var preview = await Task.Run(() => manager.Preview(dialog.FileName));
        var detail = $"游戏：{target.Game.Name}\n目录：{target.Directory}\n将安装 {preview.Count} 个文件，并备份将被覆盖的文件。\n\n" + string.Join("\n", preview.Take(14)) + (preview.Count > 14 ? "\n…" : "") + "\n\n请确认这些路径应直接放在游戏目录内。";
        if (MessageBox.Show(this, detail, "确认 Mod 安装布局", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { StatusText.Text = "已取消安装。"; return; }
        var name = ModName.Text; var version = ModVersion.Text;
        await Task.Run(() => manager.Install(dialog.FileName, name, version)); ShowInventory(); StatusText.Text = "安装完成。Mod 启动方式请遵循其作者说明。";
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Run("准备移除", async () =>
    {
        if (ModList.SelectedItem is not ModInfo item) throw new IOException("请先选择 Mod。");
        if (item.ManagedId == null) throw new IOException("这项 Mod 是手工安装的，没有可靠的文件归属和原文件备份；请按作者说明手动移除。通过魂匣安装的 Mod 可一键移除。" );
        if (MessageBox.Show(this, $"移除 {item.Name}？\n将恢复安装前的原文件，并删除由此安装新增且未经修改的文件。", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { StatusText.Text = "已取消移除。"; return; }
        var manager = new ModManager(Target()); await Task.Run(() => manager.Remove(item.ManagedId)); ShowInventory(); StatusText.Text = "已移除并恢复原文件，安装记录和备份仍保留在 .bonfire-mods。";
    });
    private async void Open_Click(object sender, RoutedEventArgs e) => await Run("打开目录", () => { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Target().Directory } }); return Task.CompletedTask; });
}
