#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MON SERVER LAUNCHER - VDS Auto-Update Script
Автоматически обрабатывает new.exe в /monl, обновляет MonLauncher.exe и launcher_version.json
"""

import os
import sys
import json
import time
import shutil

MONL_DIR = '/monl'
NEW_EXE = os.path.join(MONL_DIR, 'new.exe')
TARGET_EXE = os.path.join(MONL_DIR, 'MonLauncher.exe')
MANIFEST = os.path.join(MONL_DIR, 'launcher_version.json')
URL = 'https://site.moncraft.space/monl/MonLauncher.exe'


def get_current_info():
    if os.path.exists(MANIFEST):
        try:
            with open(MANIFEST, 'r', encoding='utf-8') as f:
                return json.load(f)
        except Exception:
            pass
    return {
        'version': '1.0.0',
        'url': URL,
        'changelog': 'Обновление лаунчера',
        'mandatory': False,
    }


def bump_version(ver_str):
    parts = ver_str.strip().split('.')
    try:
        parts[-1] = str(int(parts[-1]) + 1)
        return '.'.join(parts)
    except Exception:
        return ver_str + '.1'


def process_update(custom_version=None, custom_changelog=None):
    if not os.path.exists(NEW_EXE):
        print(f'[!] Файл {NEW_EXE} не найден в {MONL_DIR}.')
        print('    Загрузите new.exe в эту папку и запустите скрипт снова.')
        return False

    print(f'[*] Проверка завершения загрузки {NEW_EXE}...')
    last_size = -1
    for _ in range(15):
        try:
            current_size = os.path.getsize(NEW_EXE)
            if current_size == last_size and current_size > 1024 * 1024:
                break
            last_size = current_size
        except Exception:
            pass
        time.sleep(1)

    cur_info = get_current_info()
    cur_ver = cur_info.get('version', '1.0.0')

    if custom_version and custom_version.strip():
        new_ver = custom_version.strip().lstrip('v')
    else:
        new_ver = bump_version(cur_ver)

    if custom_changelog and custom_changelog.strip():
        changelog = custom_changelog.strip()
    else:
        changelog = f'• Обновление лаунчера v{new_ver}\n• Оптимизация и повышение стабильности'

    print(f'[*] Обновление лаунчера: v{cur_ver} -> v{new_ver}')

    shutil.move(NEW_EXE, TARGET_EXE)
    os.chmod(TARGET_EXE, 0o644)
    print(f'[+] Файл сохранен как {TARGET_EXE}')

    data = {
        'version': new_ver,
        'url': URL,
        'changelog': changelog,
        'mandatory': False,
    }

    with open(MANIFEST, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=4)
    os.chmod(MANIFEST, 0o644)
    print(f'[+] Манифест обновлен: {MANIFEST}')

    print('\n==========================================')
    print(f'[OK] Обновление v{new_ver} успешно опубликовано!')
    print(f'     URL скачивания: {URL}')
    print('==========================================\n')
    return True


def watch_mode():
    print('==========================================')
    print(f'[*] Фоновый наблюдатель запущен в {MONL_DIR}')
    print('    Просто закиньте new.exe в папку через SFTP / WinSCP.')
    print('    Скрипт сам переименует его и обновит версию!')
    print('==========================================\n')

    while True:
        try:
            if os.path.exists(NEW_EXE):
                print(f'\n[!] Обнаружен файл {NEW_EXE}!')
                time.sleep(2)
                process_update()
        except Exception as e:
            print(f'[ERROR] {e}')
        time.sleep(2)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] in ['--watch', '-w', 'watch']:
        watch_mode()
    else:
        ver = sys.argv[1] if len(sys.argv) > 1 else None
        log = sys.argv[2] if len(sys.argv) > 2 else None
        process_update(ver, log)
