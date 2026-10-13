using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.SignIn;

namespace Starward.Features.GameRecord.SignIn;

/// <summary>
/// 每日签到的游戏判定与归一化：哪些游戏能签、是否国际服、自动签到开关按哪个 biz 存储。
/// <para>
/// 主干没有 <c>GameFeatureConfig.SupportSignIn</c>，是否支持直接由 <see cref="SignInActivityConfig.FromGame"/>
/// 决定（返回 null 即不支持），无需再往特性表里加字段。
/// </para>
/// </summary>
internal static class SignInGameSupport
{

    /// <summary>
    /// 该 biz 是否支持每日签到（bilibili 归一到国服后判断）。
    /// </summary>
    /// <param name="gameBiz">游戏业务线，如 hk4e_cn。</param>
    /// <returns>支持时为 true。</returns>
    public static bool IsSupported(GameBiz gameBiz)
    {
        GameBiz biz = Normalize(gameBiz);
        return SignInActivityConfig.FromGame(biz.Game, biz.Server is "global") is not null;
    }


    /// <summary>
    /// 该角色是否属于国际服（HoYoLAB）。按角色自身的 <see cref="GameRecordRole.GameBiz"/> 判断，
    /// 不读 <c>GameRecordService.IsHoyolab</c>——后台自动签到不该依赖界面当前选中的区服。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <returns>国际服为 true。</returns>
    public static bool IsGlobalServerRole(GameRecordRole role)
    {
        return new GameBiz(role.GameBiz).Server is "global";
    }


    /// <summary>
    /// 自动签到开关的存储归一化：bilibili 与国服共用一份设置（签到接口本身也是国服那套）。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <returns>用于读写设置的 biz。</returns>
    public static GameBiz Normalize(GameBiz gameBiz)
    {
        if (gameBiz.Server is "bilibili")
        {
            return new GameBiz($"{gameBiz.Game}_cn");
        }
        return gameBiz;
    }


    /// <summary>
    /// 当前角色对应的签到活动配置，不支持时返回 null（不抛异常，供 UI 判可见性）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <returns>该角色可用的签到配置。</returns>
    public static SignInActivityConfig? GetConfig(GameRecordRole role)
    {
        GameBiz biz = Normalize(role.GameBiz);
        return SignInActivityConfig.FromGame(biz.Game, biz.Server is "global");
    }

}
