using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.SignIn;
using Starward.Core.HoYoPlay;
using Starward.Features.Setting;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord.SignIn;

/// <summary>
/// 游戏启动页上的每日签到入口：图标按钮 + Flyout 卡片（日历、签到 / 补签、自动签到开关）。
/// </summary>
[INotifyPropertyChanged]
public sealed partial class SignInButton : UserControl
{

    private readonly ILogger<SignInButton> _logger = AppConfig.GetLogger<SignInButton>();

    private readonly SignInService _signInService = AppConfig.GetService<SignInService>();

    private readonly AutoSignInService _autoSignInService = AppConfig.GetService<AutoSignInService>();


    /// <summary>最近一次拉取的补签信息，补签确认弹窗展示消耗用。</summary>
    private SignInResignInfo? _resignInfo;

    /// <summary>Flyout 是否已加载过签到状态，避免重复请求。</summary>
    private bool _statusLoaded;

    /// <summary>最近一次拉取的累计签到天数，语言切换时用于重算 <see cref="TotalSignDaysHint"/>。</summary>
    private int _totalSignDay;


    /// <summary>
    /// 问号提示文案：末尾补一行跳转说明，因为本分支的提示气泡没有「右下角动作区」可承载入口。
    /// </summary>
    public string AutoSignInHintTooltip => $"{Lang.SignInButton_AutoSignInHint}\n\n→ {Lang.SettingPage_StartAtLoginSwitch}";


    /// <summary>初始化控件，默认隐藏，待解析到有效角色后再显示。</summary>
    public SignInButton()
    {
        this.InitializeComponent();
        this.Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
        // TODO(账号中心): 0.19 还没有「账号 / 角色变化」的全局消息，登录完成时不会自动刷新本控件；
        // 账号中心模块迁移后，除发消息外也可以直接调用 <see cref="RefreshForRoleChanged"/>。
    }



    /// <summary>
    /// 语言切换后刷新 x:Bind 绑定与代码格式化的签到文案。
    /// </summary>
    /// <param name="_">消息发送方（未使用）。</param>
    /// <param name="__">语言变更消息（未使用）。</param>
    private void OnLanguageChanged(object _, LanguageChangedMessage __)
    {
        this.Bindings.Update();
        if (_statusLoaded)
        {
            TotalSignDaysHint = string.Format(Lang.SignInButton_SignedInForDays, _totalSignDay);
        }
    }



