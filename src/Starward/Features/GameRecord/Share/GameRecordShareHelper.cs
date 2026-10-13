using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.HoYoPlay;
using Starward.Features.Background;
using Starward.Features.Screenshot;
using Starward.Helpers;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace Starward.Features.GameRecord.Share;

/// <summary>
/// 战绩页分享图的 UI 侧流程：抓背景、读强调色、后台渲染、打开内置看图窗口。
/// </summary>
internal static class GameRecordShareHelper
{

    /// <summary>
    /// 获取当前游戏壁纸路径。
    /// 视频背景需要 <c>AppBackground</c> 提供当前帧快照，该能力在当前 main 上不存在，
    /// 因此视频背景返回 null —— 分享图退回强调色渐变底（静态图，不做视频抽帧）。
    /// </summary>
    public static Task<string?> PrepareBackgroundFileAsync(GameBiz gameBiz)
    {
        GameId? gameId = GameId.FromGameBiz(gameBiz);
        if (gameId is null)
        {
            return Task.FromResult<string?>(null);
        }

        string? backgroundFile = BackgroundService.GetCachedBackgroundFile(gameId);
        if (backgroundFile is not null && BackgroundService.FileIsSupportedVideo(backgroundFile))
        {
            backgroundFile = null;
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


    /// <summary>
    /// 渲染分享图并打开内置图片查看器。强调色在 UI 线程读取，Win2D 离屏绘制放到后台线程。
    /// </summary>
    public static async Task ShareAsync(
        FrameworkElement host,
        GameRecordRole? role,
        ILogger logger,
        Action<bool> setBusy,
        Func<string?, Color, Task<string>> render)
    {
        if (role is null)
        {
            return;
        }

        try
        {
            setBusy(true);
            string? backgroundFile = await PrepareBackgroundFileAsync(role.GameBiz);
            Color accentColor = GetAccentColor();
            string file = await Task.Run(async () => await render(backgroundFile, accentColor));
            await new ImageViewWindow2().ShowWindowAsync(host.XamlRoot.ContentIslandEnvironment.AppWindowId, file, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Share game record image ({gameBiz}, {uid}).", role.GameBiz, role.Uid);
            InAppToast.MainWindow?.Error(ex);
        }
        finally
        {
            setBusy(false);
        }
    }

}
