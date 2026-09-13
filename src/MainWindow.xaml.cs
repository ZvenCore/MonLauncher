using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MonLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherService _service;
    private AppConfig _config;

    public MainWindow()
    {
        InitializeComponent();
        _service = new LauncherService();
        _config = _service.LoadConfig();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(_service.BaseDir, ".webview_cache");
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
            MessageBox.Show($"Ошибка инициализации WebView2: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
                case "play":
                    await HandlePlayAsync();
                    break;

                case "save_settings":
                    if (root.TryGetProperty("username", out var uProp))
                    {
                        var u = uProp.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(u)) _config.Username = u;
                    }
                    if (root.TryGetProperty("ram", out var rProp) && rProp.TryGetInt32(out int ramMb))
                    {
                        _config.RamMb = ramMb;
                    }
                    if (root.TryGetProperty("quickskin", out var qProp))
                    {
                        _config.QuickSkinEnabled = qProp.GetBoolean();
                    }
                    if (root.TryGetProperty("skinBase64", out var sProp))
                    {
                        _config.CustomSkinBase64 = sProp.GetString();
                    }

                    _service.SaveConfig(_config);

                    bool silent = false;
                    if (root.TryGetProperty("silent", out var silentProp))
                    {
                        silent = silentProp.GetBoolean();
                    }
                    if (!silent)
                    {
                        await webView.ExecuteScriptAsync("showToast('Настройки сохранены!');");
                    }
                    break;

                case "get_user_mods":
                    await SendUserModsListAsync();
                    break;

                case "install_mod":
                    if (root.TryGetProperty("fileName", out var fProp) && root.TryGetProperty("base64Data", out var bProp))
                    {
                        var fileName = fProp.GetString() ?? "";
                        var base64 = bProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(base64))
                        {
                            var commaIdx = base64.IndexOf(',');
                            if (commaIdx >= 0) base64 = base64.Substring(commaIdx + 1);
                            var bytes = Convert.FromBase64String(base64);
                            _service.SaveUserMod(fileName, bytes);
                            await SendUserModsListAsync();
                            await webView.ExecuteScriptAsync($"showToast('Мод {EscapeJs(fileName)} добавлен!');");
                        }
                    }
                    break;

                case "delete_mod":
                    if (root.TryGetProperty("fileName", out var delProp))
                    {
                        var fileName = delProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            _service.DeleteUserMod(fileName);
                            await SendUserModsListAsync();
                            await webView.ExecuteScriptAsync("showToast('Мод удален');");
                        }
                    }
                    break;

                case "open_folder":
                    if (Directory.Exists(_service.UserModsDir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{_service.UserModsDir}\"",
                            UseShellExecute = true
                        });
                    }
                    break;

                case "open_link":
                    if (root.TryGetProperty("url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                        }
                    }
                    break;

                case "window_minimize":
                    WindowState = WindowState.Minimized;
                    break;

                case "window_close":
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            await webView.ExecuteScriptAsync($"alert('Ошибка: {EscapeJs(ex.Message)}');");
        }
    }

    private async Task SendUserModsListAsync()
    {
        var mods = _service.GetUserMods();
        var json = JsonSerializer.Serialize(mods);
        await webView.ExecuteScriptAsync($"renderUserModsList({json});");
    }

    private async Task HandlePlayAsync()
    {
        var username = string.IsNullOrWhiteSpace(_config.Username) ? "Player_1" : _config.Username.Trim();
        if (username.Equals("Player", StringComparison.OrdinalIgnoreCase) || 
            username.Equals("Player_1", StringComparison.OrdinalIgnoreCase) || 
            string.IsNullOrWhiteSpace(username))
        {
            await webView.ExecuteScriptAsync("openNickModal();");
            return;
        }

        await webView.ExecuteScriptAsync("setLaunchingState(true, 'Подготовка к запуску...');");

        var success = await Task.Run(async () =>
        {
            return await _service.LaunchGameAsync((msg, pct) =>
            {
                Dispatcher.Invoke(() =>
                {
                    webView.ExecuteScriptAsync($"setLaunchProgress('{EscapeJs(msg)}');");
                });
            });
        });

        if (success)
        {
            await webView.ExecuteScriptAsync("setLaunchProgress('Minecraft запущен! Закрытие лаунчера...');");
            await Task.Delay(1400);
            Close();
        }
        else
        {
            await webView.ExecuteScriptAsync("setLaunchingState(false, 'ИГРАТЬ');");
        }
    }

    private static string EscapeJs(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
    }

    private static string FormatRam(int ramMb)
    {
        double gb = ramMb / 1024.0;
        return gb == (int)gb ? $"{gb:0} ГБ" : $"{gb:0.0} ГБ";
    }

    private string GenerateHtml()
    {
        var totalRam = _service.GetSystemRamGb();
        var ramMb = _config.RamMb <= 1024 ? 6144 : _config.RamMb;
        var ramStr = FormatRam(ramMb);
        var ramLabel = $"{ramStr} ({ramMb} МБ)";
        var username = string.IsNullOrWhiteSpace(_config.Username) ? "Player_1" : _config.Username;
        var uuidDisplay = LauncherService.GenerateOfflineUuid(username);
        var quickskinEnabled = _config.QuickSkinEnabled ? "true" : "false";
        var customSkin = _config.CustomSkinBase64 != null ? $"'{EscapeJs(_config.CustomSkinBase64)}'" : "null";
        var modsJson = JsonSerializer.Serialize(_service.GetUserMods());

        return $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>MON SERVER Launcher</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700;800&family=Space+Grotesk:wght@600;700;800&family=JetBrains+Mono:wght@500;600;700&family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200&display=swap" rel="stylesheet">

    <style>
        :root {
            /* ─── ЦВЕТОВАЯ ГАММА (MON SERVER THEME) ─── */
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
            font-size: 24px;
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

        #drop-overlay {
            position: fixed;
            inset: 0;
            background: rgba(9, 11, 14, 0.92);
            backdrop-filter: blur(10px);
            z-index: 999;
            display: none;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            pointer-events: none;
        }
        #drop-overlay.active { display: flex; }
        .drop-modal {
            background: #14181f;
            border: 2px dashed var(--accent);
            padding: 40px 50px;
            border-radius: 28px;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 16px;
            text-align: center;
            box-shadow: 0 20px 50px rgba(0,0,0,0.8), 0 0 40px var(--accent-glow);
        }

        /* ─── NICKNAME MODAL POPUP ─── */
        #modal-nick-prompt {
            position: fixed;
            inset: 0;
            background: rgba(7, 9, 13, 0.88);
            backdrop-filter: blur(12px);
            z-index: 10000;
            display: none;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        #modal-nick-prompt.active {
            display: flex;
        }
        .nick-modal-box {
            background: #14181f;
            border: 1px solid var(--accent-border);
            border-radius: 24px;
            padding: 32px 36px;
            width: 440px;
            max-width: 92vw;
            display: flex;
            flex-direction: column;
            gap: 20px;
            box-shadow: 0 24px 60px rgba(0,0,0,0.85), 0 0 35px var(--accent-glow);
            animation: modalFadeIn 0.22s cubic-bezier(0.16, 1, 0.3, 1);
        }
        @keyframes modalFadeIn {
            from { opacity: 0; transform: scale(0.92) translateY(10px); }
            to { opacity: 1; transform: scale(1) translateY(0); }
        }
        .nick-modal-top {
            display: flex;
            align-items: center;
            gap: 14px;
        }
        .nick-modal-icon {
            width: 48px;
            height: 48px;
            border-radius: 14px;
            background: var(--accent-soft);
            border: 1px solid var(--accent-border);
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--accent-light);
            flex-shrink: 0;
        }
        .nick-modal-title {
            font-family: var(--font-brand);
            font-size: 20px;
            font-weight: 800;
            color: #fff;
        }
        .nick-modal-sub {
            font-size: 13px;
            color: var(--text-muted);
            margin-top: 2px;
            line-height: 1.4;
        }
        .nick-modal-input-wrap {
            position: relative;
            display: flex;
            align-items: center;
        }
        .nick-modal-input-wrap .material-symbols-rounded {
            position: absolute;
            left: 14px;
            color: var(--text-dim);
            font-size: 20px;
            pointer-events: none;
        }
        .nick-modal-input {
            width: 100%;
            height: 48px;
            padding: 0 16px 0 44px;
            background: rgba(255, 255, 255, 0.04);
            border: 1px solid var(--border-medium);
            border-radius: 14px;
            color: #fff;
            font-family: var(--font-sans);
            font-size: 15px;
            font-weight: 600;
            outline: none;
            transition: all 0.2s;
        }
        .nick-modal-input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 16px var(--accent-soft);
            background: rgba(229, 147, 56, 0.05);
        }
        .nick-modal-hint {
            font-size: 12px;
            color: var(--text-dim);
            margin-top: -12px;
            font-family: var(--font-mono);
        }
        .nick-modal-btns {
            display: flex;
            gap: 12px;
            margin-top: 4px;
        }
        .nick-modal-btn-save {
            flex: 1;
            height: 46px;
            border-radius: 13px;
            font-size: 14px;
            font-weight: 700;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
            cursor: pointer;
        }
        .nick-modal-btn-cancel {
            height: 46px;
            padding: 0 20px;
            border-radius: 13px;
            font-size: 14px;
            font-weight: 600;
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--border);
            color: var(--text-muted);
            cursor: pointer;
            transition: all 0.15s;
        }
        .nick-modal-btn-cancel:hover {
            background: rgba(255, 255, 255, 0.08);
            color: #fff;
        }

        #launcher-window {
            width: 100vw;
            height: 100vh;
            background: var(--bg);
            display: flex;
            overflow: hidden;
            position: relative;
        }

        #sidebar {
            width: 80px;
            background: var(--bg-subtle);
            border-right: 1px solid var(--border-subtle);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: space-between;
            padding: 20px 10px;
            flex-shrink: 0;
            z-index: 10;
        }
        .brand-logo {
            width: 48px;
            height: 48px;
            border-radius: 16px;
            background: linear-gradient(135deg, var(--accent-copper), var(--accent), var(--accent-light));
            color: #120e09;
            display: flex;
            align-items: center;
            justify-content: center;
            font-family: var(--font-brand);
            font-weight: 900;
            font-size: 24px;
            box-shadow: 0 6px 20px rgba(229, 147, 56, 0.35);
            cursor: pointer;
            transition: transform 0.15s ease;
        }
        .brand-logo:hover { transform: scale(1.05); }

        .nav-list {
            display: flex;
            flex-direction: column;
            gap: 12px;
            width: 100%;
            align-items: center;
        }
        .nav-item {
            width: 50px;
            height: 50px;
            border-radius: 16px;
            background: transparent;
            border: 1px solid transparent;
            color: var(--text-muted);
            display: flex;
            align-items: center;
            justify-content: center;
            cursor: pointer;
            transition: all 0.15s ease;
            position: relative;
        }
        .nav-item:hover { background: var(--surface-hover); color: #fff; }
        .nav-item.active {
            background: var(--accent-soft);
            color: var(--accent);
            border-color: var(--accent-border);
            box-shadow: inset 0 0 14px rgba(229, 147, 56, 0.18);
        }
        .nav-badge {
            position: absolute;
            top: 10px;
            right: 10px;
            width: 8px;
            height: 8px;
            background: var(--accent);
            border-radius: 50%;
        }

        .sidebar-bottom {
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 12px;
        }
        .sidebar-btn {
            width: 44px;
            height: 44px;
            border-radius: 14px;
            background: var(--surface);
            border: 1px solid var(--border);
            color: var(--text-muted);
            display: flex;
            align-items: center;
            justify-content: center;
            cursor: pointer;
            text-decoration: none;
            transition: all 0.15s ease;
        }
        .sidebar-btn:hover { border-color: var(--accent-border); color: var(--accent); }
        .sidebar-avatar-btn {
            width: 46px;
            height: 46px;
            border-radius: 14px;
            overflow: hidden;
            border: 2px solid var(--accent-border);
            background: #000;
            display: flex;
            align-items: center;
            justify-content: center;
            cursor: pointer;
            padding: 0;
            transition: transform 0.15s ease;
        }
        .sidebar-avatar-btn:hover { transform: scale(1.06); border-color: var(--accent); }

        #main-container {
            flex: 1;
            display: flex;
            flex-direction: column;
            overflow: hidden;
            background: transparent;
            position: relative;
            z-index: 5;
        }

        #top-header {
            height: 60px;
            padding: 0 24px;
            border-bottom: 1px solid var(--border-subtle);
            background: rgba(15, 19, 24, 0.85);
            backdrop-filter: blur(8px);
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-shrink: 0;
        }
        .header-title-box { display: flex; align-items: center; gap: 12px; }
        .header-title { font-family: var(--font-brand); font-size: 18px; font-weight: 800; letter-spacing: 0.5px; }
        .header-tag {
            font-family: var(--font-mono);
            font-size: 11px;
            color: var(--accent);
            background: var(--accent-soft);
            border: 1px solid var(--accent-border);
            padding: 3px 10px;
            border-radius: 999px;
            font-weight: 600;
        }

        .header-actions { display: flex; align-items: center; gap: 12px; }
        .server-pill {
            display: flex;
            align-items: center;
            gap: 8px;
            background: var(--surface);
            border: 1px solid var(--border);
            padding: 6px 14px;
            border-radius: 12px;
            font-family: var(--font-mono);
            font-size: 12px;
            color: var(--text-muted);
            cursor: pointer;
            transition: all 0.15s ease;
        }
        .server-pill:hover { background: var(--surface-hover); border-color: var(--accent-border); }
        .site-link-btn {
            display: flex;
            align-items: center;
            gap: 6px;
            background: var(--surface);
            border: 1px solid var(--border);
            padding: 6px 12px;
            border-radius: 12px;
            font-family: var(--font-mono);
            font-size: 12px;
            color: var(--text-muted);
            text-decoration: none;
            cursor: pointer;
            transition: all 0.15s ease;
        }
        .site-link-btn:hover { background: var(--surface-hover); color: var(--accent-light); }
        .window-dots {
            display: flex;
            gap: 6px;
            margin-left: 6px;
            padding-left: 12px;
            border-left: 1px solid var(--border-subtle);
        }
        .dot { width: 11px; height: 11px; border-radius: 50%; background: #334155; cursor: pointer; }
        .dot-close:hover { background: #e11d48; }

        .tab-view {
            display: none;
            flex: 1;
            flex-direction: column;
            padding: 24px 28px;
            overflow-y: auto;
        }
        .tab-view.active { display: flex; }

        .hero-grid {
            display: grid;
            grid-template-columns: 1.8fr 1fr;
            gap: 20px;
            margin: auto 0;
        }
        .clock-card {
            background: rgba(20, 24, 31, 0.85);
            backdrop-filter: blur(8px);
            border: 1px solid var(--border);
            border-radius: 24px;
            padding: 30px;
            position: relative;
            overflow: hidden;
            display: flex;
            flex-direction: column;
            justify-content: center;
            box-shadow: 0 15px 35px rgba(0,0,0,0.5);
        }
        .clock-card::before {
            content: '';
            position: absolute;
            top: -60px;
            right: -60px;
            width: 240px;
            height: 240px;
            background: radial-gradient(circle, rgba(229, 147, 56, 0.18) 0%, transparent 70%);
            pointer-events: none;
        }
        .date-chip {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            background: rgba(229, 147, 56, 0.15);
            border: 1px solid var(--accent-border);
            padding: 4px 12px;
            border-radius: 999px;
            font-family: var(--font-mono);
            font-size: 11px;
            font-weight: 700;
            color: var(--accent-light);
            margin-bottom: 12px;
            align-self: flex-start;
        }
        .digital-clock {
            font-family: var(--font-mono);
            font-size: 68px;
            font-weight: 800;
            letter-spacing: -2px;
            color: #ffffff;
            line-height: 1;
            text-shadow: 0 4px 24px rgba(0,0,0,0.8);
        }
        .motd-row {
            margin-top: 18px;
            padding-top: 14px;
            border-top: 1px solid var(--border-subtle);
            display: flex;
            align-items: center;
            gap: 8px;
            font-family: var(--font-mono);
            font-size: 12px;
            color: var(--text-muted);
        }
        .motd-text { color: var(--accent-light); font-style: italic; font-weight: 600; }

        .metrics-column { display: flex; flex-direction: column; gap: 16px; }
        .metric-card {
            background: rgba(20, 24, 31, 0.85);
            backdrop-filter: blur(8px);
            border: 1px solid var(--border);
            border-radius: 20px;
            padding: 20px;
            flex: 1;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
        }
        .metric-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-family: var(--font-mono);
            font-size: 11px;
            color: var(--text-dim);
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }
        .metric-val {
            font-family: var(--font-brand);
            font-size: 26px;
            font-weight: 800;
            color: var(--accent);
            display: flex;
            align-items: baseline;
            gap: 8px;
            margin-top: 4px;
        }
        .metric-val span {
            font-family: var(--font-mono);
            font-size: 12px;
            color: var(--text-dim);
            font-weight: normal;
        }
        .metric-footer {
            border-top: 1px solid var(--border-subtle);
            padding-top: 10px;
            font-family: var(--font-mono);
            font-size: 12px;
            color: var(--text-muted);
            display: flex;
            justify-content: space-between;
        }
        .refresh-btn {
            background: transparent;
            border: none;
            color: var(--text-dim);
            cursor: pointer;
            transition: color 0.15s;
        }
        .refresh-btn:hover { color: var(--accent); }

        .bottom-bar {
            background: rgba(15, 19, 24, 0.95);
            backdrop-filter: blur(10px);
            border: 1px solid var(--border);
            border-radius: 20px;
            padding: 14px 22px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            box-shadow: 0 10px 30px rgba(0,0,0,0.5);
        }
        .profile-summary {
            display: flex;
            align-items: center;
            gap: 16px;
            cursor: pointer;
        }
        .profile-avatar-box {
            width: 48px;
            height: 48px;
            border-radius: 14px;
            background: #000;
            border: 1.5px solid var(--accent-border);
            display: flex;
            align-items: center;
            justify-content: center;
            overflow: hidden;
            box-shadow: 0 4px 10px rgba(0,0,0,0.4);
        }
        .profile-info { display: flex; flex-direction: column; gap: 4px; }
        .profile-nick-row { display: flex; align-items: center; gap: 8px; }
        .profile-nick { font-weight: 700; font-size: 15px; color: #fff; }
        .profile-badge {
            font-family: var(--font-mono);
            font-size: 10px;
            background: var(--accent-soft);
            border: 1px solid var(--accent-border);
            color: var(--accent-light);
            padding: 1px 7px;
            border-radius: 6px;
            font-weight: 700;
        }
        .profile-sub { font-family: var(--font-mono); font-size: 12px; color: var(--text-dim); }

        .launch-btn {
            width: 260px;
            height: 52px;
            background: linear-gradient(135deg, var(--accent), var(--accent-copper));
            border: none;
            border-radius: 16px;
            color: #120e09;
            font-family: var(--font-brand);
            font-size: 17px;
            font-weight: 900;
            letter-spacing: 0.5px;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
            cursor: pointer;
            box-shadow: 0 6px 24px rgba(229, 147, 56, 0.35);
            transition: all 0.15s ease;
        }
        .launch-btn:hover {
            transform: scale(1.02);
            box-shadow: 0 8px 30px rgba(229, 147, 56, 0.5);
            background: linear-gradient(135deg, var(--accent-light), var(--accent));
        }
        .launch-btn:active { transform: scale(0.98); }
        .launch-btn:disabled { opacity: 0.7; cursor: not-allowed; transform: none; }

        .mods-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding-bottom: 16px;
            border-bottom: 1px solid var(--border-subtle);
            margin-bottom: 18px;
        }
        .view-title {
            font-family: var(--font-brand);
            font-size: 24px;
            font-weight: 800;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .view-subtitle { font-size: 13px; color: var(--text-dim); margin-top: 4px; }
        .btn-group { display: flex; gap: 10px; }
        .btn-primary {
            background: var(--accent);
            color: #120e09;
            border: none;
            padding: 10px 18px;
            border-radius: 12px;
            font-weight: 700;
            font-size: 13px;
            display: flex;
            align-items: center;
            gap: 6px;
            cursor: pointer;
            box-shadow: 0 4px 14px rgba(229, 147, 56, 0.25);
            transition: all 0.15s;
        }
        .btn-primary:hover {
            background: var(--accent-light);
            transform: scale(1.02);
        }
        .btn-secondary {
            background: var(--surface);
            border: 1px solid var(--border);
            color: #fff;
            padding: 10px 18px;
            border-radius: 12px;
            font-weight: 600;
            font-size: 13px;
            display: flex;
            align-items: center;
            gap: 6px;
            cursor: pointer;
            transition: all 0.15s;
        }
        .btn-secondary:hover { border-color: var(--accent-border); background: var(--surface-hover); }

        .dropzone-box {
            border: 2px dashed var(--border);
            border-radius: 20px;
            padding: 24px;
            background: rgba(20, 24, 31, 0.7);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 8px;
            cursor: pointer;
            transition: all 0.15s ease;
            margin-bottom: 20px;
            text-align: center;
        }
        .dropzone-box:hover {
            border-color: var(--accent);
            background: var(--surface-hover);
        }
        .dropzone-title { font-weight: 700; font-size: 14px; color: #fff; }
        .dropzone-sub { font-size: 12px; color: var(--text-dim); }

        .table-card {
            background: rgba(20, 24, 31, 0.85);
            backdrop-filter: blur(8px);
            border: 1px solid var(--border);
            border-radius: 18px;
            overflow: hidden;
        }
        .table-head {
            background: #0d1117;
            padding: 12px 18px;
            border-bottom: 1px solid var(--border-subtle);
            font-family: var(--font-mono);
            font-size: 11px;
            font-weight: 700;
            color: var(--text-dim);
            display: flex;
            justify-content: space-between;
        }
        .table-row {
            padding: 14px 18px;
            border-bottom: 1px solid var(--border-subtle);
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-family: var(--font-mono);
            font-size: 13px;
            transition: background 0.15s;
        }
        .table-row:last-child { border-bottom: none; }
        .table-row:hover { background: var(--surface-hover); }
        .mod-meta { display: flex; align-items: center; gap: 12px; }
        .mod-name { font-weight: 700; color: #fff; display: block; }
        .mod-size { font-size: 11px; color: var(--text-dim); margin-top: 2px; }
        .btn-icon-del {
            background: transparent;
            border: none;
            color: var(--text-dim);
            cursor: pointer;
            padding: 6px;
            border-radius: 8px;
            transition: all 0.15s;
        }
        .btn-icon-del:hover { color: var(--bad); background: rgba(226, 92, 74, 0.1); }

        .settings-grid {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 20px;
            margin-top: 16px;
        }
        .settings-card {
            background: rgba(20, 24, 31, 0.85);
            backdrop-filter: blur(8px);
            border: 1px solid var(--border);
            border-radius: 22px;
            padding: 24px;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
        }
        .settings-section-title {
            font-family: var(--font-mono);
            font-size: 11px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            color: var(--text-dim);
            margin-bottom: 16px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .avatar-nick-row {
            display: flex;
            align-items: center;
            gap: 16px;
            margin-bottom: 20px;
        }
        .settings-avatar-wrap {
            position: relative;
            background: #000;
            border: 2px solid var(--accent-border);
            border-radius: 18px;
            padding: 4px;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .settings-avatar-badge {
            position: absolute;
            bottom: -4px;
            right: -4px;
            width: 20px;
            height: 20px;
            background: var(--accent);
            color: #000;
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 12px;
            font-weight: bold;
        }

        .form-group { flex: 1; }
        .form-label { font-size: 12px; color: var(--text-dim); margin-bottom: 6px; display: block; }
        .form-input {
            width: 100%;
            background: var(--surface);
            border: 1px solid var(--border);
            padding: 10px 14px;
            border-radius: 12px;
            font-family: var(--font-mono);
            font-size: 14px;
            color: #fff;
            outline: none;
            transition: border 0.15s;
        }
        .form-input:focus { border-color: var(--accent); }

        .quickskin-box {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 16px;
            margin-bottom: 16px;
        }
        .quickskin-top {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 8px;
        }
        .switch-title {
            font-size: 13px;
            font-weight: 700;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .switch {
            position: relative;
            display: inline-block;
            width: 42px;
            height: 24px;
        }
        .switch input { opacity: 0; width: 0; height: 0; }
        .slider {
            position: absolute;
            cursor: pointer;
            inset: 0;
            background-color: #334155;
            transition: .2s;
            border-radius: 24px;
        }
        .slider:before {
            position: absolute;
            content: "";
            height: 18px;
            width: 18px;
            left: 3px;
            bottom: 3px;
            background-color: white;
            transition: .2s;
            border-radius: 50%;
        }
        input:checked + .slider { background-color: var(--accent); }
        input:checked + .slider:before { transform: translateX(18px); }

        .quickskin-desc {
            font-size: 12px;
            color: var(--text-dim);
            line-height: 1.4;
            margin-bottom: 12px;
        }

        .skin-btn-row { display: flex; gap: 8px; }
        .skin-file-btn {
            flex: 1;
            padding: 8px 12px;
            background: var(--surface-hover);
            border: 1px solid var(--border);
            border-radius: 10px;
            color: var(--accent-light);
            font-family: var(--font-mono);
            font-size: 12px;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
            cursor: pointer;
            transition: all 0.15s;
        }
        .skin-file-btn:hover { border-color: var(--accent-border); background: var(--surface); }
        .skin-reset-btn {
            padding: 8px 12px;
            background: var(--surface-hover);
            border: 1px solid var(--border);
            border-radius: 10px;
            color: var(--text-muted);
            font-family: var(--font-mono);
            font-size: 12px;
            cursor: pointer;
            transition: all 0.15s;
        }
        .skin-reset-btn:hover { color: #fff; border-color: var(--accent-border); }

        .uuid-box {
            background: var(--surface);
            border: 1px solid var(--border-subtle);
            border-radius: 12px;
            padding: 10px 14px;
            font-family: var(--font-mono);
            font-size: 11px;
            color: var(--text-dim);
        }
        .uuid-val { color: var(--accent-light); word-break: break-all; margin-top: 2px; display: block; }

        .ram-label-row {
            display: flex;
            justify-content: space-between;
            align-items: baseline;
            margin-bottom: 8px;
            font-size: 13px;
        }
        .ram-val-display {
            font-family: var(--font-mono);
            font-weight: 800;
            font-size: 16px;
            color: var(--accent);
        }
        .range-slider {
            -webkit-appearance: none;
            width: 100%;
            height: 6px;
            border-radius: 4px;
            background: #1e2530;
            outline: none;
            cursor: pointer;
            margin: 12px 0 16px 0;
        }
        .range-slider::-webkit-slider-thumb {
            -webkit-appearance: none;
            appearance: none;
            width: 18px;
            height: 18px;
            border-radius: 50%;
            background: var(--accent);
            cursor: pointer;
            box-shadow: 0 0 12px var(--accent-glow);
        }
        .chip-grid {
            display: grid;
            grid-template-columns: repeat(4, 1fr);
            gap: 8px;
            margin-bottom: 16px;
        }
        .chip-btn {
            padding: 8px;
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 10px;
            color: var(--text-muted);
            font-family: var(--font-mono);
            font-size: 12px;
            cursor: pointer;
            transition: all 0.15s;
        }
        .chip-btn:hover { color: #fff; border-color: var(--accent-border); }
        .chip-btn.active {
            background: var(--accent-soft);
            border-color: var(--accent);
            color: var(--accent-light);
            font-weight: 700;
        }
        .ram-note {
            background: var(--surface);
            border: 1px solid var(--border-subtle);
            border-radius: 14px;
            padding: 12px;
            font-size: 12px;
            color: var(--text-dim);
            line-height: 1.4;
            display: flex;
            gap: 10px;
        }

        .pulse-dot {
            width: 8px;
            height: 8px;
            background-color: var(--ok);
            border-radius: 50%;
            box-shadow: 0 0 0 0 rgba(127, 209, 121, 0.7);
            animation: beacon 2s infinite;
        }
        @keyframes beacon {
            0% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(127, 209, 121, 0.7); }
            70% { transform: scale(1); box-shadow: 0 0 0 6px rgba(127, 209, 121, 0); }
            100% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(127, 209, 121, 0); }
        }

        .pixelated {
            image-rendering: pixelated;
            image-rendering: -moz-crisp-edges;
            image-rendering: crisp-edges;
        }

        #toast {
            position: fixed;
            bottom: 24px;
            right: 24px;
            background: var(--accent);
            color: #120e09;
            padding: 12px 20px;
            border-radius: 14px;
            font-weight: 800;
            font-size: 13px;
            box-shadow: 0 12px 30px rgba(0,0,0,0.6);
            display: flex;
            align-items: center;
            gap: 8px;
            transform: translateY(100px);
            opacity: 0;
            transition: all 0.25s cubic-bezier(0.175, 0.885, 0.32, 1.275);
            z-index: 1000;
            pointer-events: none;
        }
        #toast.show { transform: translateY(0); opacity: 1; }
    </style>
</head>
<body>

    <div id="drop-overlay">
        <div class="drop-modal">
            <span class="material-symbols-rounded" style="font-size: 64px; color: var(--accent);">upload_file</span>
            <h3 style="font-family: var(--font-brand); font-size: 24px; font-weight: 800; color: #fff;">Перетащите файлы сюда</h3>
            <p style="font-size: 13px; color: #cbd5e1; line-height: 1.5;">
                • Файлы <strong style="color: var(--accent);">.jar</strong> добавятся в <code>user-mods</code><br>
                • Файлы <strong style="color: var(--accent);">.png</strong> установятся как скин <strong>QuickSkin</strong>
            </p>
        </div>
    </div>

    <div id="launcher-window">
        
        <canvas id="bgNetwork"></canvas>

        <!-- SIDEBAR -->
        <aside id="sidebar">
            <div style="display: flex; flex-direction: column; align-items: center; gap: 20px; width: 100%;">
                <div class="brand-logo" onclick="switchTab('main')" title="Главная">M</div>

                <nav class="nav-list">
                    <button id="nav-main" class="nav-item active" onclick="switchTab('main')" title="Главная">
                        <span class="material-symbols-rounded material-symbols-fill">sports_esports</span>
                    </button>
                    <button id="nav-mods" class="nav-item" onclick="switchTab('mods')" title="Пользовательские моды">
                        <span class="material-symbols-rounded">extension</span>
                        <span class="nav-badge"></span>
                    </button>
                    <button id="nav-settings" class="nav-item" onclick="switchTab('settings')" title="Настройки">
                        <span class="material-symbols-rounded">settings</span>
                    </button>
                </nav>
            </div>

            <div class="sidebar-bottom">
                <button class="sidebar-btn" onclick="openLink('https://site.moncraft.space')" title="Официальный сайт проекта">
                    <span class="material-symbols-rounded" style="font-size: 20px;">language</span>
                </button>
                <button onclick="switchTab('settings')" class="sidebar-avatar-btn" title="Настройки профиля">
                    <canvas id="sidebar-skin-canvas" width="32" height="32" class="pixelated" style="width: 32px; height: 32px;"></canvas>
                </button>
            </div>
        </aside>

        <!-- MAIN CONTAINER -->
        <div id="main-container">
            
            <!-- HEADER -->
            <header id="top-header">
                <div class="header-title-box">
                    <span class="header-title">MON <span style="color: var(--accent);">SERVER</span></span>
                    <span class="header-tag">NeoForge 21.1.249</span>
                </div>

                <div class="header-actions">
                    <div class="server-pill" onclick="copyIp()" title="Нажмите, чтобы скопировать IP">
                        <span id="nav-server-dot" class="pulse-dot"></span>
                        <strong style="color: var(--accent-light);">moncraft.space</strong>
                        <span style="color: var(--text-dim);">|</span>
                        <span id="nav-server-players">Онлайн: ...</span>
                        <span id="nav-server-ping" style="color: var(--accent); font-weight: 700;">.. ms</span>
                    </div>

                    <button class="site-link-btn" onclick="openLink('https://site.moncraft.space')" title="Открыть сайт">
                        <span>site.moncraft.space</span>
                        <span class="material-symbols-rounded" style="font-size: 15px;">open_in_new</span>
                    </button>
                </div>
            </header>

            <!-- TAB 1: ГЛАВНАЯ -->
            <main id="view-main" class="tab-view active">
                <div class="hero-grid">
                    <div class="clock-card">
                        <div class="date-chip">
                            <span class="material-symbols-rounded" style="font-size: 14px;">calendar_today</span>
                            <span id="live-date">СУББОТА, 12 СЕНТЯБРЯ 2026</span>
                            <span style="color: var(--accent-light); opacity: 0.8; margin-left: 4px;">MSK (UTC+3)</span>
                        </div>
                        <div class="digital-clock" id="live-clock">15:45:00</div>
                        <div class="motd-row">
                            <span class="material-symbols-rounded" style="font-size: 16px; color: var(--accent);">campaign</span>
                            <span>MOTD:</span>
                            <span id="server-motd" class="motd-text">«Открытие урааа»</span>
                        </div>
                    </div>

                    <div class="metrics-column">
                        <div class="metric-card">
                            <div>
                                <div class="metric-header">
                                    <span>Сервер moncraft.space</span>
                                    <button class="refresh-btn" onclick="pingServer()" title="Обновить пинг">
                                        <span class="material-symbols-rounded" id="refresh-icon" style="font-size: 18px;">sync</span>
                                    </button>
                                </div>
                                <div class="metric-val" id="hero-status-text">
                                    ОНЛАЙН <span id="hero-ping-text">-- ms</span>
                                </div>
                            </div>
                            <div class="metric-footer">
                                <span>Игроки онлайн:</span>
                                <strong id="hero-players-text" style="color: #fff;">Загрузка...</strong>
                            </div>
                        </div>

                        <div class="metric-card">
                            <div>
                                <div class="metric-header">
                                    <span>Сообщество & Сайт</span>
                                </div>
                                <div style="margin-top: 6px;">
                                    <button onclick="openLink('https://site.moncraft.space')" style="background: none; border: none; color: #fff; font-weight: 700; font-size: 14px; display: flex; align-items: center; gap: 6px; cursor: pointer; padding: 0;">
                                        <span>site.moncraft.space</span>
                                        <span class="material-symbols-rounded" style="font-size: 15px; color: var(--accent);">open_in_new</span>
                                    </button>
                                </div>
                            </div>
                            <div class="metric-footer">
                                <span>Discord:</span>
                                <button onclick="openLink('https://discord.gg/E2Zv9pEwYG')" style="background: none; border: none; color: var(--accent-light); font-weight: 700; cursor: pointer; padding: 0;">Вступить</button>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="bottom-bar">
                    <div class="profile-summary" onclick="switchTab('settings')" title="Перейти в настройки">
                        <div class="profile-avatar-box">
                            <canvas id="main-skin-canvas" width="40" height="40" class="pixelated" style="width: 36px; height: 36px;"></canvas>
                        </div>
                        <div class="profile-info">
                            <div class="profile-nick-row">
                                <span class="profile-nick" id="main-nick-display">{{username}}</span>
                                <span class="profile-badge" id="main-skin-badge">QUICKSKIN</span>
                            </div>
                            <div class="profile-sub">
                                ОЗУ: <strong id="main-ram-display" style="color: var(--accent-light);">{{ramStr}}</strong> • 101 мод • NeoForge 21.1.249
                            </div>
                        </div>
                    </div>

                    <button class="launch-btn" id="main-launch-btn" onclick="triggerPlay()">
                        <span class="material-symbols-rounded material-symbols-fill" style="font-size: 24px;">play_arrow</span>
                        <span>ИГРАТЬ</span>
                    </button>
                </div>
            </main>

            <!-- TAB 2: ПОЛЬЗОВАТЕЛЬСКИЕ МОДЫ -->
            <section id="view-mods" class="tab-view">
                <div class="mods-header">
                    <div>
                        <h2 class="view-title">
                            <span class="material-symbols-rounded" style="font-size: 28px; color: var(--accent);">extension</span>
                            <span>Пользовательские моды</span>
                            <span class="header-tag" style="font-size: 11px;">user-mods</span>
                        </h2>
                        <p class="view-subtitle">Перетащите файлы .jar прямо в это окно или выберите через кнопку.</p>
                    </div>
                    <div class="btn-group">
                        <label class="btn-primary">
                            <span class="material-symbols-rounded" style="font-size: 18px;">add</span>
                            <span>Добавить .jar</span>
                            <input type="file" multiple accept=".jar" onchange="handleModFileInput(event)" style="display: none;">
                        </label>
                        <button class="btn-secondary" onclick="openUserModsFolder()">
                            <span class="material-symbols-rounded" style="font-size: 18px;">folder_open</span>
                            <span>Открыть папку</span>
                        </button>
                    </div>
                </div>

                <div class="dropzone-box" onclick="document.querySelector('#view-mods input[type=file]').click()">
                    <span class="material-symbols-rounded" style="font-size: 40px; color: var(--accent);">upload_file</span>
                    <span class="dropzone-title">Перетащите файлы .jar прямо в окно лаунчера</span>
                    <span class="dropzone-sub">Лаунчер автоматически свяжет их с игрой без дублирования места на диске</span>
                </div>

                <div class="table-card">
                    <div class="table-head">
                        <span>ФАЙЛ ПОЛЬЗОВАТЕЛЬСКОГО МОДА</span>
                        <span>ДЕЙСТВИЕ</span>
                    </div>
                    <div id="user-mods-list"></div>
                </div>
            </section>

            <!-- TAB 3: НАСТРОЙКИ -->
            <section id="view-settings" class="tab-view">
                <div class="mods-header">
                    <div>
                        <h2 class="view-title">
                            <span class="material-symbols-rounded" style="font-size: 28px; color: var(--accent);">settings</span>
                            <span>Параметры игрока и лаунчера</span>
                        </h2>
                        <p class="view-subtitle">Настройка никнейма, скина (QuickSkin или по нику) и выделения оперативной памяти.</p>
                    </div>
                </div>

                <div class="settings-grid">
                    <div class="settings-card">
                        <div>
                            <div class="settings-section-title">
                                <span>Профиль & Скин</span>
                                <span class="profile-badge" id="skin-status-pill">QUICKSKIN АКТИВЕН</span>
                            </div>

                            <div class="avatar-nick-row">
                                <div class="settings-avatar-wrap">
                                    <canvas id="settings-skin-canvas" width="64" height="64" class="pixelated" style="width: 60px; height: 60px; border-radius: 12px;"></canvas>
                                    <div class="settings-avatar-badge" title="QuickSkin активен">✓</div>
                                </div>
                                <div class="form-group">
                                    <label class="form-label">Никнейм в игре</label>
                                    <input type="text" id="settings-nick-input" class="form-input" value="{{username}}" oninput="handleNickChange(this.value)" onblur="saveProfileSettings(true)">
                                </div>
                            </div>

                            <div class="quickskin-box">
                                <div class="quickskin-top">
                                    <span class="switch-title">
                                        <span class="material-symbols-rounded" style="color: var(--accent); font-size: 20px;">face</span>
                                        <span>Режим QuickSkin</span>
                                    </span>
                                    <label class="switch">
                                        <input type="checkbox" id="quickskin-toggle" checked onchange="toggleQuickSkin(this.checked)">
                                        <span class="slider"></span>
                                    </label>
                                </div>
                                <p class="quickskin-desc">
                                    Определяет скин напрямую из текстуры мода <b>QuickSkin</b> (не зависит от никнейма и базы Mojang).
                                </p>
                                <div class="skin-btn-row">
                                    <label class="skin-file-btn">
                                        <span class="material-symbols-rounded" style="font-size: 16px;">upload</span>
                                        <span>Выбрать файл (.png)</span>
                                        <input type="file" accept=".png" onchange="handleSkinUpload(event)" style="display: none;">
                                    </label>
                                    <button class="skin-reset-btn" onclick="resetToSampleSkin()">Сброс</button>
                                </div>
                            </div>

                            <div class="uuid-box">
                                <span>Офлайн UUID игрока</span>
                                <span class="uuid-val" id="settings-uuid">{{uuidDisplay}}</span>
                            </div>
                        </div>

                        <button class="btn-primary" style="margin-top: 16px; justify-content: center;" onclick="saveProfileSettings()">
                            <span class="material-symbols-rounded" style="font-size: 18px;">check</span>
                            <span>Сохранить настройки</span>
                        </button>
                    </div>

                    <div class="settings-card">
                        <div>
                            <div class="settings-section-title">
                                <span>Выделение оперативной памяти (ОЗУ)</span>
                            </div>

                            <div class="ram-label-row">
                                <span style="color: var(--text-dim);">Объём памяти</span>
                                <span class="ram-val-display" id="settings-ram-label">{{ramLabel}}</span>
                            </div>

                            <input type="range" min="2048" max="16384" step="1024" value="{{ramMb}}" class="range-slider" oninput="updateRamSlider(this.value)" onchange="saveProfileSettings(true)">

                            <div class="chip-grid">
                                <button class="chip-btn" id="ram-btn-4" onclick="setRamVal(4096)">4G</button>
                                <button class="chip-btn active" id="ram-btn-6" onclick="setRamVal(6144)">6G</button>
                                <button class="chip-btn" id="ram-btn-8" onclick="setRamVal(8192)">8G</button>
                                <button class="chip-btn" id="ram-btn-12" onclick="setRamVal(12288)">12G</button>
                            </div>

                            <div class="ram-note">
                                <span class="material-symbols-rounded" style="color: var(--accent); font-size: 20px;">memory</span>
                                <span>желательно выделить 4 гб озу</span>
                            </div>
                        </div>

                        <div style="border-top: 1px solid var(--border-subtle); padding-top: 14px; display: flex; justify-content: space-between; font-family: var(--font-mono); font-size: 12px; color: var(--text-dim);">
                            <span>Общая память ПК:</span>
                            <strong style="color: #fff;">{{totalRam}} ГБ</strong>
                        </div>
                    </div>
                </div>
            </section>

        </div>
    <!-- MODAL: ВВЕДИТЕ НИКНЕЙМ -->
    <div id="modal-nick-prompt">
        <div class="nick-modal-box">
            <div class="nick-modal-top">
                <div class="nick-modal-icon">
                    <span class="material-symbols-rounded">badge</span>
                </div>
                <div>
                    <div class="nick-modal-title">Введите никнейм</div>
                    <div class="nick-modal-sub">Укажите ваш игровой никнейм для игры на сервере</div>
                </div>
            </div>

            <div class="nick-modal-input-wrap">
                <span class="material-symbols-rounded">person</span>
                <input type="text" id="modal-nick-input" class="nick-modal-input" placeholder="Например: MonHero" maxlength="16" onkeydown="if(event.key==='Enter') confirmNickModal()">
            </div>
            <div class="nick-modal-hint">Допустимы латинские буквы, цифры и символ _ (до 16 симв.)</div>

            <div class="nick-modal-btns">
                <button class="btn-primary nick-modal-btn-save" onclick="confirmNickModal()">
                    <span class="material-symbols-rounded">check</span>
                    <span>Сохранить</span>
                </button>
                <button class="nick-modal-btn-cancel" onclick="closeNickModal()">Отмена</button>
            </div>
        </div>
    </div>

    <div id="toast">
        <span class="material-symbols-rounded" style="font-size: 18px;">check_circle</span>
        <span id="toast-text">IP скопирован!</span>
    </div>

    <script>
        // ─── CONSTELLATION MESH CANVAS ───
        const canvasBg = document.getElementById('bgNetwork');
        const ctxBg = canvasBg.getContext('2d');
        let width, height;
        let particles = [];
        const PARTICLE_COUNT = 110;
        let mouseX = -9999, mouseY = -9999;

        function resizeCanvas() {
            const rect = document.getElementById('launcher-window').getBoundingClientRect();
            width = canvasBg.width = rect.width;
            height = canvasBg.height = rect.height;
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
                if (dist < 230 && dist > 0) {
                    const force = (230 - dist) / 230 * 0.015;
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

        document.getElementById('launcher-window').addEventListener('mousemove', (e) => {
            const rect = canvasBg.getBoundingClientRect();
            mouseX = e.clientX - rect.left;
            mouseY = e.clientY - rect.top;
        });

        document.getElementById('launcher-window').addEventListener('mouseleave', () => {
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
                    if (distMouse < 200) maxDist = 145;

                    if (dist < maxDist) {
                        const alpha = (1 - dist / maxDist) * 0.25;
                        ctxBg.beginPath();
                        ctxBg.moveTo(p1.x, p1.y);
                        ctxBg.lineTo(p2.x, p2.y);
                        ctxBg.strokeStyle = `rgba(229, 147, 56, ${alpha})`;
                        ctxBg.lineWidth = 1;
                        ctxBg.stroke();
                    }
                }
            }

            for (let p of particles) {
                p.update();
                p.draw();
            }

            requestAnimationFrame(animateConstellation);
        }
        animateConstellation();

        // ─── LAUNCHER LOGIC ───
        let currentSkinSource = {{quickskinEnabled}} ? 'quickskin' : 'mojang';
        let currentSkinImage = null;
        let savedCustomSkin = {{customSkin}};
        let initialUserMods = {{modsJson}};
        let currentRamVal = {{ramMb}};

        // 1. LIVE SERVER PING
        async function pingServer() {
            const icon = document.getElementById('refresh-icon');
            if (icon) icon.style.animation = 'spin 1s linear infinite';

            const t0 = performance.now();
            try {
                const res = await fetch('https://api.mcstatus.io/v2/status/java/moncraft.space');
                const t1 = performance.now();
                const pingMs = Math.round(t1 - t0);

                if (res.ok) {
                    const data = await res.json();
                    if (data.online) {
                        const onlineCount = data.players ? data.players.online : 0;
                        const maxCount = data.players ? data.players.max : 2026;
                        const motd = (data.motd && data.motd.clean) ? data.motd.clean : "Открытие урааа";

                        document.getElementById('nav-server-dot').className = "pulse-dot";
                        document.getElementById('nav-server-players').textContent = `Онлайн: ${onlineCount} / ${maxCount}`;
                        document.getElementById('nav-server-ping').textContent = `${pingMs} ms`;

                        document.getElementById('hero-status-text').innerHTML = `ОНЛАЙН <span id="hero-ping-text">${pingMs} ms</span>`;
                        document.getElementById('hero-status-text').style.color = "var(--ok)";
                        document.getElementById('hero-players-text').textContent = `${onlineCount} / ${maxCount}`;
                        document.getElementById('server-motd').textContent = `«${motd}»`;
                    } else {
                        setOfflineUI();
                    }
                } else {
                    setOfflineUI();
                }
            } catch (e) {
                setOfflineUI();
            } finally {
                if (icon) icon.style.animation = 'none';
            }
        }

        function setOfflineUI() {
            document.getElementById('nav-server-dot').className = "";
            document.getElementById('nav-server-dot').style.background = "var(--bad)";
            document.getElementById('nav-server-players').textContent = "Офлайн";
            document.getElementById('nav-server-ping').textContent = "--";
            document.getElementById('hero-status-text').innerHTML = `ОФЛАЙН <span id="hero-ping-text">--</span>`;
            document.getElementById('hero-status-text').style.color = "var(--bad)";
            document.getElementById('hero-players-text').textContent = "0 / 0";
            document.getElementById('server-motd').textContent = "Сервер временно недоступен";
        }

        pingServer();
        setInterval(pingServer, 30000);

        // 2. LIVE CLOCK
        function updateClock() {
            const now = new Date();
            const hours = String(now.getHours()).padStart(2, '0');
            const minutes = String(now.getMinutes()).padStart(2, '0');
            const seconds = String(now.getSeconds()).padStart(2, '0');
            document.getElementById('live-clock').textContent = `${hours}:${minutes}:${seconds}`;

            const options = { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' };
            const dateStr = now.toLocaleDateString('ru-RU', options).toUpperCase();
            document.getElementById('live-date').textContent = dateStr;
        }
        setInterval(updateClock, 1000);
        updateClock();

        // 3. QUICKSKIN CANVAS
        function renderSkinFace(img, canvasId) {
            const canvas = document.getElementById(canvasId);
            if (!canvas) return;
            const ctx = canvas.getContext('2d');
            ctx.imageSmoothingEnabled = false;
            ctx.clearRect(0, 0, canvas.width, canvas.height);

            const scale = img.width / 64;
            ctx.drawImage(img, 8 * scale, 8 * scale, 8 * scale, 8 * scale, 0, 0, canvas.width, canvas.height);
            ctx.drawImage(img, 40 * scale, 8 * scale, 8 * scale, 8 * scale, 0, 0, canvas.width, canvas.height);
        }

        function refreshAllSkinViews() {
            if (!currentSkinImage) return;
            renderSkinFace(currentSkinImage, 'sidebar-skin-canvas');
            renderSkinFace(currentSkinImage, 'main-skin-canvas');
            renderSkinFace(currentSkinImage, 'settings-skin-canvas');
        }

        function loadSkinFromUrl(url) {
            const img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = () => {
                currentSkinImage = img;
                refreshAllSkinViews();
            };
            img.src = url;
        }

        if (savedCustomSkin) {
            const img = new Image();
            img.onload = () => {
                currentSkinImage = img;
                refreshAllSkinViews();
            };
            img.src = savedCustomSkin;
        } else {
            loadSkinFromUrl('https://minotar.net/skin/MHF_Steve');
        }

        function handleSkinUpload(e) {
            const file = e.target.files[0];
            if (file) applySkinFile(file);
        }

        function applySkinFile(file) {
            const reader = new FileReader();
            reader.onload = (event) => {
                const dataUrl = event.target.result;
                const img = new Image();
                img.onload = () => {
                    currentSkinImage = img;
                    savedCustomSkin = dataUrl;
                    currentSkinSource = 'quickskin';
                    document.getElementById('quickskin-toggle').checked = true;
                    document.getElementById('skin-status-pill').textContent = "QUICKSKIN (ИЗ ФАЙЛА)";
                    document.getElementById('main-skin-badge').textContent = "QUICKSKIN";
                    refreshAllSkinViews();
                    showToast(`Скин QuickSkin применен: ${file.name}`);
                    saveProfileSettings();
                };
                img.src = dataUrl;
            };
            reader.readAsDataURL(file);
        }

        function resetToSampleSkin() {
            savedCustomSkin = null;
            loadSkinFromUrl('https://minotar.net/skin/MHF_Steve');
            showToast('Скин сброшен');
            saveProfileSettings();
        }

        function toggleQuickSkin(enabled) {
            if (enabled) {
                currentSkinSource = 'quickskin';
                document.getElementById('skin-status-pill').textContent = "QUICKSKIN АКТИВЕН";
                document.getElementById('main-skin-badge').textContent = "QUICKSKIN";
                refreshAllSkinViews();
                showToast('Режим QuickSkin активен');
            } else {
                currentSkinSource = 'mojang';
                document.getElementById('skin-status-pill').textContent = "ПО НИКУ (MOJANG)";
                document.getElementById('main-skin-badge').textContent = "MOJANG";
                const nick = document.getElementById('settings-nick-input').value.trim() || 'Player_1';
                loadSkinFromUrl(`https://minotar.net/skin/${nick}`);
                showToast('Режим: скин по никнейму');
            }
        }

        let autoSaveTimer = null;
        function triggerAutoSave() {
            clearTimeout(autoSaveTimer);
            autoSaveTimer = setTimeout(() => {
                saveProfileSettings(true);
            }, 400);
        }

        function handleNickChange(nick) {
            const clean = nick.trim() || 'Player_1';
            document.getElementById('main-nick-display').textContent = clean;
            if (currentSkinSource === 'mojang') {
                loadSkinFromUrl(`https://minotar.net/skin/${clean}`);
            }
            let hash = 0;
            for (let i = 0; i < clean.length; i++) hash = (hash << 5) - hash + clean.charCodeAt(i);
            const hex = Math.abs(hash).toString(16).padStart(8, '0');
            document.getElementById('settings-uuid').textContent = `3a9f0293-${hex.slice(0,4)}-38b4-9382-${hex.slice(4)}847`;
            triggerAutoSave();
        }

        // 4. NAVIGATION
        function switchTab(tab) {
            clearTimeout(autoSaveTimer);
            saveProfileSettings(true);

            document.querySelectorAll('.tab-view').forEach(v => v.classList.remove('active'));
            document.querySelectorAll('.nav-item').forEach(b => b.classList.remove('active'));

            const targetView = document.getElementById('view-' + tab);
            const targetBtn = document.getElementById('nav-' + tab);

            if (targetView) targetView.classList.add('active');
            if (targetBtn) targetBtn.classList.add('active');
        }

        // 5. RAM SLIDER
        function updateRamSlider(val) {
            currentRamVal = parseInt(val);
            const gb = (currentRamVal / 1024).toFixed(1);
            const formatted = (currentRamVal % 1024 === 0) ? (currentRamVal / 1024) + " ГБ" : gb + " ГБ";
            document.getElementById('settings-ram-label').textContent = `${formatted} (${currentRamVal} МБ)`;
            document.getElementById('main-ram-display').textContent = formatted;
            const noteGb = document.getElementById('ram-note-gb');
            if (noteGb) noteGb.textContent = formatted;

            [4096, 6144, 8192, 12288].forEach(chip => {
                const btn = document.getElementById('ram-btn-' + (chip / 1024));
                if (btn) {
                    if (currentRamVal === chip) btn.classList.add('active');
                    else btn.classList.remove('active');
                }
            });
            triggerAutoSave();
        }

        function setRamVal(val) {
            document.querySelector('#view-settings input[type=range]').value = val;
            updateRamSlider(val);
            saveProfileSettings(true);
        }
        updateRamSlider(currentRamVal);

        // 6. C# INTEROP & NICKNAME MODAL
        function triggerPlay() {
            const nickInput = document.getElementById('settings-nick-input');
            const nick = (nickInput ? nickInput.value.trim() : '') || 'Player_1';
            if (!nick || nick === 'Player_1' || nick.toLowerCase() === 'player') {
                openNickModal();
                return;
            }
            window.chrome.webview.postMessage({ action: 'play' });
        }

        function openNickModal() {
            const modal = document.getElementById('modal-nick-prompt');
            const input = document.getElementById('modal-nick-input');
            const settingsInput = document.getElementById('settings-nick-input');
            if (input && settingsInput) {
                let cur = settingsInput.value.trim();
                input.value = (cur === 'Player_1' || cur.toLowerCase() === 'player') ? '' : cur;
            }
            if (modal) {
                modal.classList.add('active');
                setTimeout(() => { if (input) { input.focus(); input.select(); } }, 60);
            }
        }

        function closeNickModal() {
            const modal = document.getElementById('modal-nick-prompt');
            if (modal) modal.classList.remove('active');
        }

        function confirmNickModal() {
            const input = document.getElementById('modal-nick-input');
            let nick = (input ? input.value.trim() : '');
            if (!nick || nick === 'Player_1' || nick.toLowerCase() === 'player') {
                showToast('Пожалуйста, укажите ваш никнейм!');
                if (input) input.focus();
                return;
            }
            const settingsInput = document.getElementById('settings-nick-input');
            if (settingsInput) settingsInput.value = nick;
            handleNickChange(nick);
            saveProfileSettings(true);
            closeNickModal();
            showToast(`Ник сохранен: ${nick}`);
            window.chrome.webview.postMessage({ action: 'play' });
        }

        function setLaunchingState(launching, msg) {
            const btn = document.getElementById('main-launch-btn');
            btn.disabled = launching;
            if (launching) {
                btn.innerHTML = `<span class="material-symbols-rounded" style="animation: spin 1s linear infinite; font-size: 20px;">sync</span> <span>${msg}</span>`;
            } else {
                btn.innerHTML = `<span class="material-symbols-rounded material-symbols-fill" style="font-size: 24px;">play_arrow</span> <span>${msg}</span>`;
            }
        }

        function setLaunchProgress(msg) {
            const btn = document.getElementById('main-launch-btn');
            btn.innerHTML = `<span class="material-symbols-rounded" style="animation: spin 1s linear infinite; font-size: 20px;">sync</span> <span>${msg}</span>`;
        }

        function saveProfileSettings(silent = false) {
            const nickInput = document.getElementById('settings-nick-input');
            const nick = (nickInput ? nickInput.value.trim() : '') || 'Player_1';
            const qs = document.getElementById('quickskin-toggle');
            const quickskin = qs ? qs.checked : true;
            window.chrome.webview.postMessage({
                action: 'save_settings',
                username: nick,
                ram: currentRamVal,
                quickskin: quickskin,
                skinBase64: savedCustomSkin,
                silent: !!silent
            });
        }

        function openUserModsFolder() {
            window.chrome.webview.postMessage({ action: 'open_folder' });
        }

        function openLink(url) {
            window.chrome.webview.postMessage({ action: 'open_link', url: url });
        }

        function minimizeWindow() {
            window.chrome.webview.postMessage({ action: 'window_minimize' });
        }

        function closeWindow() {
            window.chrome.webview.postMessage({ action: 'window_close' });
        }

        function copyIp() {
            navigator.clipboard.writeText('moncraft.space');
            showToast('IP moncraft.space скопирован!');
        }

        function showToast(msg) {
            const toast = document.getElementById('toast');
            document.getElementById('toast-text').textContent = msg;
            toast.classList.add('show');
            setTimeout(() => { toast.classList.remove('show'); }, 2600);
        }

        // 7. USER MODS RENDERING
        function renderUserModsList(mods) {
            const container = document.getElementById('user-mods-list');
            container.innerHTML = '';
            if (!mods || mods.length === 0) {
                container.innerHTML = '<div style="padding: 20px; text-align: center; color: var(--text-dim); font-size: 12px; font-family: var(--font-mono);">Папка user-mods пуста. Перетащите файлы .jar сюда.</div>';
                return;
            }

            for (let mod of mods) {
                const row = document.createElement('div');
                row.className = 'table-row';
                row.innerHTML = `
                    <div class="mod-meta">
                        <span class="material-symbols-rounded" style="color: var(--accent); font-size: 22px;">inventory_2</span>
                        <div>
                            <span class="mod-name">${mod.Name}</span>
                            <span class="mod-size">Пользовательский мод • ${mod.SizeFormatted}</span>
                        </div>
                    </div>
                    <button class="btn-icon-del" onclick="deleteUserMod('${mod.Name}')" title="Удалить">
                        <span class="material-symbols-rounded" style="font-size: 20px;">delete</span>
                    </button>
                `;
                container.appendChild(row);
            }
        }
        renderUserModsList(initialUserMods);

        function deleteUserMod(name) {
            if (confirm(`Удалить мод ${name}?`)) {
                window.chrome.webview.postMessage({ action: 'delete_mod', fileName: name });
            }
        }

        function handleModFileInput(e) {
            if (e.target.files && e.target.files.length > 0) {
                processDroppedFiles(e.target.files);
            }
        }

        // 8. GLOBAL DRAG & DROP
        const dropOverlay = document.getElementById('drop-overlay');
        let dragCounter = 0;

        window.addEventListener('dragenter', (e) => {
            e.preventDefault();
            dragCounter++;
            dropOverlay.classList.add('active');
        });

        window.addEventListener('dragleave', (e) => {
            e.preventDefault();
            dragCounter--;
            if (dragCounter <= 0) {
                dragCounter = 0;
                dropOverlay.classList.remove('active');
            }
        });

        window.addEventListener('dragover', (e) => { e.preventDefault(); });

        window.addEventListener('drop', (e) => {
            e.preventDefault();
            dragCounter = 0;
            dropOverlay.classList.remove('active');

            const files = e.dataTransfer.files;
            if (files.length > 0) processDroppedFiles(files);
        });

        function processDroppedFiles(files) {
            for (let file of files) {
                if (file.name.endsWith('.jar')) {
                    const reader = new FileReader();
                    reader.onload = (event) => {
                        window.chrome.webview.postMessage({
                            action: 'install_mod',
                            fileName: file.name,
                            base64Data: event.target.result
                        });
                    };
                    reader.readAsDataURL(file);
                    switchTab('mods');
                } else if (file.name.endsWith('.png')) {
                    applySkinFile(file);
                }
            }
        }

        // 9. STARTUP NICKNAME CHECK
        window.addEventListener('DOMContentLoaded', () => {
            const currentNick = document.getElementById('settings-nick-input')?.value.trim() || 'Player_1';
            if (currentNick === 'Player_1' || currentNick.toLowerCase() === 'player' || !currentNick) {
                setTimeout(() => {
                    openNickModal();
                }, 350);
            }
        });
    </script>
</body>
</html>
""";
    }
}