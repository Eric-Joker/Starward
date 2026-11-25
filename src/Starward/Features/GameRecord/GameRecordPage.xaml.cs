using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.GameRecord.AutoRefresh;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using Starward.Features.MapTool;
using Starward.Features.Passport;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord;

public sealed partial class GameRecordPage : PageBase
{

    private readonly ILogger<GameRecordPage> _logger = AppConfig.GetLogger<GameRecordPage>();

    private readonly GameRecordRoleService _gameRecordService = AppConfig.GetService<GameRecordRoleService>();


    public GameRecordPage()
    {
        this.InitializeComponent();
    }



    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        InitializeNavigationViewItemVisibility();
    }


    protected override async void OnLoaded()
    {
        if (AppConfig.HoyolabToolboxPaneOpen)
        {
            OpenNavigationViewPane();
        }
        else
        {
            CloseNavigationViewPane();
        }
        WeakReferenceMessenger.Default.Register<HypThirdPartyLoginMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(() => _ = InitializeGameRoleAsync());
        });
        // 账号中心登录 / 切号之后，角色行与 Cookie 已经由账号池服务同步进库，这里重新加载一次；
        // 登录完成可能发生在页面已经打开之后（之前 CurrentRole 还是空的）。
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LoadGameRoles(m.GameRole);
                // 已经打开的战绩窗口跟着换成新的角色，登录之后不用再点一次。
                if (_battleChronicleWindow?.AppWindow is not null && CurrentRole is not null)
                {
                    _battleChronicleWindow.CurrentRole = CurrentRole;
                    _battleChronicleWindow.Activate();
                }
                // 账号池刚刷新过工具箱数据，当前子页面重新导航一次，直接显示新记录。
                ReloadCurrentPage();
            });
        });
        await Task.Delay(16);
        NavigateTo(typeof(BlankPage));
        await InitializeGameRoleAsync();
    }


    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        NavigationViewItem_BattleChronicle.Tapped -= NavigationViewItem_BattleChronicle_Tapped;
        NavigationViewItem_MapTool.Tapped -= NavigationViewItem_MapTool_Tapped;
        NavigationViewItem_UpdateDeviceInfo.Tapped -= NavigationViewItem_UpdateDeviceInfo_Tapped;
        // 战绩 / 地图窗口是工具箱页带出来的，离开工具箱就别再留着它们（顺带释放内嵌的 WebView2）。
        try
        {
            if (_battleChronicleWindow?.AppWindow is not null)
            {
                _battleChronicleWindow.Close();
            }
        }
        catch { }
        try
        {
            if (_mapToolWindow?.AppWindow is not null)
            {
                _mapToolWindow.Close();
            }
        }
        catch { }
        CurrentRole = null;
        GameRoleList = null!;
        _battleChronicleWindow = null;
        _mapToolWindow = null;
    }



    #region Navigate


    public Thickness NavigationViewItemContentMargin { get; set => SetProperty(ref field, value); } = new Thickness(-2, 0, 0, 0);


    // Close pane
    private void Grid_Avatar_1_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        CloseNavigationViewPane();
    }


    // Open pane
    private void Border_Avatar_2_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        OpenNavigationViewPane();
    }


    private void OpenNavigationViewPane()
    {
        NavigationViewItemContentMargin = new Thickness(-2, 0, 0, 0);
        NavigationView_Toolbox.IsPaneOpen = true;
        Grid_Avatar_1.Visibility = Visibility.Visible;
        Border_Avatar_2.Visibility = Visibility.Collapsed;
        AppConfig.HoyolabToolboxPaneOpen = true;
    }


    private void CloseNavigationViewPane()
    {
        NavigationViewItemContentMargin = new Thickness(2, 0, 0, 0);
        NavigationView_Toolbox.IsPaneOpen = false;
        Grid_Avatar_1.Visibility = Visibility.Collapsed;
        Border_Avatar_2.Visibility = Visibility.Visible;
        AppConfig.HoyolabToolboxPaneOpen = false;
    }


    private void InitializeNavigationViewItemVisibility()
    {
        if (CurrentGameBiz.Game is GameBiz.bh3)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            // 崩坏3战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/4d94fbd5ff63c8b4344876ce21e04d10_2581928258151711511.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.hk4e)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_TravelersDiary.Visibility = Visibility.Visible;
            NavigationViewItem_SpiralAbyss.Visibility = Visibility.Visible;
            NavigationViewItem_ImaginariumTheater.Visibility = Visibility.Visible;
            NavigationViewItem_StygianOnslaught.Visibility = Visibility.Visible;
            // 原神战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/ced4deac2162690105bbc8baad2b51a3_4109616186965788891.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.hkrpg)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_TrailblazeMonthlyCalendar.Visibility = Visibility.Visible;
            NavigationViewItem_SimulatedUniverse.Visibility = Visibility.Visible;
            NavigationViewItem_ForgottenHall.Visibility = Visibility.Visible;
            NavigationViewItem_PureFiction.Visibility = Visibility.Visible;
            NavigationViewItem_ApocalypticShadow.Visibility = Visibility.Visible;
            NavigationViewItem_ChallengePeak.Visibility = Visibility.Visible;
            // 铁道战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/ade9545750299456a3fcbc8c3b63521d_2941971308029698042.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.nap)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_InterKnotMonthlyReport.Visibility = Visibility.Visible;
            NavigationViewItem_ShiyuDefense.Visibility = Visibility.Visible;
            NavigationViewItem_DeadlyAssault.Visibility = Visibility.Visible;
            // 绝区零战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/bc8f0b7384b306c80f2a1fcca9f3d14b_8590605504999484795.png"));
        }
        // 地图工具：原神、星穹铁道有官方互动地图，其余游戏没有这份目录，直接不显示这一项。
        if (MapToolCatalog.GetOptions(CurrentGameBiz).Count > 0)
        {
            NavigationViewItem_MapTool.Visibility = Visibility.Visible;
            // 原神叫「大地图」，星铁叫「地图」，按游戏取名字。
            TextBlock_MapTool.Text = MapToolCatalog.GetMapTitle(CurrentGameBiz);
            // 图标同样按游戏取：原神一张、星穹铁道一张。
            Image_MapTool.Source = new BitmapImage(new(MapToolCatalog.GetMapIconPath(CurrentGameBiz)));
        }
    }


    private void NavigateTo(Type? page, object? parameter = null, bool force_navigate = false)
    {
        if (page is null)
        {
            return;
        }
        if (!force_navigate && frame.SourcePageType == page)
        {
            return;
        }
        frame.Navigate(page, parameter ?? CurrentRole);
    }


    /// <summary>工具箱数据刚被刷新过，当前的子页面重新导航一次，显示的就是新记录。</summary>
    private void ReloadCurrentPage()
    {
        try
        {
            LoadGameRoles();
            var page = frame.SourcePageType;
            if (page is not null && page != typeof(BlankPage))
            {
                NavigateTo(page, force_navigate: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reload toolbox page.");
        }
    }


    /// <summary>
    /// 工具箱里的数据页换了：把页脚那个「自动更新」入口指向当前页面对应的数据板块。
    /// <para>
    /// 0.19 的数据页工具栏里没有单独的自动更新按钮，整个工具箱共用页脚上的一个入口，
    /// 配置哪个板块由这里按当前 frame 的内容换算。
    /// 没有对应板块的页面（空白页等）把入口收起来，免得用户以为配的是刚离开的那页。
    /// </para>
    /// </summary>
    private void Frame_Navigated(object sender, NavigationEventArgs e)
    {
        try
        {
            if (RecordRefreshConfigButton_Auto is null)
            {
                return;
            }
            if (GetRefreshItem(e.SourcePageType) is RecordRefreshItem item)
            {
                RecordRefreshConfigButton_Auto.GameRole = CurrentRole;
                RecordRefreshConfigButton_Auto.Item = item;
                RecordRefreshConfigButton_Auto.Visibility = Visibility.Visible;
            }
            else
            {
                RecordRefreshConfigButton_Auto.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update the record refresh entry ({page}).", e.SourcePageType);
        }
    }



    /// <summary>
    /// 数据页类型到自动更新板块的换算。没有对应板块的页面返回 null。
    /// </summary>
    /// <param name="pageType">frame 当前页面的类型。</param>
    /// <returns>对应的数据板块；不认识时返回 null。</returns>
    private static RecordRefreshItem? GetRefreshItem(Type? pageType)
    {
        if (pageType == typeof(SpiralAbyssPage))
        {
            return RecordRefreshItem.SpiralAbyss;
        }
        if (pageType == typeof(ImaginariumTheaterPage))
        {
            return RecordRefreshItem.ImaginariumTheater;
        }
        if (pageType == typeof(StygianOnslaughtPage))
        {
            return RecordRefreshItem.StygianOnslaught;
        }
        if (pageType == typeof(TravelersDiaryPage))
        {
            return RecordRefreshItem.TravelersDiary;
        }
        if (pageType == typeof(ForgottenHallPage))
        {
            return RecordRefreshItem.ForgottenHall;
        }
        if (pageType == typeof(PureFictionPage))
        {
            return RecordRefreshItem.PureFiction;
        }
        if (pageType == typeof(ApocalypticShadowPage))
        {
            return RecordRefreshItem.ApocalypticShadow;
        }
        if (pageType == typeof(ChallengePeakPage))
        {
            return RecordRefreshItem.ChallengePeak;
        }
        if (pageType == typeof(SimulatedUniversePage))
        {
            return RecordRefreshItem.SimulatedUniverse;
        }
        if (pageType == typeof(TrailblazeCalendarPage))
        {
            return RecordRefreshItem.TrailblazeCalendar;
        }
        if (pageType == typeof(ShiyuDefensePage))
        {
            return RecordRefreshItem.ShiyuDefense;
        }
        if (pageType == typeof(DeadlyAssaultPage))
        {
            return RecordRefreshItem.DeadlyAssault;
        }
        if (pageType == typeof(InterKnotMonthlyReportPage))
        {
            return RecordRefreshItem.InterKnotReport;
        }
        return null;
    }



    private void NavigationView_Toolbox_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            var item = args.InvokedItemContainer as NavigationViewItem;
            if (item != null)
            {
                if (args.InvokedItemContainer?.IsSelected ?? false)
                {
                    return;
                }
                var type = item.Tag switch
                {
                    nameof(TravelersDiaryPage) => typeof(TravelersDiaryPage),
                    nameof(SpiralAbyssPage) => typeof(SpiralAbyssPage),
                    nameof(ImaginariumTheaterPage) => typeof(ImaginariumTheaterPage),
                    nameof(StygianOnslaughtPage) => typeof(StygianOnslaughtPage),
                    nameof(TrailblazeCalendarPage) => typeof(TrailblazeCalendarPage),
                    nameof(SimulatedUniversePage) => typeof(SimulatedUniversePage),
                    nameof(ForgottenHallPage) => typeof(ForgottenHallPage),
                    nameof(PureFictionPage) => typeof(PureFictionPage),
                    nameof(ApocalypticShadowPage) => typeof(ApocalypticShadowPage),
                    nameof(ChallengePeakPage) => typeof(ChallengePeakPage),
                    nameof(InterKnotMonthlyReportPage) => typeof(InterKnotMonthlyReportPage),
                    nameof(ShiyuDefensePage) => typeof(ShiyuDefensePage),
                    nameof(DeadlyAssaultPage) => typeof(DeadlyAssaultPage),
                    _ => null,
                };
                NavigateTo(type);
            }
        }
        catch { }
    }


    private void NavigationViewItem_BattleChronicle_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ShowBattleChronicleWindow();
    }


    private BattleChronicleWindow? _battleChronicleWindow;


    private void ShowBattleChronicleWindow()
    {
        // 窗口关闭后 AppWindow is null
        if (_battleChronicleWindow?.AppWindow is null)
        {
            _battleChronicleWindow = new BattleChronicleWindow
            {
                CurrentRole = CurrentRole,
            };
        }
        else if (_battleChronicleWindow.CurrentRole != CurrentRole)
        {
            _battleChronicleWindow.CurrentRole = CurrentRole;
        }
        _battleChronicleWindow.Activate();
    }



    private void NavigationViewItem_MapTool_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ShowMapToolWindow();
    }



    private MapToolWindow? _mapToolWindow;



    private void ShowMapToolWindow()
    {
        // 窗口关闭后 AppWindow is null
        if (_mapToolWindow?.AppWindow is null)
        {
            _mapToolWindow = new MapToolWindow
            {
                CurrentGameBiz = CurrentGameBiz,
            };
        }
        else if (_mapToolWindow.CurrentGameBiz != CurrentGameBiz)
        {
            _mapToolWindow.CurrentGameBiz = CurrentGameBiz;
        }
        _mapToolWindow.Activate();
    }


    #endregion




    #region Game Role Info



    public GameRecordRole? CurrentRole
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AvatarUrl));
                // 页脚的「自动更新」是按账号存配置的，换号后要跟着换
                if (RecordRefreshConfigButton_Auto is not null)
                {
                    RecordRefreshConfigButton_Auto.GameRole = value;
                }
            }
        }
    }


    public List<GameRecordRole> GameRoleList { get; set => SetProperty(ref field, value); }


    public string AvatarUrl => !string.IsNullOrWhiteSpace(CurrentRole?.HeadIcon) ? CurrentRole.HeadIcon : $"ms-appx:///Assets/Image/icon_{(CurrentGameBiz.IsGlobalServer() ? "hoyolab" : "hyperion")}.png";


    private void LoadGameRoles(GameRecordRole? role = null)
    {
        try
        {
            if (role != null)
            {
                AppConfig.SetLastSelectGameRecordRole(CurrentGameBiz, role.Uid);
            }
            role ??= GameRecordRoleService.GetLastSelectGameRecordRoleOrTheFirstOne(CurrentGameBiz);
            var list = GameRecordRoleService.GetGameRoles(CurrentGameBiz);
            CurrentRole = role ?? list.FirstOrDefault();
            GameRoleList = list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load game roles ({gameBiz}).", CurrentGameBiz);
        }
    }


    /// <summary>
    /// 进入页面时初始化游戏角色信息
    /// </summary>
    /// <returns></returns>
    private async Task InitializeGameRoleAsync()
    {
        try
        {
            LoadGameRoles();
            if (CurrentRole is null)
            {
                await AddGameRolesByPassportAsync();
            }
            else
            {
                await UpdateSelectedGameRoleAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize game role ({gameBiz}).", CurrentGameBiz);
        }
    }


    /// <summary>
    /// 没有游戏角色时，通过当前通行证账号获取游戏角色
    /// </summary>
    /// <returns></returns>
    private async Task AddGameRolesByPassportAsync()
    {
        var passport = MainView.Current.CurrentPassportUser;
        if (passport is null)
        {
            return;
        }
        try
        {
            await _gameRecordService.UpdateGameRolesByPassportAsync(passport);
            LoadGameRoles();
            if (CurrentRole is not null)
            {
                await UpdateSelectedGameRoleAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game roles by passport ({aid}).", passport.Aid);
        }
    }


    /// <summary>
    /// 更新当前选中的游戏角色信息，失败时跳过
    /// </summary>
    /// <returns></returns>
    private async Task UpdateSelectedGameRoleAsync()
    {
        GameRecordRole? role = CurrentRole;
        if (role is null)
        {
            return;
        }
        try
        {
            await _gameRecordService.UpdateGameRoleAsync(role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update game role ({gameBiz}, {uid}).", role.GameBiz, role.Uid);
        }
        LoadGameRoles();
    }


    [RelayCommand]
    private async Task UpdateGameRolesCookieAsync()
    {
        try
        {
            if (GameRoleList is null)
            {
                return;
            }
            var aids = GameRoleList.Select(x => x.Aid).Distinct().ToList();
            await Parallel.ForEachAsync(aids, UnloadCancellationToken, async (aid, token) =>
            {
                try
                {
                    var passport = PassportService.GetPassportUserInfo(aid);
                    if (passport is null || passport.ErrorType is not PassportErrorType.Success)
                    {
                        return;
                    }
                    await _gameRecordService.UpdateGameRolesByPassportAsync(passport, token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UpdateGameRolesCookieAsync");
                }
            });
            LoadGameRoles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateGameRolesCookieAsync");
        }
    }


    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            var model = await MainView.Current.HypLoginAsync();
            if (model is not null)
            {
                var role = GameRecordRoleService.GetGameRoles(CurrentGameBiz).FirstOrDefault(x => x.Aid == model.Aid);
                LoadGameRoles(role);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Login failed: {ex}");
        }
    }



    private void ListView_GameRoles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is GameRecordRole role)
        {
            CurrentRole = role;
            AppConfig.SetLastSelectGameRecordRole(CurrentGameBiz, role.Uid);
        }
    }


    private void MenuFlyoutItem_DeleteGameRole_Click(object sender, RoutedEventArgs e)
    {
        GameRecordRole? gameRole = null;
        try
        {
            if (sender is FrameworkElement { Tag: GameRecordRole role })
            {
                gameRole = role;
                GameRecordRoleService.DeleteGameRole(role);
                LoadGameRoles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete game role ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }


    private async void NavigationViewItem_UpdateDeviceInfo_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        GameRecordServiceBase.ResetDeviceInfo();
        InAppToast.MainWindow?.Success(Lang.HoyolabToolboxPage_TheDeviceFingerprintIsAlreadyUpdated);
    }



    #endregion


}