    /// <summary>
    /// 当前启动页所选游戏，变更时重新解析角色。
    /// </summary>
    public GameId? CurrentGameId
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                if (IsLoaded)
                {
                    InitializeForCurrentGame();
                }
            }
        }
    }


    /// <summary>当前用于签到的游戏角色（最近选中或首个）。</summary>
    private GameRecordRole? GameRecordRole { get; set => SetProperty(ref field, value); }


    /// <summary>本月奖励日历数据源。</summary>
    public ObservableCollection<SignInAwardView> Awards { get; } = new();


    /// <summary>
    /// 签到入口按钮上的图标。
    /// <para>
    /// 主干按游戏用 signin_ys / signin_sr / signin_zzz / signin_bh3 区分，但那几张图随
    /// Starward.Assets 资源包发布，0.19 使用的资源包版本里还没有它们，这里先用通用签到图标；
    /// 资源包升级到含 signin_*.png 的版本后改回按游戏映射即可。
    /// </para>
    /// </summary>
    public string SignInIcon => "ms-appx:///Assets/Image/Sign.png";


    /// <summary>「本月已签 X 天」提示文案。</summary>
    public string? TotalSignDaysHint { get; set => SetProperty(ref field, value); }


    /// <summary>今日是否已签到，控制状态标签与签到按钮可用性。</summary>
    public bool IsTodaySigned { get; set => SetProperty(ref field, value); }


    /// <summary>拉取状态失败时的错误提示。</summary>
    public string? ErrorMessage { get; set => SetProperty(ref field, value); }


    /// <summary>正在拉取签到状态 / 奖励列表，控制日历区加载动画。</summary>
    public bool IsLoading { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 当前签到对应的游戏（已处理 bilibili→cn 映射），自动签到开关按它区分存储。
    /// </summary>
    private GameBiz _signInGameBiz;

    private bool _autoSignInEnabled;

    /// <summary>
    /// 当前游戏的自动签到开关，双向绑定 ToggleSwitch。
    /// </summary>
    public bool AutoSignInEnabled
    {
        get => _autoSignInEnabled;
        set
        {
            if (SetProperty(ref _autoSignInEnabled, value) && !string.IsNullOrEmpty(_signInGameBiz.Value))
            {
                _autoSignInService.SetEnabled(_signInGameBiz, value);
                if (value)
                {
                    _autoSignInService.RequestImmediateCheck();
                }
            }
        }
    }



    /// <summary>控件加载时按当前游戏初始化可见性与角色。</summary>
    private void SignInButton_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeForCurrentGame();
    }


    /// <summary>控件卸载时清理状态，避免切换页面后残留旧数据。</summary>
    private void SignInButton_Unloaded(object sender, RoutedEventArgs e)
    {
        GameRecordRole = null;
        _resignInfo = null;
        _statusLoaded = false;
        Awards.Clear();
        ErrorMessage = null;
        IsLoading = false;
    }



    /// <summary>
    /// 解析当前游戏对应的角色，支持签到时显示按钮。
    /// </summary>
    private void InitializeForCurrentGame()
    {
        try
        {
            if (CurrentGameId is null)
            {
                this.Visibility = Visibility.Collapsed;
                return;
            }
            GameBiz gameBiz = CurrentGameId.GameBiz;
            if (!SignInGameSupport.IsSupported(gameBiz))
            {
                this.Visibility = Visibility.Collapsed;
                return;
            }
            // 自动签到开关按游戏区分：切换游戏时同步开关状态（直接写字段，避免把读到的值回写设置）
            GameBiz signInBiz = SignInGameSupport.Normalize(gameBiz);
            _signInGameBiz = signInBiz;
            OnPropertyChanged(nameof(SignInIcon));
            _autoSignInEnabled = _autoSignInService.IsEnabled(signInBiz);
            OnPropertyChanged(nameof(AutoSignInEnabled));
            // B 服角色可能记在原始的 *_bilibili biz 下，先按原始 biz 找，找不到再按归一化 biz 兜底
            GameRecordRole = GameRecordRoleService.GetLastSelectGameRecordRoleOrTheFirstOne(gameBiz)
                             ?? (signInBiz.Value != gameBiz.Value ? GameRecordRoleService.GetLastSelectGameRecordRoleOrTheFirstOne(signInBiz) : null);
            if (GameRecordRole is null)
            {
                this.Visibility = Visibility.Collapsed;
                return;
            }
            this.Visibility = Visibility.Visible;
            _statusLoaded = false;
            ErrorMessage = null;
            IsLoading = false;
            // 自动签到由常驻循环统一调度，切换游戏只同步开关状态，不在这里触发。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize sign-in button failed (Biz: {GameBiz})", CurrentGameId?.GameBiz);
        }
    }



    /// <summary>Flyout 首次打开时懒加载签到状态。</summary>
    private void Flyout_SignIn_Opened(object sender, object e)
    {
        if (GameRecordRole is null)
        {
            // 角色被删 / 切号后残留的旧状态：重新解析一次再决定要不要拉数据
            RefreshForRoleChanged();
        }
        if (!_statusLoaded && GameRecordRole is not null)
        {
            RefreshSignInStatusCommand.Execute(null);
        }
    }



    /// <summary>
    /// 角色或当前账号变化后重新解析这个游戏的角色（供账号中心模块在登录、切号后调用）。
    /// <para>
    /// 0.19 目前没有账号 / 角色变化的全局消息，登录完成可能发生在本控件已加载之后，
    /// 那时它还停在 Collapsed；账号中心模块迁移后应在其登录 / 切号成功处调用本方法。
    /// </para>
    /// </summary>
    public void RefreshForRoleChanged()
    {
        if (!IsLoaded)
        {
            return;
        }
        // 消息可能从后台线程发出来，解析角色、动界面都回到 UI 线程做
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                InitializeForCurrentGame();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh sign-in button for role change (Biz: {GameBiz})", CurrentGameId?.GameBiz);
            }
        }))
        {
            _logger.LogDebug("Skip sign-in button refresh: no UI dispatcher.");
        }
    }



    /// <summary>
    /// 关闭签到卡片并跳到设置「常规」页的开机自启一节（自动签到想常驻得先开机自启）。
    /// </summary>
    private void Button_AutoSignInHint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Flyout_SignIn.Hide();
            WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(SettingPage), Parameter: SettingPage.StartAtLoginSection));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigate to start at login setting");
        }
    }



    /// <summary>拉取并刷新签到卡片数据（奖励日历、今日状态、补签信息）。</summary>
    [RelayCommand]
    private async Task RefreshSignInStatusAsync()
    {
        if (GameRecordRole is null || IsLoading)
        {
            return;
        }
        IsLoading = true;
        try
        {
            ErrorMessage = null;
            SignInStatus status = await _signInService.GetSignInStatusAsync(GameRecordRole);
            _resignInfo = status.ResignInfo;
            IsTodaySigned = status.Info.IsSign;
            _totalSignDay = status.Info.TotalSignDay;
            TotalSignDaysHint = string.Format(Lang.SignInButton_SignedInForDays, _totalSignDay);

            Awards.Clear();
            int day = 0;
            foreach (var award in status.Reward.Awards)
            {
                var view = SignInAwardView.Create(award, day);
                // 索引小于已签天数的天数标记为已领取
                view.IsClaimed = day < status.Info.TotalSignDay;
                Awards.Add(view);
                day++;
            }
            _statusLoaded = true;
        }
        catch (miHoYoApiException ex)
        {
            ErrorMessage = GetSignInErrorMessage(ex);
            _logger.LogError(ex, "Refresh sign-in status failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Refresh sign-in status failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Refresh sign-in status failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
        finally
        {
            IsLoading = false;
        }
    }



    /// <summary>执行今日签到，成功后刷新卡片。</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (GameRecordRole is null)
        {
            return;
        }
        try
        {
            SignInActionResponse result = await _signInService.ClaimSignInAsync(GameRecordRole);
            ShowResultToast(result, isResign: false);
            if (result.Kind is SignInActionResult.Success or SignInActionResult.AlreadySigned)
            {
                await RefreshSignInStatusAsync();
            }
        }
        catch (HttpRequestException ex)
        {
            InAppToast.MainWindow?.Error(Lang.SignInButton_SignIn, ex.Message);
            _logger.LogError(ex, "Claim sign-in failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(ex);
            _logger.LogError(ex, "Claim sign-in failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
    }



    /// <summary>确认消耗后执行补签，成功后刷新卡片。</summary>
    [RelayCommand]
    private async Task ReSignInAsync()
    {
        if (GameRecordRole is null)
        {
            return;
        }
        try
        {
            var dialog = new ContentDialog
            {
                Title = Lang.SignInButton_ReSign,
                Content = string.Format(Lang.SignInButton_ReSignConfirmContent, _resignInfo?.CoinCost ?? 0, _resignInfo?.CoinCount ?? 0),
                PrimaryButtonText = Lang.Common_Confirm,
                CloseButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
            {
                return;
            }
            SignInActionResponse result = await _signInService.ClaimReSignInAsync(GameRecordRole);
            ShowResultToast(result, isResign: true);
            if (result.Kind is SignInActionResult.Success)
            {
                await RefreshSignInStatusAsync();
            }
        }
        catch (HttpRequestException ex)
        {
            InAppToast.MainWindow?.Error(Lang.SignInButton_ReSign, ex.Message);
            _logger.LogError(ex, "Claim re-sign-in failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(ex);
            _logger.LogError(ex, "Claim re-sign-in failed (Biz: {GameBiz}, Uid: {Uid})", GameRecordRole?.GameBiz, GameRecordRole?.Uid);
        }
    }



    /// <summary>
    /// 根据结构化结果弹出 InAppToast 提示。
    /// </summary>
    /// <param name="result">签到 / 补签操作结果。</param>
    /// <param name="isResign">是否为补签操作，影响标题文案。</param>
    private void ShowResultToast(SignInActionResponse result, bool isResign)
    {
        var toast = InAppToast.MainWindow;
        if (toast is null)
        {
            return;
        }
        string actionTitle = isResign ? Lang.SignInButton_ReSign : Lang.SignInButton_SignIn;
        switch (result.Kind)
        {
            case SignInActionResult.Success:
                toast.Success(actionTitle, Lang.SignInButton_SignInSucceeded);
                break;
            case SignInActionResult.AlreadySigned:
                toast.Information(actionTitle, FormatResultMessage(Lang.SignInButton_AlreadySignedToday, result.ReturnCode));
                break;
            case SignInActionResult.CookieExpired:
                toast.Warning(actionTitle, Lang.SignInButton_LoginExpired);
                break;
            case SignInActionResult.RiskControl:
                // 0.19 还没有「账号验证 / 重新登录」的统一入口，这里只如实提示去米游社 App 或网页签到
                toast.Warning(actionTitle, Lang.SignInButton_RiskControl);
                break;
            case SignInActionResult.NotEnoughCoin:
                toast.Warning(actionTitle, FormatResultMessage(Lang.SignInButton_NotEnoughCoin, result.ReturnCode));
                break;
            case SignInActionResult.ResignQuotaUsedUp:
                toast.Warning(actionTitle, FormatResultMessage(Lang.SignInButton_ResignQuotaUsedUp, result.ReturnCode));
                break;
            case SignInActionResult.NoResignDate:
                toast.Information(actionTitle, FormatResultMessage(Lang.SignInButton_NoResignDate, result.ReturnCode));
                break;
            case SignInActionResult.PleaseSignInFirst:
                toast.Warning(actionTitle, FormatResultMessage(Lang.SignInButton_PleaseSignInFirst, result.ReturnCode));
                break;
            default:
                // 0.19 的 miHoYoApiException.Message 已经带 retcode（"message (code)"），不再重复拼接
                toast.Warning(actionTitle, result.ResponseMessage ?? FormatResultMessage(Lang.SignInButton_SignInFailed, result.ReturnCode));
                break;
        }
    }



    /// <summary>
    /// 把接口业务异常映射为界面文案：登录态失效给专用提示，其余直接展示带 retcode 的原始消息。
    /// </summary>
    /// <param name="ex">接口业务异常。</param>
    /// <returns>可显示的错误文案。</returns>
    private static string GetSignInErrorMessage(miHoYoApiException ex)
    {
        if (SignInReturnCode.IsLoginExpired(ex.ReturnCode))
        {
            return Lang.SignInButton_LoginExpired;
        }
        return ex.Message;
    }



    /// <summary>
    /// 将已知签到结果的本地化文案附加 retcode，便于用户反馈问题。
    /// </summary>
    /// <param name="message">本地化结果文案。</param>
    /// <param name="returnCode">接口返回码；可为 null。</param>
    /// <returns>包含状态码的显示文案。</returns>
    private static string FormatResultMessage(string message, int? returnCode)
    {
        return returnCode.HasValue ? $"{message} ({returnCode.Value})" : message;
    }


}
