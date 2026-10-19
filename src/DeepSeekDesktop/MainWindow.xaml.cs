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
    private bool busy, polling, quitting, initialized;
    private bool stopInProgress;
    private int editing = -1;
    private CancellationTokenSource? testCancellation;

    public MainWindow()
    {
        InitializeComponent();
        engine.Log += line => { if (logs.Count < 2000) logs.Enqueue(line); };
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
        tray = new Forms.NotifyIcon { Text = "DeepSeek Desktop · 已停止", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void LoadSettings()
    {
        PortBox.Text = settings.Port.ToString(); QuotaBox.Text = settings.HourlyQuota.ToString(); ProxyBox.Text = settings.ProxyUrl;
        SearchEnabled.IsChecked = settings.SearchEnabled; EndpointBox.Text = settings.BaseUrl;
    }
    private void LoadAccounts()
    {
        AccountList.ItemsSource = null; AccountList.ItemsSource = settings.Accounts;
        AccountSummary.Text = $"{settings.Accounts.Count} 个账号 · {settings.Accounts.Count(a => a.Enabled)} 个启用";
    }
    private void ResetAccount()
    {
        editing = -1; AccountList.SelectedIndex = -1; AccountEditorTitle.Text = "添加账号";
        AccountName.Text = "我的 DeepSeek"; AccountLogin.Text = ""; AccountPassword.Clear(); AccountDevice.Clear(); AccountEnabled.IsChecked = true;
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
        StopButton.IsEnabled = (running || busy) && !stopInProgress;
        ProbeButton.IsEnabled = ready && !busy && !stopInProgress;
        TestButton.IsEnabled = ready && !busy && !stopInProgress && testCancellation == null;
        AccountEditor.IsEnabled = SettingsEditor.IsEnabled = NewAccountButton.IsEnabled = DeleteAccountButton.IsEnabled = !running && !busy && !stopInProgress;
        ServiceStatus.Text = stopInProgress ? "停止中" : busy ? "正在启动" : ready ? "运行中" : running ? "初始化中" : "已停止";
        StatusPill.Background = new SolidColorBrush(ready ? Color.FromRgb(225, 246, 236) : Color.FromRgb(237, 240, 246));
        ServiceDetail.Text = ready ? "本地端口已就绪。可复制下方地址和 Key 到你的客户端。" : busy ? "正在连接上游并初始化账号，最长等待 3 分钟；可随时停止。" : "服务已停止，账号和连接设置可修改。";
        if (tray != null) tray.Text = "DeepSeek Desktop · " + ServiceStatus.Text;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        busy = true; UpdateState(); TotalCount.Text = SuccessCount.Text = FailedCount.Text = "0";
        try { await engine.StartAsync(settings); Tell("本地接口已就绪；可检查接口，或主动发送测试消息验证上游。"); }
        catch (Exception ex) { if (!quitting && !stopInProgress) Error(ex); }
        finally { busy = false; UpdateState(); }
    }
    private async Task StopServiceAsync()
    {
        if (stopInProgress) return;
        stopInProgress = true; testCancellation?.Cancel(); UpdateState();
        try { await engine.StopAsync(); Tell("服务已停止。"); }
        catch (Exception ex) { if (!quitting) Error(ex); }
        finally { stopInProgress = false; UpdateState(); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopServiceAsync();
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        try { var models = await engine.ModelsAsync(); Tell("本地接口正常，可用模型：" + string.Join("、", models) + "。此检查未发送对话消息。"); }
        catch (Exception ex) { Error(ex); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        testCancellation = new CancellationTokenSource(); UpdateState(); TestReply.Text = "等待 DeepSeek 回复…";
        try { TestReply.Text = await engine.SendTestAsync(testCancellation.Token); Tell("已收到上游响应。"); }
        catch (OperationCanceledException) { TestReply.Text = "测试已取消。"; }
        catch (Exception ex) { TestReply.Text = "测试未成功，请查看运行日志。"; Error(ex); }
        finally { testCancellation.Dispose(); testCancellation = null; UpdateState(); }
    }

    private async Task RefreshAsync()
    {
        while (logs.TryDequeue(out var line)) LogsBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        if (LogsBox.Text.Length > 120000) { var text = LogsBox.Text; var cut = text.IndexOf('\n', text.Length - 90000); LogsBox.Text = cut >= 0 ? text[(cut + 1)..] : text[^90000..]; }
        if (Pages.SelectedIndex == 3) LogsBox.ScrollToEnd();
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
            if (engine.IsRunning) ServiceDetail.Text = $"账号池：空闲 {Number(status, "idle")} · 忙碌 {Number(status, "busy")} · 错误 {Number(status, "error")} · 不可用 {Number(status, "invalid")}。本地端口已就绪。";
        }
        catch (Exception) { if (engine.IsRunning && !stopInProgress) Tell("状态读取暂时失败，请检查本地接口或查看日志。"); }
        finally { polling = false; }
    }
    private static string Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) ? value.ToString() : "0";

    private void Navigate_Click(object sender, RoutedEventArgs e) { if (sender is Button b) Pages.SelectedIndex = int.Parse((string)b.Tag); }
    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account a) return;
        editing = AccountList.SelectedIndex; AccountEditorTitle.Text = "编辑账号"; AccountName.Text = a.Name; AccountLogin.Text = a.Login;
        AccountPassword.Clear(); AccountDevice.Clear(); AccountEnabled.IsChecked = a.Enabled;
    }
    private void NewAccount_Click(object sender, RoutedEventArgs e) { ResetAccount(); AccountName.Focus(); }
    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = Clone();
            var old = editing >= 0 ? candidate.Accounts[editing] : null;
            var value = new Account { Name = AccountName.Text.Trim(), Login = AccountLogin.Text.Trim(), Password = AccountPassword.Password.Length > 0 ? AccountPassword.Password : old?.Password ?? "", DeviceId = AccountDevice.Password.Length > 0 ? AccountDevice.Password.Trim() : old?.DeviceId ?? "", Enabled = AccountEnabled.IsChecked == true };
            if (value.Name.Length == 0) throw new ArgumentException("请填写账号名称。");
            if (old != null && old.Login != value.Login && (AccountPassword.Password.Length == 0 || AccountDevice.Password.Length == 0)) throw new ArgumentException("更换登录名时，请同时填写该账号的密码和 device_id。");
            if (editing >= 0) candidate.Accounts[editing] = value; else candidate.Accounts.Add(value);
            Save(candidate); ResetAccount(); Tell("账号已加密保存。启动服务后可查看账号池状态。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedIndex < 0) { Tell("请先选择账号。"); return; }
        if (MessageBox.Show(this, "从本机删除选中账号的保存信息？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { var candidate = Clone(); candidate.Accounts.RemoveAt(AccountList.SelectedIndex); Save(candidate); ResetAccount(); Tell("账号已从本机删除。"); } catch (Exception ex) { Error(ex); }
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
    private void Copy(string value, string notice)
    {
        try { Clipboard.SetText(value); Tell(notice); } catch (Exception ex) { Error(ex); }
    }
    private void CopyUrl_Click(object sender, RoutedEventArgs e) => Copy(settings.BaseUrl, "API 地址已复制。");
    private void CopyKey_Click(object sender, RoutedEventArgs e) => Copy(settings.ApiKey, "API Key 已复制，请粘贴到客户端的 Key 设置中。");
    private void CopyLogs_Click(object sender, RoutedEventArgs e) => Copy(LogsBox.Text, "日志已复制；对外分享前请检查是否含个人信息。");
    private void ClearLogs_Click(object sender, RoutedEventArgs e) { logs.Clear(); LogsBox.Clear(); Tell("日志显示已清空。"); }
    private void OpenData_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(PrivateStorage.Root) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); } }
    private void OpenReadme_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "README.md")) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); } }
    private void DeviceHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "本版本需要已有 DeepSeek 账号的真实 device_id。\n\n在浏览器登录 chat.deepseek.com，开发者工具 → Network 中查看 users/login 请求体里的 device_id。每个账号分别获取。\n\n上游接口可能改变；若找不到该字段或登录触发验证码，本版本暂无法自动处理。不要把密码、Token 或 device_id 发到聊天或公开 Issue。\n\n更完整步骤见发布包的 README.md。", "设备信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true; Hide();
        if (tray != null) tray.ShowBalloonTip(1800, "DeepSeek Desktop", "程序已收起到托盘。本地服务保持当前状态。", Forms.ToolTipIcon.Info);
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true; timer.Stop(); testCancellation?.Cancel();
        try { if (initialized) await engine.StopAsync(); }
        catch (Exception) { MessageBox.Show(this, "退出时未能完成运行文件清理，下次启动将再次尝试清理。", "DeepSeek Desktop", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { tray?.Dispose(); engine.Dispose(); Application.Current.Shutdown(); }
    }
}
