using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Gacha;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord;
using Starward.Features.Screenshot;
using Starward.Features.SelfQuery;
using System.Collections.Generic;

namespace Starward.Features;

internal partial class GameFeatureConfig
{

    private GameFeatureConfig()
    {

    }


    /// <summary>
    /// 支持的页面
    /// </summary>
    public List<string> SupportedPages { get; init; } = [];

    /// <summary>
    /// 游戏内通知内容
    /// </summary>
    public bool InGameNoticesWindow { get; init; }

    /// <summary>
    /// 支持云游戏
    /// </summary>
    public bool SupportCloudGame { get; init; }

    /// <summary>
    /// 支持实时便笺
    /// </summary>
    public bool SupportDailyNote { get; init; }


    public static GameFeatureConfig FromGameId(GameId? gameId)
    {
        if (gameId is null)
        {
            return None;
        }
        return FromGameBiz(gameId.GameBiz);
    }



    /// <summary>
    /// 按区服取游戏功能配置。便笺提醒这类只拿到战绩角色（<see cref="Core.GameRecord.GameRecordRole.GameBiz"/>）的地方用它。
    /// </summary>
    /// <param name="gameBiz">游戏区服。</param>
    /// <returns>该区服的功能配置；未知区服返回默认配置。</returns>
    public static GameFeatureConfig FromGameBiz(GameBiz gameBiz)
    {
        GameFeatureConfig config = gameBiz.Value switch
        {
            GameBiz.bh3_cn => bh3_cn,
            GameBiz.bh3_global or GameBiz.bh3_jp or GameBiz.bh3_kr or GameBiz.bh3_os or GameBiz.bh3_asia => bh3_global,
            GameBiz.hk4e_cn => hk4e_cn,
            GameBiz.hk4e_global => hk4e_global,
            GameBiz.hk4e_bilibili => hk4e_bilibili,
            GameBiz.hkrpg_cn => hkrpg_cn,
            GameBiz.hkrpg_global => hkrpg_global,
            GameBiz.hkrpg_bilibili => hkrpg_bilibili,
            GameBiz.nap_cn => nap_cn,
            GameBiz.nap_global => nap_global,
            GameBiz.nap_bilibili => nap_bilibili,
            _ => Default,
        };
        return config;
    }


    private static readonly GameFeatureConfig None = new();


    private static readonly GameFeatureConfig Default = new()
    {
        SupportedPages = [nameof(GameLauncherPage)]
    };


    private static readonly GameFeatureConfig bh3_cn = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GameRecordPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig bh3_global = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GameRecordPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hk4e_cn = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
            nameof(GenshinBeyondGachaPage),
        ],
        InGameNoticesWindow = true,
        SupportCloudGame = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hk4e_global = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
            nameof(GenshinBeyondGachaPage),
        ],
        InGameNoticesWindow = true,
        SupportCloudGame = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hk4e_bilibili = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
            nameof(GenshinBeyondGachaPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hkrpg_cn = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hkrpg_global = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig hkrpg_bilibili = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };



    private static readonly GameFeatureConfig nap_cn = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
        SupportCloudGame = true,
    };


    private static readonly GameFeatureConfig nap_global = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };


    private static readonly GameFeatureConfig nap_bilibili = new()
    {
        SupportedPages =
        [
            nameof(GameLauncherPage),
            nameof(ScreenshotPage),
            nameof(GachaLogPage),
            nameof(GameRecordPage),
            nameof(SelfQueryPage),
        ],
        InGameNoticesWindow = true,
        SupportDailyNote = true,
    };



}
