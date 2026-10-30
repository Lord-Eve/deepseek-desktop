using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;
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
    private bool updating;
    private CancellationTokenSource? installUpdateCancellation;
    private DesktopRelease? availableRelease;
    private string updatePageUrl = UpdateFeed.ReleasesUrl;
    private bool loadingProvider;
    private CancellationTokenSource? grokLoginCancellation;
    private GrokLoginInput? grokLoginInput;
    private CancellationTokenSource? buddyOperationCancellation;
    private readonly Dictionary<string, string[]> buddyModels = new();

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
        engine.GrokTokensChanged += (id, tokens) => Dispatcher.Invoke(() =>
        {
            var candidate = Clone(); var account = candidate.Accounts.Single(a => a.Provider == Providers.Grok && a.Id == id);
            account.AccessToken = tokens.AccessToken; account.RefreshToken = tokens.RefreshToken; account.TokenExpiresAt = tokens.ExpiresAt;
            PrivateStorage.Save(candidate); settings = candidate;
        });
        engine.BuddyTokensChanged += (id, tokens) => Dispatcher.Invoke(() => SaveBuddyTokens(settings.Provider, id, tokens));
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
            bool resume = UpdateService.ConfirmRestart(Environment.GetCommandLineArgs()[1..]);
            try { UpdateService.CleanCompleted(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (resume) Start_Click(this, new RoutedEventArgs());
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
        ProviderBox.SelectedIndex = settings.Provider switch { Providers.Qingyan => 1, Providers.Grok => 2, Providers.CodeBuddy => 3, Providers.WorkBuddy => 4, _ => 0 };
        GrokModelBox.ItemsSource = GrokProtocol.Models; GrokModelBox.SelectedItem = settings.GrokModel;
        BuddyModelBox.ItemsSource = buddyModels.TryGetValue(BuddyModelCacheKey(), out var models) ? models : new[] { settings.Model };
        BuddyModelBox.Text = settings.Model;
        loadingProvider = false;
        bool qingyan = settings.Provider == Providers.Qingyan, grok = settings.Provider == Providers.Grok, buddy = Providers.IsBuddy(settings.Provider);
        GrokModelPanel.Visibility = GrokCredentials.Visibility = grok ? Visibility.Visible : Visibility.Collapsed;
        BuddyModelPanel.Visibility = BuddyCredentials.Visibility = buddy ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekCredentials.Visibility = qingyan || grok || buddy ? Visibility.Collapsed : Visibility.Visible;
        QingyanCredentials.Visibility = qingyan ? Visibility.Visible : Visibility.Collapsed;
        BuddyLoginButton.Content = "登录 / 重新登录 " + settings.ProviderName;
        BuddyLoginHelp.Text = settings.Provider == Providers.CodeBuddy ? "使用微信或手机号在国内官方网页授权。每次启用一个国内账号；无需安装 CodeBuddy 或 WorkBuddy，授权后可关闭浏览器。" : "在国际版官方网页通过 Google 等方式授权。每次启用一个国际账号；无需安装 WorkBuddy，授权后可关闭浏览器。";
        CredentialHelpButton.Content = buddy ? "Buddy 登录说明" : grok ? "Grok 登录说明" : qingyan ? "如何获取登录态" : "如何获取 device_id";
        ModelHint.Text = buddy ? $"默认测试模型：{settings.Model}；客户端模型请使用当前账号目录中的 ID。" : grok ? $"默认测试模型：{settings.Model}；客户端可选择 /v1/models 中的其他预设。" : $"模型：{settings.Model}（网页端当前默认模式）";
        StartupHint.Text = buddy ? "启动只刷新授权和读取模型目录，不发送对话。积分、限免和模型权限以官方账号及实际调用为准；工具由客户端执行。" : grok ? "Grok 使用 OAuth 授权及官方 CLI 通道；启动不发送对话。模型目录为预设，订阅资格、模型权限和额度以实际调用为准。" : qingyan ? "清言启动仅校验登录态，不发送对话。支持文本和工具调用兼容适配，MCP 由客户端执行；暂不支持图片。" : "DeepSeek 启动会登录启用账号，并发送后端自带的健康检查消息。";
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
        GrokLoginStatus.Text = "尚未登录 Grok，请先填写名称后点击登录。";
        BuddyLoginStatus.Text = "请先填写账号名称，再点击登录，在官方网页完成授权。";
        BuddyBalanceStatus.Text = "积分余额尚未查询；查询不会领取奖励或发送对话。";
    }
    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingProvider) return;
        try
        {
            var candidate = Clone(); candidate.Provider = ProviderBox.SelectedIndex switch { 1 => Providers.Qingyan, 2 => Providers.Grok, 3 => Providers.CodeBuddy, 4 => Providers.WorkBuddy, _ => Providers.DeepSeek };
            Save(candidate); ResetAccount(); poolStatus = null; TestReply.Text = "尚未测试"; UpdateState();
            BuddyModelsStatus.Text = "登录后可获取当前地区的模型目录；此操作不发送对话。";
            Tell($"已切换到{settings.ProviderName}。原平台账号已保留；客户端模型请改为 {settings.Model}。");
        }
        catch (Exception ex) { LoadSettings(); Error(ex); }
    }
    private void GrokModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingProvider || GrokModelBox.SelectedItem is not string model) return;
        try { var candidate = Clone(); candidate.GrokModel = model; Save(candidate); Tell("Grok 默认测试模型已保存；客户端请求可自行指定模型。"); }
        catch (Exception ex) { LoadSettings(); Error(ex); }
    }
    private Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
    private void Save(Settings candidate)
    {
        if (engine.IsRunning || busy || stopInProgress || grokLoginCancellation != null || buddyOperationCancellation != null || updating) throw new InvalidOperationException("请先停止服务或完成当前登录、查询、更新，再修改配置。");
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
        bool signingIn = grokLoginCancellation != null || buddyOperationCancellation != null;
        StartButton.IsEnabled = !running && !busy && !stopInProgress && !signingIn && !updating;
        ProviderBox.IsEnabled = GrokModelBox.IsEnabled = !running && !busy && !stopInProgress && !signingIn && !updating;
        BuddyModelBox.IsEnabled = SaveBuddyModelButton.IsEnabled = RefreshBuddyModelsButton.IsEnabled = !running && !busy && !stopInProgress && !signingIn && !updating;
        CancelBuddyOperationButton.Visibility = buddyOperationCancellation != null ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = (running || busy) && !stopInProgress;
        ProbeButton.IsEnabled = ready && !busy && !stopInProgress;
        TestButton.IsEnabled = ready && poolStatus?.HasUsableAccount == true && !busy && !stopInProgress && testCancellation == null;
        AccountEditor.IsEnabled = SettingsEditor.IsEnabled = NewAccountButton.IsEnabled = DeleteAccountButton.IsEnabled = !running && !busy && !stopInProgress && !updating;
        SettingsEditor.IsEnabled &= !signingIn;
        NewAccountButton.IsEnabled &= !signingIn; DeleteAccountButton.IsEnabled &= !signingIn;
        AccountList.IsEnabled = SaveAccountButton.IsEnabled = AccountName.IsEnabled = AccountEnabled.IsEnabled = !signingIn;
        GrokLoginButton.IsEnabled = !signingIn; CancelGrokLoginButton.IsEnabled = grokLoginCancellation != null;
        BuddyLoginButton.IsEnabled = QueryBuddyBalanceButton.IsEnabled = !signingIn;
        CancelBuddyLoginButton.IsEnabled = buddyOperationCancellation != null;
        GrokCodePanel.Visibility = grokLoginCancellation != null ? Visibility.Visible : Visibility.Collapsed;
        GrokAuthorizationCode.IsEnabled = SubmitGrokCodeButton.IsEnabled = grokLoginCancellation != null && grokLoginInput?.CanSubmit == true;
        DownloadUpdateButton.IsEnabled = availableRelease != null && !updating && !checkingUpdates && !signingIn && !busy && !stopInProgress;
        CheckUpdatesButton.IsEnabled = !checkingUpdates && !updating;
        CancelUpdateButton.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
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
        GrokLoginStatus.Text = a.Provider == Providers.Grok && a.RefreshToken.Length > 0 ? "已保存 Grok 授权。启动时按需刷新；实际模型权限和额度请单独测试。" : "尚未登录 Grok。";
        BuddyLoginStatus.Text = Providers.IsBuddy(a.Provider) && a.RefreshToken.Length > 0 ? $"已保存{Providers.DisplayName(a.Provider)}授权；启动时按需刷新，实际模型权限请发送测试消息确认。" : "尚未完成浏览器授权。";
        BuddyBalanceStatus.Text = "所选账号的积分余额尚未查询。";
    }
    private void NewAccount_Click(object sender, RoutedEventArgs e) { ResetAccount(); AccountName.Focus(); }
    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = Clone();
            var old = editing >= 0 ? candidate.Accounts[editing] : null;
            var value = new Account { Id = old?.Id ?? Guid.NewGuid().ToString("N"), AccessToken = old?.AccessToken ?? "", TokenExpiresAt = old?.TokenExpiresAt ?? 0, Provider = settings.Provider, RefreshToken = AccountToken.Password.Length > 0 ? AccountToken.Password.Trim() : old?.RefreshToken ?? "", Name = AccountName.Text.Trim(), Login = AccountLogin.Text.Trim(), Password = AccountPassword.Password.Length > 0 ? AccountPassword.Password : old?.Password ?? "", DeviceId = AccountDevice.Password.Length > 0 ? AccountDevice.Password.Trim() : old?.DeviceId ?? "", Enabled = AccountEnabled.IsChecked == true };
            if (old != null)
            {
                value.BuddyUserId = old.BuddyUserId; value.BuddyDomain = old.BuddyDomain; value.BuddyEnterpriseId = old.BuddyEnterpriseId;
                value.BuddyAccountType = old.BuddyAccountType; value.BuddyRefreshExpiresAt = old.BuddyRefreshExpiresAt;
            }
            if (value.Name.Length == 0) throw new ArgumentException("请填写账号名称。");
            if (settings.Provider == Providers.DeepSeek && old != null && old.Login != value.Login && (AccountPassword.Password.Length == 0 || AccountDevice.Password.Length == 0)) throw new ArgumentException("更换登录名时，请同时填写该账号的密码和 device_id。");
            if (editing >= 0) candidate.Accounts[editing] = value; else candidate.Accounts.Add(value);
            Save(candidate); ResetAccount(); Tell("账号已加密保存。启动服务后可查看账号池状态。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void SaveBuddyModel_Click(object sender, RoutedEventArgs e)
    {
        if (!Providers.IsBuddy(settings.Provider)) return;
        try
        {
            var candidate = Clone(); string model = BuddyModelBox.Text.Trim();
            if (candidate.Provider == Providers.CodeBuddy) candidate.CodeBuddyModel = model; else candidate.WorkBuddyModel = model;
            Save(candidate); Tell("默认测试模型已保存；客户端请求仍可自行指定当前账号有权限的模型。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private bool CanStartBuddyOperation() => Providers.IsBuddy(settings.Provider) && !quitting && !engine.IsRunning && !busy && !stopInProgress && !updating && grokLoginCancellation == null && buddyOperationCancellation == null;
    private string BuddyModelCacheKey() => settings.Provider + ":" + settings.Accounts.FirstOrDefault(a => a.Provider == settings.Provider && a.Enabled)?.Id;
    private HttpClient CreateBuddyHttp() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = settings.ProxyUrl.Length > 0, Proxy = settings.ProxyUrl.Length > 0 ? new WebProxy(settings.ProxyUrl) : null }) { Timeout = TimeSpan.FromSeconds(30) };
    private void SaveBuddyTokens(string provider, string id, BuddyTokens tokens)
    {
        var candidate = Clone();
        var account = candidate.Accounts.Single(a => a.Provider == provider && a.Id == id);
        tokens.Apply(account); PrivateStorage.Save(candidate); settings = candidate;
    }
    private Account BuddyQueryAccount(bool selected)
    {
        var candidate = Clone();
        var account = selected && editing >= 0 && editing < candidate.Accounts.Count && candidate.Accounts[editing].Provider == settings.Provider
            ? candidate.Accounts[editing] : candidate.Accounts.SingleOrDefault(a => a.Provider == settings.Provider && a.Enabled);
        if (account == null || account.RefreshToken.Length < 16) throw new ArgumentException("请先在账号管理中完成当前平台的浏览器授权，并启用一个账号。");
        return account;
    }
    private async void BuddyLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartBuddyOperation()) return;
        string name = AccountName.Text.Trim(), provider = settings.Provider;
        if (name.Length == 0) { Tell("请先填写账号名称。"); return; }
        int index = editing; bool enabled = AccountEnabled.IsChecked == true;
        if (enabled && settings.Accounts.Where((_, i) => i != index).Any(a => a.Provider == provider && a.Enabled)) { Tell("请先停用当前平台的其他账号，再登录新的启用账号。"); return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        buddyOperationCancellation = cancellation; UpdateState(); BuddyLoginStatus.Text = "正在打开官方登录页，请在浏览器完成授权；最长等待 5 分钟。";
        try
        {
            using var http = CreateBuddyHttp();
            var oauth = new BuddyOAuth(http, provider);
            var tokens = await oauth.LoginAsync(url => { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (quitting) return;
            var candidate = Clone(); var account = index >= 0 ? candidate.Accounts[index] : new Account { Provider = provider };
            if (account.Provider != provider) throw new InvalidOperationException("账号平台已变化，请重新登录。");
            account.Name = name; account.Enabled = enabled; tokens.Apply(account);
            if (index < 0) candidate.Accounts.Add(account);
            PrivateStorage.Save(candidate); settings = candidate; buddyModels.Remove(BuddyModelCacheKey());
            LoadSettings(); LoadAccounts(); AccountList.SelectedItem = account;
            BuddyLoginStatus.Text = "授权已加密保存，可以关闭浏览器。请获取模型目录，再启动服务发送测试消息。";
            BuddyModelsStatus.Text = "授权已更新，请重新获取当前账号的模型目录。";
            Tell("浏览器授权已保存；尚未发送模型对话。");
        }
        catch (OperationCanceledException) { if (!quitting) { BuddyLoginStatus.Text = "登录已取消或超时，原有授权保持不变。"; Tell(BuddyLoginStatus.Text); } }
        catch (Exception ex) { if (!quitting) { BuddyLoginStatus.Text = "登录未完成或授权未能保存，原有设置保持不变。"; Error(ex); } }
        finally { buddyOperationCancellation = null; if (!quitting) UpdateState(); }
    }
    private async void RefreshBuddyModels_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartBuddyOperation()) return;
        Account account;
        try { account = BuddyQueryAccount(false); } catch (Exception ex) { Error(ex); return; }
        string provider = settings.Provider;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        buddyOperationCancellation = cancellation; UpdateState(); BuddyModelsStatus.Text = "正在读取当前账号模型目录，不发送对话…";
        try
        {
            using var http = CreateBuddyHttp();
            var client = new BuddyClient(http, account, (id, tokens) => Dispatcher.Invoke(() => SaveBuddyTokens(provider, id, tokens)));
            var models = await client.FetchModelsAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (quitting) return;
            var ids = models.Select(model => model.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("账号没有返回可用模型，未替换当前测试模型。");
            buddyModels[provider + ":" + account.Id] = ids; string typed = BuddyModelBox.Text; BuddyModelBox.ItemsSource = ids; BuddyModelBox.Text = typed;
            BuddyModelsStatus.Text = $"已读取 {ids.Length} 个模型；选择后点击“保存测试模型”。目录不代表已验证每个模型的调用权限。";
            Tell("账号模型目录已读取，未发送模型对话。");
        }
        catch (OperationCanceledException) { if (!quitting) BuddyModelsStatus.Text = "模型查询已取消或超时，原有目录和测试模型保留。"; }
        catch (Exception ex) { if (!quitting) { BuddyModelsStatus.Text = "模型目录读取失败，原有目录和测试模型保留。"; Error(ex); } }
        finally { buddyOperationCancellation = null; if (!quitting) UpdateState(); }
    }
    private async void QueryBuddyBalance_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartBuddyOperation()) return;
        Account account;
        try { account = BuddyQueryAccount(true); } catch (Exception ex) { Error(ex); return; }
        string provider = settings.Provider;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        buddyOperationCancellation = cancellation; UpdateState(); BuddyBalanceStatus.Text = "正在读取所选账号的积分余额…";
        try
        {
            using var http = CreateBuddyHttp();
            var client = new BuddyClient(http, account, (id, tokens) => Dispatcher.Invoke(() => SaveBuddyTokens(provider, id, tokens)));
            var balance = await client.FetchBalanceAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (quitting) return;
            BuddyBalanceStatus.Text = $"账号「{account.Name}」：{balance.Credits:0.####} 积分；{balance.ActivePackages} 个有效资源包；查询于 {balance.CheckedAt.ToLocalTime():MM-dd HH:mm}。";
            Tell("积分余额已读取，未领取奖励或发送对话。");
        }
        catch (OperationCanceledException) { if (!quitting) BuddyBalanceStatus.Text = "积分查询已取消或超时，余额未确认。"; }
        catch (Exception ex) { if (!quitting) { BuddyBalanceStatus.Text = "积分余额读取失败，余额未确认。"; Error(ex); } }
        finally { buddyOperationCancellation = null; if (!quitting) UpdateState(); }
    }
    private void CancelBuddyLogin_Click(object sender, RoutedEventArgs e) => buddyOperationCancellation?.Cancel();
    private async void GrokLogin_Click(object sender, RoutedEventArgs e)
    {
        if (settings.Provider != Providers.Grok || grokLoginCancellation != null || buddyOperationCancellation != null || engine.IsRunning || busy || stopInProgress || updating) return;
        string name = AccountName.Text.Trim();
        if (name.Length == 0) { Tell("请先填写 Grok 账号名称。"); return; }
        int index = editing; bool enabled = AccountEnabled.IsChecked == true;
        if (enabled && settings.Accounts.Where((_, i) => i != index).Any(a => a.Provider == Providers.Grok && a.Enabled)) { Tell("请先停用其他 Grok 账号，再登录新的启用账号。"); return; }
        grokLoginCancellation = new CancellationTokenSource(); UpdateState(); GrokLoginStatus.Text = "正在打开 xAI 官方登录页；请在浏览器完成授权，最长等待 5 分钟。";
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = settings.ProxyUrl.Length > 0, Proxy = settings.ProxyUrl.Length > 0 ? new WebProxy(settings.ProxyUrl) : null }) { Timeout = TimeSpan.FromSeconds(30) };
            var oauth = new GrokOAuth(http);
            var tokens = await oauth.LoginAsync(url => { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }, grokLoginCancellation.Token, input =>
            {
                grokLoginInput = input; UpdateState();
                GrokLoginStatus.Text = "请在浏览器授权；若网页显示授权码，请粘贴到下方完成登录。最长等待 5 分钟。";
            });
            if (quitting) return;
            var candidate = Clone(); var account = index >= 0 ? candidate.Accounts[index] : new Account { Provider = Providers.Grok };
            account.Name = name; account.Enabled = enabled; account.AccessToken = tokens.AccessToken; account.RefreshToken = tokens.RefreshToken; account.TokenExpiresAt = tokens.ExpiresAt;
            if (index < 0) candidate.Accounts.Add(account);
            PrivateStorage.Save(candidate); settings = candidate; LoadSettings(); LoadAccounts(); AccountList.SelectedItem = account;
            GrokLoginStatus.Text = "Grok 授权已加密保存，可关闭浏览器页面，无需再次粘贴授权码。请启动服务后发送测试消息。"; Tell("Grok 授权已保存；当前尚未发送模型对话。");
        }
        catch (OperationCanceledException) { if (!quitting) { GrokLoginStatus.Text = "登录已取消或超时，原有授权保持不变。"; Tell(GrokLoginStatus.Text); } }
        catch (Exception ex) { if (!quitting) { GrokLoginStatus.Text = "登录未完成，原有授权保持不变。"; Error(ex is System.IO.IOException ? new InvalidOperationException("无法监听登录回调端口 56121，请完成或取消其他程序的 Grok 登录后重试。") : ex); } }
        finally { grokLoginInput = null; GrokAuthorizationCode.Clear(); grokLoginCancellation.Dispose(); grokLoginCancellation = null; if (!quitting) UpdateState(); }
    }
    private void SubmitGrokCode_Click(object sender, RoutedEventArgs e)
    {
        bool accepted = grokLoginInput?.SubmitCode(GrokAuthorizationCode.Password) == true;
        GrokAuthorizationCode.Clear();
        GrokLoginStatus.Text = accepted ? "正在换取并保存 Grok 授权，请稍候。" : "未能提交，请粘贴本次登录页面上的完整授权码；过期后需重新登录。";
        UpdateState();
    }
    private void CancelGrokLogin_Click(object sender, RoutedEventArgs e) => grokLoginCancellation?.Cancel();
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
        if (checkingUpdates || quitting || updating) return;
        checkingUpdates = true; UpdateState();
        UpdateStatus.Text = "正在检查更新…";
        try
        {
            var release = await UpdateChecker.CheckAsync(updateCancellation.Token);
            if (quitting) return;
            bool newer = release.Version > Version.Parse(App.Version);
            availableRelease = newer ? release : null;
            updatePageUrl = newer ? release.PageUrl : UpdateFeed.ReleasesUrl;
            UpdateStatus.Text = newer ? $"发现新版本 {release.Version}，当前为 {App.Version}。可点击“下载并重启更新”；账号设置会保留。" : $"当前 {App.Version} 已是最新版本。";
            SidebarUpdateButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (quitting) { }
        catch (Exception ex)
        {
            if (!quitting) UpdateStatus.Text = ex is InvalidOperationException or TimeoutException or InvalidDataException
                ? ex.Message : "未能读取更新信息，请稍后重试或前往下载页查看。";
        }
        finally { checkingUpdates = false; if (!quitting) UpdateState(); }
    }
    private void ShowUpdates_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 2;
        UpdateStatus.BringIntoView();
    }
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableRelease == null || updating || checkingUpdates || quitting || grokLoginCancellation != null || buddyOperationCancellation != null || busy || stopInProgress) return;
        string? session = null; bool launched = false;
        updating = true; installUpdateCancellation = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token); UpdateState();
        try
        {
            session = await UpdateService.PrepareAsync(availableRelease, () => engine.IsRunning, new Progress<string>(text => { if (!quitting) UpdateStatus.Text = text; }), installUpdateCancellation.Token);
            installUpdateCancellation.Token.ThrowIfCancellationRequested();
            UpdateService.Launch(session);
            launched = true;
            UpdateStatus.Text = "更新已准备好，正在退出并重启…";
            await QuitAsync();
        }
        catch (OperationCanceledException) { if (!quitting) UpdateStatus.Text = "更新已取消，当前版本继续运行。"; }
        catch (Exception ex) { if (!quitting) UpdateStatus.Text = "更新未完成，当前版本继续运行。" + (ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException or UnauthorizedAccessException ? ex.Message : "请稍后重试。"); }
        finally
        {
            if (!launched && session != null) try { Directory.Delete(session, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            installUpdateCancellation.Dispose(); installUpdateCancellation = null; updating = false; if (!quitting) UpdateState();
        }
    }
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => installUpdateCancellation?.Cancel();
    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(updatePageUrl) { UseShellExecute = true }); }
        catch (Exception) { Tell("无法打开浏览器，请访问 GitHub 仓库的 Releases 页面下载新版。"); }
    }
    private void DeviceHelp_Click(object sender, RoutedEventArgs e)
    {
        if (Providers.IsBuddy(settings.Provider))
        {
            MessageBox.Show(this, "填写账号名称后点击登录，在官方网页完成授权。国内版使用微信或手机号，国际版支持 Google 等方式。授权加密保存在当前 Windows 用户下，到期按需刷新；授权完成后可关闭浏览器。\n\n无需安装 WorkBuddy、CodeBuddy、Harness 或 Node。每个平台每次启用一个账号，国内与国际凭据分别保存。\n\n“获取账号模型列表”和“查询积分余额”只读取当前账号信息，不发送对话、不领取奖励。模型权限、积分和免费活动由官方账号决定。", "Buddy 浏览器登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (settings.Provider == Providers.Grok)
        {
            MessageBox.Show(this, "填写账号名称后点击“登录 / 重新登录 Grok”，在 xAI 官方页面完成授权。无需粘贴 Token；本程序不读取 Cherry Studio 或 Grok CLI 的登录文件。\n\n回调仅监听本机 127.0.0.1:56121；若其他程序正在登录 Grok，请先完成或取消它。可随时点击“取消登录”。\n\n本地模型列表是预设目录，实际权限与订阅额度以官方账号和调用结果为准。每次启用一个 Grok 账号。", "Grok OAuth 登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
        quitting = true; timer.Stop(); testCancellation?.Cancel(); updateCancellation.Cancel(); grokLoginCancellation?.Cancel(); buddyOperationCancellation?.Cancel();
        try { if (initialized) await engine.StopAsync(); }
        catch (Exception) { MessageBox.Show(this, "退出时未能完成运行文件清理，下次启动将再次尝试清理。", "DeepSeek Desktop", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { tray?.Dispose(); trayIcon?.Dispose(); engine.Dispose(); Application.Current.Shutdown(); }
    }
}
