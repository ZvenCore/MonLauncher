using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace MonLauncher;

public class AppConfig
{
    public string Username { get; set; } = "Player_1";
    public int RamMb { get; set; } = 6144;
    public bool QuickSkinEnabled { get; set; } = true;
    public string? CustomSkinBase64 { get; set; } = null;
    public string SyncServerUrl { get; set; } = "https://site.moncraft.space";
}

public class ModFileInfo
{
    public string Name { get; set; } = "";
    public string SizeFormatted { get; set; } = "";
    public long SizeBytes { get; set; }
}

public class ServerModItem
{
    public string name { get; set; } = "";
    public long size { get; set; }
    public string? sha1 { get; set; }
}

public class ServerModListResponse
{
    public long updated_at { get; set; }
    public string? client_zip { get; set; }
    public string? mods_base_url { get; set; }
    public int total_mods { get; set; }
    public List<ServerModItem>? mods { get; set; }
}

public class LauncherUpdateInfo
{
    public string version { get; set; } = "";
    public string url { get; set; } = "";
    public string? changelog { get; set; }
    public bool mandatory { get; set; } = false;
}

public class LauncherService
{
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public const string AppVersion = "1.0.0";
    public const string VersionName = "neoforge-21.1.249";
    public const string DefaultSyncUrl = "https://site.moncraft.space";
    public const string UpdateManifestPath = "/monl/launcher_version.json";
    public const string DiscordUrl = "https://discord.gg/E2Zv9pEwYG";
    public const string TelegramDevUrl = "https://t.me/ZvenCore";
    public const string AdoptiumJava21Url = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse?project=jdk";

    private readonly string _baseDir;
    private readonly string _minecraftDir;
    private readonly string _configFile;
    private readonly string _serverModsDir;
    private readonly string _userModsDir;
    private readonly string _gameModsDir;
    private readonly string _javaPortableDir;
    private readonly HttpClient _http;

    public LauncherService()
    {
        _baseDir = ResolveBaseDir();
        _minecraftDir = Path.Combine(_baseDir, ".minecraft");
        _configFile = Path.Combine(_baseDir, "launcher_config.json");
        _serverModsDir = Path.Combine(_baseDir, "server-mods");
        _userModsDir = Path.Combine(_baseDir, "user-mods");
        _gameModsDir = Path.Combine(_minecraftDir, "mods");
        _javaPortableDir = Path.Combine(_baseDir, "java");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MonLauncher/2.0");

        Directory.CreateDirectory(_serverModsDir);
        Directory.CreateDirectory(_userModsDir);
        Directory.CreateDirectory(_gameModsDir);
    }

    public string BaseDir => _baseDir;
    public string MinecraftDir => _minecraftDir;
    public string UserModsDir => _userModsDir;

    public List<ModFileInfo> GetUserMods()
    {
        var list = new List<ModFileInfo>();
        if (!Directory.Exists(_userModsDir)) return list;

        foreach (var file in Directory.EnumerateFiles(_userModsDir, "*.jar"))
        {
            try
            {
                var info = new FileInfo(file);
                double mb = info.Length / (1024.0 * 1024.0);
                var sizeStr = mb >= 1.0 ? $"{mb:0.00} МБ" : $"{Math.Max(1, info.Length / 1024.0):0} КБ";
                list.Add(new ModFileInfo
                {
                    Name = info.Name,
                    SizeBytes = info.Length,
                    SizeFormatted = sizeStr
                });
            }
            catch { }
        }
        return list;
    }

