using System.Globalization;
using System.Text;
using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class WorldSettingsService(PalContext context, LoggingService log, SafeFileService files)
{
    public const int CurrentProfileSchema = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] GuildLifecycleKeys =
    [
        "bAutoResetGuildNoOnlinePlayers",
        "AutoResetGuildTimeNoOnlinePlayers",
        "AutoTransferMasterCheckIntervalSeconds",
        "AutoTransferMasterThresholdDays"
    ];
    private static readonly string[] ProfileSchema3Keys =
    [
        "AutoSaveSpan",
        "DropItemMaxNum",
        "DropItemAliveMaxHours",
        "BuildObjectHpRate"
    ];

    public static IReadOnlyList<WorldSettingDefinition> ProfileDefinitions { get; } =
    [
        N("DayTimeSpeedRate", "世界", "白天流逝速度", "数值越大，白天越短。"),
        N("NightTimeSpeedRate", "世界", "夜晚流逝速度", "数值越大，夜晚越短。"),
        C("Difficulty", "世界", "难度预设", "仅作为底层难度标识；下方自定义数值仍分别生效。", "None", "Normal", "Hard"),
        C("RandomizerType", "世界", "随机化模式", "随机化帕鲁出现区域。", "None", "Region", "All"),
        T("RandomizerSeed", "世界", "随机种子", "留空时由游戏决定。"),
        B("bIsRandomizerPalLevelRandom", "世界", "帕鲁等级随机", "随机化模式下同时随机等级。"),
        B("bHardcore", "世界", "Hardcore 模式", "启用硬核规则。"),
        B("bCharacterRecreateInHardcore", "世界", "Hardcore 角色重建", "硬核死亡后允许重建角色。"),
        I("AutoSaveSpan", "世界", "自动保存间隔（秒）", "Palworld 自动保存世界的时间间隔；不会改变管理器的手动保存与关服流程。", 10, 3600),

        N("ExpRate", "玩家", "经验倍率", "玩家与帕鲁获得经验的倍率。", 0.1, 100),
        N("PlayerDamageRateAttack", "玩家", "玩家攻击伤害倍率", "玩家造成的伤害。"),
        N("PlayerDamageRateDefense", "玩家", "玩家承伤倍率", "玩家受到的伤害。"),
        N("PlayerStomachDecreaceRate", "玩家", "玩家饥饿消耗倍率", "越高越快饥饿。"),
        N("PlayerStaminaDecreaceRate", "玩家", "玩家耐力消耗倍率", "越高消耗越快。"),
        N("PlayerAutoHPRegeneRate", "玩家", "玩家生命恢复倍率", "清醒状态生命恢复。"),
        N("PlayerAutoHpRegeneRateInSleep", "玩家", "玩家睡眠恢复倍率", "睡眠状态生命恢复。"),
        N("ItemWeightRate", "玩家", "物品重量倍率", "越低物品越轻。", 0, 20),
        N("EquipmentDurabilityDamageRate", "玩家", "装备耐久消耗倍率", "越低越耐用。", 0, 20),
        C("DeathPenalty", "玩家", "死亡惩罚", "控制死亡时掉落内容。", "None", "Item", "ItemAndEquipment", "All"),
        B("bAllowEnhanceStat_Health", "玩家", "允许强化生命", "允许分配生命属性点。"),
        B("bAllowEnhanceStat_Attack", "玩家", "允许强化攻击", "允许分配攻击属性点。"),
        B("bAllowEnhanceStat_Stamina", "玩家", "允许强化耐力", "允许分配耐力属性点。"),
        B("bAllowEnhanceStat_Weight", "玩家", "允许强化负重", "允许分配负重属性点。"),
        B("bAllowEnhanceStat_WorkSpeed", "玩家", "允许强化工作速度", "允许分配工作速度属性点。"),

        N("PalCaptureRate", "帕鲁", "捕获率倍率", "影响帕鲁捕获成功率。"),
        N("PalSpawnNumRate", "帕鲁", "帕鲁出现数量倍率", "提高会明显增加服务器负载。", 0.1, 10),
        N("PalDamageRateAttack", "帕鲁", "帕鲁攻击伤害倍率", "帕鲁造成的伤害。"),
        N("PalDamageRateDefense", "帕鲁", "帕鲁承伤倍率", "帕鲁受到的伤害。"),
        N("PalStomachDecreaceRate", "帕鲁", "帕鲁饥饿消耗倍率", "越高越快饥饿。"),
        N("PalStaminaDecreaceRate", "帕鲁", "帕鲁耐力消耗倍率", "越高消耗越快。"),
        N("PalAutoHPRegeneRate", "帕鲁", "帕鲁生命恢复倍率", "工作或战斗外恢复。"),
        N("PalAutoHpRegeneRateInSleep", "帕鲁", "帕鲁睡眠恢复倍率", "帕鲁盒内或睡眠恢复。"),
        N("PalEggDefaultHatchingTime", "帕鲁", "巨大蛋孵化小时", "0 表示立即孵化。", 0, 240),
        N("WorkSpeedRate", "帕鲁", "工作速度倍率", "世界整体工作速度。", 0.1, 20),
        N("MonsterFarmActionSpeedRate", "帕鲁", "牧场产出速度倍率", "影响牧场帕鲁行动速度。", 0.1, 20),

        N("CollectionDropRate", "掉落与资源", "采集掉落倍率", "采集资源数量。"),
        N("CollectionObjectHpRate", "掉落与资源", "采集物生命倍率", "矿石、树木等耐久。"),
        N("CollectionObjectRespawnSpeedRate", "掉落与资源", "资源刷新间隔倍率", "越低刷新越快。", 0.1, 20),
        N("EnemyDropItemRate", "掉落与资源", "敌人掉落倍率", "敌人掉落物数量。"),
        N("ItemCorruptionMultiplier", "掉落与资源", "食物腐坏倍率", "越低腐坏越慢；0 可停止腐坏。", 0, 20),
        I("SupplyDropSpan", "掉落与资源", "补给投放间隔（分钟）", "补给箱事件间隔。", 1, 10080),
        N("FishingDifficultyRate", "掉落与资源", "钓鱼难度倍率", "越高钓鱼越难。", 0.1, 1),
        I("DropItemMaxNum", "掉落与资源", "世界掉落物数量上限", "世界中同时保留的掉落物总数；设置过高会增加服务器负载。", 100, 100000),
        N("DropItemAliveMaxHours", "掉落与资源", "掉落物保留时间（小时）", "无人拾取的世界掉落物可保留的时间。", 0.1, 168),

        N("BuildObjectHpRate", "建造与据点", "建筑生命倍率", "调整所有建筑物的最大生命值。", 0.1, 20),
        N("BuildObjectDamageRate", "建造与据点", "建筑受伤倍率", "建筑受到的伤害。", 0, 20),
        N("BuildObjectDeteriorationDamageRate", "建造与据点", "建筑劣化倍率", "据点外建筑随时间损坏；0 为关闭。", 0, 20),
        I("BaseCampMaxNum", "建造与据点", "全服据点上限", "服务器可存在的据点总数。", 1, 1024),
        I("BaseCampMaxNumInGuild", "建造与据点", "每公会据点上限", "每个公会可建据点数；当前官方上限 10。", 1, 10),
        I("BaseCampWorkerMaxNum", "建造与据点", "据点工作帕鲁上限", "每个据点工作帕鲁上限；当前官方上限 50。", 1, 50),
        I("MaxBuildingLimitNum", "建造与据点", "每玩家建筑上限", "0 表示不限制。", 0, 1000000),
        B("bBuildAreaLimit", "建造与据点", "限制建造区域", "启用游戏的建造区域限制。"),
        B("bAllowGlobalPalboxExport", "跨界帕鲁终端", "允许存入跨界帕鲁终端", "允许玩家把当前世界的帕鲁保存到跨界帕鲁终端（Export）。"),
        B("bAllowGlobalPalboxImport", "跨界帕鲁终端", "允许从跨界帕鲁终端取出", "允许玩家把跨界帕鲁终端中的帕鲁载入当前世界（Import）。"),

        I("GuildPlayerMaxNum", "多人游戏", "公会人数上限", "每个公会最多玩家数。", 1, 100),
        B("bAutoResetGuildNoOnlinePlayers", "多人游戏", "无人公会自动清理", "开启后，公会持续无人登录达到设定时长时，会重置其建筑与据点帕鲁。"),
        N("AutoResetGuildTimeNoOnlinePlayers", "多人游戏", "无人公会清理等待（小时）", "仅在“无人公会自动清理”开启时生效。", 1, 87600),
        I("AutoTransferMasterThresholdDays", "多人游戏", "会长离线转移门槛（天）", "会长连续离线达到该天数后，才允许自动转移会长。", 1, 36500),
        I("AutoTransferMasterCheckIntervalSeconds", "多人游戏", "会长转移检查间隔（秒）", "游戏检查会长自动转移条件的间隔；默认 3600 秒（1 小时）。", 60, 604800),
        B("bIsPvP", "多人游戏", "PvP 模式", "启用服务器 PvP 规则。"),
        B("bEnablePlayerToPlayerDamage", "多人游戏", "玩家互相伤害", "允许玩家造成 PvP 伤害。"),
        B("bEnableFriendlyFire", "多人游戏", "友军伤害", "允许同阵营伤害。"),
        B("bEnableInvaderEnemy", "多人游戏", "据点袭击事件", "允许敌人袭击据点。"),
        B("bEnableFastTravel", "多人游戏", "允许快速旅行", "允许使用快速旅行点。"),
        B("bEnableFastTravelOnlyBaseCamp", "多人游戏", "仅据点快速旅行", "快速旅行仅限据点。"),
        B("bIsStartLocationSelectByMap", "多人游戏", "允许选择出生点", "新角色可在地图选择初始位置。"),
        B("bExistPlayerAfterLogout", "多人游戏", "离线角色留在世界", "玩家退出后角色仍存在。"),
        B("bCanPickupOtherGuildDeathPenaltyDrop", "多人游戏", "可拾取其他公会死亡掉落", "允许跨公会拾取死亡掉落。"),
        B("EnablePredatorBossPal", "多人游戏", "捕食者 Boss 帕鲁", "允许捕食者 Boss 帕鲁出现。"),
        B("bPalLost", "多人游戏", "Hardcore 死亡丢失帕鲁", "硬核死亡时丢失帕鲁。")
    ];

    public static IReadOnlyList<WorldSettingDefinition> GlobalDefinitions { get; } =
    [
        T("ServerName", "服务器", "服务器名称", "服务器列表及游戏内显示名称。", true),
        T("ServerDescription", "服务器", "服务器描述", "服务器列表中的简介。", true),
        T("ServerPassword", "服务器", "加入密码", "玩家加入服务器使用；不会写入存档 profile。", true, true),
        I("ServerPlayerMaxNum", "服务器", "最大玩家数", "服务器玩家人数上限。", 1, 128, true),
        P("CrossplayPlatforms", "服务器", "跨平台范围", "逗号分隔，例如 Steam,Xbox,PS5,Mac。"),
        B("bIsShowJoinLeftMessage", "服务器", "显示加入/离开消息", "在游戏内显示玩家加入与离开。", true),
        B("bAllowClientMod", "服务器", "允许客户端 Mod", "允许使用客户端 Mod。", true),
        B("bIsUseBackupSaveData", "服务器", "使用游戏自动备份", "启用 Palworld 自带备份存档。", true)
    ];

    public async Task InitializeAsync(SaveSlotRegistry registry, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(context.StatePaths.WorldSettingsRoot);
        if (File.Exists(context.StatePaths.WorldSettingsMarkerPath))
        {
            await UpgradeExistingProfilesAsync(registry, cancellationToken);
            return;
        }
        var current = await ReadValuesAsync(context.ServerPaths.SettingsPath, ProfileDefinitions, cancellationToken);
        foreach (var slot in registry.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetProfilePath(slot.Id);
            if (File.Exists(path)) continue;
            var profile = NewProfile(slot.Id, slot.WorldGuid, slot.Tag, current);
            await WriteProfileAtomicAsync(path, profile, cancellationToken, overwrite: false);
            await log.WriteAsync($"Created initial world-settings profile for save {slot.Id} from the current runtime INI.", cancellationToken);
        }
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = CurrentProfileSchema,
            completedAtUtc = DateTimeOffset.UtcNow,
            source = "PalWorldSettings.ini"
        }, JsonOptions);
        await WriteBytesAtomicAsync(context.StatePaths.WorldSettingsMarkerPath, marker, cancellationToken);
        await log.WriteAsync("Initialized per-save world-settings profiles.", cancellationToken);
    }

    public async Task<WorldSettingsEditorSession> LoadEditorSessionAsync(SaveSlot slot, CancellationToken cancellationToken = default)
    {
        var profile = await LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
        var globals = await ReadValuesAsync(context.ServerPaths.SettingsPath, GlobalDefinitions, cancellationToken);
        return new WorldSettingsEditorSession(slot.Id, slot.Tag, slot.WorldGuid,
            new Dictionary<string, string>(profile.Values, StringComparer.OrdinalIgnoreCase), globals, false);
    }

    public async Task<WorldSettingsEditorSession> CreateDraftSessionAsync(
        NewWorldSettingsMode mode,
        SaveSlot activeSlot,
        Dictionary<string, string>? existingCustom,
        Dictionary<string, string>? existingGlobals = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> values;
        if (existingCustom is not null) values = FilterValues(existingCustom, ProfileDefinitions);
        else if (mode == NewWorldSettingsMode.GameDefaults) values = await GetDefaultValuesAsync(cancellationToken);
        else
        {
            var current = await LoadProfileAsync(activeSlot.Id, activeSlot.WorldGuid, cancellationToken);
            values = new Dictionary<string, string>(current.Values, StringComparer.OrdinalIgnoreCase);
        }
        var globals = existingGlobals is null
            ? await ReadValuesAsync(context.ServerPaths.SettingsPath, GlobalDefinitions, cancellationToken)
            : FilterValues(existingGlobals, GlobalDefinitions);
        return new WorldSettingsEditorSession(-1, "新存档（尚未创建）", "", values, globals, true);
    }

    public Task<Dictionary<string, string>> ImportCurrentIniAsync(CancellationToken cancellationToken = default) =>
        ReadValuesAsync(context.ServerPaths.SettingsPath, ProfileDefinitions, cancellationToken);

    public Task<Dictionary<string, string>> ImportProfileFromIniAsync(string path, CancellationToken cancellationToken = default) =>
        ReadValuesAsync(Path.GetFullPath(path), ProfileDefinitions, cancellationToken);

    public async Task<Dictionary<string, string>> ResolveExistingWorldSourceAsync(
        ExistingWorldSettingsChoice choice, SaveSlotRegistry registry, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> values = choice.Mode switch
        {
            ExistingWorldSettingsMode.ActiveProfile => new((await LoadProfileAsync(registry.ActiveSlotId,
                registry.Slots.Single(slot => slot.Id == registry.ActiveSlotId).WorldGuid, cancellationToken)).Values, StringComparer.OrdinalIgnoreCase),
            ExistingWorldSettingsMode.OtherProfile when choice.SourceSlotId is { } id && registry.Slots.Any(slot => slot.Id == id) =>
                new((await LoadProfileAsync(id, registry.Slots.Single(slot => slot.Id == id).WorldGuid, cancellationToken)).Values,
                    StringComparer.OrdinalIgnoreCase),
            ExistingWorldSettingsMode.OtherProfile => throw new InvalidOperationException("请选择已登记的设置来源存档。"),
            ExistingWorldSettingsMode.CurrentServerIni => await ImportCurrentIniAsync(cancellationToken),
            ExistingWorldSettingsMode.GameDefaults => await GetDefaultValuesAsync(cancellationToken),
            ExistingWorldSettingsMode.Custom when choice.CustomValues is not null => FilterValues(choice.CustomValues, ProfileDefinitions),
            ExistingWorldSettingsMode.Custom => throw new InvalidOperationException("尚未完成自定义世界设置。"),
            ExistingWorldSettingsMode.ExternalIni when !string.IsNullOrWhiteSpace(choice.ExternalIniPath) =>
                await ImportProfileFromIniAsync(choice.ExternalIniPath, cancellationToken),
            ExistingWorldSettingsMode.ExternalIni => throw new InvalidOperationException("请选择外部 PalWorldSettings.ini。"),
            _ => throw new InvalidOperationException("未知的世界设置来源。")
        };
        ValidateProfileValues(values);
        return values;
    }

    public async Task<Dictionary<string, string>> GetDefaultValuesAsync(CancellationToken cancellationToken = default)
    {
        var values = await ReadValuesAsync(context.ServerPaths.DefaultWorldSettingsPath, ProfileDefinitions, cancellationToken);
        if (!File.Exists(context.StatePaths.NewWorldDefaultsPath)) return values;

        await using var stream = new FileStream(context.StatePaths.NewWorldDefaultsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var overrides = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("新世界默认设置覆盖文件为空。");
        foreach (var pair in overrides)
        {
            var definition = ProfileDefinitions.FirstOrDefault(item => string.Equals(item.Key, pair.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"新世界默认设置包含未知参数 {pair.Key}。");
            values[definition.Key] = pair.Value;
        }
        ValidateProfileValues(values);
        return values;
    }

    public async Task SaveEditorResultAsync(SaveSlot slot, WorldSettingsEditorResult result, CancellationToken cancellationToken = default)
    {
        var profilePath = GetProfilePath(slot.Id);
        var oldProfile = File.Exists(profilePath) ? await File.ReadAllBytesAsync(profilePath, cancellationToken) : null;
        var profile = NewProfile(slot.Id, slot.WorldGuid, slot.Tag, FilterValues(result.ProfileValues, ProfileDefinitions));
        try
        {
            await WriteProfileAtomicAsync(profilePath, profile, cancellationToken, overwrite: true);
            await ApplyValuesToIniAsync(FilterValues(result.GlobalValues, GlobalDefinitions), cancellationToken);
        }
        catch
        {
            if (oldProfile is not null) await WriteBytesAtomicAsync(profilePath, oldProfile, CancellationToken.None);
            else if (File.Exists(profilePath)) File.Delete(profilePath);
            throw;
        }
        await log.WriteAsync($"World-settings profile saved for save {slot.Id}; global server fields were updated separately.", cancellationToken);
    }

    public async Task ApplyProfileToRuntimeAsync(int slotId, string expectedWorldGuid, CancellationToken cancellationToken = default)
    {
        var profile = await LoadProfileAsync(slotId, expectedWorldGuid, cancellationToken);
        await ApplyValuesToIniAsync(profile.Values, cancellationToken);
        await log.WriteAsync($"Applied world-settings profile for save {slotId} to PalWorldSettings.ini.", cancellationToken);
    }

    public async Task UpdateProfileTagAsync(SaveSlot slot, CancellationToken cancellationToken = default)
    {
        var profile = await LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
        if (string.Equals(profile.Tag, slot.Tag, StringComparison.Ordinal)) return;
        profile.Tag = slot.Tag;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await WriteProfileAtomicAsync(GetProfilePath(slot.Id), profile, cancellationToken, overwrite: true);
    }

    public async Task CreateProfileForNewSlotAsync(
        int slotId,
        string worldGuid,
        string tag,
        NewWorldSettingsChoice choice,
        int activeSlotId,
        string activeWorldGuid,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> values = choice.Mode switch
        {
            NewWorldSettingsMode.GameDefaults => await GetDefaultValuesAsync(cancellationToken),
            NewWorldSettingsMode.Custom when choice.CustomValues is not null => FilterValues(choice.CustomValues, ProfileDefinitions),
            NewWorldSettingsMode.Custom => throw new InvalidOperationException("新存档的自定义世界设置尚未完成。 "),
            _ => new Dictionary<string, string>((await LoadProfileAsync(activeSlotId, activeWorldGuid, cancellationToken)).Values, StringComparer.OrdinalIgnoreCase)
        };
        var profile = NewProfile(slotId, worldGuid, tag, values);
        var profilePath = GetProfilePath(slotId);
        await WriteProfileAtomicAsync(profilePath, profile, cancellationToken, overwrite: false);
        try
        {
            if (choice.GlobalValues is not null)
                await ApplyValuesToIniAsync(FilterValues(choice.GlobalValues, GlobalDefinitions), cancellationToken);
        }
        catch
        {
            if (File.Exists(profilePath)) File.Delete(profilePath);
            throw;
        }
    }

    public async Task<Dictionary<string, string>> PrepareNewWorldRuntimeAsync(
        NewWorldSettingsChoice choice,
        int activeSlotId,
        string activeWorldGuid,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> values = choice.Mode switch
        {
            NewWorldSettingsMode.GameDefaults => await GetDefaultValuesAsync(cancellationToken),
            NewWorldSettingsMode.Custom when choice.CustomValues is not null => FilterValues(choice.CustomValues, ProfileDefinitions),
            NewWorldSettingsMode.Custom => throw new InvalidOperationException("新存档的自定义世界设置尚未完成。"),
            _ => new Dictionary<string, string>((await LoadProfileAsync(activeSlotId, activeWorldGuid, cancellationToken)).Values, StringComparer.OrdinalIgnoreCase)
        };
        ValidateProfileValues(values);
        if (choice.GlobalValues is not null) await ApplyValuesToIniAsync(FilterValues(choice.GlobalValues, GlobalDefinitions), cancellationToken);
        await ApplyValuesToIniAsync(values, cancellationToken);
        return values;
    }

    public Task CommitNewWorldProfileAsync(int slotId, string worldGuid, string tag, Dictionary<string, string> values, CancellationToken cancellationToken = default) =>
        WriteProfileAtomicAsync(GetProfilePath(slotId), NewProfile(slotId, worldGuid, tag, values), cancellationToken, overwrite: false);

    public void DeleteProfileIfExists(int slotId)
    {
        var path = GetProfilePath(slotId);
        if (File.Exists(path)) File.Delete(path);
    }

    public Task<byte[]> CaptureRuntimeIniAsync(CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(context.ServerPaths.SettingsPath, cancellationToken);

    public Task RestoreRuntimeIniAsync(byte[] content, CancellationToken cancellationToken = default) =>
        WriteBytesAtomicAsync(context.ServerPaths.SettingsPath, content, cancellationToken);

    public async Task<WorldSettingsProfile> LoadProfileAsync(int slotId, string expectedWorldGuid, CancellationToken cancellationToken = default)
    {
        var path = GetProfilePath(slotId);
        if (!File.Exists(path)) throw new InvalidOperationException($"存档 {slotId} 缺少世界设置 profile；为防止套用其他存档设置，已取消启动。 ");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var profile = await JsonSerializer.DeserializeAsync<WorldSettingsProfile>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("profile 内容为空。 ");
            if (profile.SchemaVersion != CurrentProfileSchema || profile.SlotId != slotId ||
                !string.Equals(profile.WorldGuid, expectedWorldGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("profile 的存档编号或世界 UID 不匹配。 ");
            if (profile.Values is null) throw new InvalidOperationException("profile 缺少 Values。 ");
            profile.Values = FilterValues(profile.Values, ProfileDefinitions);
            if (profile.Values.Count != ProfileDefinitions.Count)
                throw new InvalidOperationException("profile 缺少受管理的世界设置。 ");
            ValidateProfileValues(profile.Values);
            return profile;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"存档 {slotId} 的世界设置 profile 无效：{exception.Message} 已取消启动。", exception);
        }
    }

    public string GetProfilePath(int slotId) => context.StatePaths.WorldProfilePath(slotId);

    public async Task RestoreProfileSnapshotAsync(SaveSlot slot, WorldSettingsProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.SlotId != slot.Id || !string.Equals(profile.WorldGuid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("历史世界设置不属于当前存档。");
        profile.SchemaVersion = CurrentProfileSchema;
        profile.Tag = slot.Tag;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        profile.Values = FilterValues(profile.Values, ProfileDefinitions);
        ValidateProfileValues(profile.Values);
        await WriteProfileAtomicAsync(GetProfilePath(slot.Id), profile, cancellationToken, overwrite: true);
        await log.WriteAsync($"Restored historical world-settings profile for save {slot.Id} from a manager snapshot.", cancellationToken);
    }

    private async Task ApplyValuesToIniAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        var document = await PalWorldIniDocument.LoadAsync(context.ServerPaths.SettingsPath, cancellationToken);
        foreach (var pair in values) document.SetValue(pair.Key, pair.Value);
        await document.WriteAtomicAsync(context.ServerPaths.SettingsPath, cancellationToken);
    }

    private static async Task<Dictionary<string, string>> ReadValuesAsync(
        string path,
        IReadOnlyList<WorldSettingDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var document = await PalWorldIniDocument.LoadAsync(path, cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions) values[definition.Key] = document.GetRequiredValue(definition.Key);
        return values;
    }

    private static Dictionary<string, string> FilterValues(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyList<WorldSettingDefinition> definitions)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (source.TryGetValue(definition.Key, out var value)) result[definition.Key] = value;
        }
        return result;
    }

    private static WorldSettingsProfile NewProfile(int slotId, string worldGuid, string tag, Dictionary<string, string> values)
    {
        ValidateProfileValues(values);
        return new WorldSettingsProfile
        {
            SlotId = slotId,
            WorldGuid = worldGuid.ToUpperInvariant(),
            Tag = tag,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task UpgradeExistingProfilesAsync(SaveSlotRegistry registry, CancellationToken cancellationToken)
    {
        Dictionary<string, string>? currentUpgradeValues = null;
        foreach (var slot in registry.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetProfilePath(slot.Id);
            if (!File.Exists(path)) continue;
            WorldSettingsProfile? profile;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                profile = await JsonSerializer.DeserializeAsync<WorldSettingsProfile>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"存档 {slot.Id} 的世界设置 profile 已损坏，无法安全升级：{exception.Message}", exception);
            }
            if (profile is null) throw new InvalidOperationException($"存档 {slot.Id} 的世界设置 profile 为空，无法安全升级。");
            if (profile.SchemaVersion >= CurrentProfileSchema) continue;
            if (profile.SchemaVersion is < 1 or > 2 || profile.SlotId != slot.Id ||
                !string.Equals(profile.WorldGuid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase) || profile.Values is null)
            {
                await log.WriteAsync($"Skipped world-settings upgrade for save {slot.Id}: profile identity or schema is invalid.", cancellationToken);
                continue;
            }
            var previousSchema = profile.SchemaVersion;
            var keysToAdd = previousSchema == 1
                ? GuildLifecycleKeys.Concat(ProfileSchema3Keys).ToArray()
                : ProfileSchema3Keys;
            currentUpgradeValues ??= await ReadValuesAsync(
                context.ServerPaths.SettingsPath,
                ProfileDefinitions.Where(item => GuildLifecycleKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase) ||
                                                 ProfileSchema3Keys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray(),
                cancellationToken);
            foreach (var key in keysToAdd)
            {
                if (!profile.Values.ContainsKey(key)) profile.Values[key] = currentUpgradeValues[key];
            }
            profile.SchemaVersion = CurrentProfileSchema;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await WriteProfileAtomicAsync(path, profile, cancellationToken, overwrite: true);
            await log.WriteAsync($"Upgraded world-settings profile for save {slot.Id} from schema {previousSchema} to schema {CurrentProfileSchema}.", cancellationToken);
        }
    }

    public static void ValidateProfileValues(IReadOnlyDictionary<string, string> values) =>
        WorldSettingsValueValidator.ValidateProfile(values);

    private Task WriteProfileAtomicAsync(string path, WorldSettingsProfile profile, CancellationToken cancellationToken, bool overwrite)
    {
        if (!overwrite && File.Exists(path)) throw new IOException($"文件已存在：{path}");
        return files.WriteJsonAsync(path, profile, JsonOptions, keepPrevious: overwrite, cancellationToken: cancellationToken);
    }

    private Task WriteBytesAtomicAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        files.WriteBytesAsync(path, content, keepPrevious: true, cancellationToken: cancellationToken);

    private static WorldSettingDefinition N(string key, string category, string label, string description, double min = 0, double max = 20) => new(key, category, label, description, WorldSettingKind.Number, min, max, 0.1);
    private static WorldSettingDefinition I(string key, string category, string label, string description, double min, double max, bool global = false) => new(key, category, label, description, WorldSettingKind.Integer, min, max, 1, IsGlobal: global);
    private static WorldSettingDefinition B(string key, string category, string label, string description, bool global = false) => new(key, category, label, description, WorldSettingKind.Boolean, IsGlobal: global);
    private static WorldSettingDefinition C(string key, string category, string label, string description, params string[] choices) => new(key, category, label, description, WorldSettingKind.Choice, Choices: choices);
    private static WorldSettingDefinition T(string key, string category, string label, string description, bool global = false, bool secret = false) => new(key, category, label, description, WorldSettingKind.Text, IsGlobal: global, IsSecret: secret);
    private static WorldSettingDefinition P(string key, string category, string label, string description) => new(key, category, label, description, WorldSettingKind.PlatformList, IsGlobal: true);
}
