using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace CsgoWorkshop.Core;

public sealed record SteamRun(int ExitCode, SteamDownloadResult? Download, string Transcript)
{
    public string Tail(int lines)
    {
        var all = Transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}

public sealed class SteamCmd(string dir, string homeDir, string contentDir)
{
    private const int GameAppId = 730;
    private const int PrimeAttempts = 3;
    private const int DownloadAttempts = 3;

    private static readonly string[] WindowsUrls =
    [
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
        "https://media.steampowered.com/installer/steamcmd.zip",
    ];

    private static readonly string[] LinuxUrls =
    [
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz",
        "https://media.steampowered.com/installer/steamcmd_linux.tar.gz",
    ];

    private static readonly HashSet<string> TransientReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timeout", "No Connection", "Failure", "Busy", "Service Unavailable", "Try Another CM", "Remote Disconnect",
    };

    public static SteamCmd Default() => new(AppPaths.SteamCmdDir, AppPaths.SteamHomeDir, AppPaths.ContentDir);

    private string ExePath => Path.Combine(dir, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
    private string PrimedMarker => Path.Combine(dir, ".primed");
    private string WorkshopCacheDir => Path.Combine(contentDir, "steamapps", "workshop");

    public bool IsInstalled =>
        File.Exists(ExePath) && (OperatingSystem.IsWindows() || File.Exists(Path.Combine(dir, "linux32", "steamcmd")));

    public string ItemDir(ulong id) =>
        Path.Combine(WorkshopCacheDir, "content", GameAppId.ToString(), id.ToString());

    public async Task EnsureInstalledAsync(Reporter reporter, CancellationToken ct)
    {
        if (IsInstalled)
            return;

        reporter.Log("steamcmd не найден, скачиваю с серверов Valve.");
        Directory.CreateDirectory(dir);

        var archive = Path.Combine(Path.GetTempPath(), "csgo-workshop-steamcmd-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await DownloadAsync(OperatingSystem.IsWindows() ? WindowsUrls : LinuxUrls, archive, reporter, ct);
            reporter.Status("Распаковка steamcmd…", null);

            if (OperatingSystem.IsWindows())
            {
                ZipFile.ExtractToDirectory(archive, dir, overwriteFiles: true);
            }
            else
            {
                await using var file = File.OpenRead(archive);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, dir, overwriteFiles: true, ct);
                MakeExecutable();
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InstallException($"Не удалось установить steamcmd в {dir}: {e.Message}");
        }
        finally
        {
            TryDelete(archive);
        }

        if (!IsInstalled)
            throw new InstallException("Скачанный архив steamcmd не содержит нужных файлов.");
    }

    // The very first launch makes steamcmd update itself and, on Windows, may exit with a
    // non-zero code halfway; doing it separately keeps the real download run clean.
    public async Task PrimeAsync(Reporter reporter, CancellationToken ct)
    {
        if (File.Exists(PrimedMarker))
            return;

        const string status = "Первый запуск steamcmd: обновление, это может занять несколько минут…";
        reporter.Log("Первый запуск: steamcmd обновляет себя (скачивает около 100 МБ), это происходит один раз.");

        for (var attempt = 1; attempt <= PrimeAttempts; attempt++)
        {
            reporter.Status(status, null);
            var run = await RunAsync(["+quit"], status, reporter, ct);
            ThrowIfCannotStart(run);

            if (run.ExitCode == 0)
            {
                File.WriteAllText(PrimedMarker, "");
                return;
            }

            reporter.Log($"steamcmd завершился с кодом {run.ExitCode}, перезапуск ({attempt} из {PrimeAttempts}).");
        }
    }

    public async Task<string> DownloadItemAsync(ulong id, Reporter reporter, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            WipeWorkshopCache();

            var status = $"Загрузка предмета {id} через steamcmd…";
            reporter.Status(status, null);
            reporter.Log($"steamcmd: workshop_download_item {GameAppId} {id} (попытка {attempt} из {DownloadAttempts})");

            var run = await RunAsync(
                [
                    "+@ShutdownOnFailedCommand", "1",
                    "+@NoPromptForPassword", "1",
                    "+force_install_dir", contentDir,
                    "+login", "anonymous",
                    "+workshop_download_item", GameAppId.ToString(), id.ToString(),
                    "+quit",
                ],
                status, reporter, ct);

            ThrowIfCannotStart(run);

            if (run.Download is { Success: true } ok)
            {
                // For single-file items steamcmd reports the file, not the folder.
                var folder = ok.Path switch
                {
                    not null when Directory.Exists(ok.Path) => ok.Path,
                    not null when File.Exists(ok.Path) => Path.GetDirectoryName(ok.Path) ?? ItemDir(id),
                    _ => ItemDir(id),
                };

                if (Directory.Exists(folder))
                    return folder;

                throw new InstallException($"steamcmd сообщил об успехе, но папка предмета не найдена: {folder}");
            }

            var reason = run.Download?.Reason;
            if (attempt < DownloadAttempts && (reason is null || TransientReasons.Contains(reason)))
            {
                reporter.Log("Сбой, повторяю через несколько секунд.");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            throw new InstallException(Explain(run, id));
        }
    }

    public void WipeWorkshopCache()
    {
        try
        {
            if (Directory.Exists(WorkshopCacheDir))
                Directory.Delete(WorkshopCacheDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stale cache only costs disk space.
        }
    }

    private async Task<SteamRun> RunAsync(IReadOnlyList<string> args, string status, Reporter reporter, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (!OperatingSystem.IsWindows())
        {
            // steamcmd writes ~/Steam and ~/.steam; keep them away from a real Steam client.
            Directory.CreateDirectory(homeDir);
            startInfo.Environment["HOME"] = homeDir;
        }

        var output = new SteamOutput(reporter.Log, percent => reporter.Status(status, percent));
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            throw new InstallException($"Не удалось запустить steamcmd ({ExePath}): {e.Message}");
        }

        process.StandardInput.Close();

        async Task Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                var chunk = new string(buffer, 0, read);
                lock (output)
                    output.Feed(chunk);
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));

