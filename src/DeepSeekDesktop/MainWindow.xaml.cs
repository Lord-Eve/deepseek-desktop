using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeepSeekDesktop.Core;
using Forms = System.Windows.Forms;

namespace DeepSeekDesktop;

public partial class MainWindow : Window
{
    private Settings settings = new();
    private readonly EngineHost engine = new();
    private readonly ConcurrentQueue<string> logs = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool busy, polling, quitting, initialized;
    private bool stopInProgress;
    private int editing = -1;
    private CancellationTokenSource? testCancellation;
    private PoolStatus? poolStatus;
    private readonly CancellationTokenSource updateCancellation = new();
    private bool checkingUpdates, startupUpdateChecked;
    private string updatePageUrl = UpdateFeed.ReleasesUrl;
    private bool loadingProvider;

    public MainWindow()
    {
        InitializeComponent();
        engine.Log += line => { if (logs.Count < 2000) logs.Enqueue(line); };
        engine.RefreshTokenChanged += (old, next) => Dispatcher.Invoke(() =>
        {
            var candidate = Clone();
            var account = candidate.Accounts.Single(a => a.Enabled && a.Provider == Providers.Qingyan && a.RefreshToken == old);
            account.RefreshToken = next;
            PrivateStorage.Save(candidate);
            settings = candidate;
        });
        Loaded += (_, _) => Initialize();
        timer.Tick += async (_, _) => await RefreshAsync();
    }

