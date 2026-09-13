#!/usr/bin/env python3
"""
MON SERVER Launcher - Ultra-Fast Edition
Modern GUI Launcher with hardware-accelerated WebView2,
instant NTFS hardlinks, parallel mod downloading, and auto-Java 21 detection.
"""

import sys
import os
import subprocess
import uuid
import json
import shutil
import psutil
import threading
import webbrowser
import base64
import zipfile
import time
import re
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed

import requests
import webview

# =============================================================================
# CONFIGURATION
# =============================================================================

# Determine base directory (portable: works as script or PyInstaller exe)
if getattr(sys, 'frozen', False):
    BASE_DIR = os.path.dirname(sys.executable)
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

MINECRAFT_DIR = os.path.join(BASE_DIR, ".minecraft")
CONFIG_FILE = os.path.join(BASE_DIR, "launcher_config.json")
JAVA_PORTABLE_DIR = os.path.join(BASE_DIR, "java")

# NeoForge version
VERSION = "neoforge-21.1.249"

# Auto-update / VDS endpoint
# By default, uses GitHub manifest, or replace with your VDS URL (e.g. "http://your-vds-ip/mods.json")
MANIFEST_URL = "https://raw.githubusercontent.com/ilya90308/mon-server-mods/refs/heads/main/mods.json"

SERVER_MODS_DIR = os.path.join(BASE_DIR, "server-mods")
USER_MODS_DIR = os.path.join(BASE_DIR, "user-mods")
GAME_MODS_DIR = os.path.join(MINECRAFT_DIR, "mods")

# Social links
DISCORD_URL = "https://discord.gg/E2Zv9pEwYG"
TELEGRAM_DEV_URL = "https://t.me/ZvenCore"

# Fallback OpenJDK 21 portable zip for Windows (Adoptium / Eclipse Temurin)
ADOPTIUM_JAVA_21_URL = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse?project=jdk"


# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def get_system_ram_gb():
    """Get total system RAM in GB (rounded down)."""
    try:
        return psutil.virtual_memory().total // (1024 ** 3)
    except Exception:
        return 8


def load_config():
    """Load configuration from JSON file."""
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except (json.JSONDecodeError, IOError):
            pass
    return {}


def save_config(config):
    """Save configuration to JSON file."""
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(config, f, ensure_ascii=False, indent=4)
    except Exception as e:
        print(f"Error saving config: {e}")


def get_allocated_ram_str():
    """Get formatted allocated RAM string."""
    config = load_config()
    ram_mb = config.get("ram_mb", 4096)
    ram_gb = ram_mb / 1024
    if ram_gb == int(ram_gb):
        return f"{int(ram_gb)} ГБ"
    return f"{ram_gb:.1f} ГБ"


def get_username():
    """Get username from config."""
    config = load_config()
    return config.get("username", "")


def is_valid_username(username):
    """Check if username is valid (not empty and not default)."""
    if not username or not username.strip():
        return False
    if username.strip().lower() == "player":
        return False
    return True


def generate_offline_uuid(username: str) -> str:
    """Generate valid offline UUID from username."""
    offline_string = f"OfflinePlayer:{username}"
    return str(uuid.uuid3(uuid.NAMESPACE_DNS, offline_string))


# =============================================================================
# JAVA 21 AUTO-DETECTION & PORTABLE JRE
# =============================================================================

