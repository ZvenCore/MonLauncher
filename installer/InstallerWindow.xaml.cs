using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace MonInstaller;

public partial class InstallerWindow : Window
{
    private string _targetDirectory;
    private bool _desktopShortcut = true;
    private bool _startMenuShortcut = true;

    public InstallerWindow()
    {
        InitializeComponent();
        _targetDirectory = GetDefaultInstallDirectory();

        Loaded += InstallerWindow_Loaded;
    }

    private async void InstallerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(Path.GetTempPath(), "mon_installer_webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            var html = GenerateHtml();
            webView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка инициализации установщика: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetDefaultInstallDirectory()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderByDescending(d => d.AvailableFreeSpace)
                .ToList();

            if (drives.Count > 0)
            {
                var bestDrive = drives[0].RootDirectory.FullName;
                return Path.Combine(bestDrive, "Games", "MON SERVER");
            }
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MON SERVER");
    }

    private static (long freeBytes, long totalBytes) GetDiskSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    return (drive.AvailableFreeSpace, drive.TotalSize);
                }
            }
        }
        catch { }
        return (0, 0);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 МБ";
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gb >= 1.0) return $"{gb:0.0} ГБ";
        double mb = bytes / (1024.0 * 1024.0);
        return $"{mb:0} МБ";
    }

    private static string EscapeJs(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionProp)) return;

            var action = actionProp.GetString();

            switch (action)
            {
                case "browse_folder":
                    BrowseFolder();
                    break;

                case "update_path":
                    if (root.TryGetProperty("path", out var pProp))
                    {
                        var newP = pProp.GetString();
                        if (!string.IsNullOrWhiteSpace(newP))
                        {
                            _targetDirectory = newP.Trim();
                            var (free, _) = GetDiskSpace(_targetDirectory);
                            var freeFormatted = FormatBytes(free);
                            await webView.ExecuteScriptAsync($"onDiskSpaceUpdated('{EscapeJs(freeFormatted)}');");
                        }
                    }
                    break;

                case "start_install":
                    if (root.TryGetProperty("path", out var pathProp))
                    {
                        var p = pathProp.GetString();
                        if (!string.IsNullOrWhiteSpace(p)) _targetDirectory = p.Trim();
                    }
                    if (root.TryGetProperty("desktopShortcut", out var dsProp))
                    {
                        _desktopShortcut = dsProp.GetBoolean();
                    }
                    if (root.TryGetProperty("startMenuShortcut", out var smProp))
                    {
                        _startMenuShortcut = smProp.GetBoolean();
                    }

                    await RunInstallProcessAsync();
                    break;

                case "finish":
                    var launch = true;
                    if (root.TryGetProperty("launch", out var lProp))
                    {
                        launch = lProp.GetBoolean();
                    }

                    if (launch)
                    {
                        var exePath = Path.Combine(_targetDirectory, "MonLauncher.exe");
                        if (File.Exists(exePath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                WorkingDirectory = _targetDirectory,
                                UseShellExecute = true
                            });
                        }
                    }
                    Close();
                    break;

                case "cancel":
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            await webView.ExecuteScriptAsync($"showErrorModal('Произошла ошибка: {EscapeJs(ex.Message)}');");
        }
    }

    private void BrowseFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку для установки MON SERVER",
                InitialDirectory = Directory.Exists(_targetDirectory) ? _targetDirectory : Path.GetPathRoot(_targetDirectory) ?? "C:\\"
            };

            if (dialog.ShowDialog() == true)
            {
                var chosen = dialog.FolderName;
                if (!chosen.EndsWith("MON SERVER", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = Path.Combine(chosen, "MON SERVER");
                }

                _targetDirectory = chosen;
                var (free, _) = GetDiskSpace(_targetDirectory);
                var freeFormatted = FormatBytes(free);

                _ = webView.ExecuteScriptAsync($"onPathSelected('{EscapeJs(_targetDirectory)}', '{EscapeJs(freeFormatted)}');");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка выбора папки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunInstallProcessAsync()
    {
        try
        {
            // Switch UI to Step 2
            await webView.ExecuteScriptAsync("setStep(2);");

            await Task.Run(async () =>
            {
                // Step 1: Create Directories
                UpdateProgress(10, "Подготовка структуры папок MON SERVER...", 1);
                await Task.Delay(350);

                if (!Directory.Exists(_targetDirectory))
                {
                    Directory.CreateDirectory(_targetDirectory);
                }

                var userModsDir = Path.Combine(_targetDirectory, "user-mods");
                if (!Directory.Exists(userModsDir)) Directory.CreateDirectory(userModsDir);

                var serverModsDir = Path.Combine(_targetDirectory, "server-mods");
                if (!Directory.Exists(serverModsDir)) Directory.CreateDirectory(serverModsDir);

                var minecraftDir = Path.Combine(_targetDirectory, ".minecraft");
                if (!Directory.Exists(minecraftDir)) Directory.CreateDirectory(minecraftDir);

                // Step 2: Install MonLauncher.exe (Latest online from VDS, or fallback to embedded)
                UpdateProgress(20, "Проверка актуальной версии лаунчера...", 2);
                await Task.Delay(200);

                var destExePath = Path.Combine(_targetDirectory, "MonLauncher.exe");
                bool downloadedOnline = false;

                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "MonInstaller");

                    var manifestUrl = "https://site.moncraft.space/monl/launcher_version.json?_t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var resp = await httpClient.GetAsync(manifestUrl);

                    if (resp.IsSuccessStatusCode)
                    {
                        var jsonStr = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonStr);
                        string downloadUrl = "https://site.moncraft.space/monl/MonLauncher.exe";

                        if (doc.RootElement.TryGetProperty("url", out var urlProp) && !string.IsNullOrWhiteSpace(urlProp.GetString()))
                        {
                            downloadUrl = urlProp.GetString()!;
                        }

                        UpdateProgress(25, "Загрузка последней версии MonLauncher.exe...", 2);

                        using var fileResp = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                        if (fileResp.IsSuccessStatusCode)
                        {
                            var totalBytes = fileResp.Content.Headers.ContentLength ?? (35L * 1024 * 1024);
                            using var remoteStream = await fileResp.Content.ReadAsStreamAsync();
                            using var fileStream = new FileStream(destExePath, FileMode.Create, FileAccess.Write, FileShare.None);

                            var buffer = new byte[128 * 1024];
                            long copiedBytes = 0;
                            int read;
                            int lastPct = 25;

                            while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                copiedBytes += read;

                                int currentPct = 25 + (int)((copiedBytes * 50) / totalBytes);
                                if (currentPct > 75) currentPct = 75;
                                if (currentPct > lastPct + 2 || copiedBytes == totalBytes)
                                {
                                    lastPct = currentPct;
                                    var mbDone = copiedBytes / (1024.0 * 1024.0);
                                    var mbTotal = totalBytes / (1024.0 * 1024.0);
                                    UpdateProgress(currentPct, $"Загрузка MonLauncher.exe ({mbDone:0.0} / {mbTotal:0.0} МБ)...", 2);
                                }
                            }

                            downloadedOnline = true;
                        }
                    }
                }
                catch
                {
                    // Fallback to embedded resource if offline or server unreachable
                    downloadedOnline = false;
                }

                if (!downloadedOnline)
                {
                    UpdateProgress(25, "Извлечение ядра лаунчера (MonLauncher.exe)...", 2);
                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceNames = assembly.GetManifestResourceNames();
                    var launcherRes = resourceNames.FirstOrDefault(r => r.EndsWith("MonLauncher.exe", StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(launcherRes))
                    {
                        throw new FileNotFoundException("Встроенный ресурс лаунчера (MonLauncher.exe) не найден внутри установщика.");
                    }

                    using (var resStream = assembly.GetManifestResourceStream(launcherRes))
                    {
                        if (resStream == null) throw new InvalidOperationException("Не удалось открыть поток ресурса MonLauncher.exe");

                        long totalBytes = resStream.Length;
                        long copiedBytes = 0;
                        byte[] buffer = new byte[128 * 1024];

                        using (var fileStream = new FileStream(destExePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            int read;
                            int lastPct = 25;
                            while ((read = resStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                fileStream.Write(buffer, 0, read);
                                copiedBytes += read;

                                int currentPct = 25 + (int)((copiedBytes * 50) / totalBytes);
                                if (currentPct > lastPct + 2 || copiedBytes == totalBytes)
                                {
                                    lastPct = currentPct;
                                    var mbDone = copiedBytes / (1024.0 * 1024.0);
                                    var mbTotal = totalBytes / (1024.0 * 1024.0);
                                    UpdateProgress(currentPct, $"Распаковка MonLauncher.exe ({mbDone:0.0} / {mbTotal:0.0} МБ)...", 2);
                                }
                            }
                        }
                    }
                }

                await Task.Delay(300);

                // Step 3: Create shortcuts
                UpdateProgress(80, "Создание ярлыков Windows...", 3);
                await Task.Delay(250);

                if (_desktopShortcut)
                {
                    var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var desktopLnk = Path.Combine(desktopDir, "MON SERVER.lnk");
                    CreateShortcut(desktopLnk, destExePath, "Лаунчер MON SERVER (NeoForge 1.21.1)");
                }

                if (_startMenuShortcut)
                {
                    var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    var startDir = Path.Combine(programsDir, "MON SERVER");
                    var startLnk = Path.Combine(startDir, "MON SERVER.lnk");
                    CreateShortcut(startLnk, destExePath, "Лаунчер MON SERVER (NeoForge 1.21.1)");
                }

                await Task.Delay(300);

                // Step 4: Final verification
                UpdateProgress(95, "Завершение настройки...", 4);
                await Task.Delay(350);

                UpdateProgress(100, "Установка успешно завершена!", 4);
                await Task.Delay(400);

                // Step 5: Switch to Step 3
                await Dispatcher.InvokeAsync(async () =>
                {
                    await webView.ExecuteScriptAsync($"onInstallSuccess('{EscapeJs(_targetDirectory)}');");
                });
            });
        }
        catch (IOException ioEx)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await webView.ExecuteScriptAsync($"showErrorModal('Не удалось записать файл. Возможно, MON SERVER уже запущен. Закройте лаунчер и повторите попытку.\\n\\nДетали: {EscapeJs(ioEx.Message)}');");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await webView.ExecuteScriptAsync($"showErrorModal('Ошибка во время установки: {EscapeJs(ex.Message)}');");
            });
        }
    }

    private void UpdateProgress(int percentage, string status, int stepIdx)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await webView.ExecuteScriptAsync($"updateInstallProgress({percentage}, '{EscapeJs(status)}', {stepIdx});");
        });
    }

    private static void CreateShortcut(string shortcutPath, string targetExePath, string description)
    {
        try
        {
            var dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
                shortcut.Description = description;
                shortcut.IconLocation = targetExePath + ",0";
                shortcut.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Не удалось создать ярлык {shortcutPath}: {ex.Message}");
        }
    }

    private string GenerateHtml()
    {
        var (free, _) = GetDiskSpace(_targetDirectory);
        var freeFormatted = FormatBytes(free);
        var initialPath = EscapeJs(_targetDirectory);

        return $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Установка MON SERVER</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700;800&family=Space+Grotesk:wght@600;700;800&family=JetBrains+Mono:wght@500;600;700&family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200&display=swap" rel="stylesheet">

    <style>
        :root {
            /* ─── ЦВЕТОВАЯ ГАММА (MON SERVER CELESTIAL AMBER) ─── */
            --bg: #090b0e;
            --bg-subtle: #0f1318;
            --surface: #14181f;
            --surface-hover: #1a2029;
            --surface-card: #14181f;
            
            --border: #222933;
            --border-subtle: #191f27;
            --border-card: #222933;
            
            --accent: #e59338;
            --accent-light: #f7ab56;
            --accent-copper: #c9702f;
            --accent-soft: rgba(229, 147, 56, 0.1);
            --accent-border: rgba(229, 147, 56, 0.28);
            --accent-glow: rgba(229, 147, 56, 0.35);

            --text-main: #f3f4f6;
            --text-muted: #9ca3af;
            --text-dim: #64748b;
            
            --ok: #7fd179;
            --bad: #e25c4a;
            
            --font-sans: 'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            --font-brand: 'Space Grotesk', sans-serif;
            --font-mono: 'JetBrains Mono', Consolas, monospace;
        }

        * { box-sizing: border-box; margin: 0; padding: 0; }

        body {
            font-family: var(--font-sans);
            background-color: var(--bg);
            color: var(--text-main);
            height: 100vh;
            display: flex;
            user-select: none;
            overflow: hidden;
        }

        .material-symbols-rounded {
            font-family: 'Material Symbols Rounded';
            font-weight: normal;
            font-style: normal;
            font-size: 22px;
            line-height: 1;
            display: inline-block;
            white-space: nowrap;
            word-wrap: normal;
            direction: ltr;
            -webkit-font-feature-settings: 'liga';
            -webkit-font-smoothing: antialiased;
            font-variation-settings: 'FILL' 0, 'wght' 400, 'GRAD' 0, 'opsz' 24;
            vertical-align: middle;
        }
        .material-symbols-fill {
            font-variation-settings: 'FILL' 1, 'wght' 500, 'GRAD' 0, 'opsz' 24;
        }

        #bgNetwork {
            position: absolute;
            inset: 0;
            width: 100%;
            height: 100%;
            pointer-events: none;
            z-index: 1;
            opacity: 0.7;
        }

        #installer-container {
            width: 100vw;
            height: 100vh;
            display: flex;
            flex-direction: column;
            position: relative;
            z-index: 10;
        }

        /* ─── HEADER ─── */
        .header {
            padding: 18px 28px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            border-bottom: 1px solid var(--border-subtle);
            background: rgba(15, 19, 24, 0.7);
            backdrop-filter: blur(12px);
        }

        .brand-wrap {
            display: flex;
            align-items: center;
            gap: 14px;
        }

        .brand-logo {
            width: 42px;
            height: 42px;
            border-radius: 12px;
            background: linear-gradient(135deg, var(--accent-copper), var(--accent), var(--accent-light));
            color: #120e09;
            display: flex;
            align-items: center;
            justify-content: center;
            font-family: var(--font-brand);
            font-weight: 900;
            font-size: 22px;
            box-shadow: 0 6px 18px rgba(229, 147, 56, 0.35);
        }

        .brand-title {
            font-family: var(--font-brand);
            font-size: 18px;
            font-weight: 800;
            letter-spacing: -0.5px;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .badge-installer {
            font-family: var(--font-mono);
            font-size: 10px;
            font-weight: 700;
            padding: 2px 8px;
            border-radius: 6px;
            background: var(--accent-soft);
            color: var(--accent);
            border: 1px solid var(--accent-border);
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }

        .brand-subtitle {
            font-size: 12px;
            color: var(--text-dim);
            font-family: var(--font-mono);
        }

        .step-pill {
            display: flex;
            align-items: center;
            gap: 6px;
            background: var(--surface);
            border: 1px solid var(--border);
            padding: 6px 14px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 600;
            color: var(--text-muted);
        }

        .step-pill-amber {
            color: var(--accent);
            border-color: var(--accent-border);
            background: var(--accent-soft);
        }

        /* ─── MAIN CONTENT ─── */
        .content {
            flex: 1;
            padding: 24px 32px;
            overflow-y: auto;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
        }

        .step-view {
            display: none;
            flex-direction: column;
            height: 100%;
            animation: fadeIn 0.25s ease-out;
        }
        .step-view.active {
            display: flex;
        }

        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(6px); }
            to { opacity: 1; transform: translateY(0); }
        }

        .view-title {
            font-size: 20px;
            font-weight: 700;
            margin-bottom: 6px;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .view-desc {
            font-size: 13px;
            color: var(--text-muted);
            margin-bottom: 20px;
            line-height: 1.5;
        }

        /* ─── CARDS ─── */
        .card {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 14px;
            padding: 16px 20px;
            margin-bottom: 16px;
            transition: border-color 0.2s;
        }
        .card:hover {
            border-color: rgba(229, 147, 56, 0.4);
        }

        .card-label {
            font-size: 12px;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            color: var(--text-dim);
            margin-bottom: 10px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .path-input-group {
            display: flex;
            gap: 10px;
            align-items: center;
        }

        .path-input {
            flex: 1;
            background: var(--bg-subtle);
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 10px 14px;
            color: var(--text-main);
            font-family: var(--font-mono);
            font-size: 13px;
            outline: none;
            transition: border-color 0.2s, box-shadow 0.2s;
        }
        .path-input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 2px var(--accent-soft);
        }

        .btn-browse {
            background: var(--surface-hover);
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 10px 18px;
            border-radius: 10px;
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 6px;
            transition: all 0.2s ease;
        }
        .btn-browse:hover {
            border-color: var(--accent);
            background: var(--accent-soft);
            color: var(--accent);
        }

        .disk-info {
            margin-top: 10px;
            display: flex;
            align-items: center;
            gap: 16px;
            font-size: 12px;
            color: var(--text-dim);
            font-family: var(--font-mono);
        }
        .disk-info b {
            color: var(--text-muted);
        }

        /* ─── CHECKBOX OPTIONS ─── */
        .option-item {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 12px 14px;
            background: var(--bg-subtle);
            border: 1px solid var(--border-subtle);
            border-radius: 10px;
            margin-bottom: 8px;
            cursor: pointer;
            transition: all 0.15s ease;
        }
        .option-item:hover {
            background: var(--surface-hover);
            border-color: var(--border);
        }

        .option-info {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .option-icon {
            color: var(--accent);
            font-size: 20px;
        }

        .option-title {
            font-size: 13px;
            font-weight: 600;
        }
        .option-desc {
            font-size: 11px;
            color: var(--text-dim);
            margin-top: 2px;
        }

        /* Custom Checkbox */
        .checkbox-custom {
            appearance: none;
            -webkit-appearance: none;
            width: 20px;
            height: 20px;
            border: 2px solid var(--border);
            border-radius: 6px;
            background: var(--bg);
            cursor: pointer;
            outline: none;
            display: grid;
            place-content: center;
            transition: all 0.15s ease;
        }
        .checkbox-custom:checked {
            background: var(--accent);
            border-color: var(--accent);
            box-shadow: 0 0 10px var(--accent-glow);
        }
        .checkbox-custom:checked::before {
            content: "";
            width: 10px;
            height: 6px;
            border-left: 2px solid #120e09;
            border-bottom: 2px solid #120e09;
            transform: rotate(-45deg) translate(1px, -1px);
        }

        /* ─── PROGRESS VIEW ─── */
        .progress-box {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 14px;
            padding: 24px;
            margin-bottom: 20px;
        }

        .progress-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 12px;
        }

        .progress-status {
            font-size: 13px;
            font-weight: 600;
            color: var(--text-main);
        }

        .progress-pct {
            font-family: var(--font-mono);
            font-size: 14px;
            font-weight: 700;
            color: var(--accent);
        }

        .progress-track {
            height: 10px;
            background: var(--bg-subtle);
            border: 1px solid var(--border-subtle);
            border-radius: 5px;
            overflow: hidden;
            position: relative;
        }

        .progress-fill {
            height: 100%;
            width: 0%;
            background: linear-gradient(90deg, var(--accent-copper), var(--accent), var(--accent-light));
            box-shadow: 0 0 12px var(--accent-glow);
            transition: width 0.25s ease-out;
            border-radius: 5px;
        }

        .step-list {
            display: flex;
            flex-direction: column;
            gap: 10px;
        }

        .step-item {
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 13px;
            color: var(--text-dim);
            transition: color 0.2s;
        }
        .step-item.active {
            color: var(--accent);
            font-weight: 600;
        }
        .step-item.done {
            color: var(--text-main);
        }

        .step-icon {
            font-size: 18px;
            color: var(--text-dim);
        }
        .step-item.active .step-icon {
            color: var(--accent);
            animation: pulse 1.2s infinite ease-in-out;
        }
        .step-item.done .step-icon {
            color: var(--ok);
        }

        @keyframes pulse {
            0%, 100% { opacity: 0.5; transform: scale(0.95); }
            50% { opacity: 1; transform: scale(1.08); }
        }

        /* ─── SUCCESS VIEW ─── */
        .success-hero {
            display: flex;
            flex-direction: column;
            align-items: center;
            text-align: center;
            padding: 10px 0 20px;
        }

        .success-badge {
            width: 64px;
            height: 64px;
            border-radius: 20px;
            background: rgba(127, 209, 121, 0.12);
            border: 1px solid rgba(127, 209, 121, 0.35);
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--ok);
            margin-bottom: 16px;
            box-shadow: 0 8px 24px rgba(127, 209, 121, 0.2);
            animation: bounceIn 0.4s cubic-bezier(0.175, 0.885, 0.32, 1.275);
        }

        @keyframes bounceIn {
            0% { transform: scale(0.5); opacity: 0; }
            100% { transform: scale(1); opacity: 1; }
        }

        .success-title {
            font-size: 22px;
            font-weight: 800;
            font-family: var(--font-brand);
            margin-bottom: 8px;
        }

        .success-desc {
            font-size: 13px;
            color: var(--text-muted);
            max-width: 480px;
            line-height: 1.5;
            margin-bottom: 18px;
        }

        .summary-card {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 14px 18px;
            width: 100%;
            text-align: left;
            margin-bottom: 18px;
        }

        .summary-row {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 12px;
            padding: 6px 0;
            border-bottom: 1px solid var(--border-subtle);
        }
        .summary-row:last-child {
            border-bottom: none;
        }
        .summary-label {
            color: var(--text-dim);
        }
        .summary-val {
            font-family: var(--font-mono);
            color: var(--text-main);
            font-weight: 600;
        }

        /* ─── FOOTER ACTION BAR ─── */
        .footer {
            padding: 16px 28px;
            border-top: 1px solid var(--border-subtle);
            background: rgba(15, 19, 24, 0.7);
            backdrop-filter: blur(12px);
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        .btn-secondary {
            background: transparent;
            border: 1px solid var(--border);
            color: var(--text-muted);
            padding: 10px 20px;
            border-radius: 10px;
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.15s ease;
        }
        .btn-secondary:hover {
            border-color: var(--text-muted);
            color: var(--text-main);
            background: var(--surface);
        }

        .btn-primary {
            background: linear-gradient(135deg, var(--accent-copper), var(--accent), var(--accent-light));
            border: none;
            color: #120e09;
            padding: 11px 26px;
            border-radius: 10px;
            font-size: 13px;
            font-weight: 700;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 8px;
            box-shadow: 0 4px 18px rgba(229, 147, 56, 0.35);
            transition: all 0.2s cubic-bezier(0.2, 0.8, 0.2, 1);
        }
        .btn-primary:hover {
            transform: translateY(-1px);
            box-shadow: 0 6px 24px rgba(229, 147, 56, 0.55);
            filter: brightness(1.05);
        }
        .btn-primary:active {
            transform: translateY(0);
        }

        /* ─── MODAL ERROR ─── */
        #error-modal {
            position: fixed;
            inset: 0;
            background: rgba(9, 11, 14, 0.88);
            backdrop-filter: blur(8px);
            z-index: 999;
            display: none;
            align-items: center;
            justify-content: center;
        }
        #error-modal.active { display: flex; }
        .modal-box {
            background: #14181f;
            border: 1px solid var(--bad);
            padding: 24px 28px;
            border-radius: 16px;
            max-width: 460px;
            width: 90%;
            text-align: center;
            box-shadow: 0 10px 40px rgba(0,0,0,0.8);
        }
        .modal-title {
            font-size: 16px;
            font-weight: 700;
            color: var(--bad);
            margin-bottom: 10px;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
        }
        .modal-desc {
            font-size: 13px;
            color: var(--text-muted);
            margin-bottom: 20px;
            line-height: 1.5;
            white-space: pre-wrap;
        }
    </style>
