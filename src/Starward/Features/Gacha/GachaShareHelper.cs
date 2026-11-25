using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Background;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace Starward.Features.Gacha;

/// <summary>
/// 抽卡分享图的 UI 侧流程辅助：抓背景、读强调色。
/// <para>
/// 原实现与「战绩分享图」共用 <c>GameRecordShareHelper</c>（main 分支的 GameRecord.Share 模块），
/// 该模块尚未迁移到 0.19 架构，因此这里先行提供抽卡分享图所需的最小实现。
/// 迁移战绩分享图时可以让两者重新合并。
/// </para>
/// </summary>
internal static class GachaShareHelper
{

    /// <summary>
    /// 获取当前游戏壁纸路径。
    /// <para>
    /// 视频背景在磁盘上只有视频文件，没有可以当作背景的图片。0.19 的 <see cref="AppBackground"/>
    /// 还没有抓帧接口（main 后续提交），抓不到静帧时返回 null，分享图退回强调色渐变底。
    /// </para>
    /// </summary>
    public static Task<string?> PrepareBackgroundFileAsync(GameBiz gameBiz)
    {
        GameId? gameId = GameId.FromGameBiz(gameBiz);
        if (gameId is null)
        {
            return Task.FromResult<string?>(null);
        }

        string? backgroundFile = BackgroundService.GetLastAppBackgroundFile(gameId);
        if (backgroundFile is not null && BackgroundService.FileIsSupportedVideo(backgroundFile))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(backgroundFile);
    }


    /// <summary>在 UI 线程读取主题强调色，供离屏渲染使用。</summary>
    public static Color GetAccentColor()
    {
        if (Application.Current.Resources["AccentFillColorDefaultBrush"] is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return Color.FromArgb(0xFF, 0x4C, 0x8B, 0xF5);
    }

}
