using CommunityToolkit.Mvvm.Messaging;
using Starward.Core;
using Starward.Features.Background;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Starward.Features.GameLauncher;

/// <summary>
/// 启动预设的读写入口。每个游戏区服一份有序的预设列表，序列化成 JSON 存在 Setting 表的单个键里。
/// <para>
/// 键名：<c>launch_profiles_{GameBiz}</c>。除此之外每个区服还记一个
/// <c>current_launch_profile_{GameBiz}</c>，保存当前选中的预设（内部名）。
/// </para>
/// <para>
/// 迁移规则：读取时如果新键不存在（或内容为空、解析失败、解析出空列表），
/// 就用旧的单一配置（<c>start_argument_*</c>、<c>enable_auth_ticket_login_*</c>、
/// <c>enable_dx12_*</c>、<c>enable_third_party_tool_*</c>、<c>third_party_tool_path_*</c>、
/// <c>custom_bg_*</c>、<c>enable_custom_bg_*</c>、<c>bg_*</c>，以及全局的显示游戏公告开关）
/// 造出第一个预设
/// <see cref="GameLaunchProfile.DefaultId"/> 并写回新键。旧键不会被删除，
/// 降级回旧版本时仍然可用。
/// </para>
/// </summary>
public static class GameLaunchProfileStore
{

    /// <summary>预设列表（JSON）的设置键前缀。</summary>
    public const string ProfilesKeyPrefix = "launch_profiles_";

    /// <summary>当前选中预设（内部名）的设置键前缀。</summary>
    public const string CurrentProfileKeyPrefix = "current_launch_profile_";


    private static readonly Dictionary<GameBiz, List<GameLaunchProfile>> _cache = new();

