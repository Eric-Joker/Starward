namespace Starward.Features.MapTool;

/// <summary>
/// 地图工具中的一个站点。
/// x:Bind 要求 public。
/// </summary>
public class MapToolOption
{

    /// <summary>
    /// 初始化一条地图站点。
    /// </summary>
    /// <param name="title">界面显示名。</param>
    /// <param name="url">应用内 WebView 打开的地址。</param>
    public MapToolOption(string title, string url)
    {
        Title = title;
        Url = url;
    }


    /// <summary>列表上显示的名称。</summary>
    public string Title { get; }


    /// <summary>要显示的地址。</summary>
    public string Url { get; }

}
