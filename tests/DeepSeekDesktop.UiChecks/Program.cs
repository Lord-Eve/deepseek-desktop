using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        // Do not Show or run the dispatcher: Loaded would access real user settings.
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        root.Measure(new Size(1080, 740));
        root.Arrange(new Rect(0, 0, 1080, 740));
        root.UpdateLayout();
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }
        var start = (Button)window.FindName("StartButton");
        var caption = Descendants(start).OfType<TextBlock>().First(t => t.Text == "启动服务");
        Check(caption.Foreground.ToString() == "#FFFFFFFF", "Primary button caption must inherit white.");
        var exit = Descendants(root).OfType<Button>().First(b => b.Content as string == "退出程序");
        var exitCaption = Descendants(exit).OfType<TextBlock>().First(t => t.Text == "退出程序");
        Check(exitCaption.Foreground.ToString() == "#FFCBD5EC", "Dark sidebar button caption must inherit light text.");
        var version = FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location);
        Check(((TextBlock)window.FindName("VersionLabel")).Text == $"{version.ProductVersion!.Split('+')[0]} · 非官方客户端",
            "Sidebar version must match the compiled product version.");
        Check(((TextBlock)window.FindName("VersionDetail")).Text == $"桌面端 {version.ProductVersion!.Split('+')[0]} · 协议引擎 ds-free-api v0.5.1",
            "Settings version must match the compiled product version.");
        Check(window.Icon is BitmapFrame frame && frame.PixelWidth > 0, "Window icon must load from the application resources.");
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DeepSeekDesktop;component/Assets/deepseek-whale-girl.ico")).Stream)
        {
            var decoder = new IconBitmapDecoder(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(new[] { 16, 24, 32, 48, 256 }.All(size => decoder.Frames.Any(f => f.PixelWidth == size && f.PixelHeight == size)),
                "Application icon must include native tray, taskbar and Explorer sizes.");
        }
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DeepSeekDesktop;component/Assets/deepseek-whale-girl.ico")).Stream)
        using (var icon = new System.Drawing.Icon(resource, new System.Drawing.Size(16, 16)))
            Check(icon.Width == 16 && icon.Height == 16, "Native tray icon must decode at small size.");
        var pages = (TabControl)window.FindName("Pages");
        pages.SelectedIndex = 3;
        var logs = (TextBox)window.FindName("LogsBox");
        logs.Text = "[隔离界面测试] 此处为模拟日志，无真实账号。\n[隔离界面测试] 本地端口已启动，但无可用账号。";
        root.Measure(new Size(1080, 740));
        root.Arrange(new Rect(0, 0, 1080, 740));
        root.UpdateLayout();
        Check(logs.VerticalContentAlignment == VerticalAlignment.Top, "Log text must align at the top.");
        Check(logs.ActualHeight > 300, "Log viewer must fill the available area.");
        // Inject contention without reading or replacing the user's clipboard.
        int attempts = 0;
        string? copied = null;
        var recovered = ClipboardWriter.CopyAsync("fixture-only", value =>
        {
            if (++attempts < 3) throw new System.Runtime.InteropServices.ExternalException("busy", unchecked((int)0x800401D0));
            copied = value;
        }).GetAwaiter().GetResult();
        Check(recovered && attempts == 3 && copied == "fixture-only", "Temporary clipboard contention must recover.");
        attempts = 0;
        var exhausted = ClipboardWriter.CopyAsync("fixture-only", _ =>
        {
            attempts++;
            throw new System.Runtime.InteropServices.ExternalException("busy", unchecked((int)0x800401D0));
        }).GetAwaiter().GetResult();
        Check(!exhausted && attempts == 10, "Permanent contention must stop retrying without claiming success.");
        if (args.Length == 1)
        {
            var bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(args[0]);
            encoder.Save(file);
        }
        int closeAttempts = 0;
        window.Closing += (_, e) => { if (e.Cancel) closeAttempts++; };
        window.Close();
        window.Close();
        Check(closeAttempts == 2 && !window.IsVisible, "Repeated closes must keep the window available in the tray.");
        Console.WriteLine($"PASS: {checks} WPF layout and inherited color checks (no real settings or login)");
        app.Shutdown();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
