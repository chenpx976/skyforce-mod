#!/usr/bin/env bash
# 还原 Assembly-CSharp.dll 到打补丁之前的状态。
set -euo pipefail

DECK="${DECK:-deck@192.168.1.28}"
GAME_DIR="${GAME_DIR:-/home/deck/.local/share/Steam/steamapps/common/Sky Force Reloaded}"
REMOTE_MANAGED="$GAME_DIR/Sky Force Reloaded_Data/Managed"
REMOTE_DLL="$REMOTE_MANAGED/Assembly-CSharp.dll"
REMOTE_HELPER="$REMOTE_MANAGED/SkyForceTweaks.dll"
REMOTE_HOME="${REMOTE_HOME:-/home/deck/.skyforce-mod}"
REMOTE_BACKUP="$REMOTE_HOME/backup"

if ssh "$DECK" "pgrep -f '[S]ky Force Reloaded.exe' > /dev/null"; then
  echo "⚠ 检测到游戏正在运行，请先退出游戏。" >&2
  exit 1
fi

if ! ssh "$DECK" "[ -f '$REMOTE_BACKUP/Assembly-CSharp.dll.orig' ]"; then
  echo "找不到备份 $REMOTE_BACKUP/Assembly-CSharp.dll.orig" >&2
  echo "可改用 Steam『验证游戏文件完整性』。" >&2
  exit 1
fi

ssh "$DECK" "cp '$REMOTE_BACKUP/Assembly-CSharp.dll.orig' '$REMOTE_DLL' && rm -f '$REMOTE_HELPER'"
echo "✅ 已还原原始 Assembly-CSharp.dll，并移除 SkyForceTweaks.dll。"
