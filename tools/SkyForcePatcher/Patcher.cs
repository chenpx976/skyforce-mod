using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace SkyForcePatcher;

/// <summary>
/// 对 Assembly-CSharp.dll 做最小化 IL 注入。
///
/// 设计原则：
/// 1. 只插入算术指令，不新增类型 / 不新增引用，保证 Unity 5.5 (Mono / .NET 3.5) 能加载。
/// 2. 每个补丁点都靠"指令模式"定位，因此原版 dll 与已被旧 mod 改过的 dll 都能打。
/// 3. 所有数值来自 ModConfig，在打补丁时被烤进 IL 常量，运行期零开销、零文件 IO。
/// </summary>
internal static class Patcher
{
    private const string MarkerTypeName = "SkyForceTweaks.PatchedMarker";

    // helper 程序集的 Hooks 类型与方法（未启用 helper 时为 null）
    private static TypeReference? helperTypeRef;
    private static MethodReference? tickMethod;

    public static List<string> Apply(ModuleDefinition module, ModConfig config, string? helperPath = null)
    {
        // 防重复打补丁：倍率是乘法，打两次会变成 x36。
        if (module.GetType(MarkerTypeName) is not null)
        {
            throw new PatchException("输入 dll 已经打过补丁（存在 SkyForceTweaks.PatchedMarker）。请用原版 dll 作基线，或先 ./restore.sh。");
        }

        var applied = new List<string>();

        PatchCardDropRate(module, config.Card, applied);
        PatchSkinDropRate(module, config.Skin, applied);
        PatchLuckSettings(module, config, applied);
        PatchStars(module, config.Stars, applied);
        DisableLegacyCardMod(module, applied);

        // 先注册 helper，HUD 的 OnGUI 里要顺带挂上复活弹窗的每帧回调。
        if (!string.IsNullOrEmpty(helperPath))
        {
            PatchHelperHooks(module, helperPath!, config, applied);
        }

        PatchHud(module, config, applied);

        if (applied.Count > 0)
        {
            AddPatchMarker(module);
        }

        return applied;
    }

    // ── 卡片 / 皮肤掉落总概率 ────────────────────────────────────────────────

    private static void PatchCardDropRate(ModuleDefinition module, DropTuning tuning, List<string> applied)
    {
        // CardsManager.CheckIfCanSpawnCard(float fMinProb, bool lastBox)
        //   fMinProb += ExtraCardLuckModificator;
        //   → fMinProb = clamp01(fMinProb * dropMultiplier)
        var method = FindMethod(module, "CardsManager", "CheckIfCanSpawnCard");
        var prob = method.Parameters[0];

        PatchDropProbability(module, method, prob, tuning.DropMultiplier,
            "卡片掉落率倍率", applied);
    }

    private static void PatchSkinDropRate(ModuleDefinition module, DropTuning tuning, List<string> applied)
    {
        // SkinsManager.CheckIfCanSpawnSkin(float fMinProb, bool lastBox)
        var method = FindMethod(module, "SkinsManager", "CheckIfCanSpawnSkin");
        var prob = method.Parameters[0];

        PatchDropProbability(module, method, prob, tuning.DropMultiplier,
            "皮肤掉落率倍率", applied);
    }

    private static void PatchDropProbability(
        ModuleDefinition module,
        MethodDefinition method,
        ParameterDefinition probability,
        float multiplier,
        string label,
        List<string> applied)
    {
        if (Approximately(multiplier, 1f))
        {
            return;
        }

        var body = BeginEdit(method);
        try
        {
            // 锚点：把 "运气加成" 累加进 fMinProb 的那条 starg。
            var anchor = body.Instructions.FirstOrDefault(ins =>
                IsStargTo(ins, probability) && ins.Previous is { OpCode.Value: var op } && op == OpCodes.Add.Value);

            if (anchor is null)
            {
                throw new PatchException($"{method.FullName}: 未找到 fMinProb 累加锚点");
            }

            var clamp01 = ExternalMethod(module, "UnityEngine", "UnityEngine", "Mathf", "Clamp01",
                module.TypeSystem.Single, module.TypeSystem.Single);

            InsertAfter(body.GetILProcessor(), anchor,
                Instruction.Create(OpCodes.Ldarg, probability),
                Instruction.Create(OpCodes.Ldc_R4, multiplier),
                Instruction.Create(OpCodes.Mul),
                Instruction.Create(OpCodes.Call, clamp01),
                Instruction.Create(OpCodes.Starg, probability));

            applied.Add($"{label}: x{multiplier:0.##} ({method.DeclaringType.Name}.{method.Name})");
        }
        finally
        {
            EndEdit(method);
        }
    }

