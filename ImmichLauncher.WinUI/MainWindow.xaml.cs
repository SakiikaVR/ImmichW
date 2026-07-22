using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ImmichLauncher;

public sealed partial class MainWindow : Window
{
    private readonly NativeStack _stack = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _busy;

    private static readonly Color Green = Color.FromArgb(255, 16, 160, 90);
    private static readonly Color Red = Color.FromArgb(255, 200, 60, 60);

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 760));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "immichw.ico");
        if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        Log("ようこそ。「起動」で PostgreSQL → Redis → Immich の順に立ち上がります。");
        CurrentLibraryText.Text = $"現在の保存先: {NativeStack.LibraryDir}";
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        _ = RefreshStatusAsync();
    }

    private void Log(string message)
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        if (LogText.Text.Length > 200_000)
        {
            LogText.Text = LogText.Text[^150_000..];
        }
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private async Task RefreshStatusAsync()
    {
        var status = await NativeStack.ProbeAsync();
        PgDot.Fill = new SolidColorBrush(status.Postgres ? Green : Red);
        RedisDot.Fill = new SolidColorBrush(status.Redis ? Green : Red);
        ImmichDot.Fill = new SolidColorBrush(status.Immich ? Green : Red);
        ImmichStateText.Text = status.Immich ? "Immich: 稼働中" : "Immich: 停止";
        SetupBar.IsOpen = !status.Installed;
        StartButton.IsEnabled = !_busy && status.Installed;
        StopButton.IsEnabled = !_busy;
        OpenButton.IsEnabled = status.Immich;
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy)
        {
            Log("別の処理が実行中です。");
            return;
        }
        _busy = true;
        BusyRing.IsActive = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"エラー: {ex.Message}");
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            await RefreshStatusAsync();
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            var ok = await Task.Run(() => _stack.StartAllAsync(m => DispatcherQueue.TryEnqueue(() => Log(m))));
            Log(ok ? $"起動完了: {NativeStack.ImmichUrl}" : "起動に失敗しました。ログフォルダを確認してください。");
        });
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(() =>
            Task.Run(() => _stack.StopAllAsync(m => DispatcherQueue.TryEnqueue(() => Log(m)))));
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(NativeStack.ImmichUrl) { UseShellExecute = true });

    private void OnOpenLibrary(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(NativeStack.LibraryDir);
        Process.Start(new ProcessStartInfo(NativeStack.LibraryDir) { UseShellExecute = true });
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(NativeStack.LogDir);
        Process.Start(new ProcessStartInfo(NativeStack.LogDir) { UseShellExecute = true });
    }

    // ---------- アカウント / 保存先 ----------

    private async void OnApplyAccount(object sender, RoutedEventArgs e)
    {
        var email = AdminEmailBox.Text.Trim();
        var password = AdminPasswordBox.Password;
        var name = AdminNameBox.Text.Trim();
        await RunExclusiveAsync(async () =>
        {
            var ok = await _stack.SetAdminAccountAsync(email, password, name,
                m => DispatcherQueue.TryEnqueue(() => Log(m)));
            if (ok) AdminPasswordBox.Password = "";
        });
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            NewLibraryBox.Text = System.IO.Path.Combine(folder.Path, "ImmichLibrary");
        }
    }

    private async void OnMoveLibrary(object sender, RoutedEventArgs e)
    {
        var target = NewLibraryBox.Text.Trim();
        if (target.Length == 0)
        {
            Log("移動先フォルダを入力してください (例: D:\\ImmichLibrary)。");
            return;
        }
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "保存先の変更",
            Content = $"写真ライブラリを\n{NativeStack.LibraryDir}\nから\n{target}\nへ移動します。よろしいですか?\n(Immich は一時停止し、完了後に自動で再起動します)",
            PrimaryButtonText = "移動する",
            CloseButtonText = "キャンセル",
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        await RunExclusiveAsync(async () =>
        {
            var ok = await Task.Run(() => _stack.ChangeMediaLocationAsync(target,
                m => DispatcherQueue.TryEnqueue(() => Log(m))));
            CurrentLibraryText.Text = $"現在の保存先: {NativeStack.LibraryDir}";
            Log(ok ? "保存先の変更が完了しました。" : "保存先の変更に失敗しました。");
        });
    }

    // ---------- Tailscale ----------

    private async void OnTailscaleEnable(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            var ts = NativeStack.TailscaleExe();
            if (ts == null)
            {
                Log("Tailscale が見つかりません。https://tailscale.com/download からインストールしてください。");
                return;
            }
            var output = await NativeStack.RunCaptureAsync(ts, "serve --bg http://127.0.0.1:2283");
            var approval = NativeStack.ExtractApprovalUrl(output);
            if (approval != null && output.Contains("not enabled"))
            {
                Log("この tailnet では Serve 機能が未承認です。ブラウザで承認ページを開きました。");
                Log("承認後、もう一度「外出先アクセスを有効化」を押してください。");
                Process.Start(new ProcessStartInfo(approval) { UseShellExecute = true });
                return;
            }
            Log(output.Length > 0 ? output : "tailscale serve を有効化しました。");
            await ShowTailscaleUrlAsync(ts);
            Log("スマホの Immich アプリには上記 https:// の URL を入力してください(スマホ側にも Tailscale が必要)。");
        });
    }

    private async void OnTailscaleDisable(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            var ts = NativeStack.TailscaleExe();
            if (ts == null)
            {
                Log("Tailscale が見つかりません。");
                return;
            }
            var output = await NativeStack.RunCaptureAsync(ts, "serve reset");
            Log(output.Length > 0 ? output : "tailscale serve を無効化しました。");
            TsUrlText.Text = "";
        });
    }

    private async void OnTailscaleStatus(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            var ts = NativeStack.TailscaleExe();
            if (ts == null)
            {
                Log("Tailscale が見つかりません。");
                return;
            }
            Log(await NativeStack.RunCaptureAsync(ts, "serve status"));
            await ShowTailscaleUrlAsync(ts);
        });
    }

    private async Task ShowTailscaleUrlAsync(string ts)
    {
        var json = await NativeStack.RunCaptureAsync(ts, "status --json");
        const string key = "\"DNSName\":\"";
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return;
        var rest = json[(i + key.Length)..];
        var j = rest.IndexOf('"');
        if (j <= 0) return;
        var dns = rest[..j].TrimEnd('.');
        TsUrlText.Text = $"外出先用 URL: https://{dns}";
    }
}
