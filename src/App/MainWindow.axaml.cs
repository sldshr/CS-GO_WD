using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
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
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _elapsedTimer;

    private CancellationTokenSource? _cts;
    private int _activeRunId;
    private double? _currentPercent;
    private bool _busy;
    private bool _cancelling;
    private bool _copiedToClipboard;

    public MainWindow()
    {
        InitializeComponent();

        MapsBox.Text = ResolveInitialMapsDir();

        _elapsedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (_busy)
                UpdateProgressMeta();
        });

        StartButton.Click += async (_, _) => await StartAsync();
        CancelButton.Click += (_, _) => CancelCurrentRun();
        PasteButton.Click += async (_, _) => await PasteItemAsync();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        CopyButton.Click += async (_, _) => await CopyCommandAsync();
        LibraryButton.Click += async (_, _) => await ShowInstalledAsync();
        AuthorLink.Click += (_, _) => OpenAuthorPage();
        Opened += (_, _) => ItemBox.Focus();
        Closing += OnClosing;
    }

    private string ResolveInitialMapsDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.MapsDir))
        {
            if (Directory.Exists(_settings.MapsDir))
                return _settings.MapsDir;

            var detected = MapsFolder.Detect();
            return detected ?? _settings.MapsDir;
        }

        return MapsFolder.Detect() ?? "";
    }

    private async Task StartAsync()
    {
        if (_busy)
            return;

        ResultPanel.IsVisible = false;
        SetDetail("");

        if (!WorkshopItem.TryParseId(ItemBox.Text, out var id))
        {
            ResetProgressBar(0);
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
            ResetProgressBar(0);
            SetStatus(e.Message, Tone.Error);
            MapsBox.Focus();
            return;
        }

        MapsBox.Text = mapsDir;
        _settings.MapsDir = mapsDir;
        SaveSettings();

        _log.Clear();
        LogText.Text = "";

        var runId = ++_activeRunId;
        SetBusy(true);
        ShowProgress(runId, $"Подготовка загрузки предмета {id}…", null);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var reporter = new Reporter
        {
            Log = line => Dispatcher.UIThread.Post(() =>
            {
                if (runId == _activeRunId)
                    AppendLog(line);
            }),
            Status = (text, percent) => Dispatcher.UIThread.Post(() => ShowProgress(runId, text, percent)),
        };

        try
        {
            var request = new InstallRequest(id, mapsDir);
            var installer = new Installer(SteamCmd.Default(), MapLibrary.Default());
            var result = await Task.Run(() => installer.RunAsync(request, reporter, token));

            SetBusy(false, finalProgress: 100);
            await ShowResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, finalProgress: 0);
            SetStatus("Отменено.", Tone.Normal);
            AppendLog("Отменено пользователем.");
        }
        catch (InstallException e)
        {
            SetBusy(false, finalProgress: 0);
            SetStatus(e.Message, Tone.Error);
            AppendLog("Ошибка: " + e.Message);
        }
        catch (Exception e)
        {
            SetBusy(false, finalProgress: 0);
            SetStatus("Непредвиденная ошибка: " + e.Message, Tone.Error);
            AppendLog(e.ToString());
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (_busy)
                SetBusy(false, finalProgress: 0);
        }
    }

    private void CancelCurrentRun()
    {
        if (!_busy || _cancelling)
            return;

        _cancelling = true;
        CancelButton.IsEnabled = false;
        _currentPercent = null;
        Progress.IsIndeterminate = true;
        SetStatus("Отмена загрузки…", Tone.Normal);
        UpdateProgressMeta();
        _cts?.Cancel();
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

    private void SetBusy(bool busy, double finalProgress = 0)
    {
        _busy = busy;
        _cancelling = false;

        StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ItemBox.IsEnabled = !busy;
        PasteButton.IsEnabled = !busy;
        MapsBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        LibraryButton.IsEnabled = !busy;

        if (busy)
        {
            _currentPercent = null;
            _elapsed.Restart();
            _elapsedTimer.Start();
            Progress.IsIndeterminate = true;
            Progress.Value = 0;
            UpdateProgressMeta();
        }
        else
        {
            _elapsedTimer.Stop();
            _elapsed.Stop();
            _currentPercent = null;
            ProgressMetaText.IsVisible = false;
            ResetProgressBar(finalProgress);
        }
    }

    private void ResetProgressBar(double value)
    {
        Progress.IsIndeterminate = false;
        Progress.Value = Math.Clamp(value, 0, 100);
    }

    private void ShowProgress(int runId, string text, double? percent)
    {
        if (!_busy || _cancelling || runId != _activeRunId)
            return;

        _currentPercent = percent is { } p ? Math.Clamp(p, 0, 100) : null;
        SetStatus(text, Tone.Normal);

        Progress.IsIndeterminate = _currentPercent is null;
        if (_currentPercent is { } value)
            Progress.Value = value;

        UpdateProgressMeta();
    }

    private void UpdateProgressMeta()
    {
        if (!_busy)
        {
            ProgressMetaText.IsVisible = false;
            return;
        }

        var duration = Format.Duration(_elapsed.Elapsed);
        ProgressMetaText.Text = _currentPercent is { } p
            ? $"{p:0}% · {duration}"
            : duration;
        ProgressMetaText.IsVisible = true;
        StatusRow.IsVisible = StatusText.Text is { Length: > 0 } || ProgressMetaText.IsVisible;
    }

    private void SetDetail(string text)
    {
        DetailText.Text = text;
        DetailText.IsVisible = text.Length > 0;
    }

    private void SetStatus(string text, Tone tone)
    {
        StatusText.Text = text;
        StatusRow.IsVisible = text.Length > 0 || (_busy && ProgressMetaText.IsVisible);
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
        _elapsedTimer.Stop();

        try
        {
            _settings.MapsDir = MapsFolder.Resolve(MapsBox.Text);
        }
        catch (InstallException)
        {
        }

        SaveSettings();
        if (_copiedToClipboard)
            FlushClipboard();
    }

    // Avalonia's clipboard data is a managed COM object; if it outlives the runtime, Windows crashes the process on exit.
    private static void FlushClipboard()
    {
        if (!OperatingSystem.IsWindows())
            return;

        for (var attempt = 0; attempt < 6 && OleFlushClipboard() != 0; attempt++)
            Thread.Sleep(30);
    }

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    private async Task ShowInstalledAsync()
    {
        if (_busy)
            return;

        var dialog = new InstalledWindow(MapLibrary.Default());
        await dialog.ShowDialog(this);
        if (dialog.CopiedToClipboard)
            _copiedToClipboard = true;
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

    private async Task PasteItemAsync()
    {
        try
        {
            if (Clipboard is not { } clipboard)
                return;

            var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
            if (!string.IsNullOrWhiteSpace(text))
            {
                ItemBox.Text = text.Trim();
                ItemBox.CaretIndex = ItemBox.Text.Length;
                ItemBox.Focus();
            }
        }
        catch (Exception)
        {
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
            _copiedToClipboard = true;
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
            var raw = MapsBox.Text?.Trim().Trim('"');
            var expanded = !string.IsNullOrEmpty(raw) ? MapsFolder.ExpandHome(raw) : null;
            var path = await PickFolderAsync(expanded);
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

        var startPath = ResolveExistingParentDir(current);
        IStorageFolder? start = startPath is not null ? await StorageProvider.TryGetFolderFromPathAsync(startPath) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка maps",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static string? ResolveExistingParentDir(string? path)
    {
        var cursor = path;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (Directory.Exists(cursor))
                return cursor;

            cursor = Path.GetDirectoryName(cursor);
        }

        return null;
    }

    // Avalonia's built-in GTK dialog shows an empty list when there is no xdg portal, so Linux tries zenity/kdialog first.
    private static async Task<string?> PickFolderWithExternalDialogAsync(string? current)
    {
        var startDir = ResolveExistingParentDir(current)
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var title = IsUtf8Locale() ? "Папка maps" : "maps folder";
        var dialogs = new (string Exe, string[] Args)[]
        {
            ("zenity", ["--file-selection", "--directory", $"--title={title}", $"--filename={startDir.TrimEnd('/')}/"]),
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
