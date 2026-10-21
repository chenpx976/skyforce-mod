# Sky Force Reloaded 几率 Mod

对 Steam 版 *Sky Force Reloaded*（AppID `667600`）的 `Assembly-CSharp.dll` 做**静态 IL 注入**，
把「掉落概率 / 稀有度 / 幸运补偿 / 星星倍率」这些原本写死在场景资源里的数值变成可调的。

不改存档、不装 BepInEx、不引入任何运行时依赖——补丁只是往方法体里插几条算术指令。

## 为什么重写

游戏目录里原本有 `Sky Force Reloaded_Data/Mods/CardDropRateMultiplier/`，但那个版本**完全无效**：

- 它的逻辑被注入在 `TCard.Start()` 里；
- `TCard` 是挂在 prefab 上的组件，游戏从不 `Instantiate` 它（卡牌数据通过 `CardsManager._AllCards` 数组直接读取），
  所以 `Awake`/`Start` 永远不会执行；
- 另外它只改「每张卡各自的权重」，没有改「箱子出卡的总概率」，即使执行了效果也几乎不可感知。

这个版本把补丁点挪到真正每局都会执行的地方（`CardsManager.Awake` / `CheckIfCanSpawnCard` 等），
并顺手把旧的 `TCard.Start` 清空，避免它残留的 `PlayerPrefs` 写入干扰。

## 用法

```bash
cd skyforce-mod
vim config.json      # 调参数
./apply.sh           # 打补丁 + 推送到 Deck（会先备份原 dll）
# 然后重启游戏
```

- 游戏更新过 / 在 Steam 里验证过文件完整性后，先 `./apply.sh --refresh` 重新取基线，再 `./apply.sh`。
- `./apply.sh --dry-run` 只在本地生成 `build/Assembly-CSharp.dll`，不上传。
- `./apply.sh --check` 对比 Deck 上的 dll 是否还是当前这份补丁。
- `./restore.sh` 还原原始 dll（备份在 Deck 的 `~/.skyforce-mod/backup/`）。

只在「游戏已退出」时才能替换 dll，脚本会检查。

### 不需要启动项，也不需要关 Steam 的校验

- Steam **不会**在启动游戏时校验/还原游戏文件；实测启动再退出后 dll 的 sha256 不变。
- 不需要 `WINEDLLOVERRIDES=...` 之类的启动选项——这里没有 DLL 代理注入，游戏本来就会加载自己的 `Assembly-CSharp.dll`。
- 只有两种情况会覆盖补丁：手动「验证游戏文件完整性」、或 Valve 真的推送了新 build（`buildid` 变化）。后者跑一次 `./apply.sh --refresh && ./apply.sh` 即可。
- Steam 下载页里那种「验证/更新中」如果只有几 MB 且进度很慢，通常是**着色器预缓存**（`Shader update`），与游戏文件无关，可以放心取消。

## 右上角 HUD

补丁会给 `CardsManager` 追加一个 `OnGUI` 消息，游戏右上角常显当前生效的参数（单行、右对齐、贴右下角）：

```
card x6  skin x5  luck 0.55  pity 2/3  stars x2
```

- 不用新建类型、不用 `AddComponent`，只是给常驻单例加一个 Unity 消息方法。
- 文本**只能用 ASCII**：Unity 内置 IMGUI 字体（Arial）没有中文字形，中文会变方块，补丁器会直接报错阻止。
- 想关掉：`"hud": { "enabled": false }`；想自定义文案：`"hud": { "label": "my text" }`（`\n` 换行）。

## 手感预设

```bash
cp presets/balanced.json config.json && ./apply.sh
```

| 预设 | 定位 | 掉率 | 保底 | 星星 |
|---|---|---|---|---|
| `gentle.json` | 约原版 2 倍，保留「刷」的价值 | 2x | 15 局 | 1.25x |
| `balanced.json` | **推荐**：2~4 局一张，节奏稳 | 3x | 4 局 | 1.5x |
| `sprint.json` | 专门补卡：1~2 局一张 | 6x | 2 局 | 2x |

调整思路：

