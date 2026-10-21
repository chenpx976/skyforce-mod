#!/usr/bin/env bash
# 把 config.json 里的几率配置烧进 Assembly-CSharp.dll，并推送到 Steam Deck。
#
#   ./apply.sh              # 用 assets/Assembly-CSharp.dll.orig 作基线打补丁并上传
#   ./apply.sh --refresh    # 先从 Deck 拉当前 dll 作基线（游戏更新后需要）
#   ./apply.sh --dry-run    # 只在本地生成补丁，不上传
set -euo pipefail

DECK="${DECK:-deck@192.168.1.28}"
GAME_DIR="${GAME_DIR:-/home/deck/.local/share/Steam/steamapps/common/Sky Force Reloaded}"
REMOTE_DLL="$GAME_DIR/Sky Force Reloaded_Data/Managed/Assembly-CSharp.dll"
REMOTE_HOME="${REMOTE_HOME:-/home/deck/.skyforce-mod}"
REMOTE_BACKUP="$REMOTE_HOME/backup"

cd "$(dirname "$0")"

BASELINE="assets/Assembly-CSharp.dll.orig"
PATCHED="build/Assembly-CSharp.dll"
CONFIG="config.json"
REFRESH=0
DRY_RUN=0
CHECK=0

for arg in "$@"; do
  case "$arg" in
    --refresh) REFRESH=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --check) CHECK=1 ;;
    *) echo "未知参数: $arg" >&2; exit 1 ;;
  esac
done

[ -f "$CONFIG" ] || { echo "缺少 $CONFIG" >&2; exit 1; }

# ── 检查 Deck 上的 dll 是不是当前配置的补丁 ──────────────────────
if [ "$CHECK" = 1 ]; then
  remote_hash=$(ssh "$DECK" "sha256sum '$REMOTE_DLL' 2>/dev/null | cut -d' ' -f1" || true)
  orig_hash=$(shasum -a 256 "$BASELINE" 2>/dev/null | cut -d' ' -f1 || true)
  if [ -f "$PATCHED" ]; then
    patched_hash=$(shasum -a 256 "$PATCHED" | cut -d' ' -f1)
  else
    patched_hash=""
  fi

  echo "Deck : $remote_hash"
  [ -n "$patched_hash" ] && echo "本地 : $patched_hash  (build/)"
  echo "基线 : $orig_hash  (${BASELINE})"
  echo

  if [ -z "$remote_hash" ]; then
    echo "❌ 读不到 Deck 上的 dll"
    exit 1
  elif [ "$remote_hash" = "$patched_hash" ]; then
    echo "✅ 当前是这份配置的 mod。"
  elif [ "$remote_hash" = "$orig_hash" ]; then
    echo "⚠  已被还原成原版（Steam 验证 / 游戏更新）。重新跑 ./apply.sh 即可。"
  else
    echo "⚠  是别的版本（游戏更新了？）。跑 ./apply.sh --refresh 再 ./apply.sh。"
  fi
  exit 0
fi

if [ "$REFRESH" = 1 ]; then
  echo "→ 从 Deck 拉取当前 dll 作为基线…"
  mkdir -p assets
  ssh "$DECK" "cat '$REMOTE_DLL'" > "$BASELINE"
fi

[ -f "$BASELINE" ] || { echo "缺少基线 $BASELINE（先跑 ./apply.sh --refresh）" >&2; exit 1; }

# 1. 备份 Deck 上最原始的 dll（只备份一次，供 restore.sh 还原）
if [ "$DRY_RUN" = 0 ]; then
  echo "→ 确认备份…"
  ssh "$DECK" "mkdir -p '$REMOTE_BACKUP' && \
    if [ ! -f '$REMOTE_BACKUP/Assembly-CSharp.dll.orig' ]; then \
      cp '$REMOTE_DLL' '$REMOTE_BACKUP/Assembly-CSharp.dll.orig' && echo '   已备份原始 dll'; \
    else echo '   备份已存在，跳过'; fi"
fi

# 2. 构建 patcher
echo "→ 构建 patcher…"
dotnet build tools/SkyForcePatcher/SkyForcePatcher.csproj -c Release -v q --nologo > /dev/null

# 3. 打补丁
echo "→ 应用补丁…"
mkdir -p build
dotnet tools/SkyForcePatcher/bin/Release/net7.0/skyforce-patcher.dll \
  --input "$BASELINE" --output "$PATCHED" --config "$CONFIG"

if [ "$DRY_RUN" = 1 ]; then
  echo "→ dry-run：补丁文件在 ${PATCHED}，未上传"
  exit 0
fi

# 4. 检查游戏是否在运行（Thermal: proton 进程名带 exe）
if ssh "$DECK" "pgrep -f 'Sky Force Reloaded.exe' > /dev/null"; then
  echo "⚠ 检测到游戏正在运行，请先退出游戏再重试。" >&2
  exit 1
fi

# 5. 上传 dll + 配置留档
echo "→ 上传到 Deck…"
ssh "$DECK" "cat > '$REMOTE_DLL'" < "$PATCHED"
ssh "$DECK" "mkdir -p '$REMOTE_HOME' && cat > '$REMOTE_HOME/config.last.json'" < "$CONFIG"

echo
echo "✅ 完成。启动游戏即可生效。"
echo "   还原: ./restore.sh"