        using (ct.Register(() => Kill(process)))
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));
        }

        lock (output)
            output.Flush();

        ct.ThrowIfCancellationRequested();
        return new SteamRun(process.ExitCode, output.Download, output.Transcript);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    private static string Explain(SteamRun run, ulong id)
    {
        if (run.Download is { Success: false, Reason: { } reason })
        {
            return reason switch
            {
                "File Not Found" => $"Предмет {id} не найден в Workshop: неверный ID, предмет удалён или скрыт.",
                "Access Denied" => $"Нет доступа к предмету {id}: он скрыт или доступен только друзьям автора.",
                "Timeout" or "No Connection" => "Нет связи со Steam. Проверьте интернет и повторите.",
                _ => $"steamcmd не смог скачать предмет {id}: {reason}.",
            };
        }

        if (run.Transcript.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase))
            return "Steam временно ограничил число входов. Подождите несколько минут и повторите.";

        if (run.Transcript.Contains("No Connection", StringComparison.OrdinalIgnoreCase))
            return "Нет связи со Steam. Проверьте интернет и повторите.";

        return $"steamcmd завершился с кодом {run.ExitCode} и не сообщил результат.\nПоследние строки вывода:\n{run.Tail(6)}";
    }

    private static void ThrowIfCannotStart(SteamRun run)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (run.ExitCode is 126 or 127 || run.Transcript.Contains("error while loading shared libraries", StringComparison.Ordinal))
        {
            throw new InstallException(
                "Не удаётся запустить steamcmd: скорее всего, не установлены 32-битные библиотеки.\n" +
                "Debian/Ubuntu: sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install lib32gcc-s1 lib32stdc++6\n" +
                "Fedora: sudo dnf install glibc.i686 libstdc++.i686\n" +
                "Arch: включите репозиторий multilib и установите lib32-gcc-libs");
        }
    }

    private void MakeExecutable()
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var relative in new[] { "steamcmd.sh", Path.Combine("linux32", "steamcmd") })
        {
            var path = Path.Combine(dir, relative);
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path,
                    File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
    }

    private static async Task DownloadAsync(string[] urls, string destination, Reporter reporter, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("csgo-workshop/1.0");

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(destination);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        reporter.Status("Загрузка steamcmd…", done * 100.0 / total);
                }

                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                last = e;
                reporter.Log($"Не удалось скачать {url}: {e.Message}");
            }
        }

        throw new InstallException($"Не удалось скачать steamcmd. Проверьте подключение к интернету.\n{last?.Message}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp file; the OS will clean it eventually.
        }
    }
}