    // ── 掉落品质偏向（缩放运气阈值） ────────────────────────────────────────

    private static void PatchLuckSettings(ModuleDefinition module, ModConfig config, List<string> applied)
    {
        PatchQualityBias(module, config.Card, "CardsManager", "GetRandomCard", "卡片", applied);
        PatchQualityBias(module, config.Skin, "SkinsManager", "GetRandomSkin", "皮肤", applied);

        PatchLuckFields(module, config.Card, "CardsManager", "CardsManager.Awake",
            new Dictionary<string, object>
            {
                ["_ExtraLuckFromBadLuck"] = config.Card.LuckFromBadLuck,
                ["_MaxExtraLuckFromBadLuck"] = config.Card.MaxLuckFromBadLuck,
                ["_ExtraLuckFromGamesWithoutCards"] = config.Card.LuckFromEmptyRuns,
                ["_MaxExtraLuckFromGamesWithoutCards"] = config.Card.MaxLuckFromEmptyRuns,
                ["_MaxNumGamesWithoutCard"] = config.Card.MaxRunsWithoutDrop,
            }, "卡片", applied);

        PatchLuckFields(module, config.Skin, "SkinsManager", "SkinsManager.Awake",
            new Dictionary<string, object>
            {
                ["_ExtraLuckFromBadLuck"] = config.Skin.LuckFromBadLuck,
                ["_MaxExtraLuckFromBadLuck"] = config.Skin.MaxLuckFromBadLuck,
                ["_ExtraLuckFromGamesWithoutSkins"] = config.Skin.LuckFromEmptyRuns,
                ["_MaxExtraLuckFromGamesWithoutSkins"] = config.Skin.MaxLuckFromEmptyRuns,
                ["_MaxNumGamesWithoutSkin"] = config.Skin.MaxRunsWithoutDrop,
            }, "皮肤", applied);
    }

    private static void PatchQualityBias(
        ModuleDefinition module,
        DropTuning tuning,
        string typeName,
        string methodName,
        string label,
        List<string> applied)
    {
        if (Approximately(tuning.LuckBias, 1f))
        {
            return;
        }

        // protected GetRandomCard(EGameDifficulty eDifficulty, float fLuck, bool lastBox)
        var method = module.GetType(typeName)
                         .Methods
                         .FirstOrDefault(m => m.Name == methodName
                                              && m.HasBody
                                              && m.Parameters.Count == 3
                                              && m.Parameters[1].ParameterType.MetadataType == MetadataType.Single)
                     ?? throw new PatchException($"找不到 {typeName}.{methodName}(EGameDifficulty, float, bool)");

        var luck = method.Parameters[1];
        var body = BeginEdit(method);
        try
        {
            var anchor = body.Instructions.FirstOrDefault(ins => IsStargTo(ins, luck))
                         ?? throw new PatchException($"{method.FullName}: 未找到 fLuck 写入点");

            var clamp01 = ExternalMethod(module, "UnityEngine", "UnityEngine", "Mathf", "Clamp01",
                module.TypeSystem.Single, module.TypeSystem.Single);

            InsertAfter(body.GetILProcessor(), anchor,
                Instruction.Create(OpCodes.Ldarg, luck),
                Instruction.Create(OpCodes.Ldc_R4, tuning.LuckBias),
                Instruction.Create(OpCodes.Mul),
                Instruction.Create(OpCodes.Call, clamp01),
                Instruction.Create(OpCodes.Starg, luck));

            applied.Add($"{label}品质偏向: x{tuning.LuckBias:0.##} ({typeName}.{methodName})");
        }
        finally
        {
            EndEdit(method);
        }
    }

