using Starward.Core;
using Starward.Language;
using System.Collections.Generic;

namespace Starward.Features.MapTool;

/// <summary>
/// 地图工具站点目录：只收录官方互动地图（国服走米游社，国际服走 HoYoLAB）。
/// </summary>
/// <remarks>
/// <para>
/// 目录里没有任何第三方 / 社区站点：官方地图只用米游社的 <c>webstatic.mihoyo.com</c>
/// 与 HoYoLAB 的 <c>act.hoyolab.com</c>，宿主 WebView 也只允许官方域名的子域导航。
/// </para>
/// <para>
/// 站点名一律取自已有的 <c>Lang</c> 键（星铁「地图」、原神「大地图」），不再写字面量。
/// </para>
/// </remarks>
internal static class MapToolCatalog
{

    private const string GenshinOfficialCn = "https://webstatic.mihoyo.com/ys/app/interactive-map/index.html";

    private const string GenshinOfficialOs = "https://act.hoyolab.com/ys/app/interactive-map/index.html";

    private const string StarRailOfficialCn = "https://webstatic.mihoyo.com/sr/app/interactive-map/index.html";

    private const string StarRailOfficialOs = "https://act.hoyolab.com/sr/app/interactive-map/index.html";

    private const string GenshinMapIcon = "ms-appx:///Assets/Image/icon_map_genshin.png";

    private const string StarRailMapIcon = "ms-appx:///Assets/Image/icon_map_starrail.png";



    /// <summary>
    /// 官方互动地图的名字：原神是「大地图」，星穹铁道是「地图」；没有官方地图的游戏为空串。
    /// </summary>
    /// <param name="gameBiz">当前游戏区服。</param>
    public static string GetMapTitle(GameBiz gameBiz) => gameBiz.Game switch
    {
        GameBiz.hk4e => Lang.MapTool_BigMap,
        GameBiz.hkrpg => Lang.MapTool_Map,
        _ => "",
    };



    /// <summary>
    /// 工具箱导航项用的地图图标：原神一张、星穹铁道一张；没有官方地图的游戏为空串。
    /// </summary>
    /// <param name="gameBiz">当前游戏区服。</param>
    public static string GetMapIconPath(GameBiz gameBiz) => gameBiz.Game switch
    {
        GameBiz.hk4e => GenshinMapIcon,
        GameBiz.hkrpg => StarRailMapIcon,
        _ => "",
    };



    /// <summary>
    /// 官方互动地图的地址：国服（含 B 服）走米游社，国际服走 HoYoLAB；没有地图的游戏为 null。
    /// </summary>
    /// <param name="gameBiz">当前游戏区服。</param>
    public static string? GetMapUrl(GameBiz gameBiz)
    {
        bool oversea = gameBiz.IsGlobalServer();
        return gameBiz.Game switch
        {
            GameBiz.hk4e => oversea ? GenshinOfficialOs : GenshinOfficialCn,
            GameBiz.hkrpg => oversea ? StarRailOfficialOs : StarRailOfficialCn,
            _ => null,
        };
    }



    /// <summary>
    /// 当前游戏可用的地图站点；没有官方地图的游戏为空列表。
    /// </summary>
    /// <param name="gameBiz">当前工具箱页的游戏区服。</param>
    /// <returns>按显示顺序排列的选项。</returns>
    public static IReadOnlyList<MapToolOption> GetOptions(GameBiz gameBiz)
    {
        List<MapToolOption> options = [];
        string title = GetMapTitle(gameBiz);
        string? url = GetMapUrl(gameBiz);
        if (!string.IsNullOrEmpty(title) && url is not null)
        {
            options.Add(new MapToolOption(title, url));
        }
        return options;
    }



    /// <summary>
    /// 窗口该打开的那一条官方地图。
    /// <para>
    /// 目录按区服已经挑好（国服 / B 服走米游社，国际服走 HoYoLAB），同一个游戏在当前区服下只有一条，
    /// 所以这里取第一条；将来同一区服出现多条（例如活动地图）时，在这里按区服再筛一次即可。
    /// </para>
    /// </summary>
    /// <param name="gameBiz">当前游戏区服。</param>
    /// <returns>要打开的地图；没有官方地图时为 null。</returns>
    public static MapToolOption? GetCurrentOption(GameBiz gameBiz)
    {
        IReadOnlyList<MapToolOption> options = GetOptions(gameBiz);
        return options.Count > 0 ? options[0] : null;
    }


}