    private static readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = AppConfig.JsonSerializerOptions.Encoder,
    };


    private static string ProfilesKey(GameBiz gameBiz) => $"{ProfilesKeyPrefix}{gameBiz}";

    private static string CurrentProfileKey(GameBiz gameBiz) => $"{CurrentProfileKeyPrefix}{gameBiz}";



    /// <summary>
    /// 该区服的全部预设，至少一个。返回的列表是缓存里的活对象，界面可以直接改。
    /// </summary>
    public static List<GameLaunchProfile> GetProfiles(GameBiz gameBiz)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(gameBiz, out List<GameLaunchProfile>? cached) && cached is not null)
            {
                return cached;
            }
            // 先进缓存一份**空列表**，再读存储 / 迁移旧配置：
            // 迁移走的那些读取旧键的方法（AppConfig.GetStartArgument 等）会回调到这里，
            // 看到「这个区服还没有预设」（FindProfile 对空列表返回 null）就会去读旧键，也不会无限递归。
            // 这里**不能**先塞一个占位预设：那样这些方法读到的是这份还没填好的预设，
            // 拿到的一律是 null / false，旧版本的启动参数、自定义背景、自动登录升级后全会丢。
            var profiles = new List<GameLaunchProfile>();
            _cache[gameBiz] = profiles;
            LoadInto(gameBiz, profiles);
            Normalize(profiles);
            Save(gameBiz, profiles);
            return profiles;
        }
    }



    /// <summary>该区服的第一个预设，也就是旧配置迁移过来的那个。</summary>
    public static GameLaunchProfile GetFirstProfile(GameBiz gameBiz)
    {
        return GetProfiles(gameBiz)[0];
    }



    /// <summary>按内部名取预设，找不到时返回 null。</summary>
    public static GameLaunchProfile? GetProfile(GameBiz gameBiz, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return GetProfiles(gameBiz).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
    }



    /// <summary>当前选中的预设。内部名不存在时退回第一个预设。</summary>
    public static GameLaunchProfile GetCurrentProfile(GameBiz gameBiz)
    {
        var profiles = GetProfiles(gameBiz);
        string? id = AppConfig.GetCurrentLaunchProfileId(gameBiz);
        return profiles.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal)) ?? profiles[0];
    }



    /// <summary>
    /// 选中某个预设并保存。内部名不存在时不做任何事。
    /// <para>
    /// 背景（壁纸类型）已经属于预设：切换后如果该预设保存的背景与切换前**确实不同**，
    /// 才广播一次 <see cref="BackgroundChangedMessage"/> 让背景跟着换；相同就不发，
    /// 避免无谓地重新加载背景（换一张同款背景会闪一下）。
    /// </para>
    /// <para>
    /// 是否显示游戏公告也属于预设：切换后确实不同才广播
    /// <see cref="GameAnnouncementSettingChangedMessage"/>，让公告栏跟着显示 / 收起。
    /// </para>
    /// </summary>
    public static bool SetCurrentProfile(GameBiz gameBiz, string id)
    {
        if (GetProfile(gameBiz, id) is not GameLaunchProfile profile)
        {
            return false;
        }
        GameLaunchProfile oldProfile = GetCurrentProfile(gameBiz);
        AppConfig.SetCurrentLaunchProfileId(gameBiz, id);
        if (BackgroundDiffers(oldProfile, profile))
        {
            WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage());
        }
        if (AnnouncementDiffers(oldProfile, profile))
        {
            WeakReferenceMessenger.Default.Send(new GameAnnouncementSettingChangedMessage());
        }
        return true;
    }



    /// <summary>
    /// 两个预设的"背景相关配置"是否不同。只看这三项：正在使用的背景文件、
    /// 自定义背景文件、是否启用自定义背景——它们决定画面显示哪张图。
    /// </summary>
    private static bool BackgroundDiffers(GameLaunchProfile a, GameLaunchProfile b)
    {
        return !string.Equals(a.Bg, b.Bg, StringComparison.Ordinal)
            || !string.Equals(a.CustomBg, b.CustomBg, StringComparison.Ordinal)
            || a.EnableCustomBg != b.EnableCustomBg;
    }



    /// <summary>
    /// 两个预设的"是否显示游戏公告"是否不同。null 表示跟随全局开关，所以先按全局值折算成实际取值再比。
    /// </summary>
    private static bool AnnouncementDiffers(GameLaunchProfile a, GameLaunchProfile b)
    {
        return (a.EnableBannerAndPost ?? AppConfig.EnableBannerAndPost)
            != (b.EnableBannerAndPost ?? AppConfig.EnableBannerAndPost);
    }



    /// <summary>
    /// 新增预设并返回它。该区服就是当前启动器页的区服时，继承当前预设的配置；否则完全使用默认值。
    /// <para>
    /// 默认名序号由 <see cref="Normalize"/> 分配一个当前没被占用的最小号（通常是最后一个号 +1，
    /// 删过预设时空出来的号会被复用），新预设从此有自己的名字，不再由位置决定。
    /// </para>
    /// </summary>
    public static GameLaunchProfile AddProfile(GameBiz gameBiz)
    {
        var profiles = GetProfiles(gameBiz);
        GameLaunchProfile current = GetCurrentProfile(gameBiz);
        var profile = new GameLaunchProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            // 「无」启动参数是合法配置，继承时原样继承；不继承时默认什么都不附加。
            Argument = null,
            // 自动登录默认开启：新预设也用这个默认值，只有用户在界面里主动关掉才关。
            EnableAuthTicketLogin = AppConfig.EnableAuthTicketLogin,
            EnableDX12 = false,
            EnableThirdPartyTool = false,
            ThirdPartyToolPath = null,
            CustomBg = null,
            EnableCustomBg = false,
            Bg = null,
            // 显示游戏公告：不在初始化里定值，让还没改过这一项的预设继续跟随全局开关。
            EnableBannerAndPost = null,
        };
        if (AppConfig.CurrentGameBiz == gameBiz)
        {
            profile.CopyFrom(current);
        }
        profiles.Add(profile);
        Normalize(profiles);
        Save(gameBiz, profiles);
        // 新预设立即成为当前预设：调用方随后按「新增了哪个预设」把界面切过去。
        AppConfig.SetCurrentLaunchProfileId(gameBiz, profile.Id);
        return profile;
    }



    /// <summary>
    /// 重命名预设。名称为空白时清除自定义名称，回到该预设自己的默认名「预设 N」——
    /// 这里的 N 是它创建时分配（<see cref="GameLaunchProfile.Number"/>）的号，不是当前位置，
    /// 所以重命名和拖动排序互不影响。
    /// </summary>
    public static void RenameProfile(GameBiz gameBiz, string id, string? name)
    {
        if (GetProfile(gameBiz, id) is not GameLaunchProfile profile)
        {
            return;
        }
        profile.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Save(gameBiz, GetProfiles(gameBiz));
    }



    /// <summary>
    /// 把预设从 <paramref name="oldIndex"/> 移到 <paramref name="newIndex"/>，顺序立即持久化。
    /// </summary>
    public static void MoveProfile(GameBiz gameBiz, int oldIndex, int newIndex)
    {
        var profiles = GetProfiles(gameBiz);
        if (oldIndex < 0 || oldIndex >= profiles.Count || newIndex < 0 || newIndex >= profiles.Count || oldIndex == newIndex)
        {
            return;
        }
        GameLaunchProfile profile = profiles[oldIndex];
        profiles.RemoveAt(oldIndex);
        profiles.Insert(newIndex, profile);
        Normalize(profiles);
        Save(gameBiz, profiles);
    }



    /// <summary>
    /// 删除预设，用于预设编辑窗口。
    /// <para>
    /// 内置名不存在时什么都不做。每个区服至少要留一个预设，剩下的最后一个不允许删除，返回 null。
    /// 删掉的是当前选中的预设时，把当前预设挪到剩下的第一个并立即保存，界面不会停在一个已经消失的预设上。
    /// </para>
    /// </summary>
    /// <returns>删除成功时返回删除后的当前预设，被拒绝时返回 null。</returns>
    public static GameLaunchProfile? RemoveProfile(GameBiz gameBiz, string id)
    {
        var profiles = GetProfiles(gameBiz);
        if (profiles.Count <= 1)
        {
            return null;
        }
        if (GetProfile(gameBiz, id) is not GameLaunchProfile profile)
        {
            return null;
        }
        profiles.Remove(profile);
        Normalize(profiles);
        Save(gameBiz, profiles);
        if (string.Equals(profile.Id, AppConfig.GetCurrentLaunchProfileId(gameBiz), StringComparison.Ordinal))
        {
            // 删掉的是当前预设：挪到剩下的第一个并写进设置，否则下次读到的还是这个已经不存在的内部名，
            // GetCurrentProfile 就要靠 fallback 兜。这里显式落一次，让存储里始终有一个有效的当前预设。
            AppConfig.SetCurrentLaunchProfileId(gameBiz, profiles[0].Id);
            // 背景也属于预设：换到第一个预设之后如果背景不一样，同样要让画面跟着换。
            if (BackgroundDiffers(profile, profiles[0]))
            {
                WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage());
            }
            // 显示游戏公告也属于预设：换过去之后公告栏的显隐要跟着变。
            if (AnnouncementDiffers(profile, profiles[0]))
            {
                WeakReferenceMessenger.Default.Send(new GameAnnouncementSettingChangedMessage());
            }
        }
        return GetCurrentProfile(gameBiz);
    }



    /// <summary>
    /// 预设的内容改变后持久化。启动参数等值都是界面直接改对象属性，改完调用这个方法。
    /// </summary>
    public static void Save(GameBiz gameBiz)
    {
        Save(gameBiz, GetProfiles(gameBiz));
    }



    /// <summary>
    /// 兜底：任何情况下都保证该区服至少有一个预设。读取路径已经保证，这里只用于界面刷新前再确认一次。
    /// </summary>
    public static void EnsureProfiles(GameBiz gameBiz)
    {
        GetProfiles(gameBiz);
    }




    #region 内部实现


    /// <summary>
    /// 从设置里读出预设列表写进 <paramref name="list"/>。新键不存在（或内容为空、解析失败、
    /// 解析出空列表）时，用旧版本的单套配置造出第一个预设，保证老用户升级后设置不变。
    /// </summary>
    private static void LoadInto(GameBiz gameBiz, List<GameLaunchProfile> list)
    {
        List<GameLaunchProfile>? stored = null;
        try
        {
            string? json = AppConfig.GetValue<string>(null, ProfilesKey(gameBiz));
            if (!string.IsNullOrWhiteSpace(json))
            {
                stored = JsonSerializer.Deserialize<List<GameLaunchProfile>>(json, _jsonOptions);
            }
        }
        catch
        {
            stored = null;
        }
        stored?.RemoveAll(x => x is null);
        if (stored is { Count: > 0 })
        {
            list.Clear();
            list.AddRange(stored);
            return;
        }
        // 旧版本（包括上游 Scighost:main）的单套配置迁移成这个区服的第一个预设，也就是「预设 1」。
        // 这里必须用**空列表**的状态去读旧键（所以先 SeedLegacyValues 再 Add），见 GetProfiles 里的说明。
        var profile = CreateDefaultProfile(gameBiz);
        SeedLegacyValues(gameBiz, profile);
        list.Add(profile);
    }



    /// <summary>
    /// 把旧版本的单套配置读进第一个预设。
    /// <para>
    /// 故意走 <see cref="AppConfig"/> 上那些旧的读取方法，而不是自己读旧键：
    /// 自动登录的旧键缺失时要沿用全局默认值，那段逻辑在旧方法里。
    /// 这些方法会回调 <see cref="GetProfiles"/>，此时缓存里是这个区服的**空**预设列表，
    /// 所以它们真的会去读旧键；调用顺序不能反过来（见 <see cref="GetProfiles"/>）。
    /// </para>
    /// </summary>
    private static void SeedLegacyValues(GameBiz gameBiz, GameLaunchProfile profile)
    {
        profile.Id = GameLaunchProfile.DefaultId;
        profile.Argument = AppConfig.GetStartArgument(gameBiz);
        profile.EnableAuthTicketLogin = AppConfig.GetEnableAuthTicketLogin(gameBiz);
        profile.EnableDX12 = AppConfig.GetEnableDX12(gameBiz);
        profile.EnableThirdPartyTool = AppConfig.GetEnableThirdPartyTool(gameBiz);
        profile.ThirdPartyToolPath = AppConfig.GetThirdPartyToolPath(gameBiz);
        profile.CustomBg = AppConfig.GetCustomBg(gameBiz);
        profile.EnableCustomBg = AppConfig.GetEnableCustomBg(gameBiz);
        // 当前背景以前是全局键 bg_{biz}：迁移时把它读进这个预设，用户当前背景不丢。
        // 这一步在预设列表还是空的时候做（见 LoadInto），所以 GetBg 里的 FindProfile 返回 null，
        // 真的会把旧键读出来。
        profile.Bg = AppConfig.GetBg(gameBiz);
        // 显示游戏公告以前是全局开关，没有按区服的旧键，迁移时直接取全局值。
        profile.EnableBannerAndPost = AppConfig.EnableBannerAndPost;
    }



    /// <summary>
    /// 迁移用的空预设，只定下内部名和默认名序号；下面填的这些配置值随后都会被
    /// <see cref="SeedLegacyValues"/> 用旧版本的值覆盖。
    /// </summary>
    private static GameLaunchProfile CreateDefaultProfile(GameBiz gameBiz)
    {
        var profile = new GameLaunchProfile
        {
            Id = GameLaunchProfile.DefaultId,
            // 默认名序号：迁移过来的第一个预设固定是 1。
            Number = 1,
            Argument = null,
            // 自动登录默认开启（旧配置里明确关掉的会在迁移时读出来覆盖它）。
            EnableAuthTicketLogin = true,
            EnableDX12 = false,
            EnableThirdPartyTool = false,
            ThirdPartyToolPath = null,
            CustomBg = null,
            EnableCustomBg = false,
            // 不继承时也不预设背景：GetBg 会回退到旧键，界面显示的还是用户原来的背景。
            Bg = null,
            // 显示游戏公告不预留值：没记过这一项时读取会回退到全局开关（默认为开）。
            EnableBannerAndPost = null,
        };
        profile.Index = 1;
        return profile;
    }



    /// <summary>
    /// 补齐内部名、补默认名序号、刷新当前位置。**不改任何预设的名字。**
    /// <list type="bullet">
    /// <item><see cref="GameLaunchProfile.Id"/>：缺失补一个，重复重新生成，保证唯一；</item>
    /// <item><see cref="GameLaunchProfile.Number"/>：只在缺失或重复时补一个当前没被占用的最小号
    /// （旧数据首次读取时正好补成 1..N，和以前的默认名一致），**已有的号一律不动**，
    /// 所以拖动排序、增删都不会给任何预设改名；</item>
    /// <item><see cref="GameLaunchProfile.Index"/>：刷成当前位置，只表示位置，不参与显示。</item>
    /// </list>
    /// </summary>
    private static void Normalize(List<GameLaunchProfile> profiles)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < profiles.Count; i++)
        {
            GameLaunchProfile profile = profiles[i];
            // 旧数据可能没有内部名，第一个预设补成固定名，其余补成新的 GUID。
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = i == 0 ? GameLaunchProfile.DefaultId : Guid.NewGuid().ToString("N");
            }
            // 内部名重复时重新生成一个，保证 Id 唯一。
            while (!usedIds.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            profile.Index = i + 1;
        }
        NormalizeNumbers(profiles);
    }



    /// <summary>
    /// 默认名序号（<see cref="GameLaunchProfile.Number"/>）：缺失或重复的补一个当前没被占用的最小号，
    /// 已有的号保持原样。这是"名字属于预设本身、不随位置重新编号"的落点：
    /// 拖动排序只改列表顺序，不会走到这里改动任何已有序号。
    /// </summary>
    private static void NormalizeNumbers(List<GameLaunchProfile> profiles)
    {
        var used = new HashSet<int>();
        foreach (GameLaunchProfile profile in profiles)
        {
            // 缺号、或者和别人重号：先清成 0，下一轮统一补一个空闲号。
            if (profile.Number <= 0 || !used.Add(profile.Number))
            {
                profile.Number = 0;
            }
        }
        int next = 1;
        foreach (GameLaunchProfile profile in profiles)
        {
            if (profile.Number <= 0)
            {
                while (used.Contains(next))
                {
                    next++;
                }
                profile.Number = next;
                used.Add(next);
            }
        }
    }



    private static void Save(GameBiz gameBiz, List<GameLaunchProfile> profiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(profiles, _jsonOptions);
            AppConfig.SetValue(json, ProfilesKey(gameBiz));
        }
        catch
        {
            // 设置写不进去时不影响运行，下次修改会再试一次。
        }
    }


    #endregion


}
