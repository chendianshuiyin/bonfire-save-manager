using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Bonfire.Core;
using Microsoft.Win32;

namespace Bonfire.App;

public record TargetSetting(string GameId, string Path);
public record Settings(string repository, List<TargetSetting>? targets = null, string? vaultPath = null);

public partial class MainWindow : Window
{
    private readonly string dataRoot = Environment.GetEnvironmentVariable("BONFIRE_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BonfireSave");
    private Vault vault;
    private readonly List<SaveFile> manualTargets = [];
    private bool busy;
    public MainWindow()
    {
        InitializeComponent();
        vault = new Vault(Path.Combine(dataRoot, "vault"));
        RepoBox.Text = "";
        try
        {
            var settings = Path.Combine(dataRoot, "settings.json");
            if (File.Exists(settings))
            {
                var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings));
                RepoBox.Text = config?.repository ?? "";
                if (!string.IsNullOrWhiteSpace(config?.vaultPath)) vault = new Vault(config.vaultPath);
                foreach (var item in config?.targets ?? []) if (File.Exists(item.Path)) manualTargets.Add(new(Game.ById(item.GameId), item.Path));
            }
            Scan(); Refresh();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        Closing += (_, e) => { if (busy) { e.Cancel = true; Status.Text = "正在处理存档，请等待操作完成再关闭。"; } };
    }
    private void SaveSettings() => File.WriteAllText(Path.Combine(dataRoot, "settings.json"), JsonSerializer.Serialize(new Settings(RepoBox.Text.Trim(), manualTargets.DistinctBy(s => s.Path).Select(s => new TargetSetting(s.Game.Id, s.Path)).ToList(), vault.Root), Vault.Json));
    private GitHubVault Cloud() => new(RepoBox.Text.Trim());
    private SaveFile Target() => Targets.SelectedItem as SaveFile ?? throw new InvalidOperationException("请先扫描或手动定位游戏存档。");
    private Snapshot Selected() => Backups.SelectedItem as Snapshot ?? throw new InvalidOperationException("请先选择一份本地备份。");
    private void Refresh(string? selectId = null)
    {
        var items = vault.List(); Backups.ItemsSource = items;
        Backups.SelectedItem = items.FirstOrDefault(i => i.Id == selectId) ?? items.FirstOrDefault();
        CountText.Text = $"{items.Count} 份备份 · 本地保存";
        VaultPathText.Text = vault.Root;
    }
    private void Scan()
    {
        var previous = (Targets.SelectedItem as SaveFile)?.Path;
        var items = SaveScanner.Scan().Concat(manualTargets).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Targets.ItemsSource = items;
        Targets.SelectedItem = items.FirstOrDefault(s => s.Path == previous) ?? items.FirstOrDefault();
        TargetPath.Text = items.Count == 0 ? "未找到存档。请先运行游戏创建存档，或点击手动定位。" : Target().Path;
        Status.Text = $"扫描完成，找到 {items.Count} 个游戏存档。";
    }
    private async Task Run(string description, Func<Task> action)
    {
        if (busy) return;
        busy = true; Workspace.IsEnabled = false; Status.Text = description + "…";
        try { await action(); }
        catch (Exception ex) { Status.Text = "操作未完成：" + ex.Message; MessageBox.Show(this, ex.Message, "篝火 · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; Workspace.IsEnabled = true; }
    }
    private Game? SelectGame(string path)
    {
        var candidates = Game.Identify(path).ToList();
        if (candidates.Count == 0) throw new InvalidDataException("文件名无法识别。请选择保留原始主文件名的存档，例如 DS30000.sl2、ER0000.co2、S0000.sl2。");
        if (candidates.Count == 1) return candidates[0];
        var dialog = new Window { Title = "确认游戏版本", Width = 460, Height = 220, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "受死版与重制版使用相同文件名，请选择来源版本。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Black });
        var select = new ComboBox { ItemsSource = candidates, SelectedIndex = -1, Margin = new Thickness(0, 14, 0, 14) }; panel.Children.Add(select);
        var confirm = new Button { Content = "确认版本" }; confirm.Click += (_, _) => { if (select.SelectedItem != null) dialog.DialogResult = true; }; panel.Children.Add(confirm);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? select.SelectedItem as Game : null;
    }
    private void Target_Changed(object sender, SelectionChangedEventArgs e) { if (TargetPath != null && Targets.SelectedItem is SaveFile save) TargetPath.Text = save.Path; }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await Run("扫描", () => { Scan(); return Task.CompletedTask; });
    private async void Locate_Click(object sender, RoutedEventArgs e) => await Run("定位", () =>
    {
        var dialog = new OpenFileDialog { Filter = "原版 / 联机存档|*.sl2;*.co2|自定义 Mod 存档|*.*", Title = "选择游戏实际使用的存档" };
        if (dialog.ShowDialog(this) != true) { Status.Text = "已取消定位。"; return Task.CompletedTask; }
        var game = SelectGame(dialog.FileName);
        if (game != null) { _ = Vault.ReadSave(dialog.FileName); var save = new SaveFile(game, dialog.FileName); manualTargets.Add(save); SaveSettings(); Scan(); Targets.SelectedItem = ((IEnumerable<SaveFile>)Targets.ItemsSource).First(s => s.Path == save.Path); }
        return Task.CompletedTask;
    });
    private async void Capture_Click(object sender, RoutedEventArgs e) => await Run("提取存档", async () =>
    {
        var target = Target(); var label = LabelBox.Text;
        var item = await Task.Run(() => vault.Capture(target.Path, target.Game, label)); Refresh(item.Id); Status.Text = "已保存：" + item.Label;
    });
    private async void Import_Click(object sender, RoutedEventArgs e) => await Run("导入存档", async () =>
    {
        var dialog = new OpenFileDialog { Filter = "原版 / 联机存档|*.sl2;*.co2|自定义 Mod 存档|*.*" };
        if (dialog.ShowDialog(this) != true) { Status.Text = "已取消导入。"; return; }
        var game = SelectGame(dialog.FileName); if (game == null) { Status.Text = "已取消导入。"; return; }
        var label = LabelBox.Text;
        var item = await Task.Run(() => vault.Capture(dialog.FileName, game, label)); Refresh(item.Id); Status.Text = "已导入备份库，可选择后替换游戏存档。";
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Run("替换存档", async () =>
    {
        var item = Selected(); var target = Target();
        if (item.GameId != target.Game.Id) throw new InvalidOperationException("备份与目标的游戏版本不同，不能替换。");
        var accountNote = item.Account != target.Account ? "\n来源与目标目录账号标识不同，游戏可能拒绝读取；本工具不会转换账号绑定。\n" : "";
        if (MessageBox.Show(this, $"备份：{item.Label}\n游戏：{target.Game.Name}\n目标：{target.Path}\n{accountNote}\n将替换整份存档（所有角色）。旧存档会先保存到本地备份库。请确认游戏已完全退出。", "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { Status.Text = "已取消替换。"; return; }
        var backup = await Task.Run(() => vault.Restore(item, target)); Refresh(backup.Id); Status.Text = "替换完成。需要撤销时，选择“替换前”备份并再次替换。";
    });
    private async void Export_Click(object sender, RoutedEventArgs e) => await Run("导出存档", async () =>
    {
        var item = Selected(); var filename = Game.ById(item.GameId).FileName; var extension = Path.GetExtension(filename);
        var dialog = new SaveFileDialog { Filter = $"存档 (*{extension})|*{extension}", FileName = filename, Title = "导出到新文件（不覆盖现有文件）" };
        if (dialog.ShowDialog(this) != true) { Status.Text = "已取消导出。"; return; }
        await Task.Run(() => vault.Export(item, dialog.FileName)); Status.Text = "已导出：" + dialog.FileName;
    });
    private void OpenVault_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { vault.Root }, UseShellExecute = true });
    private async Task ShowInspection(byte[] bytes, Game game)
    {
        var result = await Task.Run(() => SaveInspector.Inspect(bytes, game));
        var lines = result.Characters.Select(c => $"槽位 {c.Slot}  ·  {c.Name}\n等级：{c.Level}    游玩时间：{c.PlayTime}\n位置：{c.Location}\n{c.Extra}\n");
        var text = game.Name + "\n\n" + result.Summary + "\n\n" + string.Join("\n", lines);
        var view = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20), FontSize = 15 };
        new Window { Title = "角色信息 · 只读解析", Width = 780, Height = 550, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Content = view }.ShowDialog();
        Status.Text = "角色信息解析完成，未修改存档。";
    }
    private async void InspectTarget_Click(object sender, RoutedEventArgs e) => await Run("解析当前存档", async () => { var target = Target(); await ShowInspection(await Task.Run(() => Vault.ReadSave(target.Path)), target.Game); });
    private async void InspectBackup_Click(object sender, RoutedEventArgs e) => await Run("解析备份", async () => { var item = Selected(); await ShowInspection(await Task.Run(() => vault.Read(item)), Game.ById(item.GameId)); });
    private void Mods_Click(object sender, RoutedEventArgs e) => new ModsWindow { Owner = this }.ShowDialog();
    private async void ChooseVault_Click(object sender, RoutedEventArgs e) => await Run("选择备份目录", () =>
    {
        var dialog = new OpenFolderDialog { Title = "选择备份库目录（切换目录不会移动或删除旧备份）", InitialDirectory = vault.Root };
        if (dialog.ShowDialog(this) != true) { Status.Text = "已取消选择。"; return Task.CompletedTask; }
        var next = new Vault(dialog.FolderName); _ = next.List(); vault = next; SaveSettings(); Refresh();
        Status.Text = "已切换备份库。原目录的备份仍保留，可随时切换回来。"; return Task.CompletedTask;
    });
    private async void Connect_Click(object sender, RoutedEventArgs e) => await Run("检查仓库", async () => { await Cloud().CheckPrivate(); SaveSettings(); Status.Text = "已连接私有仓库：" + RepoBox.Text; });
    private async void CreateRepo_Click(object sender, RoutedEventArgs e) => await Run("创建私有仓库", async () => { await Cloud().CreatePrivate(); SaveSettings(); Status.Text = "私有仓库已创建：" + RepoBox.Text; });
    private async void Upload_Click(object sender, RoutedEventArgs e) => await Run("上传备份", async () => { var item = Selected(); await Cloud().Upload(vault, item); SaveSettings(); Status.Text = "已上传并保留本地备份：" + item.Label; });
    private async void Sync_Click(object sender, RoutedEventArgs e) => await Run("双向同步", async () =>
    {
        SaveSettings();
        try
        {
            var result = await Cloud().Sync(vault, new Progress<string>(message => Status.Text = message));
            Status.Text = $"同步完成：上传 {result.Uploaded} 份，下载 {result.Downloaded} 份。游戏存档未替换。";
        }
        finally { Refresh(); }
    });
    private async void Cloud_Click(object sender, RoutedEventArgs e) => await Run("读取云端列表", async () =>
    {
        var cloud = Cloud(); var items = await cloud.List(); SaveSettings();
        if (items.Count == 0) { Status.Text = "云端还没有备份，请先上传一份本地备份。"; return; }
        var dialog = new Window { Title = "云端备份 · 下载后在本地库查看详情", Owner = this, Width = 620, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var dock = new DockPanel { Margin = new Thickness(20) }; var button = new Button { Content = "下载所选到本地备份库", Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(button, Dock.Bottom); dock.Children.Add(button);
        var list = new ListBox { ItemsSource = items, DisplayMemberPath = "Display", SelectedIndex = 0, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28,34,43)) }; dock.Children.Add(list); button.Click += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; }; dialog.Content = dock;
        if (dialog.ShowDialog() != true) { Status.Text = "已取消下载。"; return; }
        Status.Text = "正在下载并校验备份…";
        var item = await cloud.Download(vault, (CloudSave)list.SelectedItem); Refresh(item.Id); Status.Text = "已下载到备份库，游戏存档尚未替换：" + item.Label;
    });
    private async void Login_Click(object sender, RoutedEventArgs e) => await Run("启动 GitHub 登录", () =>
    {
        Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, ArgumentList = { "-NoExit", "-Command", "gh auth login --hostname github.com --git-protocol https --web" } });
        Status.Text = "请在弹出的终端完成 GitHub 登录，然后点击连接仓库。"; return Task.CompletedTask;
    });
}