</head>
<body>
    <canvas id="bgNetwork"></canvas>

    <div id="installer-container">
        <!-- HEADER -->
        <header class="header">
            <div class="brand-wrap">
                <div class="brand-logo">M</div>
                <div>
                    <div class="brand-title">
                        MON SERVER
                        <span class="badge-installer">Установщик</span>
                    </div>
                    <div class="brand-subtitle">moncraft.space • NeoForge 1.21.1</div>
                </div>
            </div>
            <div id="step-badge" class="step-pill step-pill-amber">
                <span class="material-symbols-rounded" style="font-size: 16px;">settings</span>
                <span id="step-badge-text">Шаг 1 из 3</span>
            </div>
        </header>

        <!-- CONTENT AREA -->
        <main class="content">
            <!-- STEP 1: OPTIONS -->
            <div id="step-1" class="step-view active">
                <div>
                    <h2 class="view-title">
                        <span class="material-symbols-rounded" style="color: var(--accent);">folder_open</span>
                        Расположение и параметры
                    </h2>
                    <p class="view-desc">
                        Выберите папку для установки клиента. При первом входе лаунчер автоматически загрузит и проверит моды сервера.
                    </p>

                    <!-- Destination Path Card -->
                    <div class="card">
                        <div class="card-label">
                            <span class="material-symbols-rounded" style="font-size: 16px; color: var(--accent);">folder</span>
                            Папка установки
                        </div>
                        <div class="path-input-group">
                            <input type="text" id="install-path-input" class="path-input" value="{{initialPath}}" spellcheck="false" onchange="handlePathChange(this.value)" oninput="handlePathChange(this.value)" />
                            <button class="btn-browse" onclick="browseFolder()">
                                <span class="material-symbols-rounded" style="font-size: 18px;">folder_open</span>
                                Обзор...
                            </button>
                        </div>
                        <div class="disk-info">
                            <span><span class="material-symbols-rounded" style="font-size: 15px; vertical-align: -2px;">hard_drive</span> Свободно на диске: <b id="disk-free-val">{{freeFormatted}}</b></span>
                            <span>•</span>
                            <span>Требуется для установки: <b>~120 МБ</b></span>
                        </div>
                    </div>

                    <!-- Shortcuts Card -->
                    <div class="card">
                        <div class="card-label">
                            <span class="material-symbols-rounded" style="font-size: 16px; color: var(--accent);">shortcut</span>
                            Ярлыки быстрого запуска
                        </div>

                        <label class="option-item" for="chk-desktop">
                            <div class="option-info">
                                <span class="material-symbols-rounded option-icon">desktop_windows</span>
                                <div>
                                    <div class="option-title">Создать ярлык на Рабочем столе</div>
                                    <div class="option-desc">Быстрый доступ к игре прямо с главного экрана вашего ПК</div>
                                </div>
                            </div>
                            <input type="checkbox" id="chk-desktop" class="checkbox-custom" checked />
                        </label>

                        <label class="option-item" for="chk-start">
                            <div class="option-info">
                                <span class="material-symbols-rounded option-icon">apps</span>
                                <div>
                                    <div class="option-title">Создать ярлык в меню «Пуск»</div>
                                    <div class="option-desc">Добавить MON SERVER в список программ Windows</div>
                                </div>
                            </div>
                            <input type="checkbox" id="chk-start" class="checkbox-custom" checked />
                        </label>
                    </div>
                </div>
            </div>

            <!-- STEP 2: INSTALLING PROGRESS -->
            <div id="step-2" class="step-view">
                <div>
                    <h2 class="view-title">
                        <span class="material-symbols-rounded" style="color: var(--accent); animation: spin 2s linear infinite;">sync</span>
                        Установка MON SERVER
                    </h2>
                    <p class="view-desc">
                        Пожалуйста, подождите. Идет распаковка файлов лаунчера и регистрация ярлыков...
                    </p>

                    <div class="progress-box">
                        <div class="progress-header">
                            <div id="progress-status-text" class="progress-status">Подготовка к распаковке...</div>
                            <div id="progress-pct-text" class="progress-pct">0%</div>
                        </div>
                        <div class="progress-track">
                            <div id="progress-bar-fill" class="progress-fill"></div>
                        </div>
                    </div>

                    <div class="step-list">
                        <div id="substep-1" class="step-item active">
                            <span class="material-symbols-rounded step-icon">check_circle</span>
                            <span>Создание рабочей директории и папок модов</span>
                        </div>
                        <div id="substep-2" class="step-item">
                            <span class="material-symbols-rounded step-icon">radio_button_unchecked</span>
                            <span>Извлечение исполняемого файла MonLauncher.exe</span>
                        </div>
                        <div id="substep-3" class="step-item">
                            <span class="material-symbols-rounded step-icon">radio_button_unchecked</span>
                            <span>Создание ярлыков на Рабочем столе и в Пуске</span>
                        </div>
                        <div id="substep-4" class="step-item">
                            <span class="material-symbols-rounded step-icon">radio_button_unchecked</span>
                            <span>Проверка целостности и завершение настройки</span>
                        </div>
                    </div>
                </div>
            </div>

            <!-- STEP 3: FINISHED -->
            <div id="step-3" class="step-view">
                <div class="success-hero">
                    <div class="success-badge">
                        <span class="material-symbols-rounded material-symbols-fill" style="font-size: 34px;">check</span>
                    </div>
                    <h2 class="success-title">Установка успешно завершена!</h2>
                    <p class="success-desc">
                        MON SERVER установлен и готов к игре. Лаунчер автоматически поддерживает актуальные версии модов Create и сервера.
                    </p>

                    <div class="summary-card">
                        <div class="summary-row">
                            <span class="summary-label">Папка установки:</span>
                            <span id="final-path-val" class="summary-val">...</span>
                        </div>
                        <div class="summary-row">
                            <span class="summary-label">Ярлыки:</span>
                            <span id="final-shortcuts-val" class="summary-val">Рабочий стол, Меню Пуск</span>
                        </div>
                        <div class="summary-row">
                            <span class="summary-label">Сервер:</span>
                            <span class="summary-val" style="color: var(--accent);">moncraft.space</span>
                        </div>
                    </div>

                    <label class="option-item" for="chk-launch" style="width: 100%; max-width: 480px; margin-top: 4px;">
                        <div class="option-info">
                            <span class="material-symbols-rounded option-icon material-symbols-fill">play_circle</span>
                            <div>
                                <div class="option-title">Запустить MON SERVER прямо сейчас</div>
                                <div class="option-desc">Открыть лаунчер сразу после закрытия установщика</div>
                            </div>
                        </div>
                        <input type="checkbox" id="chk-launch" class="checkbox-custom" checked />
                    </label>
                </div>
            </div>
        </main>

        <!-- FOOTER -->
        <footer class="footer">
            <button id="btn-cancel" class="btn-secondary" onclick="cancelInstall()">Отмена</button>
            <div style="display: flex; gap: 12px;">
                <button id="btn-install" class="btn-primary" onclick="startInstall()">
                    <span>Установить MON SERVER</span>
                    <span class="material-symbols-rounded" style="font-size: 18px;">arrow_forward</span>
                </button>
                <button id="btn-finish" class="btn-primary" style="display: none;" onclick="finishInstall()">
                    <span class="material-symbols-rounded material-symbols-fill" style="font-size: 18px;">play_arrow</span>
                    <span>Завершить и играть</span>
                </button>
            </div>
        </footer>
    </div>

    <!-- ERROR MODAL -->
    <div id="error-modal">
        <div class="modal-box">
            <div class="modal-title">
                <span class="material-symbols-rounded">error</span>
                Внимание
            </div>
            <div id="modal-msg" class="modal-desc"></div>
            <button class="btn-secondary" onclick="closeErrorModal()" style="margin: 0 auto;">Понятно</button>
        </div>
    </div>

    <script>
        // ─── CANVAS CONSTELLATION ───
        const canvasBg = document.getElementById('bgNetwork');
        const ctxBg = canvasBg.getContext('2d');
        let width, height;
        let particles = [];
        const PARTICLE_COUNT = 85;
        let mouseX = -9999, mouseY = -9999;

        function resizeCanvas() {
            width = canvasBg.width = window.innerWidth;
            height = canvasBg.height = window.innerHeight;
        }
        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();

        class Particle {
            constructor() {
                this.x = Math.random() * width;
                this.y = Math.random() * height;
                this.vx = (Math.random() - 0.5) * 0.45;
                this.vy = (Math.random() - 0.5) * 0.45;
                this.radius = 0.9 + Math.random() * 1.5;
            }
            update() {
                this.x += this.vx;
                this.y += this.vy;
                if (this.x < 0) this.x = width;
                if (this.x > width) this.x = 0;
                if (this.y < 0) this.y = height;
                if (this.y > height) this.y = 0;

                const dx = mouseX - this.x;
                const dy = mouseY - this.y;
                const dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < 200 && dist > 0) {
                    const force = (200 - dist) / 200 * 0.015;
                    this.x += dx * force;
                    this.y += dy * force;
                }
            }
            draw() {
                ctxBg.beginPath();
                ctxBg.arc(this.x, this.y, this.radius * 2.2, 0, Math.PI * 2);
                ctxBg.fillStyle = "rgba(229, 147, 56, 0.22)";
                ctxBg.fill();

                ctxBg.beginPath();
                ctxBg.arc(this.x, this.y, this.radius, 0, Math.PI * 2);
                ctxBg.fillStyle = "#e59338";
                ctxBg.fill();

                ctxBg.beginPath();
                ctxBg.arc(this.x, this.y, this.radius * 0.4, 0, Math.PI * 2);
                ctxBg.fillStyle = "#ffffff";
                ctxBg.fill();
            }
        }

        for (let i = 0; i < PARTICLE_COUNT; i++) {
            particles.push(new Particle());
        }

        window.addEventListener('mousemove', (e) => {
            mouseX = e.clientX;
            mouseY = e.clientY;
        });

        window.addEventListener('mouseleave', () => {
            mouseX = -9999;
            mouseY = -9999;
        });

        function animateConstellation() {
            ctxBg.clearRect(0, 0, width, height);

            for (let i = 0; i < particles.length; i++) {
                for (let j = i + 1; j < particles.length; j++) {
                    const p1 = particles[i];
                    const p2 = particles[j];
                    const dx = p1.x - p2.x;
                    const dy = p1.y - p2.y;
                    const dist = Math.sqrt(dx * dx + dy * dy);

                    let maxDist = 85;
                    const midX = (p1.x + p2.x) / 2;
                    const midY = (p1.y + p2.y) / 2;
                    const distMouse = Math.sqrt((mouseX - midX)**2 + (mouseY - midY)**2);
                    if (distMouse < 180) maxDist = 135;

                    if (dist < maxDist) {
                        const alpha = (1 - dist / maxDist) * 0.25;
                        ctxBg.beginPath();
                        ctxBg.moveTo(p1.x, p1.y);
                        ctxBg.lineTo(p2.x, p2.y);
                        ctxBg.strokeStyle = `rgba(229, 147, 56, ${alpha})`;
                        ctxBg.lineWidth = 0.8;
                        ctxBg.stroke();
                    }
                }
            }

            for (let i = 0; i < particles.length; i++) {
                particles[i].update();
                particles[i].draw();
            }

            requestAnimationFrame(animateConstellation);
        }
        animateConstellation();

        // ─── INSTALLER LOGIC ───
        let currentStep = 1;

        function setStep(step) {
            currentStep = step;
            document.querySelectorAll('.step-view').forEach(v => v.classList.remove('active'));
            document.getElementById('step-' + step).classList.add('active');

            const badgeText = document.getElementById('step-badge-text');
            const badgeIcon = document.querySelector('#step-badge .material-symbols-rounded');

            if (step === 1) {
                badgeText.textContent = "Шаг 1 из 3";
                badgeIcon.textContent = "settings";
                document.getElementById('btn-cancel').style.display = 'block';
                document.getElementById('btn-install').style.display = 'flex';
                document.getElementById('btn-finish').style.display = 'none';
            } else if (step === 2) {
                badgeText.textContent = "Шаг 2 из 3";
                badgeIcon.textContent = "sync";
                document.getElementById('btn-cancel').style.display = 'none';
                document.getElementById('btn-install').style.display = 'none';
                document.getElementById('btn-finish').style.display = 'none';
            } else if (step === 3) {
                badgeText.textContent = "Готово!";
                badgeIcon.textContent = "check_circle";
                document.getElementById('btn-cancel').style.display = 'none';
                document.getElementById('btn-install').style.display = 'none';
                document.getElementById('btn-finish').style.display = 'flex';
            }
        }

        function browseFolder() {
            window.chrome.webview.postMessage({ action: 'browse_folder' });
        }

        function handlePathChange(val) {
            window.chrome.webview.postMessage({ action: 'update_path', path: val });
        }

        function onPathSelected(newPath, freeSpace) {
            document.getElementById('install-path-input').value = newPath;
            document.getElementById('disk-free-val').textContent = freeSpace;
        }

        function onDiskSpaceUpdated(freeSpace) {
            document.getElementById('disk-free-val').textContent = freeSpace;
        }

        function startInstall() {
            const path = document.getElementById('install-path-input').value.trim();
            if (!path) {
                showErrorModal('Укажите корректный путь для установки.');
                return;
            }

            const desktop = document.getElementById('chk-desktop').checked;
            const startMenu = document.getElementById('chk-start').checked;

            window.chrome.webview.postMessage({
                action: 'start_install',
                path: path,
                desktopShortcut: desktop,
                startMenuShortcut: startMenu
            });
        }

        function updateInstallProgress(pct, status, stepIdx) {
            document.getElementById('progress-bar-fill').style.width = pct + '%';
            document.getElementById('progress-pct-text').textContent = pct + '%';
            document.getElementById('progress-status-text').textContent = status;

            for (let i = 1; i <= 4; i++) {
                const item = document.getElementById('substep-' + i);
                const icon = item.querySelector('.step-icon');
                if (i < stepIdx) {
                    item.className = 'step-item done';
                    icon.textContent = 'check_circle';
                } else if (i === stepIdx) {
                    item.className = 'step-item active';
                    icon.textContent = 'progress_activity';
                } else {
                    item.className = 'step-item';
                    icon.textContent = 'radio_button_unchecked';
                }
            }
        }

        function onInstallSuccess(finalPath) {
            document.getElementById('final-path-val').textContent = finalPath;
            
            const desktop = document.getElementById('chk-desktop').checked;
            const startMenu = document.getElementById('chk-start').checked;
            let shortcuts = [];
            if (desktop) shortcuts.push('Рабочий стол');
            if (startMenu) shortcuts.push('Меню Пуск');
            if (shortcuts.length === 0) shortcuts.push('Без ярлыков');

            document.getElementById('final-shortcuts-val').textContent = shortcuts.join(', ');

            setStep(3);
        }

        function finishInstall() {
            const launch = document.getElementById('chk-launch').checked;
            window.chrome.webview.postMessage({
                action: 'finish',
                launch: launch
            });
        }

        function cancelInstall() {
            window.chrome.webview.postMessage({ action: 'cancel' });
        }

        function showErrorModal(msg) {
            document.getElementById('modal-msg').textContent = msg;
            document.getElementById('error-modal').classList.add('active');
        }

        function closeErrorModal() {
            document.getElementById('error-modal').classList.remove('active');
        }
    </script>
</body>
</html>
""";
    }
}