    public bool DeleteUserMod(string fileName)
    {
        try
        {
            var cleanName = Path.GetFileName(fileName);
            var path = Path.Combine(_userModsDir, cleanName);
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch { }
        return false;
    }

    public void SaveUserMod(string fileName, byte[] data)
    {
        var cleanName = Path.GetFileName(fileName);
        if (!cleanName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(_userModsDir, cleanName);
        File.WriteAllBytes(path, data);
    }

    private static string ResolveBaseDir()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".minecraft")))
                return current;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public AppConfig LoadConfig()
    {
        if (File.Exists(_configFile))
        {
            try
            {
                var json = File.ReadAllText(_configFile);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch { }
        }
        return new AppConfig();
    }

    public void SaveConfig(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFile, json);
        }
        catch { }
    }

    public int GetSystemRamGb()
    {
        try
        {
            var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (mem > 0)
                return (int)(mem / (1024 * 1024 * 1024));
        }
        catch { }
        return 16;
    }

    public static string GenerateOfflineUuid(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "OfflinePlayer:Player";
        var input = System.Text.Encoding.UTF8.GetBytes($"OfflinePlayer:{username.Trim()}");
        var hash = System.Security.Cryptography.MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variant IETF
        return new Guid(hash).ToString();
    }

    // =========================================================================
    // JAVA 21 RESOLUTION
    // =========================================================================

    public static bool CheckJavaVersion(string javaPath)
    {
        try
        {
            if (!File.Exists(javaPath)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var match = Regex.Match(output, @"version ""(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int major))
            {
                return major >= 21;
            }
        }
        catch { }
        return false;
    }

    public string? FindJavaExecutable()
    {
        // 1. Portable local java directory
        if (Directory.Exists(_javaPortableDir))
        {
            foreach (var file in Directory.EnumerateFiles(_javaPortableDir, "javaw.exe", SearchOption.AllDirectories))
            {
                if (CheckJavaVersion(file)) return file;
            }
            foreach (var file in Directory.EnumerateFiles(_javaPortableDir, "java.exe", SearchOption.AllDirectories))
            {
                if (CheckJavaVersion(file)) return file;
            }
        }

        // 2. .minecraft/runtime
        var runtimeDir = Path.Combine(_minecraftDir, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            foreach (var file in Directory.EnumerateFiles(runtimeDir, "javaw.exe", SearchOption.AllDirectories))
            {
                if (CheckJavaVersion(file)) return file;
            }
        }

        // 3. System PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candW = Path.Combine(dir, "javaw.exe");
            if (File.Exists(candW) && CheckJavaVersion(candW)) return candW;

            var cand = Path.Combine(dir, "java.exe");
            if (File.Exists(cand) && CheckJavaVersion(cand))
            {
                if (File.Exists(candW)) return candW;
                return cand;
            }
        }

        // 4. Common Oracle / Adoptium Program Files
        var roots = new[] { @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\BellSoft", @"C:\Program Files\Zulu" };
        foreach (var r in roots)
        {
            if (Directory.Exists(r))
            {
                foreach (var file in Directory.EnumerateFiles(r, "javaw.exe", SearchOption.AllDirectories))
                {
                    if (CheckJavaVersion(file)) return file;
                }
            }
        }

        return null;
    }

    public async Task<string?> DownloadAndExtractJavaAsync(Action<string, int?> progress)
    {
        progress("Скачивание портативной Java 21...", 0);
        Directory.CreateDirectory(_javaPortableDir);
        var zipPath = Path.Combine(_baseDir, "java21_download.zip");

        try
        {
            using var response = await _http.GetAsync(AdoptiumJava21Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (totalBytes > 0)
                {
                    int pct = (int)((totalRead * 100) / totalBytes);
                    progress($"Скачивание Java 21: {pct}%", pct);
                }
            }

            fileStream.Close();
            progress("Распаковка Java 21...", null);

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, _javaPortableDir, true));

            try { File.Delete(zipPath); } catch { }

            var javaExe = FindJavaExecutable();
            if (javaExe != null)
            {
                progress("Java 21 готова!", 100);
                return javaExe;
            }
        }
        catch (Exception ex)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            progress($"Ошибка загрузки Java 21: {ex.Message}", null);
        }

        return null;
    }

    // =========================================================================
    // CLIENT DOWNLOAD & MOD SYNC
    // =========================================================================

    public async Task<bool> EnsureClientInstalledAsync(Action<string, int?> progress)
    {
        var versionDir = Path.Combine(_minecraftDir, "versions", VersionName);
        var versionJson = Path.Combine(versionDir, $"{VersionName}.json");
        if (Directory.Exists(versionDir) && File.Exists(versionJson))
        {
            return true; // Уже установлен
        }

        progress("Первый запуск: загрузка файлов клиента (.minecraft)...", 0);

        var cfg = LoadConfig();
        var baseUrl = (string.IsNullOrWhiteSpace(cfg.SyncServerUrl) ? DefaultSyncUrl : cfg.SyncServerUrl).TrimEnd('/');
        var clientZipUrl = $"{baseUrl}/monl/client.zip";
        var tempZip = Path.Combine(_baseDir, "client_download.zip");

        try
        {
            using var resp = await _http.GetAsync(clientZipUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                progress($"Базовый архив клиента недоступен на сервере (HTTP {(int)resp.StatusCode}).", null);
                return false;
            }

            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

            var buffer = new byte[128 * 1024];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (totalBytes > 0)
                {
                    int pct = (int)((totalRead * 100) / totalBytes);
                    var mbDone = totalRead / (1024.0 * 1024.0);
                    var mbTotal = totalBytes / (1024.0 * 1024.0);
                    progress($"Загрузка клиента: {pct}% ({mbDone:0.0} / {mbTotal:0.0} МБ)...", pct);
                }
            }

            fileStream.Close();
            progress("Распаковка файлов клиента (.minecraft)...", null);

            await Task.Run(() =>
            {
                Directory.CreateDirectory(_minecraftDir);
                ZipFile.ExtractToDirectory(tempZip, _minecraftDir, true);
            });

            try { File.Delete(tempZip); } catch { }

            progress("Базовый клиент успешно установлен!", 100);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            progress($"Ошибка скачивания клиента: {ex.Message}", null);
            return false;
        }
    }

    public async Task SyncServerModsAsync(Action<string, int?> progress)
    {
        var cfg = LoadConfig();
        var baseUrl = (string.IsNullOrWhiteSpace(cfg.SyncServerUrl) ? DefaultSyncUrl : cfg.SyncServerUrl).TrimEnd('/');
        var modlistUrl = $"{baseUrl}/monl/list/modlist.json";

        progress("Синхронизация модов с сервером...", null);

        try
        {
            using var response = await _http.GetAsync(modlistUrl);
            if (!response.IsSuccessStatusCode)
            {
                progress($"Сервер модов вернул статус {(int)response.StatusCode}. Запуск с локальными модами...", null);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<ServerModListResponse>(json);
            if (manifest?.mods == null)
            {
                progress("Список модов на сервере пуст или имеет неверный формат.", null);
                return;
            }

            var serverMods = manifest.mods.Where(m => !string.IsNullOrWhiteSpace(m.name)).ToList();
            var serverModsMap = serverMods.ToDictionary(m => m.name, StringComparer.OrdinalIgnoreCase);

            // 1. Удаляем лишние моды из server-mods, которых больше нет на сервере
            int removedCount = 0;
            foreach (var file in Directory.EnumerateFiles(_serverModsDir, "*.jar"))
            {
                var fileName = Path.GetFileName(file);
                if (!serverModsMap.ContainsKey(fileName))
                {
                    try
                    {
                        File.Delete(file);
                        removedCount++;
                    }
                    catch { }
                }
            }

            if (removedCount > 0)
            {
                progress($"Удалено устаревших модов: {removedCount}", null);
            }

            // 2. Ищем недостающие или измененные по размеру моды
            var toDownload = new List<ServerModItem>();
            foreach (var mod in serverMods)
            {
                var localPath = Path.Combine(_serverModsDir, mod.name);
                if (!File.Exists(localPath))
                {
                    toDownload.Add(mod);
                }
                else if (mod.size > 0)
                {
                    var fi = new FileInfo(localPath);
                    if (fi.Length != mod.size)
                    {
                        toDownload.Add(mod);
                    }
                }
            }

            if (toDownload.Count == 0)
            {
                progress($"Все серверные моды актуальны ({serverMods.Count} шт.).", 100);
                return;
            }

            progress($"Загрузка {toDownload.Count} новых/обновленных модов...", 0);

            int completed = 0;
            int total = toDownload.Count;

            var modsSubPath = (manifest.mods_base_url ?? "/monl/mods/").TrimStart('/');
            var downloadBase = $"{baseUrl}/{modsSubPath}".TrimEnd('/') + "/";

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 6 };
            await Parallel.ForEachAsync(toDownload, parallelOptions, async (mod, ct) =>
            {
                var modUrl = downloadBase + Uri.EscapeDataString(mod.name);
                var localDest = Path.Combine(_serverModsDir, mod.name);
                var tempDest = localDest + ".part";

                try
                {
                    var data = await _http.GetByteArrayAsync(modUrl, ct);
                    await File.WriteAllBytesAsync(tempDest, data, ct);
                    if (File.Exists(localDest)) File.Delete(localDest);
                    File.Move(tempDest, localDest);
                }
                catch
                {
                    if (File.Exists(tempDest)) { try { File.Delete(tempDest); } catch { } }
                }

                var done = Interlocked.Increment(ref completed);
                int pct = (int)((done * 100.0) / total);
                progress($"Скачивание модов ({done}/{total}): {mod.name}", pct);
            });

            progress($"Синхронизация завершена! Загружено {toDownload.Count} модов.", 100);
        }
        catch (Exception ex)
        {
            progress($"Запуск с локальными модами ({ex.Message})", null);
        }
    }

    public void PrepareModsForLaunch(Action<string, int?> progress)
    {
        var desired = new Dictionary<string, string>();

        foreach (var f in Directory.EnumerateFiles(_serverModsDir, "*.jar"))
            desired[Path.GetFileName(f)] = f;

        foreach (var f in Directory.EnumerateFiles(_userModsDir, "*.jar"))
            desired[Path.GetFileName(f)] = f;

        // Remove obsolete from .minecraft/mods
        foreach (var f in Directory.EnumerateFileSystemEntries(_gameModsDir))
        {
            var name = Path.GetFileName(f);
            if (!desired.ContainsKey(name))
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    else if (Directory.Exists(f)) Directory.Delete(f, true);
                }
                catch { }
            }
        }

        // Mount / Hardlink
        int count = 0;
        foreach (var kv in desired)
        {
            var dest = Path.Combine(_gameModsDir, kv.Key);
            if (File.Exists(dest))
            {
                count++;
                continue;
            }

            // Try NTFS hardlink (instant, 0 ms)
            if (CreateHardLink(dest, kv.Value, IntPtr.Zero))
            {
                count++;
            }
            else
            {
                try
                {
                    File.Copy(kv.Value, dest, true);
                    count++;
                }
                catch { }
            }
        }

        progress($"Подключено {count} модов.", 100);
    }

    // =========================================================================
    // GAME LAUNCH
    // =========================================================================

    public async Task<bool> LaunchGameAsync(Action<string, int?> progress)
    {
        var cfg = LoadConfig();
        var username = string.IsNullOrWhiteSpace(cfg.Username) ? "Player" : cfg.Username.Trim();
        var ramMb = cfg.RamMb <= 1024 ? 4096 : cfg.RamMb;

        // 0. Проверка наличия .minecraft (при первой установке скачивает client.zip с VDS)
        var clientOk = await EnsureClientInstalledAsync(progress);
        var versionPath = Path.Combine(_minecraftDir, "versions", VersionName);
        if (!Directory.Exists(versionPath))
        {
            progress($"Файлы версии {VersionName} не найдены. Проверьте доступность VDS сервера.", null);
            return false;
        }

        // 1. Sync mods
        await SyncServerModsAsync(progress);

        // 2. Mount mods
        progress("Подготовка игровых модов...", null);
        PrepareModsForLaunch(progress);

        // 3. Resolve Java 21
        progress("Проверка Java 21...", null);
        var javaExe = FindJavaExecutable();
        if (javaExe == null)
        {
            javaExe = await DownloadAndExtractJavaAsync(progress);
            if (javaExe == null)
            {
                progress("Не удалось найти или скачать Java 21.", null);
                return false;
            }
        }

        // 4. Build process using CmlLib.Core
        progress("Загрузка и проверка ресурсов игры (звуки, текстуры)...", null);

        try
        {
            var mcPath = new MinecraftPath(_minecraftDir);
            var launcher = new MinecraftLauncher(mcPath);

            var opt = new MLaunchOption
            {
                Session = MSession.CreateOfflineSession(username),
                MaximumRamMb = ramMb,
                MinimumRamMb = ramMb,
                JavaPath = javaExe,
                GameLauncherName = "MON-SERVER-Launcher",
                GameLauncherVersion = "2.0.0"
            };

            var process = await launcher.CreateProcessAsync(VersionName, opt);

            progress("Запуск процесса Minecraft...", 100);

            process.StartInfo.WorkingDirectory = _minecraftDir;
            process.StartInfo.UseShellExecute = true;

            process.Start();

            progress("Minecraft запущен!", 100);
            return true;
        }
        catch (Exception ex)
        {
            progress($"Ошибка запуска: {ex.Message}", null);
            return false;
        }
    }

    public static void CleanupOldBinary()
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentExe))
            {
                var oldExe = currentExe + ".old";
                if (File.Exists(oldExe))
                {
                    File.Delete(oldExe);
                }
            }
        }
        catch
        {
            // Silently ignore if locked or inaccessible
        }
    }

    public async Task<LauncherUpdateInfo?> CheckForUpdatesAsync(string? serverUrl = null)
    {
        try
        {
            var baseUri = string.IsNullOrWhiteSpace(serverUrl) ? DefaultSyncUrl : serverUrl.TrimEnd('/');
            var updateUrl = $"{baseUri}{UpdateManifestPath}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            using var response = await _http.GetAsync(updateUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var info = JsonSerializer.Deserialize<LauncherUpdateInfo>(json);
            if (info == null || string.IsNullOrWhiteSpace(info.version) || string.IsNullOrWhiteSpace(info.url))
            {
                return null;
            }

            if (IsNewerVersion(info.version, AppVersion))
            {
                return info;
            }
        }
        catch
        {
            // Silently ignore network failures during background update check
        }
        return null;
    }

    public static bool IsNewerVersion(string remoteVersion, string localVersion)
    {
        var cleanRemote = remoteVersion.Trim().TrimStart('v', 'V');
        var cleanLocal = localVersion.Trim().TrimStart('v', 'V');

        if (Version.TryParse(cleanRemote, out var rVer) && Version.TryParse(cleanLocal, out var lVer))
        {
            return rVer > lVer;
        }

        return !string.Equals(cleanRemote, cleanLocal, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action<int, string>? progress = null)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return false;

        var newExe = currentExe + ".new";
        var oldExe = currentExe + ".old";

        try
        {
            progress?.Invoke(5, "Подключение к серверу обновлений...");

            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            var lastReportTime = DateTime.UtcNow;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                if ((DateTime.UtcNow - lastReportTime).TotalMilliseconds > 80 || totalRead == totalBytes)
                {
                    lastReportTime = DateTime.UtcNow;
                    if (totalBytes > 0)
                    {
                        var pct = (int)((double)totalRead / totalBytes * 100);
                        var mbRead = (totalRead / (1024.0 * 1024.0)).ToString("0.0");
                        var mbTotal = (totalBytes / (1024.0 * 1024.0)).ToString("0.0");
                        progress?.Invoke(Math.Min(99, pct), $"Загрузка обновления: {mbRead} МБ / {mbTotal} МБ ({pct}%)");
                    }
                    else
                    {
                        var mbRead = (totalRead / (1024.0 * 1024.0)).ToString("0.0");
                        progress?.Invoke(50, $"Загрузка обновления: {mbRead} МБ...");
                    }
                }
            }

            await fileStream.FlushAsync();
            fileStream.Close();

            progress?.Invoke(100, "Установка обновления и перезапуск...");
            await Task.Delay(500);

            // Atomic file swap
            if (File.Exists(oldExe))
            {
                try { File.Delete(oldExe); } catch { }
            }

            File.Move(currentExe, oldExe);
            File.Move(newExe, currentExe);

            // Launch updated binary
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                WorkingDirectory = Path.GetDirectoryName(currentExe) ?? _baseDir,
                UseShellExecute = true
            });

            // Shutdown current old instance
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(newExe)) File.Delete(newExe);
                if (!File.Exists(currentExe) && File.Exists(oldExe))
                {
                    File.Move(oldExe, currentExe); // rollback
                }
            }
            catch { }

            progress?.Invoke(-1, $"Ошибка обновления: {ex.Message}");
            return false;
        }
    }
}

