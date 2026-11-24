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
    private const int MaxTitleDisplayLength = 38;

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

    private static readonly HashSet<string> MetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vdf", ".acf", ".patch", ".manifest", ".tmp",
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
        TryDelete(PrimedMarker);

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

        const string baseStatus = "Первый запуск steamcmd: обновление…";
        reporter.Log("Первый запуск: steamcmd обновляет себя (около 100 МБ), это происходит один раз.");

        SteamRun? lastRun = null;
        for (var attempt = 1; attempt <= PrimeAttempts; attempt++)
        {
            reporter.Status(baseStatus, null);
            var currentText = baseStatus;
            SteamUpdateBytes? latestBytes = null;

            var run = await RunCoreAsync(
                ["+quit"],
                reporter.Log,
                percent =>
                {
                    if (percent is { } p && latestBytes is { TotalBytes: > 0 } b)
                    {
                        reporter.Status(
                            $"Первый запуск steamcmd: загрузка обновления ({Format.Size(b.DownloadedBytes)} из {Format.Size(b.TotalBytes)})",
                            p);
                    }
                    else
                    {
                        reporter.Status(currentText, percent);
                    }
                },
                stage =>
                {
                    currentText = stage switch
                    {
                        SteamStage.CheckingUpdates => "Первый запуск steamcmd: проверка обновлений…",
                        SteamStage.DownloadingUpdate => "Первый запуск steamcmd: загрузка обновления…",
                        SteamStage.ApplyingUpdate => "Первый запуск steamcmd: распаковка и установка обновления…",
                        SteamStage.VerifyingInstallation => "Первый запуск steamcmd: проверка файлов…",
                        SteamStage.LoadingApi => "Первый запуск steamcmd: инициализация Steam API…",
                        _ => baseStatus,
                    };
                    if (stage != SteamStage.DownloadingUpdate)
                        latestBytes = null;
                    reporter.Status(currentText, null);
                },
                bytes => latestBytes = bytes,
                monitorTaskFactory: null,
                ct);

            ThrowIfCannotStart(run);
            lastRun = run;

            if (run.ExitCode == 0 || (run.ExitCode == 7 && run.Transcript.Contains("Loading Steam API...OK", StringComparison.Ordinal)))
            {
                TryWritePrimedMarker();
                return;
            }

            if (attempt < PrimeAttempts)
                reporter.Log($"steamcmd завершился с кодом {run.ExitCode}, перезапуск ({attempt} из {PrimeAttempts}).");
        }

        if (lastRun is not null)
        {
            throw new InstallException(
                $"Не удалось обновить steamcmd при первом запуске (код {lastRun.ExitCode}). Проверьте подключение к интернету.\n" +
                $"Последние строки вывода:\n{lastRun.Tail(6)}");
        }
    }

    public Task<string> DownloadItemAsync(ulong id, Reporter reporter, CancellationToken ct, WorkshopItemInfo? metadata = null) =>
        DownloadItemCoreAsync(id, metadata, reporter, ct);

    private async Task<string> DownloadItemCoreAsync(ulong id, WorkshopItemInfo? metadata, Reporter reporter, CancellationToken ct)
    {
        var label = FormatItemLabel(id, metadata?.Title);
        var expectedBytes = metadata?.FileSize is > 0 ? metadata.FileSize : null;

        for (var attempt = 1; ; attempt++)
        {
            WipeWorkshopCache();

            var statusLock = new object();
            var currentStage = SteamStage.Starting;
            var currentStatus = $"Запуск steamcmd для загрузки {label}…";
            var lastSentText = "";
            double? lastSentPct = double.NaN;
            SteamUpdateBytes? selfUpdateBytes = null;
            var diskBytesObserved = false;

            void EmitStatus(string text, double? pct)
            {
                if (text == lastSentText && Nullable.Equals(pct, lastSentPct))
                    return;
                lastSentText = text;
                lastSentPct = pct;
                reporter.Status(text, pct);
            }

            EmitStatus(currentStatus, null);
            reporter.Log($"steamcmd: workshop_download_item {GameAppId} {id} (попытка {attempt} из {DownloadAttempts})");

            var run = await RunCoreAsync(
                [
                    "+@ShutdownOnFailedCommand", "1",
                    "+@NoPromptForPassword", "1",
                    "+force_install_dir", contentDir,
                    "+login", "anonymous",
                    "+workshop_download_item", GameAppId.ToString(), id.ToString(),
                    "+quit",
                ],
                reporter.Log,
                percent =>
                {
                    lock (statusLock)
                    {
                        if (currentStage == SteamStage.DownloadingUpdate && percent is { } up && selfUpdateBytes is { TotalBytes: > 0 } b)
                        {
                            EmitStatus(
                                $"Обновление steamcmd ({Format.Size(b.DownloadedBytes)} из {Format.Size(b.TotalBytes)})",
                                up);
                            return;
                        }

                        // Ignore stdout percent once disk monitoring is actively reporting item download bytes,
                        // unless steamcmd explicitly emits depot progress ("progress: XX.XX").
                        if (diskBytesObserved && currentStage == SteamStage.DownloadingItem && percent is null)
                            return;

                        if (currentStage == SteamStage.Finishing && diskBytesObserved && expectedBytes.HasValue)
                            return;

                        EmitStatus(currentStatus, percent);
                    }
                },
                stage =>
                {
                    lock (statusLock)
                    {
                        currentStage = stage;
                        if (stage != SteamStage.DownloadingUpdate)
                            selfUpdateBytes = null;

                        currentStatus = stage switch
                        {
                            SteamStage.CheckingUpdates => "Проверка обновлений steamcmd…",
                            SteamStage.DownloadingUpdate => "Загрузка обновления steamcmd…",
                            SteamStage.ApplyingUpdate => "Установка обновления steamcmd…",
                            SteamStage.VerifyingInstallation => "Проверка установки steamcmd…",
                            SteamStage.LoadingApi => "Запуск Steam API…",
                            SteamStage.Connecting => "Подключение к серверам Steam…",
                            SteamStage.LoggingIn => "Авторизация в Steam…",
                            SteamStage.DownloadingItem => expectedBytes is { } total
                                ? $"Подготовка скачивания {label} ({Format.Size(total)})…"
                                : $"Скачивание {label}…",
                            SteamStage.Finishing => $"Завершение загрузки {label}…",
                            _ => currentStatus,
                        };

                        if (!diskBytesObserved || stage != SteamStage.DownloadingItem)
                            EmitStatus(currentStatus, stage == SteamStage.Finishing && diskBytesObserved && expectedBytes.HasValue ? 100 : null);
                    }
                },
                bytes =>
                {
                    lock (statusLock)
                        selfUpdateBytes = bytes;
                },
                monitorCt => MonitorDownloadBytesAsync(
                    id,
                    label,
                    expectedBytes,
                    () =>
                    {
                        lock (statusLock)
                            return currentStage;
                    },
                    (text, pct) =>
                    {
                        lock (statusLock)
                        {
                            diskBytesObserved = true;
                            currentStatus = text;
                            if (currentStage is not SteamStage.Finishing)
                                EmitStatus(text, pct);
                        }
                    },
                    monitorCt),
                ct);

            ThrowIfCannotStart(run);

            if (run.Download is { Success: true } ok)
            {
                TryWritePrimedMarker();
                reporter.Status($"Загрузка {label} завершена", 100);

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
                reporter.Log("Сбой соединения со Steam, повтор через 3 секунды…");
                reporter.Status($"Сбой соединения, повторная попытка ({attempt + 1} из {DownloadAttempts})…", null);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            throw new InstallException(Explain(run, id));
        }
    }

    public void WipeWorkshopCache()
    {
        foreach (var cachePath in EnumerateCacheDirs())
        {
            try
            {
                if (Directory.Exists(cachePath))
                    Directory.Delete(cachePath, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A stale cache only costs disk space.
            }
        }
    }

    // Measures the active workshop payload size on disk without double-counting across staging folders.
    public long MeasureDownloadBytes(ulong itemId)
    {
        long maxBucket = 0;
        foreach (var bucket in EnumerateDownloadBuckets(itemId))
        {
            var bucketBytes = SumDataFiles(bucket);
            if (bucketBytes > maxBucket)
                maxBucket = bucketBytes;
        }

        return maxBucket;
    }

    private IEnumerable<string> EnumerateCacheDirs()
    {
        yield return WorkshopCacheDir;
        yield return Path.Combine(dir, "steamapps", "workshop");
        yield return Path.Combine(dir, "userdata", "anonymous", "ugc");
        yield return Path.Combine(homeDir, "Steam", "userdata", "anonymous", "ugc");
        yield return Path.Combine(homeDir, ".steam", "steam", "userdata", "anonymous", "ugc");
    }

    private IEnumerable<string> EnumerateDownloadBuckets(ulong itemId)
    {
        yield return Path.Combine(homeDir, "Steam", "userdata", "anonymous", "ugc", "download");
        yield return Path.Combine(homeDir, "Steam", "userdata", "anonymous", "ugc", "referenced");
        yield return Path.Combine(homeDir, ".steam", "steam", "userdata", "anonymous", "ugc", "download");
        yield return Path.Combine(homeDir, ".steam", "steam", "userdata", "anonymous", "ugc", "referenced");
        yield return Path.Combine(dir, "userdata", "anonymous", "ugc", "download");
        yield return Path.Combine(dir, "userdata", "anonymous", "ugc", "referenced");
        yield return Path.Combine(WorkshopCacheDir, "downloads");
        yield return Path.Combine(WorkshopCacheDir, "temp");
        yield return ItemDir(itemId);
    }

    private static long SumDataFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (MetadataExtensions.Contains(ext))
                    continue;

                try
                {
                    var len = new FileInfo(file).Length;
                    if (len > 0)
                        total += len;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    private async Task MonitorDownloadBytesAsync(
        ulong itemId,
        string label,
        long? expectedBytes,
        Func<SteamStage> getStage,
        Action<string, double?> onProgress,
        CancellationToken ct)
    {
        long bestBytes = 0;
        long lastReportedBytes = -1;
        var samples = new Queue<(long TimestampTicks, long Bytes)>();
        var sw = Stopwatch.StartNew();
        var lastEmitTicks = 0L;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var stage = getStage();
                if (stage is SteamStage.CheckingUpdates or SteamStage.DownloadingUpdate or SteamStage.ApplyingUpdate)
                    continue;

                var measured = MeasureDownloadBytes(itemId);
                if (measured > bestBytes)
                    bestBytes = measured;

                if (bestBytes <= 0)
                    continue;

                var nowTicks = sw.ElapsedTicks;
                samples.Enqueue((nowTicks, bestBytes));
                var windowTicks = Stopwatch.Frequency * 2;
                while (samples.Count > 2 && nowTicks - samples.Peek().TimestampTicks > windowTicks)
                    samples.Dequeue();

                double speed = 0;
                if (samples.Count >= 2)
                {
                    var oldest = samples.Peek();
                    var dt = (double)(nowTicks - oldest.TimestampTicks) / Stopwatch.Frequency;
                    if (dt >= 0.25 && bestBytes > oldest.Bytes)
                        speed = (bestBytes - oldest.Bytes) / dt;
                }

                // Update UI whenever bytes grew or every ~800ms to refresh the transfer rate smoothly.
                if (bestBytes == lastReportedBytes && nowTicks - lastEmitTicks < Stopwatch.Frequency * 4 / 5)
                    continue;

                lastReportedBytes = bestBytes;
                lastEmitTicks = nowTicks;

                var speedText = speed > 0 ? $", {Format.Speed(speed)}" : "";
                if (expectedBytes is > 0)
                {
                    var effectiveTotal = Math.Max(expectedBytes.Value, bestBytes);
                    var pct = Math.Clamp(bestBytes * 100.0 / effectiveTotal, 0.5, 99.0);
                    onProgress($"Скачивание {label}: {Format.Size(bestBytes)} из {Format.Size(effectiveTotal)}{speedText}", pct);
                }
                else
                {
                    onProgress($"Скачивание {label}: {Format.Size(bestBytes)}{speedText}…", null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatItemLabel(ulong id, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return $"предмета {id}";

        var clean = title.Trim();
        if (clean.Length > MaxTitleDisplayLength)
        {
            var cut = MaxTitleDisplayLength - 1;
            if (cut > 0 && char.IsHighSurrogate(clean[cut - 1]))
                cut--;
            clean = clean[..cut].TrimEnd() + "…";
        }

        return $"«{clean}»";
    }

    private async Task<SteamRun> RunCoreAsync(
        IReadOnlyList<string> args,
        Action<string> onLine,
        Action<double?> onPercent,
        Action<SteamStage>? onStage,
        Action<SteamUpdateBytes>? onUpdateBytes,
        Func<CancellationToken, Task>? monitorTaskFactory,
        CancellationToken ct)
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

        var output = new SteamOutput(onLine, onPercent, onStage, onUpdateBytes);
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
            try
            {
                while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
                {
                    var chunk = new string(buffer, 0, read);
                    lock (output)
                        output.Feed(chunk);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = monitorTaskFactory?.Invoke(monitorCts.Token) ?? Task.CompletedTask;

        try
        {
            using (ct.Register(() => Kill(process)))
            {
                await process.WaitForExitAsync(CancellationToken.None);
                var drainTimeout = ct.IsCancellationRequested ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2);
                await Task.WhenAny(pumps, Task.Delay(drainTimeout, CancellationToken.None));
            }
        }
        finally
        {
            await monitorCts.CancelAsync();
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (output)
            output.Flush();

        ct.ThrowIfCancellationRequested();
        return new SteamRun(process.ExitCode, output.Download, output.Transcript);
    }

    private void TryWritePrimedMarker()
    {
        try
        {
            File.WriteAllText(PrimedMarker, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
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
                "Invalid Parameter" => $"Предмет {id} не подходит для CS:GO (неверный тип предмета или другая игра).",
                "Disk write failure" or "Not Enough Disk Space" => "Не удалось записать временные файлы steamcmd: проверьте свободное место на диске и права доступа.",
                "No subscription" or "Locked" => $"Предмет {id} нельзя скачать анонимно через Steam Workshop ({reason}).",
                "Timeout" or "No Connection" or "Service Unavailable" => "Нет связи со Steam. Проверьте интернет и повторите.",
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("csgo-workshop/1.2");

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                reporter.Status("Загрузка steamcmd с серверов Valve…", null);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(destination);

                var buffer = new byte[81920];
                long done = 0;
                var sw = Stopwatch.StartNew();
                var lastReportMs = -100L;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;

                    var nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastReportMs >= 80 || (total > 0 && done >= total))
                    {
                        lastReportMs = nowMs;
                        if (total > 0)
                        {
                            reporter.Status(
                                $"Загрузка steamcmd: {Format.Size(done)} из {Format.Size(total)}",
                                Math.Clamp(done * 100.0 / total, 0, 100));
                        }
                        else
                        {
                            reporter.Status($"Загрузка steamcmd: {Format.Size(done)}…", null);
                        }
                    }
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
