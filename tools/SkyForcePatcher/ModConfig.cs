using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyForcePatcher;

/// <summary>
/// 全部可调项。配置在打补丁时被编译进 IL 常量，
/// 因此改完配置需要重新 apply 一次（不需要在游戏里读文件，最稳）。
/// </summary>
public sealed class ModConfig
{
    public DropTuning Card { get; set; } = new();

    public DropTuning Skin { get; set; } = new();

    public StarsTuning Stars { get; set; } = new();

    public HudTuning Hud { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到配置文件: {path}");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ModConfig>(json, Options)
                     ?? throw new InvalidDataException($"配置文件为空或格式错误: {path}");

        config.Card.Validate("card");
        config.Skin.Validate("skin");
        config.Stars.Validate();
        return config;
    }
}

public sealed class DropTuning
{
    /// <summary>掉落总概率倍率：箱子开出卡片/皮肤的概率乘数（1 = 原版，3 = 三倍）。</summary>
    public float DropMultiplier { get; set; } = 1f;

    /// <summary>品质偏向：缩放"运气阈值"，小于 1 表示更容易掉高稀有度（0.6 = 稀有物更常见）。</summary>
    public float LuckBias { get; set; } = 1f;

    /// <summary>一次未掉落之后追加的运气（原版 0.1）。</summary>
    public float LuckFromBadLuck { get; set; } = 0.1f;

    /// <summary>上述追加运气的上限（原版 0.5）。</summary>
    public float MaxLuckFromBadLuck { get; set; } = 0.5f;

    /// <summary>每打完一局仍没掉落的运气补偿（原版 0.005）。</summary>
    public float LuckFromEmptyRuns { get; set; } = 0.005f;

    /// <summary>上述补偿的上限（原版 0.6）。</summary>
    public float MaxLuckFromEmptyRuns { get; set; } = 0.6f;

    /// <summary>连续多少局没掉落就强制必出（原版 100）。</summary>
    public int MaxRunsWithoutDrop { get; set; } = 100;

    internal void Validate(string name)
    {
        Require(DropMultiplier >= 0f, name, nameof(DropMultiplier), "必须 >= 0");
        Require(LuckBias is >= 0f and <= 1f, name, nameof(LuckBias), "必须在 0..1 之间");
        Require(LuckFromBadLuck >= 0f, name, nameof(LuckFromBadLuck), "必须 >= 0");
        Require(MaxLuckFromBadLuck >= 0f, name, nameof(MaxLuckFromBadLuck), "必须 >= 0");
        Require(LuckFromEmptyRuns >= 0f, name, nameof(LuckFromEmptyRuns), "必须 >= 0");
        Require(MaxLuckFromEmptyRuns >= 0f, name, nameof(MaxLuckFromEmptyRuns), "必须 >= 0");
        Require(MaxRunsWithoutDrop >= 1, name, nameof(MaxRunsWithoutDrop), "必须 >= 1");
    }

    private static void Require(bool ok, string section, string key, string rule)
    {
        if (!ok)
        {
            throw new InvalidDataException($"配置项 {section}.{char.ToLowerInvariant(key[0])}{key[1..]} {rule}");
        }
    }
}

public sealed class StarsTuning
{
    /// <summary>敌人/星星发生器产出的星星数倍率（1 = 原版）。</summary>
    public float Multiplier { get; set; } = 1f;

    internal void Validate()
    {
        if (Multiplier < 0f)
        {
            throw new InvalidDataException("配置项 stars.multiplier 必须 >= 0");
        }
    }
}

public sealed class HudTuning
{
    /// <summary>是否在游戏右上角显示当前倍率。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>自定义 HUD 文本（留空则自动生成）。只支持 ASCII，Unity 内置 GUI 字体没有中文字形。</summary>
    public string? Label { get; set; }
}
