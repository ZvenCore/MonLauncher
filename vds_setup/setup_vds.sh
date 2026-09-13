#!/bin/bash
# ==============================================================================
# MON SERVER - Быстрая настройка /monl/ на VDS (Debian / Ubuntu)
# Запуск от root: bash setup_vds.sh
# ==============================================================================

set -e

echo "=== [1/4] Создание папок /monl ==="
mkdir -p /monl/mods
mkdir -p /monl/list
chmod -R 755 /monl

echo "=== [2/4] Установка Python-скрипта слежения ==="
cat << 'EOF' > /monl/monl_watcher.py
#!/usr/bin/env python3
import os
import sys
import json
import time
import hashlib
from pathlib import Path

BASE_DIR = Path("/monl")
MODS_DIR = BASE_DIR / "mods"
LIST_DIR = BASE_DIR / "list"
OUTPUT_FILE = LIST_DIR / "modlist.json"
CLIENT_ZIP = BASE_DIR / "client.zip"

def calculate_sha1(filepath):
    sha1 = hashlib.sha1()
    with open(filepath, "rb") as f:
        while chunk := f.read(131072):
            sha1.update(chunk)
    return sha1.hexdigest()

def scan_and_generate(cache):
    MODS_DIR.mkdir(parents=True, exist_ok=True)
    LIST_DIR.mkdir(parents=True, exist_ok=True)

    jar_files = sorted([f for f in MODS_DIR.iterdir() if f.is_file() and f.name.endswith(".jar")])
    current_names = {f.name for f in jar_files}

    for name in list(cache.keys()):
        if name not in current_names:
            del cache[name]

    changed = False
    mods_list = []

    for f in jar_files:
        try:
            stat = f.stat()
            mtime = stat.st_mtime
            size = stat.st_size

            if f.name in cache and cache[f.name]["mtime"] == mtime and cache[f.name]["size"] == size:
                sha1 = cache[f.name]["sha1"]
            else:
                sha1 = calculate_sha1(f)
                cache[f.name] = {"mtime": mtime, "size": size, "sha1": sha1}
                changed = True

            mods_list.append({"name": f.name, "size": size, "sha1": sha1})
        except Exception as e:
            print(f"[ERR] Ошибка чтения {f.name}: {e}")

    client_zip_rel = "/monl/client.zip" if CLIENT_ZIP.exists() else None

    result = {
        "updated_at": int(time.time()),
        "client_zip": client_zip_rel,
        "mods_base_url": "/monl/mods/",
        "total_mods": len(mods_list),
        "mods": mods_list
    }

    tmp_file = LIST_DIR / "modlist.json.tmp"
    with open(tmp_file, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    tmp_file.replace(OUTPUT_FILE)

    return len(mods_list), changed

def main():
    print(f"[OK] Папка модов: {MODS_DIR}")
    print(f"[OK] Выходной файл: {OUTPUT_FILE}")
    cache = {}
    count, _ = scan_and_generate(cache)
    print(f"[OK] Первичное сканирование: {count} модов.")

    if "--once" in sys.argv:
        return

    print("[INFO] Авто-обновление активно (проверка каждые 2 сек)...")
    while True:
        try:
            time.sleep(2)
            count, changed = scan_and_generate(cache)
            if changed:
                now_str = time.strftime('%Y-%m-%d %H:%M:%S')
                print(f"[{now_str}] Список обновлен! Всего модов: {count}")
        except KeyboardInterrupt:
            break
        except Exception as e:
            print(f"[ERR] {e}")
            time.sleep(3)

if __name__ == "__main__":
    main()
EOF

chmod +x /monl/monl_watcher.py

echo "=== [3/4] Создание и включение systemd сервиса monl-watcher ==="
cat << 'EOF' > /etc/systemd/system/monl-watcher.service
[Unit]
Description=MON SERVER Modlist Auto-Updater & Watcher
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/monl
ExecStart=/usr/bin/python3 /monl/monl_watcher.py
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now monl-watcher

echo "=== [4/4] Готово! Сервис monl-watcher запущен ==="
systemctl status monl-watcher --no-pager
echo ""
echo "=================================================================="
echo "ВАЖНО: Добавьте в конфигурацию Nginx для site.moncraft.space:"
echo ""
echo "    location /monl/ {"
echo "        alias /monl/;"
echo "        autoindex off;"
echo "        add_header Access-Control-Allow-Origin *;"
echo "        add_header Cache-Control 'no-cache, must-revalidate';"
echo "    }"
echo ""
echo "После добавления выполните: nginx -t && systemctl reload nginx"
echo "=================================================================="
