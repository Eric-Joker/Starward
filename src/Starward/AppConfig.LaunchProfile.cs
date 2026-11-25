using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;

namespace Starward;

public static partial class AppConfig
{


    /// <summary>
    /// 某个区服当前选中的启动预设内部名。
    /// <para>
    /// 0.19 上游按 <see cref="GameId"/> 存游戏配置、没有预设概念，这里按区服（<see cref="GameBiz"/>）存
    /// 「当前预设」，预设本体见 <see cref="GameLaunchProfileStore"/>。
    /// </para>
    /// </summary>
    internal static string? GetCurrentLaunchProfileId(GameBiz gameBiz)
    {
        return GetValue<string>(default, $"current_launch_profile_{gameBiz}");
    }


    /// <summary>
    /// 某个区服当前选中的启动预设内部名。
    /// </summary>
    internal static void SetCurrentLaunchProfileId(GameBiz gameBiz, string? id)
    {
        SetValue(id, $"current_launch_profile_{gameBiz}");
    }



    /// <summary>
    /// 某个区服当前选中的启动预设是否显示游戏公告。
    /// <para>
    /// 显示游戏公告属于启动预设。预设里还没有这一项（旧数据、或新建后没改过）时回退到全局键
    /// <see cref="EnableBannerAndPost"/>，默认开启，所以老用户升级后公告不会莫名消失。
    /// 写入端是设置对话框，它直接改当前预设，见 <see cref="GameLaunchProfile.EnableBannerAndPost"/>。
    /// </para>
    /// </summary>
    public static bool GetEnableBannerAndPost(GameBiz gameBiz)
    {
        try
        {
            if (GameLaunchProfileStore.GetProfile(gameBiz, GetCurrentLaunchProfileId(gameBiz)) is GameLaunchProfile profile
                && profile.EnableBannerAndPost is bool value)
            {
                return value;
            }
        }
        catch
        {
            // 读预设失败（迁移途中）时按全局开关处理，不能因此让公告消失
        }
        return EnableBannerAndPost;
    }



    /// <summary>
    /// 迁移旧配置时用的游戏 ID：0.19 上游把启动参数、DX12、自定义启动程序等按
    /// <see cref="GameId"/> 存，预设按区服存，读取旧值时取该区服的标准 GameId。
    /// <para>
    /// 崩坏三国际服一个区服对应多个 <see cref="GameId"/>（不同国家/地区），这里只取标准那个，
    /// 其余地区 GameId 上的旧值不会迁移；0.19 的启动器 UI 也只展示标准那个区服。
    /// </para>
    /// </summary>
    internal static GameId? GetLegacyGameId(GameBiz gameBiz)
    {
        try
        {
            return GameId.FromGameBiz(gameBiz);
        }
        catch
        {
            return null;
        }
    }


}
