#!/usr/bin/env bash
# apply-yt-cipher-patch.sh
#
# Применяет локальный патч к submodule yt-cipher, чтобы YouTube не блокировал
# скачивание player.js через Cloudflare (исправляет "All clients failed to load the item").
#
# Запускать из корня репо после:
#   git submodule update --init --recursive
#
# Безопасно запускать повторно — если патч уже применён, скрипт завершится
# с соответствующим сообщением и не сделает дубль.
#
# Использование:
#   bash tools/apply-yt-cipher-patch.sh
#
# Аналог tools/apply-yt-cipher-patch.ps1 для Windows.

set -euo pipefail

# Резолвим корень репо относительно самого скрипта (а не cwd).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SUBMODULE_PATH="$REPO_ROOT/yt-cipher"
PATCH_FILE="$SCRIPT_DIR/patches/yt-cipher-cloudflare-ua.patch"
MARKER_FILE=".yt-cipher-patch-applied"
TARGET_FILE="src/playerCache.ts"

echo "[apply-yt-cipher-patch] Repo root: $REPO_ROOT"
echo "[apply-yt-cipher-patch] Submodule:  $SUBMODULE_PATH"
echo "[apply-yt-cipher-patch] Patch file: $PATCH_FILE"

if [[ ! -f "$PATCH_FILE" ]]; then
    echo "[apply-yt-cipher-patch] ОШИБКА: файл патча не найден: $PATCH_FILE" >&2
    exit 1
fi

if [[ ! -d "$SUBMODULE_PATH" ]]; then
    echo "[apply-yt-cipher-patch] ОШИБКА: submodule yt-cipher не найден: $SUBMODULE_PATH" >&2
    echo "[apply-yt-cipher-patch] Запустите: git submodule update --init --recursive" >&2
    exit 1
fi

# Маркер лежит в корне репо (а не в submodule).
MARKER_PATH="$REPO_ROOT/$MARKER_FILE"

if [[ -f "$MARKER_PATH" ]]; then
    echo "[apply-yt-cipher-patch] Патч уже применён (маркер $MARKER_FILE существует). Пропускаю."
    exit 0
fi

if [[ ! -f "$SUBMODULE_PATH/$TARGET_FILE" ]]; then
    echo "[apply-yt-cipher-patch] ОШИБКА: файл $TARGET_FILE не найден в submodule." >&2
    echo "[apply-yt-cipher-patch] Возможно версия yt-cipher изменилась и патч нужно обновить." >&2
    exit 1
fi

echo "[apply-yt-cipher-patch] Применяю патч через git apply..."
cd "$SUBMODULE_PATH"

# --check сначала: если патч уже наложен, --check вернёт ошибку и мы выйдем без изменений.
if ! git apply --check "$PATCH_FILE" 2>/dev/null; then
    echo "[apply-yt-cipher-patch] Патч уже применён или несовместим. Создаю маркер и выхожу."
    touch "$MARKER_PATH"
    cd "$REPO_ROOT"
    exit 0
fi

if ! git apply "$PATCH_FILE"; then
    echo "[apply-yt-cipher-patch] ОШИБКА: git apply не смог применить патч." >&2
    cd "$REPO_ROOT"
    exit 1
fi

# Маркер в корне репо — чтобы не применить дважды.
touch "$MARKER_PATH"

# Добавляем маркер в .gitignore если его там ещё нет.
GITIGNORE="$REPO_ROOT/.gitignore"
if [[ -f "$GITIGNORE" ]] && ! grep -qF "$MARKER_FILE" "$GITIGNORE"; then
    printf '\n%s\n' "$MARKER_FILE" >> "$GITIGNORE"
fi

cd "$REPO_ROOT"

echo "[apply-yt-cipher-patch] Патч успешно применён к $TARGET_FILE."
echo "[apply-yt-cipher-patch] Запустите yt-cipher:"
echo "[apply-yt-cipher-patch]   cd yt-cipher && deno run --allow-net --allow-read --allow-write --allow-env server.ts"