def check_java_version(executable_path):
    """Check if a java executable is version 21 or higher."""
    try:
        startupinfo = None
        if os.name == 'nt':
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE

        result = subprocess.run(
            [executable_path, "-version"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            startupinfo=startupinfo,
            timeout=5
        )
        output = result.stdout
        match = re.search(r'version "(\d+)', output)
        if match:
            major = int(match.group(1))
            return major >= 21
    except Exception:
        pass
    return False


def find_java_executable(progress_callback=None):
    """Find a suitable Java 21+ executable (portable or system)."""
    # 1. Check local portable java folder
    portable_candidates = [
        os.path.join(JAVA_PORTABLE_DIR, "bin", "javaw.exe"),
        os.path.join(JAVA_PORTABLE_DIR, "bin", "java.exe"),
    ]
    if os.path.exists(JAVA_PORTABLE_DIR):
        for root, dirs, files in os.walk(JAVA_PORTABLE_DIR):
            if "javaw.exe" in files:
                portable_candidates.append(os.path.join(root, "javaw.exe"))
            elif "java.exe" in files:
                portable_candidates.append(os.path.join(root, "java.exe"))

    for candidate in portable_candidates:
        if os.path.exists(candidate) and check_java_version(candidate):
            return candidate

    # 2. Check Minecraft runtime directory
    runtime_dir = os.path.join(MINECRAFT_DIR, "runtime")
    if os.path.exists(runtime_dir):
        for root, dirs, files in os.walk(runtime_dir):
            if "javaw.exe" in files:
                cand = os.path.join(root, "javaw.exe")
                if check_java_version(cand):
                    return cand

    # 3. Check system PATH
    for name in ["javaw", "java"]:
        sys_path = shutil.which(name)
        if sys_path and check_java_version(sys_path):
            javaw_sibling = os.path.join(os.path.dirname(sys_path), "javaw.exe")
            if os.path.exists(javaw_sibling):
                return javaw_sibling
            return sys_path

    # 4. Check common Oracle/Adoptium Program Files directories
    common_roots = [
        r"C:\Program Files\Java",
        r"C:\Program Files\Eclipse Adoptium",
        r"C:\Program Files\BellSoft",
        r"C:\Program Files\Zulu"
    ]
    for c_root in common_roots:
        if os.path.exists(c_root):
            for root, dirs, files in os.walk(c_root):
                if "javaw.exe" in files:
                    cand = os.path.join(root, "javaw.exe")
                    if check_java_version(cand):
                        return cand

    return None


def download_and_extract_java(progress_callback=None):
    """Download portable Java 21 if completely missing on client machine."""
    if progress_callback:
        progress_callback({"action": "status", "message": "Скачивание портативной Java 21..."})

    os.makedirs(JAVA_PORTABLE_DIR, exist_ok=True)
    zip_path = os.path.join(BASE_DIR, "java21_download.zip")

    try:
        headers = {"User-Agent": "Mozilla/5.0"}
        with requests.get(ADOPTIUM_JAVA_21_URL, stream=True, headers=headers, timeout=60) as r:
            r.raise_for_status()
            total_len = int(r.headers.get("content-length", 0))
            downloaded = 0
            with open(zip_path, "wb") as f:
                for chunk in r.iter_content(chunk_size=1024 * 1024):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if total_len > 0 and progress_callback:
                            percent = int((downloaded / total_len) * 100)
                            progress_callback({
                                "action": "progress",
                                "percent": percent,
                                "message": f"Скачивание Java 21: {percent}%"
                            })

        if progress_callback:
            progress_callback({"action": "status", "message": "Распаковка Java 21..."})

        with zipfile.ZipFile(zip_path, 'r') as zip_ref:
            zip_ref.extractall(JAVA_PORTABLE_DIR)

        try:
            os.remove(zip_path)
        except OSError:
            pass

        java_exe = find_java_executable(progress_callback)
        if java_exe:
            if progress_callback:
                progress_callback({"action": "status", "message": "Java 21 готова!"})
            return java_exe
        else:
            raise Exception("Не удалось обнаружить javaw.exe после распаковки")

    except Exception as e:
        if os.path.exists(zip_path):
            try:
                os.remove(zip_path)
            except OSError:
                pass
        if progress_callback:
            progress_callback({"action": "error", "message": f"Ошибка авто-загрузки Java: {e}"})
        return None


# =============================================================================
# MOD AUTO-UPDATE & INSTANT HARDLINK SYSTEM
# =============================================================================

def ensure_mods_folders():
    """Create server-mods and user-mods folders if they don't exist."""
    os.makedirs(SERVER_MODS_DIR, exist_ok=True)
    os.makedirs(USER_MODS_DIR, exist_ok=True)
    os.makedirs(GAME_MODS_DIR, exist_ok=True)


def download_single_mod(base_url, filename, session):
    """Download a single mod file with requests session."""
    local_path = os.path.join(SERVER_MODS_DIR, filename)
    mod_url = base_url + requests.utils.quote(filename)
    try:
        resp = session.get(mod_url, timeout=30)
        resp.raise_for_status()
        with open(local_path, "wb") as f:
            f.write(resp.content)
        return True, filename
    except Exception as e:
        if os.path.exists(local_path):
            try:
                os.remove(local_path)
            except OSError:
                pass
        return False, f"{filename} ({e})"


def sync_server_mods(progress_callback=None):
    """Download mods.json and synchronize server-mods in parallel threads."""
    if not MANIFEST_URL:
        return True

    try:
        if progress_callback:
            progress_callback({"action": "status", "message": "Проверка обновлений модов..."})

        session = requests.Session()
        session.headers.update({"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})

        resp = session.get(MANIFEST_URL, timeout=15)
        if resp.status_code != 200:
            if progress_callback:
                progress_callback({"action": "status", "message": "Сервер обновлений недоступен, запуск локальных модов."})
            return False

        manifest_data = resp.json()
        base_url = manifest_data.get("base_url", "")
        mods_list = manifest_data.get("mods", [])

        if not base_url or not isinstance(mods_list, list):
            return False

        ensure_mods_folders()
        required_files = set(filter(bool, mods_list))

        # 1. HARD DELETE: remove local server-mods not in manifest
        for filename in os.listdir(SERVER_MODS_DIR):
            local_path = os.path.join(SERVER_MODS_DIR, filename)
            if os.path.isfile(local_path) and filename not in required_files:
                try:
                    os.remove(local_path)
                except OSError:
                    pass

        # 2. Identify missing mods to download
        missing_mods = [f for f in required_files if not os.path.exists(os.path.join(SERVER_MODS_DIR, f))]

        if not missing_mods:
            if progress_callback:
                progress_callback({"action": "status", "message": "Все серверные моды актуальны."})
            return True

        total_missing = len(missing_mods)
        if progress_callback:
            progress_callback({"action": "status", "message": f"Скачивание {total_missing} модов в 6 потоков..."})

        # 3. Parallel download
        completed = 0
        with ThreadPoolExecutor(max_workers=6) as executor:
            futures = [executor.submit(download_single_mod, base_url, f, session) for f in missing_mods]
            for fut in as_completed(futures):
                success, name = fut.result()
                completed += 1
                percent = int((completed / total_missing) * 100)
                if progress_callback:
                    progress_callback({
                        "action": "progress",
                        "percent": percent,
                        "message": f"Скачивание модов ({completed}/{total_missing})..."
                    })

        return True

    except Exception as e:
        if progress_callback:
            progress_callback({"action": "status", "message": f"Сеть недоступна, запуск с локальными модами ({e})"})
        return False


def link_or_copy(src, dst):
    """
    Instantly hardlink file on NTFS (0 ms, 0 disk space).
    Fallback to copy2 if hardlinks fail across partitions.
    """
    try:
        if os.path.exists(dst):
            src_stat = os.stat(src)
            dst_stat = os.stat(dst)
            if src_stat.st_ino == dst_stat.st_ino and src_stat.st_dev == dst_stat.st_dev:
                return True
            os.remove(dst)
        os.link(src, dst)
        return True
    except Exception:
        try:
            shutil.copy2(src, dst)
            return True
        except Exception:
            return False


def prepare_mods_for_launch(progress_callback=None):
    """
    Fast incremental sync of .minecraft/mods using NTFS hardlinks.
    Takes ~0.01 seconds instead of copying hundreds of megabytes.
    """
    ensure_mods_folders()

    # Collect desired files: {filename: source_path}
    desired_mods = {}
    if os.path.exists(SERVER_MODS_DIR):
        for f in os.listdir(SERVER_MODS_DIR):
            src = os.path.join(SERVER_MODS_DIR, f)
            if os.path.isfile(src) and f.endswith(".jar"):
                desired_mods[f] = src

    if os.path.exists(USER_MODS_DIR):
        for f in os.listdir(USER_MODS_DIR):
            src = os.path.join(USER_MODS_DIR, f)
            if os.path.isfile(src) and f.endswith(".jar"):
                desired_mods[f] = src

    # 1. Remove obsolete files from .minecraft/mods
    for f in os.listdir(GAME_MODS_DIR):
        dst = os.path.join(GAME_MODS_DIR, f)
        if f not in desired_mods:
            try:
                if os.path.isfile(dst) or os.path.islink(dst):
                    os.remove(dst)
                elif os.path.isdir(dst):
                    shutil.rmtree(dst)
            except OSError:
                pass

    # 2. Hardlink / mount desired mods
    linked_count = 0
    for filename, src in desired_mods.items():
        dst = os.path.join(GAME_MODS_DIR, filename)
        if link_or_copy(src, dst):
            linked_count += 1

    if progress_callback:
        progress_callback({"action": "status", "message": f"Готово: подключено {linked_count} модов."})


# =============================================================================
# GAME LAUNCH
# =============================================================================

def launch_game_window(window, progress_callback=None):
    """Full launch procedure: checks, mod sync, Java detection, and process launch."""
    config = load_config()
    username = config.get("username", "Player")
    ram_mb = config.get("ram_mb", 4096)

    # 1. Verify Minecraft directory
    if not os.path.exists(MINECRAFT_DIR):
        if progress_callback:
            progress_callback({"action": "error", "message": f"Папка .minecraft не найдена: {MINECRAFT_DIR}"})
        return False

    # 2. Verify version
    version_path = os.path.join(MINECRAFT_DIR, "versions", VERSION)
    if not os.path.exists(version_path):
        if progress_callback:
            progress_callback({"action": "error", "message": f"Версия {VERSION} не найдена в {version_path}"})
        return False

    # 3. Synchronize mods
    sync_server_mods(progress_callback)

    # 4. Instant assembly via NTFS hardlinks
    if progress_callback:
        progress_callback({"action": "status", "message": "Подготовка игровых модов..."})
    prepare_mods_for_launch(progress_callback)

    # 5. Resolve Java 21
    if progress_callback:
        progress_callback({"action": "status", "message": "Проверка Java 21..."})
    java_exe = find_java_executable(progress_callback)

    if not java_exe:
        if progress_callback:
            progress_callback({"action": "status", "message": "Java 21 не найдена. Скачивание портативной версии..."})
        java_exe = download_and_extract_java(progress_callback)
        if not java_exe:
            if progress_callback:
                progress_callback({"action": "error", "message": "Не удалось подготовить Java 21."})
            return False

    # 6. Build launch command
    offline_uuid = generate_offline_uuid(username)
    options = {
        "username": username,
        "uuid": offline_uuid,
        "token": "",
        "launcherName": "MON-SERVER-Launcher",
        "launcherVersion": "2.0.0",
        "jvmArguments": [f"-Xmx{ram_mb}M", f"-Xms{ram_mb}M"],
        "gameDirectory": MINECRAFT_DIR,
        "executablePath": java_exe
    }

    try:
        import minecraft_launcher_lib
        if progress_callback:
            progress_callback({"action": "status", "message": "Формирование команды запуска..."})

        command = minecraft_launcher_lib.command.get_minecraft_command(
            VERSION, MINECRAFT_DIR, options
        )
    except ImportError:
        if progress_callback:
            progress_callback({"action": "error", "message": "Библиотека minecraft-launcher-lib не найдена"})
        return False
    except Exception as e:
        if progress_callback:
            progress_callback({"action": "error", "message": f"Ошибка параметров запуска: {e}"})
        return False

    # 7. Spawn detached process
    if progress_callback:
        progress_callback({"action": "status", "message": "Запуск Minecraft..."})

    try:
        creation_flags = 0
        if os.name == 'nt':
            creation_flags = subprocess.CREATE_NEW_PROCESS_GROUP

        subprocess.Popen(command, cwd=MINECRAFT_DIR, creationflags=creation_flags)

        if progress_callback:
            progress_callback({"action": "status", "message": "Minecraft запущен! Закрытие лаунчера..."})

        time.sleep(1.2)
        window.destroy()
        return True
    except Exception as e:
        if progress_callback:
            progress_callback({"action": "error", "message": f"Ошибка запуска: {e}"})
        return False


# =============================================================================
# WEB INTERFACE (HTML / CSS / JS)
# =============================================================================

HTML_TEMPLATE = """
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link href="https://fonts.googleapis.com/css2?family=Press+Start+2P&family=Space+Grotesk:wght@500;600;700&family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
    <style>
        :root {
            --ink: #07080b;
            --surface: #0f1217;
            --surface-2: #161b22;
            --line: #21262d;
            --line-bright: #30363d;
            --emerald: #3fb950;
            --emerald-glow: rgba(63, 185, 80, 0.35);
            --emerald-dim: #238636;
            --text: #f0f6fc;
            --text-dim: #8b949e;
            --danger: #f85149;
        }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            padding: 0;
            font-family: 'Inter', system-ui, sans-serif;
            color: var(--text);
            background: radial-gradient(circle at 50% 30%, #151b23 0%, var(--ink) 85%);
            background-size: cover;
            background-position: center;
            height: 100vh;
            display: flex;
            flex-direction: column;
            justify-content: center;
            align-items: center;
            position: relative;
            overflow: hidden;
            user-select: none;
        }

        body::before {
            content: "";
            position: absolute;
            inset: 0;
            background-image:
                linear-gradient(to right, rgba(255,255,255,0.02) 1px, transparent 1px),
                linear-gradient(to bottom, rgba(255,255,255,0.02) 1px, transparent 1px);
            background-size: 32px 32px;
            pointer-events: none;
        }

        .eyebrow {
            font-family: 'Press Start 2P', monospace;
            font-size: 0.55rem;
            letter-spacing: 2px;
            color: var(--emerald);
            margin-bottom: 12px;
            text-transform: uppercase;
        }

        .main-block {
            position: relative;
            z-index: 1;
            text-align: center;
            background: rgba(15, 18, 23, 0.75);
            border: 1px solid var(--line);
            border-radius: 12px;
            padding: 40px 52px 32px;
            box-shadow: 0 16px 40px rgba(0, 0, 0, 0.6), 0 0 1px rgba(255, 255, 255, 0.1);
        }

        h1 {
            font-family: 'Space Grotesk', sans-serif;
            font-weight: 700;
            font-size: 3.2rem;
            margin: 0 0 6px;
            letter-spacing: 1px;
            line-height: 1.1;
        }

        h1 span {
            color: var(--emerald);
            text-shadow: 0 0 20px var(--emerald-glow);
        }

        .info-row {
            display: flex;
            gap: 10px;
            justify-content: center;
            margin: 16px 0 26px;
        }

        .chip {
            font-size: 0.8rem;
            color: var(--text-dim);
            background: rgba(255, 255, 255, 0.04);
            border: 1px solid var(--line);
            padding: 6px 14px;
            border-radius: 6px;
        }

        .buttons {
            display: flex;
            gap: 12px;
            justify-content: center;
        }

        button {
            font-family: 'Inter', sans-serif;
            padding: 13px 32px;
            font-size: 0.95rem;
            font-weight: 600;
            border: none;
            border-radius: 6px;
            cursor: pointer;
            transition: all 0.15s ease;
        }

        button:hover { transform: translateY(-1px); }
        button:active { transform: translateY(0); }

        .btn-play {
            background: var(--emerald);
            color: #041407;
            font-weight: 700;
            box-shadow: 0 4px 18px var(--emerald-glow);
            letter-spacing: 0.5px;
        }
        .btn-play:hover { background: #46cf5a; }
        .btn-play:disabled {
            background: #21262d;
            color: #6e7681;
            cursor: not-allowed;
            box-shadow: none;
            transform: none;
        }

        .btn-settings {
            background: var(--surface-2);
            color: var(--text);
            border: 1px solid var(--line-bright);
        }
        .btn-settings:hover { background: #21262d; }

        footer {
            position: absolute;
            bottom: 24px;
            display: flex;
            gap: 12px;
            z-index: 1;
        }

        .link-icon {
            text-decoration: none;
            color: var(--text-dim);
            background: rgba(255, 255, 255, 0.03);
            border: 1px solid var(--line);
            padding: 8px 16px;
            font-size: 0.8rem;
            font-weight: 500;
            border-radius: 6px;
            transition: all 0.15s;
        }
        .link-icon:hover {
            background: rgba(255, 255, 255, 0.08);
            color: var(--text);
            border-color: var(--line-bright);
        }

        .status-overlay {
            position: fixed;
            inset: 0;
            background: rgba(5, 7, 10, 0.88);
            display: none;
            justify-content: center;
            align-items: center;
            flex-direction: column;
            z-index: 1000;
        }

        .status-content {
            background: var(--surface);
            border: 1px solid var(--line-bright);
            border-radius: 10px;
            padding: 28px 36px;
            max-width: 440px;
            width: 90%;
            text-align: center;
            box-shadow: 0 16px 36px rgba(0,0,0,0.8);
        }

        .status-text {
            font-size: 0.95rem;
            margin-bottom: 16px;
            color: var(--text);
            font-weight: 500;
        }

        .progress-bar {
            width: 100%;
            height: 8px;
            background: var(--surface-2);
            border-radius: 4px;
            overflow: hidden;
        }

        .progress-fill {
            height: 100%;
            background: var(--emerald);
            width: 0%;
            transition: width 0.2s ease;
        }

        .modal, .settings-modal {
            display: none;
            position: fixed;
            inset: 0;
            background: rgba(5, 7, 10, 0.82);
            justify-content: center;
            align-items: center;
            z-index: 1000;
        }

        .modal-content, .settings-content {
            background: var(--surface);
            border: 1px solid var(--line-bright);
            border-radius: 10px;
            padding: 28px;
            max-width: 440px;
            width: 90%;
            box-shadow: 0 16px 40px rgba(0,0,0,0.8);
        }

        .settings-content {
            max-width: 540px;
            max-height: 85vh;
            overflow-y: auto;
        }

        .modal h2 {
            margin: 0 0 10px;
            font-size: 1.3rem;
            font-weight: 600;
        }

        .modal p {
            color: var(--text-dim);
            margin: 0 0 16px;
            font-size: 0.85rem;
        }

        .modal-input, .form-input {
            width: 100%;
            padding: 10px 12px;
            margin-bottom: 12px;
            border: 1px solid var(--line);
            border-radius: 6px;
            background: var(--ink);
            color: var(--text);
            font-size: 0.9rem;
            outline: none;
        }
        .modal-input:focus, .form-input:focus {
            border-color: var(--emerald);
        }

        .modal-buttons {
            display: flex;
            gap: 8px;
            justify-content: flex-end;
            margin-top: 16px;
        }

        .modal-btn {
            padding: 8px 18px;
            border-radius: 6px;
            font-size: 0.85rem;
            font-weight: 600;
        }

        .modal-btn.save {
            background: var(--emerald);
            color: #041407;
        }
        .modal-btn.cancel {
            background: var(--surface-2);
            color: var(--text-dim);
            border: 1px solid var(--line);
        }

        .settings-tabs {
            display: flex;
            gap: 8px;
            border-bottom: 1px solid var(--line);
            margin-bottom: 20px;
        }

        .tab {
            font-size: 0.8rem;
            padding: 8px 14px;
            cursor: pointer;
            color: var(--text-dim);
            border-bottom: 2px solid transparent;
            font-weight: 600;
        }
        .tab:hover { color: var(--text); }
        .tab.active {
            color: var(--emerald);
            border-bottom-color: var(--emerald);
        }

        .tab-content { display: none; }
        .tab-content.active { display: block; }

        .form-group { margin-bottom: 18px; }

        .form-label {
            display: block;
            margin-bottom: 6px;
            color: var(--text-dim);
            font-weight: 600;
            font-size: 0.8rem;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }

        .mono-box {
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.8rem;
            background: var(--ink);
            border: 1px solid var(--line);
            border-radius: 6px;
            padding: 10px 12px;
            color: var(--emerald);
            word-break: break-all;
        }

        .ram-slider {
            width: 100%;
            height: 6px;
            background: var(--surface-2);
            border-radius: 3px;
            outline: none;
            -webkit-appearance: none;
        }
        .ram-slider::-webkit-slider-thumb {
            -webkit-appearance: none;
            width: 16px;
            height: 16px;
            border-radius: 50%;
            background: var(--emerald);
            cursor: pointer;
        }

        .ram-value {
            text-align: center;
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.95rem;
            color: var(--text);
            margin: 10px 0;
        }

        .preset-buttons {
            display: flex;
            gap: 8px;
            margin-bottom: 16px;
        }

        .preset-btn {
            padding: 6px 12px;
            background: var(--surface-2);
            color: var(--text-dim);
            border: 1px solid var(--line);
            border-radius: 5px;
            cursor: pointer;
            font-size: 0.8rem;
        }
        .preset-btn:hover {
            background: var(--line);
            color: var(--text);
        }

        .error-message {
            color: var(--danger);
            font-size: 0.8rem;
            margin-top: -6px;
            margin-bottom: 8px;
        }
    </style>
</head>
<body>
    <div class="main-block">
        <div class="eyebrow">MON SERVER · FAST LAUNCHER</div>
        <h1>MON <span>SERVER</span></h1>
        <div class="info-row">
            <div class="chip" id="infoText">Никнейм: {USERNAME} · ОЗУ: {RAM_STR}</div>
        </div>
        <div class="buttons">
            <button class="btn-play" id="playBtn" onclick="pywebview.api.play_game()">▶ ИГРАТЬ</button>
            <button class="btn-settings" onclick="pywebview.api.open_settings()">⚙ Настройки</button>
        </div>
    </div>

    <footer>
        <a href="{DISCORD_URL}" class="link-icon" target="_blank">Discord</a>
        <a href="{TELEGRAM_DEV_URL}" class="link-icon" target="_blank">Telegram Dev</a>
    </footer>

    <!-- Status Overlay -->
    <div class="status-overlay" id="statusOverlay">
        <div class="status-content">
            <div class="status-text" id="statusText">Подготовка...</div>
            <div class="progress-bar">
                <div class="progress-fill" id="progressFill"></div>
            </div>
        </div>
    </div>

    <!-- Username Modal -->
    <div class="modal" id="usernameModal">
        <div class="modal-content">
            <h2>Добро пожаловать!</h2>
            <p>Укажите ваш игровой никнейм:</p>
            <input type="text" class="modal-input" id="usernameInput" placeholder="Введите никнейм" autofocus>
            <div class="error-message" id="usernameError"></div>
            <div class="modal-buttons">
                <button class="modal-btn cancel" onclick="pywebview.api.close_username_modal()">Отмена</button>
                <button class="modal-btn save" onclick="pywebview.api.save_username()">Сохранить</button>
            </div>
        </div>
    </div>

    <!-- Settings Modal -->
    <div class="settings-modal" id="settingsModal">
        <div class="settings-content">
            <div class="settings-tabs">
                <div class="tab active" onclick="showTab('profile', this)">Профиль</div>
                <div class="tab" onclick="showTab('game', this)">Игра</div>
                <div class="tab" onclick="showTab('about', this)">О клиенте</div>
            </div>

            <div id="profile-tab" class="tab-content active">
                <div class="form-group">
                    <label class="form-label">Никнейм</label>
                    <input type="text" class="form-input" id="settingsUsername" value="{USERNAME}" placeholder="Введите никнейм">
                    <div class="error-message" id="settingsUsernameError"></div>
                </div>
                <div class="form-group">
                    <label class="form-label">Offline UUID</label>
                    <div class="mono-box" id="uuidDisplay">{UUID_DISPLAY}</div>
                </div>
                <button class="modal-btn save" onclick="pywebview.api.save_settings_username()" style="float: right;">Сохранить</button>
            </div>

            <div id="game-tab" class="tab-content">
                <div class="form-group">
                    <label class="form-label">Выделение ОЗУ (Всего в системе: {TOTAL_RAM} ГБ)</label>
                    <input type="range" class="ram-slider" id="ramSlider" min="2048" max="{MAX_RAM}" value="{RAM_MB}" step="1024">
                    <div class="ram-value" id="ramValue">{RAM_DISPLAY}</div>
                    <div class="preset-buttons">
                        <button class="preset-btn" onclick="setRamPreset(4096)">4 ГБ</button>
                        <button class="preset-btn" onclick="setRamPreset(6144)">6 ГБ</button>
                        <button class="preset-btn" onclick="setRamPreset(8192)">8 ГБ</button>
                        <button class="preset-btn" onclick="setRamPreset(12288)">12 ГБ</button>
                    </div>
                </div>
                <button class="modal-btn save" onclick="pywebview.api.save_settings_ram()" style="float: right;">Применить ОЗУ</button>
            </div>

            <div id="about-tab" class="tab-content">
                <h3 style="margin: 0 0 6px;">MON SERVER Launcher</h3>
                <p style="margin: 2px 0;"><b>Версия клиента:</b> {VERSION}</p>
                <p style="margin: 2px 0 12px;"><b>Движок запуска:</b> NeoForge + Adoptium Java 21</p>
                <label class="form-label">Директория клиента</label>
                <div class="mono-box" style="margin-bottom: 12px;">{MINECRAFT_DIR}</div>
                <button class="modal-btn cancel" onclick="pywebview.api.open_folder('{MINECRAFT_DIR}')" style="float: left;">Открыть папку</button>
            </div>

            <div style="clear: both; text-align: right; margin-top: 24px;">
                <button class="modal-btn cancel" onclick="pywebview.api.close_settings()">Закрыть</button>
            </div>
        </div>
    </div>

    <script>
        function showTab(tabName, el) {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
            el.classList.add('active');
            document.getElementById(tabName + '-tab').classList.add('active');
        }

        var ramSlider = document.getElementById('ramSlider');
        var ramValue = document.getElementById('ramValue');
        ramSlider.addEventListener('input', function() {
            var val = parseInt(this.value);
            var gb = val / 1024;
            ramValue.textContent = (gb === Math.floor(gb) ? gb : gb.toFixed(1)) + ' ГБ (' + val + ' МБ)';
        });

        function setRamPreset(mb) {
            ramSlider.value = mb;
            var gb = mb / 1024;
            ramValue.textContent = (gb === Math.floor(gb) ? gb : gb.toFixed(1)) + ' ГБ (' + mb + ' МБ)';
        }

        document.getElementById('usernameInput').addEventListener('keypress', function(e) {
            if (e.key === 'Enter') pywebview.api.save_username();
        });

        function showStatus(message, percent) {
            var overlay = document.getElementById('statusOverlay');
            document.getElementById('statusText').textContent = message;
            if (percent !== undefined && percent !== null) {
                document.getElementById('progressFill').style.width = percent + '%';
            }
            overlay.style.display = 'flex';
        }

        function hideStatus() {
            document.getElementById('statusOverlay').style.display = 'none';
        }

        function disablePlayButton(msg) {
            var btn = document.getElementById('playBtn');
            btn.disabled = true;
            btn.textContent = msg || 'Запуск...';
        }

        function enablePlayButton() {
            var btn = document.getElementById('playBtn');
            btn.disabled = false;
            btn.textContent = '▶ ИГРАТЬ';
        }
    </script>
</body>
</html>
"""


# =============================================================================
# MAIN APP CLASS
# =============================================================================

class LauncherApp:
    def __init__(self):
        self.window = None
        self.username = get_username()
        self.ram_mb = load_config().get("ram_mb", 4096)

    def get_html(self):
        total_ram = get_system_ram_gb()
        max_ram = min(total_ram * 1024, 65536)
        ram_gb = self.ram_mb / 1024
        ram_display = f"{int(ram_gb)} ГБ ({self.ram_mb} МБ)" if ram_gb == int(ram_gb) else f"{ram_gb:.1f} ГБ ({self.ram_mb} МБ)"
        uuid_val = generate_offline_uuid(self.username) if self.username else "Введите никнейм"

        return (
            HTML_TEMPLATE
            .replace("{USERNAME}", self.username or "")
            .replace("{RAM_STR}", get_allocated_ram_str())
            .replace("{RAM_MB}", str(self.ram_mb))
            .replace("{RAM_DISPLAY}", ram_display)
            .replace("{TOTAL_RAM}", str(total_ram))
            .replace("{MAX_RAM}", str(max_ram))
            .replace("{UUID_DISPLAY}", uuid_val)
            .replace("{VERSION}", VERSION)
            .replace("{MINECRAFT_DIR}", MINECRAFT_DIR.replace("\\", "/"))
            .replace("{DISCORD_URL}", DISCORD_URL)
            .replace("{TELEGRAM_DEV_URL}", TELEGRAM_DEV_URL)
        )

    def start(self):
        self.window = webview.create_window(
            'MON SERVER Launcher',
            html=self.get_html(),
            js_api=self,
            width=880,
            height=580,
            min_size=(800, 500),
            resizable=True
        )

        def on_loaded():
            if not is_valid_username(self.username):
                self.window.evaluate_js("document.getElementById('usernameModal').style.display = 'flex';")

        self.window.events.loaded += on_loaded

        # Force Edge WebView2 (Chromium) for optimal speed and modern rendering
        webview.start(gui='edgechromium', debug=False)

    # JavaScript APIs
    def play_game(self):
        def worker():
            try:
                self.window.evaluate_js("disablePlayButton('Подготовка...')")
                self.window.evaluate_js("showStatus('Запуск игры...', 0)")

                def progress_cb(data):
                    action = data.get("action")
                    msg = data.get("message", "").replace("'", "\\'")
                    if action == "status":
                        self.window.evaluate_js(f"showStatus('{msg}', null)")
                    elif action == "progress":
                        p = data.get("percent", 0)
                        self.window.evaluate_js(f"showStatus('{msg}', {p})")
                    elif action == "error":
                        self.window.evaluate_js(f"alert('Ошибка: {msg}')")
                        self.window.evaluate_js("hideStatus()")
                        self.window.evaluate_js("enablePlayButton()")

                success = launch_game_window(self.window, progress_cb)
                if not success:
                    self.window.evaluate_js("hideStatus()")
                    self.window.evaluate_js("enablePlayButton()")
            except Exception as e:
                err = str(e).replace("'", "\\'")
                self.window.evaluate_js(f"alert('Критическая ошибка: {err}')")
                self.window.evaluate_js("hideStatus()")
                self.window.evaluate_js("enablePlayButton()")

        threading.Thread(target=worker, daemon=True).start()

    def open_settings(self):
        self.window.evaluate_js("document.getElementById('settingsModal').style.display = 'flex';")

    def close_settings(self):
        self.window.evaluate_js("document.getElementById('settingsModal').style.display = 'none';")

    def save_username(self):
        nick = self.window.evaluate_js("document.getElementById('usernameInput').value")
        if not is_valid_username(nick):
            self.window.evaluate_js("document.getElementById('usernameError').textContent = 'Введите корректный никнейм';")
            return

        cfg = load_config()
        cfg["username"] = nick.strip()
        save_config(cfg)
        self.username = nick.strip()

        self.window.evaluate_js("document.getElementById('usernameModal').style.display = 'none';")
        self.window.evaluate_js(f"document.getElementById('infoText').textContent = 'Никнейм: {self.username} · ОЗУ: {get_allocated_ram_str()}';")

    def close_username_modal(self):
        if not is_valid_username(self.username):
            self.window.evaluate_js("alert('Без никнейма вход в игру невозможен');")
            return
        self.window.evaluate_js("document.getElementById('usernameModal').style.display = 'none';")

    def save_settings_username(self):
        nick = self.window.evaluate_js("document.getElementById('settingsUsername').value")
        if not is_valid_username(nick):
            self.window.evaluate_js("document.getElementById('settingsUsernameError').textContent = 'Некорректный ник';")
            return

        cfg = load_config()
        cfg["username"] = nick.strip()
        save_config(cfg)
        self.username = nick.strip()

        uuid_val = generate_offline_uuid(self.username)
        self.window.evaluate_js(f"document.getElementById('uuidDisplay').textContent = '{uuid_val}';")
        self.window.evaluate_js(f"document.getElementById('infoText').textContent = 'Никнейм: {self.username} · ОЗУ: {get_allocated_ram_str()}';")
        self.window.evaluate_js("alert('Никнейм сохранен!');")

    def save_settings_ram(self):
        mb = int(self.window.evaluate_js("document.getElementById('ramSlider').value"))
        cfg = load_config()
        cfg["ram_mb"] = mb
        save_config(cfg)
        self.ram_mb = mb

        display_name = self.username if self.username else "не установлен"
        self.window.evaluate_js(f"document.getElementById('infoText').textContent = 'Никнейм: {display_name} · ОЗУ: {get_allocated_ram_str()}';")
        self.window.evaluate_js("alert('Настройки ОЗУ сохранены!');")

    def open_folder(self, path):
        try:
            os.startfile(os.path.normpath(path))
        except Exception:
            pass


if __name__ == "__main__":
    app = LauncherApp()
    app.start()
