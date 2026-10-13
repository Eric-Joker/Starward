using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Account;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord.AutoRefresh;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using Starward.Features.MapTool;
using Starward.Features.ViewHost;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord;

public sealed partial class GameRecordPage : PageBase
{


    private readonly ILogger<GameRecordPage> _logger = AppConfig.GetLogger<GameRecordPage>();


    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();



    public GameRecordPage()
    {
        this.InitializeComponent();
    }




    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // b 服走的是国服的战绩接口，但它没有米游社账号，登录只能用网页登录，
        // 所以先把原始区服记下来，再按国服处理数据。
        _bilibiliServer = CurrentGameBiz.Server is "bilibili";
        CurrentGameBiz = CurrentGameBiz.Value switch
        {
            GameBiz.hk4e_bilibili => GameBiz.hk4e_cn,
            GameBiz.hkrpg_bilibili => GameBiz.hkrpg_cn,
            GameBiz.nap_bilibili => GameBiz.nap_cn,
            _ => CurrentGameBiz,
        };
        _gameRecordService.IsHoyolab = CurrentGameBiz.IsGlobalServer();
        // 只有国服能用通行证登录（账密 / 短信 / 扫码 / 令牌导入）替代网页登录，
        // 国际服和 b 服都保留原来的网页登录，因此这里按区服切换两种入口。
        UpdateLoginMenuVisibility(CurrentGameBiz.IsGlobalServer() || _bilibiliServer);
        _gameRecordService.Language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        InitializeNavigationViewItemVisibility();
    }


    /// <summary>进来的是不是 b 服（数据按国服处理，但登录不归米游社账号管）。</summary>
    private bool _bilibiliServer;




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
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (r, m) =>
        {
            LoadGameRoles(m.GameRole);
            // 已经打开的战绩窗口跟着换成新的角色，登录之后不用再点一次。
            if (_battleChronicleWindow?.AppWindow is not null && CurrentRole is not null)
            {
                _battleChronicleWindow.CurrentRole = CurrentRole;
                _battleChronicleWindow.Activate();
            }
        });
        WeakReferenceMessenger.Default.Register<GameRecordVerifyAccountMessage>(this, (r, m) =>
        {
            // 1034 / 10041 这类错误都要重新登录一次。国服直接用通行证在账号中心里登录，
            // 国际服没有通行证账号，仍然走工具箱的网页登录（入口在战绩窗口里）。
            RequestLogin();
        });
        WeakReferenceMessenger.Default.Register<GameRecordToolboxRefreshedMessage>(this, (r, m) =>
        {
            // 账号中心刚刷新过工具箱数据，当前页面重新加载一次，直接显示新记录。
            ReloadCurrentPage();
        });
        await Task.Delay(16);
        NavigateTo(typeof(BlankPage));
        if (await CheckAgreementAsync())
        {
            LoadGameRoles();
            await UpdateDeviceInfoAsync();
            await RefreshGameRoleHeadIconSilentlyAsync();
        }
    }



    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        NavigationViewItem_BattleChronicle.Tapped -= NavigationViewItem_BattleChronicle_Tapped;
        NavigationViewItem_MapTool.Tapped -= NavigationViewItem_MapTool_Tapped;
        // 战绩窗口是工具箱页带出来的，页面上已经离开工具箱了，就别再留着它。
        try
        {
            if (_battleChronicleWindow?.AppWindow is not null)
            {
                _battleChronicleWindow.Close();
            }
        }
        catch { }
        // 地图窗口同理，关掉它顺带释放内嵌的 WebView2。
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




    private async Task<bool> CheckAgreementAsync()
    {
        try
        {
            if (!AppConfig.AcceptHoyolabToolboxAgreement)
            {
                var dialog = new ContentDialog
                {
                    Title = Lang.Common_Disclaimer,
                    Content = Lang.HoyolabToolboxPage_DisclaimerContent,
                    PrimaryButtonText = Lang.Common_Accept + " (5s)",
                    SecondaryButtonText = Lang.Common_Reject,
                    IsPrimaryButtonEnabled = false,
                    DefaultButton = ContentDialogButton.Secondary,
                    XamlRoot = this.XamlRoot,
                };
                var resultTask = dialog.ShowAsync();
                bool cancel = false;
                for (int i = 0; i < 5; i++)
                {
                    for (int j = 0; j < 10; j++)
                    {
                        await Task.Delay(100);
                        if (resultTask.Status is Windows.Foundation.AsyncStatus.Completed)
                        {
                            cancel = true;
                            break;
                        }
                    }
                    if (cancel)
                    {
                        break;
                    }
                    dialog.PrimaryButtonText = Lang.Common_Accept + $" ({4 - i}s)";
                }
                dialog.PrimaryButtonText = Lang.Common_Accept;
                dialog.IsPrimaryButtonEnabled = true;
                var result = await resultTask;
                if (result is ContentDialogResult.Primary)
                {
                    AppConfig.AcceptHoyolabToolboxAgreement = true;
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(GameLauncherPage)));
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check agreement.");
            return false;
        }
    }




    #region Navigation Style


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




    #endregion




    #region Game Role Info



    public GameRecordRole? CurrentRole
    {
        get;
        set
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
                _gameRecordService.SetLastSelectGameRecordRole(CurrentGameBiz, role);
            }
            role ??= _gameRecordService.GetLastSelectGameRecordRoleOrTheFirstOne(CurrentGameBiz);
            var list = _gameRecordService.GetGameRoles(CurrentGameBiz);
            CurrentRole = role ?? list.FirstOrDefault();
            GameRoleList = list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load game roles ({gameBiz}).", CurrentGameBiz);
        }
    }




    [RelayCommand]
    private void WebLogin()
    {
        NavigateTo(typeof(LoginPage), CurrentGameBiz);
    }




    /// <summary>
    /// 国服工具箱的登录入口和刷新信息都收在账号中心里，这里按区服决定工具箱里显示什么。
    /// <para>
    /// 国际服和 b 服没有米游社通行证账号，保留原来的网页登录菜单；只有国服把登录、
    /// 刷新信息都收进左侧导航栏的账号中心。
    /// </para>
    /// </summary>
    private void UpdateLoginMenuVisibility(bool keepWebLogin)
    {
        Visibility webVisibility = keepWebLogin ? Visibility.Visible : Visibility.Collapsed;
        MenuItem_WebLogin_1.Visibility = webVisibility;
        MenuItem_WebLogin_2.Visibility = webVisibility;
        if (!keepWebLogin)
        {
            // 只剩「添加账号」这一项，直接点开账号中心，不再多一层菜单。
            Button_Login.Flyout = null;
            Button_RefreshGameRoleInfo.Visibility = Visibility.Collapsed;
            Button_AddToolboxAccount.Visibility = Visibility.Collapsed;
            Separator_LoginActions.Visibility = Visibility.Collapsed;
        }
    }




    /// <summary>米游社通行证只有国服有，b 服和国际服都不走这条线。</summary>
    private bool CanUsePassportAccount => CurrentGameBiz.Server is "cn" && !_bilibiliServer;




    /// <summary>国服：打开统一的账号中心；其他区服的登录方式仍由菜单提供。</summary>
    private void Button_Login_Click(object sender, RoutedEventArgs e)
    {
        if (CanUsePassportAccount)
        {
            ShowAccountCenter();
        }
    }




    [RelayCommand]
    private async Task RefreshGameRoleInfoAsync()
    {
        try
        {
            // 用户自己点的「刷新信息」：这是主干里少数保留人机验证弹窗的路径，
            // 其余后台 / 自动路径默认抑制（不加载极验的第三方脚本）。
            using (GameRecordService.AllowInteractiveCaptcha())
            {
                if (CurrentRole is null)
                {
                    await _gameRecordService.RefreshAllGameRolesInfoAsync();
                }
                else
                {
                    await _gameRecordService.RefreshGameRoleInfoAsync(CurrentRole);
                }
            }
            LoadGameRoles();
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            InAppToast.MainWindow?.Warning(Lang.Common_AccountError, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            InAppToast.MainWindow?.Warning(Lang.Common_NetworkError, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            InAppToast.MainWindow?.Error(ex);
        }
    }



    private void ListView_GameRoles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is GameRecordRole role)
        {
            CurrentRole = role;
            _gameRecordService.SetLastSelectGameRecordRole(CurrentGameBiz, role);
            if (frame.SourcePageType?.Name is not nameof(LoginPage))
            {
                NavigateTo(frame.SourcePageType, force_navigate: true);
            }
        }
    }




    private void MenuFlyoutItem_CopyCookie_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: GameRecordRole role })
            {

                ClipboardHelper.SetText(role.Cookie);
            }
        }
        catch { }
    }



    private void MenuFlyoutItem_DeleteGameRole_Click(object sender, RoutedEventArgs e)
    {
        GameRecordRole? gameRole = null;
        try
        {
            if (sender is FrameworkElement { Tag: GameRecordRole role })
            {
                gameRole = role;
                _gameRecordService.DeleteGameRole(role);
                LoadGameRoles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete game role ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }



    /// <summary>
    /// 生成一个设备信息输入框，默认填当前值。
    /// </summary>
    private static TextBox CreateDeviceInfoBox(string placeholder, string text)
    {
        return new TextBox
        {
            IsSpellCheckEnabled = false,
            PlaceholderText = placeholder,
            Text = text,
        };
    }




    /// <summary>一段小字说明，跟着输入框一起放进弹窗。</summary>
    private static TextBlock CreateTipText(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };
    }




    /// <summary>
    /// 输入 Cookie：Cookie 与设备信息都可以单独填，弹窗里会互相解析/回填。
    /// <para>
    /// 便笺、绳网月报这些接口只认 <c>x-rpc-device_fp</c>，而服务端签发的指纹不一定被接受，
    /// 所以额外留一个指纹输入框；留空则沿用已经保存的指纹。
    /// 任意一栏粘贴整段请求头，其余几栏都会被识别出来。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task InputCookieAsync()
    {
        try
        {
            var textbox = new TextBox
            {
                IsSpellCheckEnabled = false,
            };
            var fpTextBox = CreateDeviceInfoBox(Lang.PassportLogin_DeviceFp, "");
            var deviceNameBox = CreateDeviceInfoBox(Lang.PassportLogin_DeviceName, AppConfig.HyperionDeviceName);
            var systemVersionBox = CreateDeviceInfoBox(Lang.PassportLogin_SystemVersion, AppConfig.HyperionSystemVersion);
            var appVersionBox = CreateDeviceInfoBox(Lang.PassportLogin_AppVersion, AppConfig.HyperionAppVersion);
            var panel = new StackPanel
            {
                Spacing = 8,
            };
            panel.Children.Add(textbox);
            panel.Children.Add(CreateTipText(Lang.PassportLogin_DeviceFpTip));
            panel.Children.Add(fpTextBox);
            panel.Children.Add(deviceNameBox);
            panel.Children.Add(systemVersionBox);
            panel.Children.Add(appVersionBox);
            panel.Children.Add(CreateTipText(Lang.PassportLogin_DeviceInfoTip));
            // 本程序还在用自己申请的指纹时多说一句：这一栏正是粘一次浏览器 Cookie / 抓包、
            // 让服务端把这枚指纹登记到账号上的地方。
            if (GameRecordService.CanPromptAccountDeviceFp() && GameRecordService.IsDeviceProfileNeutral())
            {
                panel.Children.Add(CreateTipText(Lang.HoyolabToolboxPage_DeviceIdentityHint));
            }
            var dialog = new ContentDialog
            {
                Title = Lang.HoyolabToolboxPage_InputCookie,
                Content = panel,
                PrimaryButtonText = Lang.Common_Confirm,
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result is ContentDialogResult.Primary)
            {
                var cookie = textbox.Text;
                // 任意一栏粘贴整段请求头，其余几栏都能补全；填过的项不受影响。
                var info = _gameRecordService.ParseDeviceInfoInputs(
                    cookie, fpTextBox.Text, deviceNameBox.Text, systemVersionBox.Text, appVersionBox.Text);
                // 解析结果填回输入框，方便用户核对。
                fpTextBox.Text = info.DeviceFp ?? "";
                deviceNameBox.Text = info.DeviceName ?? "";
                systemVersionBox.Text = info.SystemVersion ?? "";
                appVersionBox.Text = info.AppVersion ?? "";
                // 指纹要单独存：它决定请求里 x-rpc-device_fp 与 Cookie DEVICEFP 的值。
                bool fpSaved = _gameRecordService.SaveDeviceFp(info.DeviceFp);
                bool infoSaved = _gameRecordService.SaveDeviceInfo(info.DeviceName, info.SystemVersion, info.AppVersion);
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    // 只填了指纹或设备信息：不用登录，已经生效。
                    if (fpSaved)
                    {
                        InAppToast.MainWindow?.Success(Lang.PassportLogin_DeviceFpSaved);
                    }
                    else if (infoSaved)
                    {
                        InAppToast.MainWindow?.Success(Lang.PassportLogin_DeviceInfoSaved);
                    }
                    else
                    {
                        _logger.LogInformation("Input cookie, device fingerprint and device info are all null or white space.");
                    }
                    return;
                }
                var user = await _gameRecordService.AddRecordUserAsync(cookie);
                var roles = await _gameRecordService.AddGameRolesAsync(cookie);
                // 手工粘贴和通行证登录写的是同一批位置：粘贴之后，通行证在登录、换票、切换账号时
                // 会用自己换到的令牌重写工具箱 Cookie，把这次粘贴的键冲掉。这里把粘贴的键合并进
                // 该账号的工具箱 Cookie 并写回账号行与角色行（同名键以粘贴为准），
                // 之后 CreateToolboxCookie 的合并会保留它，粘贴才对战绩请求确定生效。
                // 账号 ID 用粘贴的 Cookie 里的 account_id，而不是接口报回来的 UID：
                // 粘贴写下的那一行是接口按它自己报的 UID 建的，通行证索引的是 account_id，两边都要更新。
                long accountId = GameRecordService.GetCookieAccountId(cookie);
                var (accountRows, roleRows) = _gameRecordService.UpdateToolboxCookieFromPaste(accountId, cookie, user.Uid);
                _logger.LogInformation(
                    "Pasted cookie applied to the toolbox account rows and roles ({accountRows} account row(s), {roleRows} role row(s); account id from paste: {hasAccountId}, response uid differs: {uidDiffers}).",
                    accountRows, roleRows, accountId > 0, user.Uid != accountId);
                // 同时填了指纹的话，两个提示合并成一条：登录成功，且设备指纹已保存。
                if (fpSaved)
                {
                    InAppToast.MainWindow?.Success(Lang.PassportLogin_DeviceFpSavedWithSignIn);
                }
                else
                {
                    string message = string.Format(Lang.LoginPage_AlreadyAddedGameRoles, roles.Count, string.Join("\r\n", roles.Select(x => $"{x.Nickname}  {x.Uid}")));
                    if (infoSaved)
                    {
                        // 设备信息与上面的提示会互相顶掉，所以拼进同一条。
                        message = $"{message}\r\n{Lang.PassportLogin_DeviceInfoSaved}";
                    }
                    InAppToast.MainWindow?.Success(null, message, 5000);
                }
                LoadGameRoles(roles.FirstOrDefault(x => x.GameBiz == CurrentGameBiz.ToString()));
            }
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Input cookie");
            InAppToast.MainWindow?.Warning(Lang.Common_AccountError, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Input cookie");
            InAppToast.MainWindow?.Warning(Lang.Common_NetworkError, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input cookie");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    private async Task RefreshGameRoleHeadIconSilentlyAsync()
    {
        try
        {
            if (CurrentRole is not null)
            {
                // 静默刷新（用户没点任何东西）：遇到风控直接放弃这次头像更新，不弹人机验证、不加载第三方脚本。
                using (GameRecordService.SuppressInteractiveCaptcha())
                {
                    await _gameRecordService.UpdateGameRoleHeadIconAsync(CurrentRole);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update game role head icon silently ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
        }
    }




    #endregion




    #region Navigate



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



    /// <summary>
    /// 工具箱里的数据页换了：把页脚那个「自动更新」入口指向当前页面对应的数据板块。
    /// <para>
    /// 主干没有 TZ 那种「每个数据页工具栏最左边各放一个配置按钮」的接线（那些页面不归本次移植改），
    /// 所以整个工具箱共用页脚上的一个入口，配置哪个板块由这里按当前 frame 的内容换算。
    /// 没有对应板块的页面（空白页、登录页、地图工具）把入口收起来，免得用户以为配的是刚离开的那页。
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
            // 网页里的登录按钮原本点了没有反应（JS 桥收下了消息却没有回话），
            // 现在把它转交给应用自己的登录入口。
            _battleChronicleWindow.LoginRequested += (_, _) => RequestLogin();
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




    /// <summary>
    /// 需要重新登录时打开登录入口：国服是账号中心（通行证登录，顺带刷新工具箱数据），
    /// 国际服没有通行证账号，仍然是工具箱自己的网页登录页。
    /// </summary>
    private void RequestLogin()
    {
        try
        {
            if (CanUsePassportAccount)
            {
                ShowAccountCenter();
            }
            else
            {
                WebLogin();
            }
            // 登录入口在主窗口里，把主窗口带到前面来，否则用户看不到它。
            ((App)Application.Current).EnsureMainWindow();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request login ({gameBiz}).", CurrentGameBiz);
        }
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




    private void ShowAccountCenter()
    {
        AccountCenter.Show(Button_Login);
    }




    #endregion




    #region Device Info




    /// <summary>
    /// 进入工具箱页时确保设备指纹可用：只在完全没有指纹时申请一枚，已有指纹就原样使用。
    /// 这里不提供强制重申请（用户可见的「更新指纹」入口已从界面移除）：
    /// 换掉账号登记过的指纹只会把可用状态换成 10041。
    /// </summary>
    private async Task UpdateDeviceInfoAsync()
    {
        try
        {
            await _gameRecordService.UpdateDeviceFpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update device info");
        }
    }





    #endregion





    /// <summary>
    /// 战绩接口的业务错误统一处理。
    /// <para>
    /// 10041（账号存在风险）在绝区零/崩坏3 这类接口上是「这枚设备指纹没有被该账号登记过」：
    /// 服务端校验的是指纹本身，与机型描述无关（实测把旧指纹写回、身份保持中性默认也能恢复正常）。
    /// 因此带上 <paramref name="gameBiz"/> 的调用方在指纹是「本程序自己申请的」时候，
    /// 会额外补一句可操作的提示——去米游社网页/App 登录一次，或在工具箱里粘贴一次抓包/Cookie。
    /// 用户自己粘贴进来的指纹不会再提示，避免让他重复做同一件事。
    /// </para>
    /// </summary>
    /// <param name="ex">战绩接口抛出的业务异常。</param>
    /// <param name="gameBiz">发起请求的游戏；未知或不传时不补设备指纹提示。</param>
    public static void HandleMiHoYoApiException(miHoYoApiException ex, GameBiz? gameBiz = null)
    {
        if (ex.ReturnCode is 1034 or 5003 or 10035 or 10041 or 10053)
        {
            string? message = ex.Message;
            if (ex.ReturnCode is 10041 && gameBiz?.Game is GameBiz.nap && GameRecordService.CanPromptAccountDeviceFp())
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? Lang.HoyolabToolboxPage_DeviceIdentityErrorHint
                    : $"{message}\r\n{Lang.HoyolabToolboxPage_DeviceIdentityErrorHint}";
            }
            InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning, Lang.Common_AccountError, message, Lang.HoyolabToolboxPage_VerifyAccount, () =>
            {
                WeakReferenceMessenger.Default.Send(new GameRecordVerifyAccountMessage());
            });
        }
        else
        {
            InAppToast.MainWindow?.Warning(Lang.Common_AccountError, ex.Message);
        }
    }


}