    private static void PatchLuckFields(
        ModuleDefinition module,
        DropTuning tuning,
        string typeName,
        string label,
        Dictionary<string, object> values,
        string kind,
        List<string> applied)
    {
        var type = module.GetType(typeName) ?? throw new PatchException($"找不到类型 {typeName}");
        var awake = type.Methods.FirstOrDefault(m => m.Name == "Awake" && m.HasBody)
                    ?? throw new PatchException($"找不到 {typeName}.Awake");

        var body = BeginEdit(awake);
        try
        {
            var insertBefore = body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
                               ?? throw new PatchException($"{awake.FullName}: 未找到 ret");

            var il = body.GetILProcessor();
            foreach (var (fieldName, rawValue) in values)
            {
                var field = type.Fields.FirstOrDefault(f => f.Name == fieldName)
                            ?? throw new PatchException($"{typeName}: 找不到字段 {fieldName}");

                il.InsertBefore(insertBefore, Instruction.Create(OpCodes.Ldarg_0));
                if (field.FieldType.MetadataType == MetadataType.Int32)
                {
                    il.InsertBefore(insertBefore, Instruction.Create(OpCodes.Ldc_I4, Convert.ToInt32(rawValue)));
                }
                else
                {
                    il.InsertBefore(insertBefore, Instruction.Create(OpCodes.Ldc_R4, Convert.ToSingle(rawValue)));
                }

                il.InsertBefore(insertBefore, Instruction.Create(OpCodes.Stfld, field));
            }

            applied.Add($"{kind}幸运参数已写入 {typeName}.Awake "
                        + $"（badLuck={tuning.LuckFromBadLuck:0.###}/{tuning.MaxLuckFromBadLuck:0.###}, "
                        + $"emptyRuns={tuning.LuckFromEmptyRuns:0.###}/{tuning.MaxLuckFromEmptyRuns:0.###}, "
                        + $"强制必出={tuning.MaxRunsWithoutDrop} 局）");
        }
        finally
        {
            EndEdit(awake);
        }
    }

    // ── 星星倍率 ────────────────────────────────────────────────────────────

    private static void PatchStars(ModuleDefinition module, StarsTuning tuning, List<string> applied)
    {
        if (Approximately(tuning.Multiplier, 1f))
        {
            return;
        }

        var patched = 0;
        foreach (var (typeName, methodName) in new[] { ("Enemy", "GetStartNo"), ("StarsGenerator", "GetStartNo") })
        {
            var type = module.GetType(typeName);
            var method = type?.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody);
            if (method is null)
            {
                continue;
            }

            var body = BeginEdit(method);
            try
            {
                var calls = body.Instructions
                                .Where(i => i.OpCode == OpCodes.Call
                                            && i.Operand is MethodReference mr
                                            && mr.DeclaringType.Name == "Mathf"
                                            && mr.Name == "FloorToInt")
                                .ToList();

                if (calls.Count == 0)
                {
                    throw new PatchException($"{method.FullName}: 未找到 Mathf.FloorToInt");
                }

                var il = body.GetILProcessor();
                foreach (var call in calls)
                {
                    il.InsertBefore(call, Instruction.Create(OpCodes.Ldc_R4, tuning.Multiplier));
                    il.InsertBefore(call, Instruction.Create(OpCodes.Mul));
                }

                patched++;
            }
            finally
            {
                EndEdit(method);
            }
        }

