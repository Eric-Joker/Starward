using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Starward.Core;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics;

namespace Starward.Features.GameLauncher;


/// <summary>
/// 启动预设的独立悬浮窗口：托盘菜单里悬停某个区服项时，弹在菜单旁边的那份预设列表。
/// <para>
/// 之所以是独立窗口而不是托盘窗口里的一块面板：面板留在托盘窗口里就得把那个窗口撑大，
/// 菜单自己的亚克力背景会跟着铺开一大片，看起来像是菜单本身被拉宽了。
/// </para>
/// <para>
/// 观感：窗口自己一层系统亚克力，用的属性和托盘菜单<b>逐字相同</b>（见 <see cref="SystemBackdropHelper"/>），
/// 所以透明度和混色和菜单一致；内容里不铺任何画刷（面板自己也把背景换成透明，见
/// <see cref="GameLaunchProfileFlyout.UseTransparentPanelBackground"/>），否则画刷取不到背景采样、
/// 会退回 FallbackColor。
/// </para>
/// <para>
/// 显示时会把窗口激活（不加 <c>WS_EX_NOACTIVATE</c>）：亚克力只在窗口处于活动状态时才是模糊的观感，
/// 非活动窗口上系统会画成接近纯色的浅色。托盘菜单因此会失去活动状态，
/// 那边在列表可见时不会把菜单收起来（见 <c>SystemTrayWindow.SystemTrayWindow_Activated</c>）。
/// </para>
/// </summary>
public sealed partial class GameLaunchProfilePopupWindow : WindowEx
{


    private readonly ILogger<GameLaunchProfilePopupWindow> _logger = AppConfig.GetLogger<GameLaunchProfilePopupWindow>();


    /// <summary>
    /// 面板外框的内边距，和 <c>GameLaunchProfileFlyout.xaml</c> 里的 <c>PanelPadding</c> 同源（当前 6）。
    /// 行锚定的语义是「列表里第一行的顶边 == 被悬停区服行的顶边」，
    /// 而面板顶边在它上方一个内边距处，所以摆放时整块往上让这个数。那个资源改了这里要跟着改。
    /// </summary>
    private const double PanelPadding = 6;


    /// <summary>
    /// 面板和托盘菜单之间的空隙（DIP）。托盘菜单是系统菜单的观感、默认贴得比较紧，
    /// 这里只用 2，看起来是贴着菜单一侧展开的。
    /// </summary>
    private const double PanelGap = 2;



    public GameLaunchProfilePopupWindow()
    {
        InitializeComponent();
        InitializeWindow();
    }




