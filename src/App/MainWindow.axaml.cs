using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CsgoWorkshop.Core;

namespace CsgoWorkshop.App;

public partial class MainWindow : Window
{
    private enum Tone { Normal, Success, Error }

    private const int MaxLogLines = 1000;
    private const string AuthorUrl = "https://sociald.fastapicloud.dev/SolidLink";

    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#1B6E2F"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B3261E"));

    private readonly Settings _settings = Settings.Load();
    private readonly List<string> _log = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        MapsBox.Text = !string.IsNullOrWhiteSpace(_settings.MapsDir) ? _settings.MapsDir : MapsFolder.Detect() ?? "";

        StartButton.Click += async (_, _) => await StartAsync();
        CancelButton.Click += (_, _) => _cts?.Cancel();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        CopyButton.Click += async (_, _) => await CopyCommandAsync();
        LibraryButton.Click += async (_, _) => await ShowInstalledAsync();
        AuthorLink.Click += (_, _) => OpenAuthorPage();
        Opened += (_, _) => ItemBox.Focus();
        Closing += OnClosing;
    }

    private async Task StartAsync()
    {
        if (_busy)
            return;

        ResultPanel.IsVisible = false;
        SetDetail("");

        if (!WorkshopItem.TryParseId(ItemBox.Text, out var id))
        {
            SetStatus("Не удалось определить ID. Вставьте ссылку на предмет Workshop или число из адреса (…?id=213240871).", Tone.Error);
            ItemBox.Focus();
            return;
        }

        string mapsDir;
        try
        {
            mapsDir = MapsFolder.Resolve(MapsBox.Text);
        }
        catch (InstallException e)
        {
            SetStatus(e.Message, Tone.Error);
            MapsBox.Focus();
            return;
        }

        MapsBox.Text = mapsDir;
        _settings.MapsDir = mapsDir;
        SaveSettings();

        _log.Clear();
        LogText.Text = "";
        SetBusy(true);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var reporter = new Reporter
        {
            Log = line => Dispatcher.UIThread.Post(() => AppendLog(line)),
            Status = (text, percent) => Dispatcher.UIThread.Post(() => ShowProgress(text, percent)),
        };

        try
        {
            var request = new InstallRequest(id, mapsDir);
            var installer = new Installer(SteamCmd.Default(), MapLibrary.Default());
            var result = await Task.Run(() => installer.RunAsync(request, reporter, token));
            await ShowResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Отменено.", Tone.Normal);
            AppendLog("Отменено пользователем.");
        }
        catch (InstallException e)
        {
            SetStatus(e.Message, Tone.Error);
            AppendLog("Ошибка: " + e.Message);
        }
        catch (Exception e)
        {
            SetStatus("Непредвиденная ошибка: " + e.Message, Tone.Error);
            AppendLog(e.ToString());
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private async Task ShowResultAsync(InstallResult result)
    {
        CommandBox.Text = result.Command;
        ResultPanel.IsVisible = true;

        var copied = await TryCopyAsync(result.Command);

        SetStatus(
            $"Готово: {result.MapName}.bsp ({Format.Size(result.Size)}) записан в папку maps" +
            (result.Replaced ? ", прежний файл заменён." : "."),
            Tone.Success);

        var details = new List<string>
        {
            copied
                ? "Команда скопирована в буфер обмена: вставьте её в консоль игры."
                : "Буфер обмена недоступен: выделите команду и скопируйте вручную.",
        };

        if (result.OtherMaps.Count > 0)
            details.Add($"В предмете есть и другие карты: {string.Join(", ", result.OtherMaps)}. Команда дана для первой.");

        SetDetail(string.Join('\n', details));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ItemBox.IsEnabled = !busy;
        MapsBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        LibraryButton.IsEnabled = !busy;

        Progress.IsIndeterminate = busy;
        if (!busy)
            Progress.Value = 0;
        else
            SetStatus("", Tone.Normal);
    }

    private void ShowProgress(string text, double? percent)
    {
        if (!_busy)
            return;

        SetStatus(percent is { } p ? $"{text} {p:0}%" : text, Tone.Normal);
        Progress.IsIndeterminate = percent is null;
        if (percent is { } value)
            Progress.Value = value;
    }

    private void SetDetail(string text)
    {
        DetailText.Text = text;
        DetailText.IsVisible = text.Length > 0;
    }

    private void SetStatus(string text, Tone tone)
    {
        StatusText.Text = text;
        StatusText.IsVisible = text.Length > 0;
        switch (tone)
        {
            case Tone.Success:
                StatusText.Foreground = SuccessBrush;
                break;
            case Tone.Error:
                StatusText.Foreground = ErrorBrush;
                break;
            default:
                StatusText.ClearValue(TextBlock.ForegroundProperty);
                break;
        }
    }

    private void AppendLog(string line)
    {
        _log.Add(line);
        if (_log.Count > MaxLogLines)
            _log.RemoveRange(0, _log.Count - MaxLogLines);

        LogText.Text = string.Join('\n', _log);
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Не удалось сохранить настройки: {e.Message}");
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts?.Cancel();

        try
        {
            _settings.MapsDir = MapsFolder.Resolve(MapsBox.Text);
        }
        catch (InstallException)
        {
        }

        SaveSettings();
        FlushClipboard();
    }

    // Avalonia's clipboard data is a managed COM object; if it outlives the runtime, Windows crashes the process on exit.
    private static void FlushClipboard()
    {
        if (!OperatingSystem.IsWindows())
            return;

        for (var attempt = 0; attempt < 10 && OleFlushClipboard() != 0; attempt++)
            Thread.Sleep(100);
    }

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    private async Task ShowInstalledAsync()
    {
        if (_busy)
            return;

        await new InstalledWindow(MapLibrary.Default()).ShowDialog(this);
    }

    private void OpenAuthorPage()
    {
        try
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(AuthorUrl) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { AuthorUrl } };
            Process.Start(info)?.Dispose();
        }
        catch (Exception e)
        {
            SetDetail($"Не удалось открыть браузер ({e.Message}). Адрес: {AuthorUrl}");
        }
    }

    private async Task CopyCommandAsync()
    {
        if (string.IsNullOrEmpty(CommandBox.Text))
            return;

        var copied = await TryCopyAsync(CommandBox.Text);
        SetDetail(copied
            ? "Команда скопирована в буфер обмена."
            : "Буфер обмена недоступен: выделите команду и скопируйте вручную.");
    }

    private async Task<bool> TryCopyAsync(string text)
    {
        try
        {
            if (Clipboard is not { } clipboard)
                return false;

            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task BrowseAsync()
    {
        try
        {
            var path = await PickFolderAsync(MapsBox.Text?.Trim().Trim('"'));
            if (path is not null)
                MapsBox.Text = path;
        }
        catch (Exception e)
        {
            SetStatus($"Не удалось открыть выбор папки ({e.Message}). Введите путь вручную.", Tone.Error);
        }
    }

    private async Task<string?> PickFolderAsync(string? current)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return await PickFolderWithExternalDialogAsync(current);
            }
            catch (NotSupportedException) when (StorageProvider.CanPickFolder)
            {
            }
        }

        if (!StorageProvider.CanPickFolder)
            throw new NotSupportedException("диалог выбора папки недоступен");

        IStorageFolder? start = Directory.Exists(current) ? await StorageProvider.TryGetFolderFromPathAsync(current) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка maps",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // Avalonia's built-in GTK dialog shows an empty list when there is no xdg portal, so Linux tries zenity/kdialog first.
    private static async Task<string?> PickFolderWithExternalDialogAsync(string? current)
    {
        var startDir = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var title = IsUtf8Locale() ? "Папка maps" : "maps folder";
        var dialogs = new (string Exe, string[] Args)[]
        {
            ("zenity", ["--file-selection", "--directory", $"--title={title}", $"--filename={startDir}/"]),
            ("kdialog", ["--getexistingdirectory", startDir, "--title", title]),
        };

        if (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("KDE", StringComparison.OrdinalIgnoreCase) == true)
            Array.Reverse(dialogs);

        foreach (var (exe, args) in dialogs)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (Win32Exception)
            {
                continue;
            }

            if (process is null)
                continue;

            using (process)
            {
                var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
                await process.WaitForExitAsync();
                if (process.ExitCode == 0 && output.Length > 0)
                    return output;
                if (process.ExitCode == 1)
                    return null;
            }
        }

        throw new NotSupportedException("zenity и kdialog недоступны");
    }

    // GTK tools reject non-ASCII arguments outside a UTF-8 locale.
    private static bool IsUtf8Locale()
    {
        var locale = new[] { "LC_ALL", "LC_CTYPE", "LANG" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));

        return locale is not null
            && (locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("utf8", StringComparison.OrdinalIgnoreCase));
    }
}