        if (patched > 0)
        {
            applied.Add($"星星倍率: x{tuning.Multiplier:0.##}（Enemy / StarsGenerator 共 {patched} 处）");
        }
    }

    // ── 停用旧版 CardDropRateMultiplier 注入（它在 TCard.Start 里，永远不会执行） ──

    private static void DisableLegacyCardMod(ModuleDefinition module, List<string> applied)
    {
        var type = module.GetType("TCard");
        var start = type?.Methods.FirstOrDefault(m => m.Name == "Start" && m.HasBody && m.Parameters.Count == 0);
        if (start is null)
        {
            return;
        }

        // 旧注入只做了一件事：调用 IncreaseProbabilities()。直接把它换成空方法体。
        if (start.Body.Instructions.Count == 1 && start.Body.Instructions[0].OpCode == OpCodes.Ret)
        {
            return;
        }

        var body = start.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        body.MaxStackSize = 0;

        applied.Add("已停用旧版 CardDropRateMultiplier（TCard.Start 清空）");
    }

    // ── 右上角 HUD ────────────────────────────────────────────────────────

    private static void PatchHud(ModuleDefinition module, ModConfig config, List<string> applied)
    {
        // 直接给 CardsManager（常驻单例）添一个 OnGUI 消息，不用新建类型也不用 AddComponent。
        var type = module.GetType("CardsManager") ?? throw new PatchException("找不到类型 CardsManager");

        var existing = type.Methods.FirstOrDefault(m => m.Name == "OnGUI");
        if (existing is not null)
        {
            type.Methods.Remove(existing);
        }

        var hudEnabled = config.Hud.Enabled;
        if (!hudEnabled && helperTypeRef is null)
        {
            applied.Add("HUD 已关闭");
            return;
        }

        var text = string.IsNullOrWhiteSpace(config.Hud.Label)
            ? BuildHudText(config)
            : config.Hud.Label!;
        if (hudEnabled && text.Any(c => c > 0x7F))
        {
            throw new PatchException("hud.label 只能使用 ASCII：Unity 内置 GUI 字体没有中文字形，会显示成方块");
        }

        var unityRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "UnityEngine")
                       ?? throw new PatchException("模块缺少 UnityEngine 引用");

        TypeReference UnityType(string name) => new("UnityEngine", name, module, unityRef);

        MethodReference Static(string typeName, string name, TypeReference returnType, params TypeReference[] parameters)
            => Signature(typeName, name, returnType, false, parameters);

        MethodReference Instance(string typeName, string name, TypeReference returnType, params TypeReference[] parameters)
            => Signature(typeName, name, returnType, true, parameters);

        MethodReference Signature(string typeName, string name, TypeReference returnType, bool hasThis, TypeReference[] parameters)
        {
            var methodRef = new MethodReference(name, returnType, UnityType(typeName)) { HasThis = hasThis };
            foreach (var parameterType in parameters)
            {
                methodRef.Parameters.Add(new ParameterDefinition(parameterType));
            }

            return methodRef;
        }

        var f32 = module.TypeSystem.Single;
        var i32 = module.TypeSystem.Int32;
        var str = module.TypeSystem.String;
        var voidType = module.TypeSystem.Void;

        var getSkin = Static("GUI", "get_skin", UnityType("GUISkin"));
        var getLabelStyle = Instance("GUISkin", "get_label", UnityType("GUIStyle"));
        var setFontSize = Instance("GUIStyle", "set_fontSize", voidType, i32);
        var getNormal = Instance("GUIStyle", "get_normal", UnityType("GUIStyleState"));
        var setTextColor = Instance("GUIStyleState", "set_textColor", voidType, UnityType("Color"));
        var colorCtor = Instance("Color", ".ctor", voidType, f32, f32, f32, f32);
        var rectCtor = Instance("Rect", ".ctor", voidType, f32, f32, f32, f32);
        var screenWidth = Static("Screen", "get_width", i32);
        var box = Static("GUI", "Box", voidType, UnityType("Rect"), str);
        var label = Static("GUI", "Label", voidType, UnityType("Rect"), str);
        var textAnchor = UnityType("TextAnchor");
        textAnchor.IsValueType = true;
        var setAlignment = Instance("GUIStyle", "set_alignment", voidType, textAnchor);

        // 右对齐、贴右下角。宽度按最长一行估（Arial 13px 约 7.4px/字符）。
        const float fontSize = 13f;
        var lines = text.Split('\n');
        var maxChars = lines.Max(line => line.Length);
        var labelWidth = Math.Min(maxChars * 7.4f + 12f, 620f);
        var boxWidth = labelWidth + 16f;
        var boxHeight = 12f + lines.Length * 22f;
        var labelHeight = lines.Length * 22f;
        const float rightMargin = 6f;
        const float top = 26f;

        var method = new MethodDefinition("OnGUI",
            MethodAttributes.Private | MethodAttributes.HideBySig, voidType);
        type.Methods.Add(method);

        var body = new MethodBody(method) { MaxStackSize = 8 };
        method.Body = body;

        var x = new VariableDefinition(f32);
        body.Variables.Add(x);

        var il = body.GetILProcessor();

        if (hudEnabled)
        {
            // GUI.skin.label.fontSize = 13;
            il.Emit(OpCodes.Call, getSkin);
            il.Emit(OpCodes.Callvirt, getLabelStyle);
            il.Emit(OpCodes.Ldc_I4, (int)fontSize);
            il.Emit(OpCodes.Callvirt, setFontSize);

            // GUI.skin.label.normal.textColor = new Color(1f, 0.95f, 0.45f, 1f);
            il.Emit(OpCodes.Call, getSkin);
            il.Emit(OpCodes.Callvirt, getLabelStyle);
            il.Emit(OpCodes.Callvirt, getNormal);
            il.Emit(OpCodes.Ldc_R4, 1f);
            il.Emit(OpCodes.Ldc_R4, 0.95f);
            il.Emit(OpCodes.Ldc_R4, 0.45f);
            il.Emit(OpCodes.Ldc_R4, 1f);
            il.Emit(OpCodes.Newobj, colorCtor);
            il.Emit(OpCodes.Callvirt, setTextColor);

            // GUI.skin.label.alignment = TextAnchor.MiddleRight;  // 5
            il.Emit(OpCodes.Call, getSkin);
            il.Emit(OpCodes.Callvirt, getLabelStyle);
            il.Emit(OpCodes.Ldc_I4, 5);
            il.Emit(OpCodes.Callvirt, setAlignment);

            // var x = Screen.width - (rightMargin + boxWidth);
            il.Emit(OpCodes.Call, screenWidth);
            il.Emit(OpCodes.Conv_R4);
            il.Emit(OpCodes.Ldc_R4, rightMargin + boxWidth);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc, x);

            // GUI.Box(new Rect(x, top, boxWidth, boxHeight), "");
            il.Emit(OpCodes.Ldloc, x);
            il.Emit(OpCodes.Ldc_R4, top);
            il.Emit(OpCodes.Ldc_R4, boxWidth);
            il.Emit(OpCodes.Ldc_R4, boxHeight);
            il.Emit(OpCodes.Newobj, rectCtor);
            il.Emit(OpCodes.Ldstr, string.Empty);
            il.Emit(OpCodes.Call, box);

            // GUI.Label(new Rect(x + 8f, top + 6f, labelWidth, labelHeight), text);
            il.Emit(OpCodes.Ldloc, x);
            il.Emit(OpCodes.Ldc_R4, 8f);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldc_R4, top + 6f);
            il.Emit(OpCodes.Ldc_R4, labelWidth);
            il.Emit(OpCodes.Ldc_R4, labelHeight);
            il.Emit(OpCodes.Newobj, rectCtor);
            il.Emit(OpCodes.Ldstr, text);
            il.Emit(OpCodes.Call, label);
        }

        // 复活弹窗：由 helper 每帧驱动（OnGUI 不受 timeScale 影响）。
        if (helperTypeRef is not null)
        {
            il.Emit(OpCodes.Call, tickMethod);
        }

        il.Emit(OpCodes.Ret);

        applied.Add($"HUD（CardsManager.OnGUI）：\"{text.Replace("\n", " / ")}\"");
    }

    private static string BuildHudText(ModConfig config)
    {
        return $"card x{config.Card.DropMultiplier:0.##}  skin x{config.Skin.DropMultiplier:0.##}  "
               + $"luck {config.Card.LuckBias:0.##}  "
               + $"pity {config.Card.MaxRunsWithoutDrop}/{config.Skin.MaxRunsWithoutDrop}  "
               + $"stars x{config.Stars.Multiplier:0.##}";
    }

    // ── helper 程序集钩子 ──────────────────────────────────────────────────

    private static void PatchHelperHooks(ModuleDefinition module, string helperPath, ModConfig config, List<string> applied)
    {
        if (!File.Exists(helperPath))
        {
            throw new PatchException($"找不到 helper 程序集: {helperPath}");
        }

        // 让 Assembly-CSharp 引用 Managed/ 下的 SkyForceTweaks.dll
        var helperRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "SkyForceTweaks");
        if (helperRef is null)
        {
            helperRef = new AssemblyNameReference("SkyForceTweaks", new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(helperRef);
        }

        var stageType = module.GetType("Stage") ?? throw new PatchException("找不到类型 Stage");
        var playerType = module.GetType("Player") ?? throw new PatchException("找不到类型 Player");

        var helperType = new TypeReference("SkyForceTweaks", "Hooks", module, helperRef);

        // 启动探针：CardsManager.Awake 末尾调一次，确认程序集真的被 mono 加载了。
        var cardsManager = module.GetType("CardsManager")
                           ?? throw new PatchException("找不到类型 CardsManager");
        var awake = cardsManager.Methods.FirstOrDefault(m => m.Name == "Awake" && m.HasBody)
                    ?? throw new PatchException("找不到 CardsManager.Awake");
        var bootHook = new MethodReference("OnGameBoot", module.TypeSystem.Void, helperType) { HasThis = false };

        // 每局开始时重置复活次数
        var evStartGame = stageType.Methods.FirstOrDefault(m => m.Name == "EvStartGame" && m.HasBody);
        if (evStartGame is not null)
        {
            var resetHook = new MethodReference("ResetStage", module.TypeSystem.Void, helperType) { HasThis = false };
            var startBody = BeginEdit(evStartGame);
            try
            {
                var startIl = startBody.GetILProcessor();
                var startExit = startBody.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                startIl.InsertBefore(startExit, Instruction.Create(OpCodes.Call, resetHook));
                applied.Add("helper hook: Stage.EvStartGame → ResetStage");
            }
            finally
            {
                EndEdit(evStartGame);
            }
        }

        helperTypeRef = helperType;
        tickMethod = new MethodReference("OnGuiTick", module.TypeSystem.Void, helperType) { HasThis = false };

        var awakeBody = BeginEdit(awake);
        try
        {
            var bootIl = awakeBody.GetILProcessor();
            var awakeExit = awakeBody.Instructions.Last(i => i.OpCode == OpCodes.Ret);
            bootIl.InsertBefore(awakeExit, Instruction.Create(OpCodes.Call, bootHook));
            applied.Add("helper boot probe: CardsManager.Awake");
        }
        finally
        {
            EndEdit(awake);
        }

        var hookMethod = new MethodReference("OnPlayerDestroyed", module.TypeSystem.Boolean, helperType)
        {
            HasThis = false,
        };
        hookMethod.Parameters.Add(new ParameterDefinition(stageType));
        hookMethod.Parameters.Add(new ParameterDefinition(playerType));

        // private void onPlayerDestroyed(Player player)
        var target = stageType.Methods.FirstOrDefault(m => m.Name == "onPlayerDestroyed" && m.HasBody)
                     ?? throw new PatchException("找不到 Stage.onPlayerDestroyed");

        var body = BeginEdit(target);
        try
        {
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            var exit = body.Instructions.Last(i => i.OpCode == OpCodes.Ret);

            // if (Hooks.OnPlayerDestroyed(this, player)) return;
            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, target.Parameters[0]));
            il.InsertBefore(first, Instruction.Create(OpCodes.Call, hookMethod));
            il.InsertBefore(first, Instruction.Create(OpCodes.Brtrue, exit));

            applied.Add($"helper hook: Stage.onPlayerDestroyed → {Path.GetFileName(helperPath)}");
        }
        finally
        {
            EndEdit(target);
        }
    }

    // ── 工具方法 ────────────────────────────────────────────────────────────

    private static void AddPatchMarker(ModuleDefinition module)
    {
        var marker = new TypeDefinition(
            "SkyForceTweaks",
            "PatchedMarker",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        module.Types.Add(marker);
    }

    private static MethodDefinition FindMethod(ModuleDefinition module, string typeName, string methodName)
    {
        var type = module.GetType(typeName) ?? throw new PatchException($"找不到类型 {typeName}");
        return type.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody && m.Parameters.Count == 2)
               ?? throw new PatchException($"找不到 {typeName}.{methodName}(float, bool)");
    }

    private static MethodBody BeginEdit(MethodDefinition method)
    {
        var body = method.Body;
        body.SimplifyMacros();
        return body;
    }

    private static void EndEdit(MethodDefinition method)
    {
        var body = method.Body;
        body.OptimizeMacros();
        body.MaxStackSize = Math.Max(body.MaxStackSize, 8);
    }

    private static bool IsStargTo(Instruction instruction, ParameterDefinition parameter)
        => (instruction.OpCode == OpCodes.Starg || instruction.OpCode == OpCodes.Starg_S)
           && instruction.Operand is ParameterDefinition pd
           && pd.Index == parameter.Index
           && pd.Method == parameter.Method;

    private static void InsertAfter(ILProcessor il, Instruction anchor, params Instruction[] instructions)
    {
        var current = anchor;
        foreach (var instruction in instructions)
        {
            il.InsertAfter(current, instruction);
            current = instruction;
        }
    }

    private static MethodReference ExternalMethod(
        ModuleDefinition module,
        string assemblyName,
        string ns,
        string typeName,
        string methodName,
        TypeReference returnType,
        params TypeReference[] parameterTypes)
    {
        var assemblyRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == assemblyName)
                          ?? throw new PatchException($"模块缺少程序集引用 {assemblyName}");

        var typeRef = new TypeReference(ns, typeName, module, assemblyRef);
        var methodRef = new MethodReference(methodName, returnType, typeRef) { HasThis = false };
        foreach (var parameterType in parameterTypes)
        {
            methodRef.Parameters.Add(new ParameterDefinition(parameterType));
        }

        return methodRef;
    }

    private static bool Approximately(float a, float b) => Math.Abs(a - b) < 0.0001f;
}

internal sealed class PatchException : Exception
{
    public PatchException(string message) : base(message)
    {
    }
}
