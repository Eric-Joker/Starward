using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Starward.Core;
using Starward.Frameworks;
using Starward.Language;
using System;
using System.Threading.Tasks;
using Windows.Graphics;


namespace Starward.Features.MapTool;

/// <summary>
/// 地图工具窗口：骨架与战绩窗口一致（32px 标题行 + 内嵌 WebView 占满第 1 行），
/// 在应用内显示当前区服的官方互动地图。
/// </summary>
/// <remarks>
/// 工具箱页只负责打开这个窗口（<c>new MapToolWindow { CurrentGameBiz = ... }</c> + <c>Activate()</c>），
/// 与战绩窗口的处理方式一致。窗口关闭时会显式释放 <c>msedgewebview2.exe</c>。
/// </remarks>
[INotifyPropertyChanged]
public sealed partial class MapToolWindow : WindowEx
{


    private readonly ILogger<MapToolWindow> _logger = AppConfig.GetLogger<MapToolWindow>();


    public MapToolWindow()
    {
        this.InitializeComponent();
        InitializeWindow();
        this.Closed += MapToolWindow_Closed;
    }



    private void InitializeWindow()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        // 先给一个通用名：调用方会在 Activate 之前把 CurrentGameBiz 塞进来，那时会换成该游戏的地图名。
        Title = Lang.MapTool_Map;
        RootGrid.RequestedTheme = ShouldAppsUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
        AdaptTitleBarButtonColorToActuallTheme();
        SetIcon();
        CenterInScreen();
    }



    private void CenterInScreen()
    {
        RectInt32 workArea = DisplayArea.GetFromWindowId(MainWindowId, DisplayAreaFallback.Nearest).WorkArea;
        int h = (int)(workArea.Height * 0.95);
        int w = (int)(h / 16.0 * 9.0);
        if (w > workArea.Width)
        {
            w = (int)(workArea.Width * 0.95);
            h = (int)(w * 16.0 / 9.0);
        }
        int x = workArea.X + (workArea.Width - w) / 2;
        int y = workArea.Y + (workArea.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }




    private GameBiz currentGameBiz;


    /// <summary>
    /// 当前游戏区服，由工具箱页在打开窗口时给出。
    /// </summary>
    public GameBiz CurrentGameBiz
    {
        get => currentGameBiz;
        set
        {
            if (SetProperty(ref currentGameBiz, value))
            {
                MapTitle = MapToolCatalog.GetMapTitle(value);
            }
        }
    }




    /// <summary>窗口标题与标题行文字：<c>Lang.MapTool_BigMap</c>（原神）或 <c>Lang.MapTool_Map</c>（星穹铁道）。</summary>
    public string MapTitle { get; set => SetProperty(ref field, value); }




    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _ = LoadMapAsync();
    }




    /// <summary>
    /// 打开当前区服对应的官方地图。目录里每个游戏只有一条（国服 / 国际服在
    /// <see cref="MapToolCatalog.GetOptions"/> 里已经按区服挑好），<see cref="MapToolCatalog.GetCurrentOption"/>
    /// 取到的就是这一条。工具箱页在没有地图时不会显示入口，所以这里拿不到的情况只记录日志。
    /// </summary>
    private async Task LoadMapAsync()
    {
        try
        {
            MapToolOption? option = MapToolCatalog.GetCurrentOption(CurrentGameBiz);
            if (option is null)
            {
                _logger.LogWarning("No map tool option for {GameBiz}.", CurrentGameBiz);
                return;
            }
            MapTitle = option.Title;
            await mapWebView.NavigateAsync(option.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load map tool page ({GameBiz}).", CurrentGameBiz);
        }
    }




    private void MapToolWindow_Closed(object sender, WindowEventArgs args)
    {
        this.Closed -= MapToolWindow_Closed;
        // WebView2 不会自己退出 msedgewebview2.exe，窗口关掉时显式释放。
        mapWebView.Close();
    }



}