    private void Initialize()
    {
        try
        {
            settings = PrivateStorage.Load();
            PrivateStorage.Save(settings);
            var runtime = Path.Combine(PrivateStorage.Root, "runtime");
            if (Directory.Exists(runtime)) foreach (var dir in Directory.EnumerateDirectories(runtime))
            {
                if (Guid.TryParseExact(Path.GetFileName(dir), "N", out _))
                    try { PrivateStorage.CleanRuntime(dir); } catch (IOException) { logs.Enqueue("历史运行目录未能清除，请停止其他后端实例后重试。"); }
            }
            initialized = true;
            LoadSettings(); LoadAccounts(); ResetAccount(); SetupTray(); UpdateState(); timer.Start();
            if (!startupUpdateChecked) { startupUpdateChecked = true; _ = CheckForUpdatesAsync(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法读取或保护本机设置。为避免覆盖现有账号数据，程序将退出。\n\n" + ex.Message, "DeepSeek Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            quitting = true; Application.Current.Shutdown();
        }
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 DeepSeek Desktop", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("停止服务", null, async (_, _) => await StopServiceAsync());
        menu.Items.Add("退出程序", null, async (_, _) => await QuitAsync());
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DeepSeekDesktop;component/Assets/deepseek-whale-girl.ico")).Stream;
        using var icon = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize);
        trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "DeepSeek Desktop · 已停止", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void LoadSettings()
    {
        loadingProvider = true;
        ProviderBox.SelectedIndex = settings.Provider == Providers.Qingyan ? 1 : 0;
        loadingProvider = false;
        bool qingyan = settings.Provider == Providers.Qingyan;
        DeepSeekCredentials.Visibility = qingyan ? Visibility.Collapsed : Visibility.Visible;
        QingyanCredentials.Visibility = qingyan ? Visibility.Visible : Visibility.Collapsed;
        CredentialHelpButton.Content = qingyan ? "如何获取登录态" : "如何获取 device_id";
        ModelHint.Text = $"模型：{settings.Model}（网页端当前默认模式）";
        StartupHint.Text = qingyan ? "清言启动仅刷新并校验登录态，不发送对话。当前支持文本多轮及流式回复；暂不支持工具调用和图片。" : "DeepSeek 启动会登录启用账号，并发送后端自带的健康检查消息。";
        PortBox.Text = settings.Port.ToString(); QuotaBox.Text = settings.HourlyQuota.ToString(); ProxyBox.Text = settings.ProxyUrl;
        SearchEnabled.IsChecked = settings.SearchEnabled; EndpointBox.Text = settings.BaseUrl;
        ApiKeyBox.Text = settings.ApiKey;
    }
    private void LoadAccounts()
    {
        var accounts = settings.Accounts.Where(a => a.Provider == settings.Provider).ToList();
        AccountList.ItemsSource = null; AccountList.ItemsSource = accounts;
        AccountSummary.Text = $"{settings.ProviderName} · {accounts.Count} 个账号 · {accounts.Count(a => a.Enabled)} 个启用";
    }
    private void ResetAccount()
    {
        editing = -1; AccountList.SelectedIndex = -1; AccountEditorTitle.Text = "添加账号";
        AccountName.Text = "我的 " + settings.ProviderName; AccountLogin.Text = ""; AccountPassword.Clear(); AccountDevice.Clear(); AccountToken.Clear(); AccountEnabled.IsChecked = true;
    }
    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingProvider) return;
        try
        {
            var candidate = Clone(); candidate.Provider = ProviderBox.SelectedIndex == 1 ? Providers.Qingyan : Providers.DeepSeek;
            Save(candidate); ResetAccount(); poolStatus = null; TestReply.Text = "尚未测试"; UpdateState();
            Tell($"已切换到{settings.ProviderName}。原平台账号已保留；客户端模型请改为 {settings.Model}。");
        }
        catch (Exception ex) { LoadSettings(); Error(ex); }
    }
    private Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
    private void Save(Settings candidate)
    {
        if (engine.IsRunning || busy || stopInProgress) throw new InvalidOperationException("请先停止服务再修改配置。");
        PrivateStorage.Save(candidate); settings = candidate; LoadSettings(); LoadAccounts();
    }
    private void Tell(string text) { Notice.Text = text; }
    private void Error(Exception ex)
    {
        var safe = LogRedactor.Redact(ex.Message, settings, "");
        Tell(safe); logs.Enqueue(safe);
        MessageBox.Show(this, safe, "DeepSeek Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateState()
    {
        bool running = engine.IsRunning, ready = running && engine.IsReady;
        StartButton.IsEnabled = !running && !busy && !stopInProgress;
        ProviderBox.IsEnabled = !running && !busy && !stopInProgress;
        StopButton.IsEnabled = (running || busy) && !stopInProgress;
        ProbeButton.IsEnabled = ready && !busy && !stopInProgress;
        TestButton.IsEnabled = ready && poolStatus?.HasUsableAccount == true && !busy && !stopInProgress && testCancellation == null;
        AccountEditor.IsEnabled = SettingsEditor.IsEnabled = NewAccountButton.IsEnabled = DeleteAccountButton.IsEnabled = !running && !busy && !stopInProgress;
        ServiceStatus.Text = stopInProgress ? "停止中" : busy ? "正在启动" : ready ? poolStatus?.Title ?? "账号状态待确认" : running ? "初始化中" : "已停止";
        StatusPill.Background = new SolidColorBrush(ready
            ? poolStatus is { HasUsableAccount: true, HasFailures: false } ? Color.FromRgb(225, 246, 236) : Color.FromRgb(255, 237, 213)
            : Color.FromRgb(237, 240, 246));
        ServiceDetail.Text = ready ? poolStatus?.Detail ?? "本地端口已启动，账号状态尚未确认；请等待状态读取或查看日志。" : busy ? "正在连接上游并初始化账号，最长等待 3 分钟；可随时停止。" : "服务已停止，账号和连接设置可修改。";
        if (tray != null) tray.Text = "DeepSeek Desktop · " + ServiceStatus.Text;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        poolStatus = null;
        busy = true; UpdateState(); TotalCount.Text = SuccessCount.Text = FailedCount.Text = "0";
        try { await engine.StartAsync(settings); Tell("本地端口已启动，正在确认账号状态。"); }
        catch (Exception ex) { if (!quitting && !stopInProgress) Error(ex); }
        finally { busy = false; UpdateState(); }
        await RefreshAsync();
    }
    private async Task StopServiceAsync()
    {
        if (stopInProgress) return;
        stopInProgress = true; testCancellation?.Cancel(); UpdateState();
        try { await engine.StopAsync(); Tell("服务已停止。"); }
        catch (Exception ex) { if (!quitting) Error(ex); }
        finally { poolStatus = null; stopInProgress = false; UpdateState(); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopServiceAsync();
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        try { var models = await engine.ModelsAsync(); Tell("本地接口正常，可用模型：" + string.Join("、", models) + "。此检查未发送对话消息。"); }
        catch (Exception ex) { Error(ex); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        testCancellation = new CancellationTokenSource(); UpdateState(); TestReply.Text = $"等待{settings.ProviderName}回复…";
        try { TestReply.Text = await engine.SendTestAsync(testCancellation.Token); Tell("已收到上游响应。"); }
        catch (OperationCanceledException) { TestReply.Text = "测试已取消。"; }
        catch (Exception ex) { TestReply.Text = "测试未成功，请查看运行日志。"; Error(ex); }
        finally { testCancellation.Dispose(); testCancellation = null; UpdateState(); }
    }

    private async Task RefreshAsync()
    {
        while (logs.TryDequeue(out var line)) LogsBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        if (LogsBox.Text.Length > 120000) { var text = LogsBox.Text; var cut = text.IndexOf('\n', text.Length - 90000); LogsBox.Text = cut >= 0 ? text[(cut + 1)..] : text[^90000..]; }
        if (!busy && !stopInProgress && engine.IsReady && !engine.IsRunning)
        {
            try { await engine.StopAsync(); Tell("协议引擎已意外退出，运行配置已清理。请查看日志后重新启动。"); }
            catch (Exception ex) { Tell("引擎已退出，清理运行文件失败：" + ex.GetType().Name); }
        }
        UpdateState();
        if (polling || !engine.IsReady || !engine.IsRunning || stopInProgress) return;
        polling = true;
        try
        {
            var stats = await engine.AdminAsync("stats");
            TotalCount.Text = Number(stats, "total_requests"); SuccessCount.Text = Number(stats, "success_requests"); FailedCount.Text = Number(stats, "failed_requests");
            var status = await engine.AdminAsync("status");
            if (engine.IsRunning && !stopInProgress)
            {
                var next = PoolStatus.Parse(status);
                if (poolStatus != next) Tell(next.Detail);
                poolStatus = next;
                UpdateState();
            }
        }
        catch (Exception) { if (engine.IsRunning && !stopInProgress) { poolStatus = null; UpdateState(); Tell("状态读取暂时失败，账号可用性尚未确认，请检查本地接口或查看日志。"); } }
        finally { polling = false; }
    }
    private static string Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) ? value.ToString() : "0";

    private void Navigate_Click(object sender, RoutedEventArgs e) { if (sender is Button b) Pages.SelectedIndex = int.Parse((string)b.Tag); }
    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account a) return;
        editing = settings.Accounts.IndexOf(a); AccountEditorTitle.Text = "编辑账号"; AccountName.Text = a.Name; AccountLogin.Text = a.Login;
        AccountPassword.Clear(); AccountDevice.Clear(); AccountToken.Clear(); AccountEnabled.IsChecked = a.Enabled;
    }
    private void NewAccount_Click(object sender, RoutedEventArgs e) { ResetAccount(); AccountName.Focus(); }
    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = Clone();
            var old = editing >= 0 ? candidate.Accounts[editing] : null;
            var value = new Account { Provider = settings.Provider, RefreshToken = AccountToken.Password.Length > 0 ? AccountToken.Password.Trim() : old?.RefreshToken ?? "", Name = AccountName.Text.Trim(), Login = AccountLogin.Text.Trim(), Password = AccountPassword.Password.Length > 0 ? AccountPassword.Password : old?.Password ?? "", DeviceId = AccountDevice.Password.Length > 0 ? AccountDevice.Password.Trim() : old?.DeviceId ?? "", Enabled = AccountEnabled.IsChecked == true };
            if (value.Name.Length == 0) throw new ArgumentException("请填写账号名称。");
            if (settings.Provider == Providers.DeepSeek && old != null && old.Login != value.Login && (AccountPassword.Password.Length == 0 || AccountDevice.Password.Length == 0)) throw new ArgumentException("更换登录名时，请同时填写该账号的密码和 device_id。");
            if (editing >= 0) candidate.Accounts[editing] = value; else candidate.Accounts.Add(value);
            Save(candidate); ResetAccount(); Tell("账号已加密保存。启动服务后可查看账号池状态。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedIndex < 0) { Tell("请先选择账号。"); return; }
        if (MessageBox.Show(this, "从本机删除选中账号的保存信息？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { var candidate = Clone(); candidate.Accounts.RemoveAt(editing); Save(candidate); ResetAccount(); Tell("账号已从本机删除。"); } catch (Exception ex) { Error(ex); }
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(QuotaBox.Text, out var quota)) throw new ArgumentException("端口和请求上限应填写整数。");
            var candidate = Clone(); candidate.Port = port; candidate.HourlyQuota = quota; candidate.ProxyUrl = ProxyBox.Text.Trim(); candidate.SearchEnabled = SearchEnabled.IsChecked == true;
            Save(candidate); Tell("连接设置已保存。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void RegenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "重新生成后，所有客户端都需要更新 API Key。继续？", "重新生成 API Key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { var candidate = Clone(); candidate.ApiKey = Settings.NewKey(); Save(candidate); Tell("新的 Key 已保存，可回到服务概览复制。"); } catch (Exception ex) { Error(ex); }
    }
    private bool copying;
    private async Task CopyAsync(string value, string notice)
    {
        if (copying) { Tell("正在等待剪贴板，请稍候。"); return; }
        copying = true;
        try
        {
            Tell("正在复制…");
            Tell(await ClipboardWriter.CopyAsync(value) ? notice :
                "剪贴板正被其他程序占用，未能完成复制。请稍后重试，或暂时关闭剪贴板管理工具。");
        }
        catch (Exception) { Tell("复制未完成，请稍后重试。"); }
        finally { copying = false; }
    }
    private async void CopyUrl_Click(object sender, RoutedEventArgs e) => await CopyAsync(settings.BaseUrl, "API 地址已复制。");
    private async void CopyKey_Click(object sender, RoutedEventArgs e) => await CopyAsync(settings.ApiKey, "API Key 已复制，请粘贴到客户端的 Key 设置中。");
    private async void CopyLogs_Click(object sender, RoutedEventArgs e) => await CopyAsync(LogsBox.Text, "日志已复制；对外分享前请检查是否含个人信息。");
    private void ClearLogs_Click(object sender, RoutedEventArgs e) { logs.Clear(); LogsBox.Clear(); Tell("日志显示已清空。"); }
    private void OpenData_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(PrivateStorage.Root) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); } }
    private void OpenReadme_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "README.md")) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); } }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();
    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdates || quitting) return;
        checkingUpdates = true; CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查更新…";
        try
        {
            var release = await UpdateChecker.CheckAsync(updateCancellation.Token);
            if (quitting) return;
            bool newer = release.Version > Version.Parse(App.Version);
            updatePageUrl = newer ? release.PageUrl : UpdateFeed.ReleasesUrl;
            UpdateStatus.Text = newer ? $"发现新版本 {release.Version}，当前为 {App.Version}。点击“前往下载页”查看更新。" : $"当前 {App.Version} 已是最新版本。";
            SidebarUpdateButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (quitting) { }
        catch (Exception ex)
        {
            if (!quitting) UpdateStatus.Text = ex is InvalidOperationException or TimeoutException or InvalidDataException
                ? ex.Message : "未能读取更新信息，请稍后重试或前往下载页查看。";
        }
        finally { checkingUpdates = false; if (!quitting) CheckUpdatesButton.IsEnabled = true; }
    }
    private void ShowUpdates_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 2;
        UpdateStatus.BringIntoView();
    }
    private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(updatePageUrl) { UseShellExecute = true }); }
        catch (Exception) { Tell("无法打开浏览器，请访问 GitHub 仓库的 Releases 页面下载新版。"); }
    }
    private void DeviceHelp_Click(object sender, RoutedEventArgs e)
    {
        if (settings.Provider == Providers.Qingyan)
        {
            MessageBox.Show(this, "在你自己的浏览器登录 https://chatglm.cn，确认可以正常对话。\n\n按 F12 → Application（应用）→ Cookies 或 Local Storage，查找 chatglm_refresh_token，复制其值到本程序的清言 refresh token 输入框。不要复制 chatglm_token（短期访问令牌）。\n\n本程序不会读取浏览器数据。登录态按 Windows 用户加密保存；上游轮换令牌时自动保存新值。不要把 Token 发到聊天、截图或公开 Issue。\n\n如果该字段不存在，请先退出并重新登录网页；网页字段可能发生变化。", "清言登录态", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(this, "本版本需要已有 DeepSeek 账号的真实 device_id。\n\n在浏览器登录 chat.deepseek.com，开发者工具 → Network 中查看 users/login 请求体里的 device_id。每个账号分别获取。\n\n上游接口可能改变；若找不到该字段或登录触发验证码，本版本暂无法自动处理。不要把密码、Token 或 device_id 发到聊天或公开 Issue。\n\n更完整步骤见发布包的 README.md。", "设备信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true; Hide();
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true; timer.Stop(); testCancellation?.Cancel(); updateCancellation.Cancel();
        try { if (initialized) await engine.StopAsync(); }
        catch (Exception) { MessageBox.Show(this, "退出时未能完成运行文件清理，下次启动将再次尝试清理。", "DeepSeek Desktop", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { tray?.Dispose(); trayIcon?.Dispose(); engine.Dispose(); Application.Current.Shutdown(); }
    }
}