    private unsafe void InitializeWindow()
    {
        // 主题显式设置：和托盘菜单同一套（列表文字、分隔线这些跟着它走）。
        RootGrid.RequestedTheme = ShouldSystemUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;

        // 亚克力和托盘菜单用逐字相同的一套配置：透明度、混色才会一致。
        // 这里不用内置的 DesktopAcrylicBackdrop —— 那是系统亚克力的默认配比，比托盘菜单明显更不透明。
        new SystemBackdropHelper(this, SystemBackdropProperty.AcrylicDefault with
        {
            TintColorLight = 0xFFE7E7E7,
            TintColorDark = 0xFF404040
        }).TrySetAcrylic(true);
        this.Activated += GameLaunchProfilePopupWindow_Activated;

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            // 和托盘窗口一样把标题栏 / 边框样式去掉：只 SetBorderAndTitleBar 还会留下一圈窗口边框。
            var style = User32.GetWindowLongPtr(WindowHandle, User32.WindowLongFlags.GWL_STYLE);
            style &= ~(nint)User32.WindowStyles.WS_CAPTION;
            style &= ~(nint)User32.WindowStyles.WS_BORDER;
            User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE, style);
            // 不进任务栏 / Alt+Tab，但不加 WS_EX_NOACTIVATE：要能被激活，亚克力才是模糊的观感。
            // 托盘菜单因此会失去活动状态，那边的 Activated 处理器在列表可见时不会把菜单收起来。
            User32.WindowStylesEx styleEx = (User32.WindowStylesEx)User32.GetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE);
            styleEx |= User32.WindowStylesEx.WS_EX_TOOLWINDOW;
            User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE, (nint)styleEx);
            // 圆角跟托盘菜单一致（DWM 的圆角，窗口内容自己不画背景）。
            DwmApi.DWM_WINDOW_CORNER_PREFERENCE corner = DwmApi.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, (nint)(&corner), sizeof(DwmApi.DWM_WINDOW_CORNER_PREFERENCE));
            // 显式把窗口边框颜色设成「无」：DWMWA_COLOR_NONE = 0xFFFFFFFE。
            uint noBorder = 0xFFFFFFFE;
            DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, (nint)(&noBorder), sizeof(uint));
        }
        // 托盘里的悬浮预设列表：不要底部按钮、宽度跟菜单走、行内边距收紧。
        LaunchProfileFlyout.ApplyTrayLayout();
    }




    /// <summary>
    /// 用户点了列表里的某一条预设。
    /// </summary>
    public event EventHandler<GameLaunchProfile>? ProfileSelected;


    /// <summary>指针移进预设列表。</summary>
    public event EventHandler? PanelPointerEntered;


    /// <summary>指针移出预设列表。</summary>
    public event EventHandler? PanelPointerExited;


    /// <summary>
    /// 窗口失去活动状态（用户点到别的地方去了）而且指针不在列表上：宿主据此把列表和托盘菜单一起收掉。
    /// </summary>
    public event EventHandler? Dismissed;


    /// <summary>指针是不是正停在列表上。</summary>
    private bool _pointerOnPanel;


    /// <summary>这个窗口是否真的活动过。显示的一瞬间可能先收到一次「失去活动」，那次不算"点去别处"。</summary>
    private bool _wasActive;




    /// <summary>
    /// 把某个区服的预设列表摆到托盘菜单旁边并显示。
    /// </summary>
    /// <param name="gameBiz">显示哪个区服的预设。</param>
    /// <param name="activeProfileId">当前预设的内部名，列表里那一行会高亮。</param>
    /// <param name="rowRect">被悬停那一行在屏幕上的矩形（物理像素）。</param>
    /// <param name="menuRect">托盘菜单窗口在屏幕上的矩形（物理像素）：宽度用它，列表摆在它左边。</param>
    public void ShowAt(GameBiz gameBiz, string? activeProfileId, RectInt32 rowRect, RectInt32 menuRect)
    {
        try
        {
            _pointerOnPanel = false;
            _wasActive = false;
            LaunchProfileFlyout.Mode = GameLaunchProfileFlyout.DisplayMode.Panel;
            LaunchProfileFlyout.Refresh(gameBiz, activeProfileId);
            double scale = UIScale;
            // 宽度跟托盘菜单一致（面板被拉满，文字超长时由列表自己省略）。
            int width = Math.Max(1, menuRect.Width);
            DisplayArea display = DisplayArea.GetFromPoint(new PointInt32(rowRect.X, rowRect.Y), DisplayAreaFallback.Nearest);
            RectInt32 area = display.WorkArea;
            // 先把它显示到屏幕外、给足高度，再量真实高度：
            // 应用生命周期里第一次悬停时列表项还一个都没实体化，直接量出来的高度只有一个内边距那么高，
            // 窗口就会"贼扁"（第二次以后列表已经有实体化的项了，量出来才是对的）。
            // 这一次不激活它（激活留到摆好位置之后）。
            AppWindow.MoveAndResize(new RectInt32(area.X - width - 400, area.Y, width, Math.Max(1, area.Height)));
            AppWindow.Show(false);
            RootGrid.UpdateLayout();
            // 高度按内容量，最多不超过工作区（装不下的部分由列表自己滚动）。
            RootGrid.Measure(new Size(width / scale, area.Height / scale));
            int height = Math.Clamp((int)Math.Ceiling(RootGrid.DesiredSize.Height * scale), 1, area.Height);
            int padding = (int)Math.Round(PanelPadding * scale);
            int gap = (int)Math.Round(PanelGap * scale);
            // 行锚定：列表里第一行的顶边对齐被悬停那一行的顶边，所以面板整体往上让出一个内边距。
            int y = rowRect.Y - padding;
            if (y + height > area.Y + area.Height)
            {
                // 往下装不下：改成「最后一项的底边对齐这一行的底边」，面板整体往上摆。
                y = rowRect.Y + rowRect.Height + padding - height;
            }
            int x = menuRect.X - gap - width;
            // 屏幕边缘兜底：列表整体始终留在工作区里（比工作区还宽 / 还高时贴住左上角，
            // 只能让右边 / 下边溢出一点，总比整块跑到屏幕外强）。
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            // 摆好之后把它激活：亚克力只有在窗口处于活动状态时才是模糊的观感，否则系统会画成接近纯色的浅色。
            // 托盘菜单会因为这一次激活收到「失去活动」，那边的处理器在列表可见时不会把菜单收起来。
            AppWindow.Show(true);
            User32.SetForegroundWindow(WindowHandle);
            AppWindow.MoveInZOrderAtTop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show launch profile popup ({biz})", gameBiz);
        }
    }




    public override void Hide()
    {
        _pointerOnPanel = false;
        _wasActive = false;
        base.Hide();
    }




    private void GameLaunchProfilePopupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState is WindowActivationState.Deactivated)
        {
            // 显示的一瞬间可能先收到一次「失去活动」，只有真的活动过才算"用户点去别处了"。
            if (_wasActive && !_pointerOnPanel)
            {
                _wasActive = false;
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _wasActive = true;
        }
    }




    private void LaunchProfileFlyout_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOnPanel = true;
        PanelPointerEntered?.Invoke(this, EventArgs.Empty);
    }



    private void LaunchProfileFlyout_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOnPanel = false;
        PanelPointerExited?.Invoke(this, EventArgs.Empty);
    }



    private void LaunchProfileFlyout_ProfileSelected(object? sender, GameLaunchProfile profile)
    {
        ProfileSelected?.Invoke(this, profile);
    }



}
