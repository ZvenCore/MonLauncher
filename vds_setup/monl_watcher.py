#!/usr/bin/env python3
"""
MON SERVER - Automatic Modlist Generator & Watcher for VDS
Scans /monl/mods for *.jar files, computes SHA1 & sizes,
and automatically writes /monl/list/modlist.json whenever files are added/removed/updated.
"""

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
        while chunk := f.read(131072): # 128KB chunks
            sha1.update(chunk)
    return sha1.hexdigest()

def scan_and_generate(cache):
    MODS_DIR.mkdir(parents=True, exist_ok=True)
    LIST_DIR.mkdir(parents=True, exist_ok=True)

    jar_files = sorted([f for f in MODS_DIR.iterdir() if f.is_file() and f.name.endswith(".jar")])
    current_names = {f.name for f in jar_files}

    # Evict deleted files from cache
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

            # Check cache to avoid re-hashing every cycle
            if f.name in cache and cache[f.name]["mtime"] == mtime and cache[f.name]["size"] == size:
                sha1 = cache[f.name]["sha1"]
            else:
                sha1 = calculate_sha1(f)
                cache[f.name] = {
                    "mtime": mtime,
                    "size": size,
                    "sha1": sha1
                }
                changed = True

            mods_list.append({
                "name": f.name,
                "size": size,
                "sha1": sha1
            })
        except Exception as e:
            print(f"[ERR] Ошибка чтения {f.name}: {e}")

    # Check client.zip info
    client_zip_rel = "/monl/client.zip" if CLIENT_ZIP.exists() else None

    result = {
        "updated_at": int(time.time()),
        "client_zip": client_zip_rel,
        "mods_base_url": "/monl/mods/",
        "total_mods": len(mods_list),
        "mods": mods_list
    }

    # Write to temp file then atomic rename to prevent half-read race conditions
    tmp_file = LIST_DIR / "modlist.json.tmp"
    with open(tmp_file, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    tmp_file.replace(OUTPUT_FILE)

    return len(mods_list), changed

def main():
    print("==================================================")
    print(" MON SERVER - Автогенератор списка модов (VDS)")
    print(f" Папка модов:      {MODS_DIR}")
    print(f" Выходной файл:    {OUTPUT_FILE}")
    print("==================================================")

    cache = {}
    count, _ = scan_and_generate(cache)
    print(f"[OK] Первичное сканирование: найдено {count} модов. modlist.json обновлен.")

    if "--once" in sys.argv:
        return

    print("[INFO] Режим слежения активен (проверка каждые 2 секунды)...")
    print("При изменении, добавлении или удалении .jar файлов список обновится автоматически.")
    print("Нажмите Ctrl+C для остановки.")

    while True:
        try:
            time.sleep(2)
            count, changed = scan_and_generate(cache)
            if changed:
                now_str = time.strftime('%Y-%m-%d %H:%M:%S')
                print(f"[{now_str}] Замечены изменения! modlist.json обновлен (Всего модов: {count})")
        except KeyboardInterrupt:
            print("\nОстановлено.")
            break
        except Exception as e:
            print(f"[ERR] Ошибка цикла: {e}")
            time.sleep(3)

if __name__ == "__main__":
    main()
