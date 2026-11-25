using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using NuGet.Versioning;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Account;
using Starward.Features.Gacha;
using Starward.Features.GameLauncher;
using Starward.Features.GamepadControl;
using Starward.Features.GameRecord;
using Starward.Features.GameSelector;
using Starward.Features.Passport;
using Starward.Features.Screenshot;
using Starward.Features.SelfQuery;
using Starward.Features.Setting;
using Starward.Features.Update;
using Starward.GameInstall;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class MainView : UserControl
{

    public static MainView Current { get; private set; }


    private readonly ILogger<MainView> _logger = AppConfig.GetLogger<MainView>();

    private readonly PassportService _passportService = AppConfig.GetService<PassportService>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();

    private readonly WindowsDeviceFpService _windowsDeviceFpService = AppConfig.GetService<WindowsDeviceFpService>();


    public GameId? CurrentGameId { get; private set => SetProperty(ref field, value); }

    public LauncherConfigModel? CurrentLauncherConfig { get; private set => SetProperty(ref field, value); }

    private GameFeatureConfig CurrentGameFeatureConfig { get; set; }



    public MainView()
    {
        Current = this;
        this.InitializeComponent();
        InitializeMainView();
    }



    private void InitializeMainView()
    {
        this.Loaded += MainView_Loaded;
        WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(this, OnMainViewNavigateMessageReceived);
        WeakReferenceMessenger.Default.Register<BH3GlobalGameServerChangedMessage>(this, OnBH3GlobalGameServerChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, (_, _) => _ = CheckUpdateOrShowRecentUpdateContentAsync());
        WeakReferenceMessenger.Default.Register<EnabledLauncherConfigChangedMessage>(this, OnEnabledLauncherConfigChanged);
        GameId? gameId = GameSwitcher.CurrentGameIcon?.GameId;
        if (gameId?.GameBiz == GameBiz.bh3_global)
        {
            string? id = AppConfig.LastGameIdOfBH3Global;
            if (!string.IsNullOrWhiteSpace(id))
            {
                gameId = new GameId(id, GameBiz.bh3_global);
            }
        }
        CurrentGameId = gameId;
        CurrentLauncherConfig = LauncherConfigModel.FromId(GameSwitcher.CurrentGameIcon?.LauncherId);
        UpdateNavigationView();
        InitializePassportUser();
        if (CurrentGameId is null)
        {
            ShowLauncherConfigView();
        }
    }




    private async void MainView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CheckSystemProxy();
        HotkeyManager.InitializeHotkey(this.XamlRoot.GetWindowHandle());
        _ = CheckUpdateOrShowRecentUpdateContentAsync();

        if (AppConfig.EnableGamepadController)
        {
            await Task.Delay(1000);
            var queue = Content.DispatcherQueue;
            _ = Task.Run(() => GamepadController.Initialize(queue));
        }
    }


    private async void CheckSystemProxy()
    {
        try
        {
            await Task.Delay(1500);
            Uri? proxy = HttpClient.DefaultProxy.GetProxy(new Uri("https://starward.scighost.com"));
            if (proxy is not null)
            {
                InAppToast.MainWindow?.Information(Lang.MainView_CheckSystemProxy_SystemProxyIsEnabled, proxy.ToString(), 5000);
            }
        }
        catch { }
    }


    private void GameSwitcher_CurrentGameChanged(object? sender, PinnedGameIcon icon)
    {
        GameId gameId = icon.GameId;
        if (icon.GameId.GameBiz == GameBiz.bh3_global)
        {
            string? id = AppConfig.LastGameIdOfBH3Global;
            if (!string.IsNullOrWhiteSpace(id))
            {
                gameId = new GameId(id, GameBiz.bh3_global);
            }
        }
        CurrentGameId = gameId;
        var newConfig = LauncherConfigModel.FromId(icon.LauncherId);
        if (CurrentLauncherConfig != newConfig)
        {
            CurrentLauncherConfig = newConfig;
            InitializePassportUser();
        }
        UpdateNavigationView();
        HideLauncherConfigView();
        UpdateDragableArea();
    }


    private void OnBH3GlobalGameServerChanged(object _, BH3GlobalGameServerChangedMessage message)
    {
        if (CurrentGameId?.GameBiz == GameBiz.bh3_global && CurrentGameId.Id != message.GameId)
        {
            CurrentGameId = new(message.GameId, GameBiz.bh3_global);
            NavigateTo(typeof(GameLauncherPage), CurrentGameId, new SuppressNavigationTransitionInfo());
        }
    }



    #region UI


    [RelayCommand]
    private void ShowLauncherConfigView()
    {
        LauncherConfigView.Visibility = Visibility.Visible;
        LauncherConfigView.Opacity = 1;
        LauncherConfigView.IsHitTestVisible = true;
        LauncherConfigView.ShowGameInfosIfNoGames();
    }



    private void HideLauncherConfigView()
    {
        LauncherConfigView.GoBack();
        LauncherConfigView.GoBack();
    }


    /// <summary>
    /// 更新窗口拖拽区域
    /// </summary>
    private void GameSwitcher_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDragableArea();
    }



    private void UpdateDragableArea()
    {
        try
        {
            double x = GameSwitcher.ActualWidth + 62;
            this.XamlRoot.SetWindowDragRectangles([new Rect(x, 0, 10000, 48)]);
        }
        catch { }
    }


    #endregion



    #region Navigation





    private void UpdateNavigationView()
    {
        CurrentGameFeatureConfig = GameFeatureConfig.FromGameId(CurrentGameId);

        NavigationViewItem_Launcher.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GameLauncherPage)).ToVisibility();
        NavigationViewItem_Screenshot.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(ScreenshotPage)).ToVisibility();
        NavigationViewItem_GachaLog.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GachaLogPage)).ToVisibility();
        NavigationViewItem_HoyolabToolbox.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GameRecordPage)).ToVisibility();
        NavigationViewItem_SelfQuery.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(SelfQueryPage)).ToVisibility();
        NavigationViewItem_GenshinBeyondGacha.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GenshinBeyondGachaPage)).ToVisibility();

        if (CurrentGameId?.GameBiz.IsChinaServer() ?? false)
        {
            ToolTipService.SetToolTip(NavigationViewItem_HoyolabToolbox, Lang.HyperionToolbox);
            TextBlock_HoyolabToolbox.Text = Lang.HyperionToolbox;
        }
        if (CurrentGameId?.GameBiz.IsGlobalServer() ?? false)
        {
            ToolTipService.SetToolTip(NavigationViewItem_HoyolabToolbox, Lang.HoYoLABToolbox);
            TextBlock_HoyolabToolbox.Text = Lang.HoYoLABToolbox;
        }

        if (CurrentGameId is null)
        {
            NavigateTo(typeof(BlankPage));
        }
        else if (MainView_Frame.SourcePageType?.Name is not nameof(SettingPage))
        {
            NavigateTo(MainView_Frame.SourcePageType);
        }
    }



    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer?.IsSelected ?? false)
            {
                return;
            }
            if (args.IsSettingsInvoked)
            {
                NavigateTo(typeof(SettingPage));
            }
            else
            {
                if (args.InvokedItemContainer is NavigationViewItem item)
                {
                    var type = item.Tag switch
                    {
                        nameof(GameLauncherPage) => typeof(GameLauncherPage),
                        nameof(ScreenshotPage) => typeof(ScreenshotPage),
                        nameof(GachaLogPage) => typeof(GachaLogPage),
                        nameof(GameRecordPage) => typeof(GameRecordPage),
                        nameof(SelfQueryPage) => typeof(SelfQueryPage),
                        nameof(GenshinBeyondGachaPage) => typeof(GenshinBeyondGachaPage),
                        _ => null,
                    };
                    NavigateTo(type);
                }
            }
        }
        catch { }
    }



    private void NavigateTo(Type? page, object? param = null, NavigationTransitionInfo? infoOverride = null)
    {
        page ??= typeof(GameLauncherPage);
        if (page.Name is nameof(BlankPage) && CurrentGameId is null)
        {

        }
        else if (page.Name is not nameof(SettingPage) && !CurrentGameFeatureConfig.SupportedPages.Contains(page.Name))
        {
            page = typeof(GameLauncherPage);
        }
        if (page.Name is nameof(GameLauncherPage))
        {
            MainView_NavigationView.SelectedItem = NavigationViewItem_Launcher;
        }
        MainView_Frame.Navigate(page, param ?? CurrentGameId, infoOverride);
        if (page.Name is nameof(BlankPage) or nameof(GameLauncherPage))
        {
            Border_OverlayMask.Opacity = 0;
        }
        else
        {
            Border_OverlayMask.Opacity = 1;
        }
    }



    private void OnMainViewNavigateMessageReceived(object _, MainViewNavigateMessage message)
    {
        if (message.GameId is not null)
        {
            CurrentGameId = message.GameId;
            // 首次从「选择游戏」页进入时也要记住当前游戏：以前只有点顶部游戏图标才写，
            // 于是重启后「当前启动器页」又丢了。
            AppConfig.CurrentGameId = message.GameId.Id;
            UpdateNavigationView();
            var launcher = _hoYoPlayService.GetLauncherConfig(message.GameId);
            if (launcher is not null)
            {
                var newConfig = LauncherConfigModel.FromId(launcher.Id);
                if (CurrentLauncherConfig != newConfig)
                {
                    CurrentLauncherConfig = newConfig;
                    InitializePassportUser();
                }
            }
            else
            {
                CurrentLauncherConfig = null;
            }
        }
        // Parameter 是页面自带的导航参数（如设置页深链锚点），优先于游戏 Id
        NavigateTo(message.Page, message.Parameter ?? (object?)message.GameId);
        HideLauncherConfigView();
    }



    private async void OnEnabledLauncherConfigChanged(object _, EnabledLauncherConfigChangedMessage message)
    {
        await Task.Delay(300);
        CurrentGameId = GameSwitcher.CurrentGameIcon?.GameId;
        if (GameSwitcher.CurrentGameIcon is not null)
        {
            var newConfig = LauncherConfigModel.FromId(GameSwitcher.CurrentGameIcon.LauncherId);
            if (CurrentLauncherConfig != newConfig)
            {
                CurrentLauncherConfig = newConfig;
                InitializePassportUser();
            }
        }
        else
        {
            CurrentLauncherConfig = null;
        }
        UpdateNavigationView();
    }




    #endregion



    #region Update


    private DateTimeOffset _lastCheckUpdateTime;

    private DateTimeOffset _lastShowUpdateTime;

    private SemaphoreSlim _updateLock = new(1, 1);


    private async Task CheckUpdateOrShowRecentUpdateContentAsync()
    {
#if DEBUG || DONOT_CHECK_UPDATE
        return;
#endif
#pragma warning disable CS0162 // 检测到无法访问的代码
        if (!await _updateLock.WaitAsync(0))
        {
            return;
        }
        await Task.Delay(1000);
#pragma warning restore CS0162 // 检测到无法访问的代码
        try
        {
            if (_lastCheckUpdateTime == default && NuGetVersion.TryParse(AppConfig.AppVersion, out var appVersion))
            {
                _ = NuGetVersion.TryParse(AppConfig.LastAppVersion, out var lastVersion);
                if (appVersion != lastVersion)
                {
                    if (AppConfig.ShowUpdateContentAfterUpdateRestart)
                    {
                        new UpdateWindow().Activate();
                    }
                    else
                    {
                        AppConfig.LastAppVersion = AppConfig.AppVersion;
                    }
                    _lastCheckUpdateTime = DateTimeOffset.Now - TimeSpan.FromMinutes(55);
                    return;
                }
            }
            DateTimeOffset now = DateTimeOffset.Now;
            if (now - _lastCheckUpdateTime > TimeSpan.FromHours(1))
            {
                var release = await AppConfig.GetService<UpdateService>().CheckUpdateAsync(false);
                _lastCheckUpdateTime = now;
                if (release != null && now - _lastShowUpdateTime > TimeSpan.FromHours(6) && now.Date != _lastShowUpdateTime.Date)
                {
                    new UpdateWindow { NewVersion = release }.Activate();
                    _lastShowUpdateTime = now;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check update");
        }
        finally
        {
            _updateLock.Release();
        }
    }


    #endregion



    #region Passport


    [ObservableProperty]
    public partial PassportUserInfoModel? CurrentPassportUser { get; set; }

    public ObservableCollection<PassportUserInfoModel>? PassportUsers { get; set => SetProperty(ref field, value); }

    public bool AuthLoginGameByPassportAccount
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.AuthLoginGameByPassportAccount = value;
            }
        }
    } = AppConfig.AuthLoginGameByPassportAccount;


    private void InitializePassportUser()
    {
        try
        {
            CurrentPassportUser = null;
            PassportUsers = null;
            UpdateHypFlyoutLayout();
            if (CurrentLauncherConfig?.CanLogin ?? false)
            {
                _ = _windowsDeviceFpService.InitializeDeviceFpAsync(CurrentLauncherConfig.Region);
                PassportUsers = new(PassportService.GetPassportUserInfos(CurrentLauncherConfig.Region));
                ApplyPassportUserOrder();
                if (PassportUsers.Count > 0)
                {
                    CurrentPassportUser = PassportUsers.FirstOrDefault(x => x.Aid == AppConfig.GetLastPassportUserAid(CurrentLauncherConfig.Region));
                    CurrentPassportUser ??= PassportUsers.FirstOrDefault();
                    UpdateHypFlyoutLayout();
                    _ = VerifyPassportSessionAsync(PassportUsers);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize passport user");
        }
    }


    private async Task VerifyPassportSessionAsync(IEnumerable<PassportUserInfoModel> list)
    {
        try
        {
            await Task.WhenAll(list.Select(x => _passportService.VerifySessionAsync(x)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verfiy passport session");
        }
    }


    partial void OnCurrentPassportUserChanged(PassportUserInfoModel? value)
    {
        _hoYoPlayService.PassportUserInfo = value;
        if (value is not null)
        {
            AppConfig.SetLastPassportUserAid(value.Region, value.Aid);
            // 账号中心切到哪个账号，首页的米游社工具（体力 / 签到等）就用哪个账号；
            // 这个「当前通行证账号」不再跟着启动页的游戏账号切换器走。
            AppConfig.GetService<PassportAccountService>().UseAccountCenterAccount(value.Region, value.Aid, value.Mid);
        }
    }


    [RelayCommand]
    public async Task<PassportUserInfoModel?> HypLoginAsync()
    {
        try
        {
            if (CurrentLauncherConfig?.CanLogin ?? false)
            {
                var model = await new HypLoginWindow
                {
                    ParentWindowHandle = MainWindow.Current.WindowHandle,
                    ServerRegion = CurrentLauncherConfig.Region
                }.LoginAsync();
                if (model is not null)
                {
                    AddPassportUser(model);
                    CurrentPassportUser = model;
                    UpdateHypFlyoutLayout();
                    PassportService.SavePassportUserInfo(model);
                    MainWindow.Current.Show();
                    // 统一账号模型：HYP 登录得到的账号同时放进账号池（国际服账号也支持），
                    // 启动页自动登录和米游社工具箱都从账号池取账号。
                    await AppConfig.GetService<PassportAccountService>().ImportPassportUserInfoAsync(model);
                    WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(true));
                    await AppConfig.GetService<GameRecordRoleService>().UpdateGameRolesByPassportAsync(model);
                    return model;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hyp login");
            InAppToast.MainWindow?.Error(ex, Lang.MainView_LoginFailed);
        }
        return null;
    }


    /// <summary>
    /// 把登录 / 导入得到的账号放进账号列表：已经存在时在原位置替换，否则追加到末尾。
    /// </summary>
    private void AddPassportUser(PassportUserInfoModel model)
    {
        PassportUsers ??= new();
        if (PassportUsers.FirstOrDefault(x => x.Aid == model.Aid) is PassportUserInfoModel oldModel)
        {
            int index = PassportUsers.IndexOf(oldModel);
            PassportUsers.Remove(oldModel);
            PassportUsers.Insert(index, model);
        }
        else
        {
            PassportUsers.Add(model);
        }
    }



    #region 右键「添加账号」走 main 的 Cookie 导入登录


    /// <summary>
    /// 右键「+」：打开 main 原来的 Cookie / 令牌导入登录。
    /// 单击仍然是上游的 HYP 网页登录，右键只是额外的入口。
    /// <para>
    /// 之前用的是长按（Holding）：触屏 / 触控板以外的设备（尤其鼠标）基本触发不了，
    /// 改成右键后各种输入设备都能用。
    /// </para>
    /// </summary>
    private void Button_HypAddAccount_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 别再让右键继续冒泡成上下文菜单或触发按钮的 Click。
        e.Handled = true;
        _ = ShowCookieImportDialogAsync();
    }


    private async void Button_HypAddAccount_Click(object sender, RoutedEventArgs e)
    {
        await HypLoginAsync();
    }


    private async Task ShowCookieImportDialogAsync()
    {
        try
        {
            var dialog = new PassportCookieImportDialog { XamlRoot = this.XamlRoot };
            // 导入成功后对话框会自己 Hide()，ShowAsync 的返回值不一定是 Primary，只认导入结果。
            _ = await dialog.ShowAsync();
            if (dialog.LoginModel is PassportUserInfoModel model)
            {
                AddPassportUser(model);
                CurrentPassportUser = model;
                UpdateHypFlyoutLayout();
                // 账号池、上游账号表、工具箱 Cookie 在导入流程（ImportStokenAsync）里已经全部写好了，
                // 这里不能再走一遍 ImportPassportUserInfoAsync：它会用一份不含派生令牌的令牌集合
                // 覆盖刚登录好的记录，让账号池退回到「只有 SToken」的状态。
                WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(true));
                try
                {
                    // 上游这里用 SToken 走 getGameRolesByStoken 拉角色，浏览器导出的 SToken 不一定会被
                    // 这个接口接受（实测返回 -100 登录状态失效）。角色在导入时已经按工具箱 Cookie 同步过，
                    // 这一步失败不能算登录失败，否则登录成功也会弹一条「登录失败」。
                    await AppConfig.GetService<GameRecordRoleService>().UpdateGameRolesByPassportAsync(model);
                }
                catch (miHoYoApiException ex)
                {
                    _logger.LogWarning(ex, "Refresh the game roles of the imported passport account (aid: {aid})", model.Aid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import passport cookie");
            InAppToast.MainWindow?.Error(ex, Lang.MainView_LoginFailed);
        }
    }


    #endregion



    #region 账号列表拖动排序


    private const string PassportUserOrderKey = "PassportUserOrder";


    /// <summary>
    /// 按保存下来的顺序重排账号列表；没有记录过顺序的账号保持数据库返回的相对顺序排在后面。
    /// 账号中心（上游 HYP 浮窗）里的列表和 main 的账号中心一样支持拖动排序。
    /// </summary>
    private void ApplyPassportUserOrder()
    {
        try
        {
            if (PassportUsers is null || CurrentLauncherConfig is null)
            {
                return;
            }
            string region = CurrentLauncherConfig.Region.ToString();
            string? order = AppConfig.GetValue<string>(null, PassportUserOrderKey, region);
            if (string.IsNullOrWhiteSpace(order))
            {
                return;
            }
            var aids = order.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => long.TryParse(x, out long aid) ? aid : 0)
                            .Where(x => x > 0)
                            .ToList();
            if (aids.Count == 0)
            {
                return;
            }
            var ordered = new List<PassportUserInfoModel>(PassportUsers.Count);
            foreach (long aid in aids)
            {
                if (PassportUsers.FirstOrDefault(x => x.Aid == aid) is PassportUserInfoModel model && !ordered.Contains(model))
                {
                    ordered.Add(model);
                }
            }
            ordered.AddRange(PassportUsers.Where(x => !ordered.Contains(x)));
            PassportUsers = new(ordered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply passport user order");
        }
    }


    /// <summary>拖动排序结束后把顺序保存下来（按服务器区域各存一份）。</summary>
    private void ListView_HypAccunt_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            if (PassportUsers is null || CurrentLauncherConfig is null || PassportUsers.Count == 0)
            {
                return;
            }
            AppConfig.SetValue(string.Join(',', PassportUsers.Select(x => x.Aid)), PassportUserOrderKey, CurrentLauncherConfig.Region.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save passport user order");
        }
    }


    #endregion

    private void Button_HypLogout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: PassportUserInfoModel model })
            {
                PassportService.DeletePassportUserInfo(model);
                _ = _passportService.LogoutAsync(model);
                if (AppConfig.GetLastPassportUserAid(model.Region) == model.Aid)
                {
                    AppConfig.SetLastPassportUserAid(model.Region, 0);
                }
                if (CurrentPassportUser == model)
                {
                    PassportUsers?.Remove(model);
                    CurrentPassportUser = PassportUsers?.FirstOrDefault();
                }
                else
                {
                    PassportUsers?.Remove(model);
                }
                UpdateHypFlyoutLayout();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hyp logout");
        }
    }


    [RelayCommand]
    private void HypLogout()
    {
        try
        {
            var model = CurrentPassportUser;
            if (model is null)
            {
                return;
            }
            PassportService.DeletePassportUserInfo(model);
            _ = _passportService.LogoutAsync(model);
            if (AppConfig.GetLastPassportUserAid(model.Region) == model.Aid)
            {
                AppConfig.SetLastPassportUserAid(model.Region, 0);
            }
            PassportUsers?.Remove(model);
            CurrentPassportUser = PassportUsers?.FirstOrDefault();
            UpdateHypFlyoutLayout();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hyp logout");
        }
    }


    private void UpdateHypFlyoutLayout()
    {
        if (PassportUsers?.Count > 1)
        {
            ColumnDefinition_HypLoginColumn0.MaxWidth = 200;
            ListView_HypAccunt.Visibility = Visibility.Visible;
            Button_HypLogout.Visibility = Visibility.Collapsed;
        }
        else if (PassportUsers?.Count > 0)
        {
            ColumnDefinition_HypLoginColumn0.MaxWidth = double.PositiveInfinity;
            ListView_HypAccunt.Visibility = Visibility.Collapsed;
            Button_HypLogout.Visibility = Visibility.Visible;
        }
        else
        {
            ColumnDefinition_HypLoginColumn0.MaxWidth = double.PositiveInfinity;
            ListView_HypAccunt.Visibility = Visibility.Collapsed;
            Button_HypLogout.Visibility = Visibility.Collapsed;
        }
    }


    #endregion


}



file static class BoolToVisibilityExtension
{

    public static Visibility ToVisibility(this bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }

}


