#!/usr/bin/env bash
# 从 Deck 拉取编译 helper 所需的两个引用程序集（体积大 / 不入库）。
#   ./fetch.sh         拉取缺失的文件
#   ./fetch.sh --force 强制重新拉取（游戏更新后用）
set -euo pipefail

DECK="${DECK:-deck@192.168.1.28}"
GAME_DIR="${GAME_DIR:-/home/deck/.local/share/Steam/steamapps/common/Sky Force Reloaded}"
REMOTE_MANAGED="$GAME_DIR/Sky Force Reloaded_Data/Managed"

cd "$(dirname "$0")"

FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

mkdir -p assets

fetch() {
  local remote="$1" local_path="$2"
  if [ -f "$local_path" ] && [ "$FORCE" = 0 ]; then
    echo "   跳过 $local_path（已存在）"
    return
  fi
  rsync -a "$DECK:$remote" "$local_path"
  echo "   $local_path"
}

echo "→ 拉取引用程序集…"
fetch "$REMOTE_MANAGED/UnityEngine.dll" assets/UnityEngine.dll
fetch "$REMOTE_MANAGED/Assembly-CSharp.dll" assets/Assembly-CSharp.dll.orig

echo "✅ 完成"