- **掉率倍率**决定「每局有多少期待」，**保底局数**决定「最坏情况有多差」。后者玩家感知最强，优先调它。
- **品质偏向**（`luckBias`）不提高总掉率，只把候选池门槛压低，让稀有卡更容易进入抽选。
- **星星倍率**是唯一会明显改变难度曲线的开关（星星 = 升级资源），建议不超过 2x。
- 卡片一旦满级，掉落会自动转给其他卡，不存在「白给」；但掉率太高会让限时 buff 频繁刷新，关卡整体变软。

## 看当前进度 / 存档

```bash
python3 research/decode_save.py research/save/SaveGame.dat | grep CCard
```

`Save/SaveGame.dat` 是 UTF-16LE JSON + 固定 XOR 键值（key 见 `SecurePrefs.cs`），
脚本已封装成 `research/decode_save.py`，可直接看卡片、星星、关卡进度。

## 可调参数

| 配置项 | 含义 | 原版值 |
|---|---|---|
| `card.dropMultiplier` / `skin.dropMultiplier` | 箱子**开出**卡片 / 皮肤的总概率倍率 | 1（各箱子自身 `_CardProbability`，约 2%~10%） |
| `card.luckBias` / `skin.luckBias` | 运气阈值缩放，`<1` 时高稀有度更常见 | 1 |
| `luckFromBadLuck` | 一次没掉落后追加的运气 | 0.1 |
| `maxLuckFromBadLuck` | 上述追加的上限 | 0.5 |
| `luckFromEmptyRuns` | 每打完一局仍没掉落的补偿 | 0.005 |
| `maxLuckFromEmptyRuns` | 上述补偿的上限 | 0.6 |
| `maxRunsWithoutDrop` | 连续多少局没掉落就强制必出 | 100 |
| `stars.multiplier` | 敌人 / 星星发生器掉落的星星数量倍率 | 1 |

## 补丁点

| 目标 | 方法 | 注入 |
|---|---|---|
| 卡片总掉率 | `CardsManager.CheckIfCanSpawnCard` | `fMinProb = Clamp01(fMinProb * mult)` |
| 皮肤总掉率 | `SkinsManager.CheckIfCanSpawnSkin` | 同上 |
| 卡片品质 | `CardsManager.GetRandomCard(EGameDifficulty, float, bool)` | `fLuck = Clamp01(fLuck * bias)` |
| 皮肤品质 | `SkinsManager.GetRandomSkin(EGameDifficulty, float, bool)` | 同上 |
| 幸运参数 | `CardsManager.Awake` / `SkinsManager.Awake` | 末尾直接 `stfld` 覆盖 5 个幸运字段 |
| 星星倍率 | `Enemy.GetStartNo` / `StarsGenerator.GetStartNo` | 每个 `Mathf.FloorToInt` 前 `* mult` |
| 旧 mod 停用 | `TCard.Start` | 方法体替换为空 `ret` |

补丁靠**指令模式匹配**定位，所以对原版 dll 和已被旧 mod 改过的 dll 都能打；
任何一处锚点找不到会直接报错退出，不会产出半截 dll。

## 结构

```
skyforce-mod/
├── config.json                     # 你调的就是这个
├── apply.sh / restore.sh
├── assets/Assembly-CSharp.dll.orig # 基线（--refresh 可更新）
├── build/                          # 打补丁产物
├── tools/
│   ├── vendor/                     # Mono.Cecil / Mono.Cecil.Rocks
│   └── SkyForcePatcher/            # 打补丁器（.NET 7 控制台）
└── research/decompiled/            # 游戏 Assembly-CSharp 反编译结果（分析用）
```

## 注意

- **存档安全**：补丁不碰 `Save/SaveGame.dat`，改配置也不会重置进度。
- 唯一副作用：`PlayerPrefs` 里旧 mod 曾写过 `CCard*_Prob*` 之类键（本次实测没有写入过，因为那段代码从未执行）。
- 游戏本体是 Unity 5.5.6f1 + Mono（**不是** IL2CPP），32 位；这也是能直接改 `Assembly-CSharp.dll` 的前提。
