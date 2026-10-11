using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.Passport;
using Starward.Features.GameRecord;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace Starward.Features.Account;

/// <summary>
/// 账号中心。挂在左侧导航栏的账号入口上，以弹出的浮窗形式显示。
/// <para>
/// 未登录时是登录界面：支持账号密码、短信验证码、扫码三种登录方式，并可以导入已有的令牌。
/// 已登录时是账号池：列出所有保存过的账号，可以拖动排序、添加、删除，
/// 并从这里刷新凭据、工具箱数据和设备指纹；游戏内账号由启动页的游戏账号切换器决定。
/// </para>
/// <para>
/// 界面保持简洁：不需要滚动的控件、亚克力模糊半透明背景、没有多余的标题栏，
/// 扫码页会自动获取二维码，登录成功后浮窗自动收起并在主界面提示。
/// </para>
/// </summary>
[INotifyPropertyChanged]
public sealed partial class AccountCenterPanel : UserControl
{


    private readonly ILogger<AccountCenterPanel> _logger = AppConfig.GetLogger<AccountCenterPanel>();


    private readonly PassportAccountService _accountService = AppConfig.GetService<PassportAccountService>();


    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();


    /// <summary>一次登录流程中允许的最大重试次数（含人机验证）。</summary>
    private const int MaxLoginRetry = 3;


    /// <summary>实测二维码的有效期约 2 分钟，留出一点余量。</summary>
    private static readonly TimeSpan QrCodeLifetime = TimeSpan.FromSeconds(110);


    /// <summary>已登录那部分面板的宽度范围：下限不让列表挤成一条，上限不让提示文字把浮窗撑宽。</summary>
    private const double MinAccountPaneWidth = 180;

    private const double MaxAccountPaneWidth = 420;


    private QrCodeImageResult? _currentQrCode;

    private PassportQrSession? _qrSession;

    private DispatcherQueueTimer? _qrTimer;

    private CancellationTokenSource? _qrCancellation;

    private DispatcherQueueTimer? _countdownTimer;

    private DateTimeOffset _smsCodeSendTime;

    private string? _smsActionType;

    private PassportCaptcha? _pendingCaptcha;

    private bool _busy;


    /// <summary>正在账号中心里添加新账号：此时显示登录界面，但当前账号仍然是登录状态。</summary>
    private bool _addingAccount;


    /// <summary>承载这个面板的浮窗，登录成功后用它收起界面。</summary>
    public FlyoutBase? Host { get; set; }




    public AccountCenterPanel()
    {
        this.InitializeComponent();
        // 这些状态在 XAML 里的默认值不是期望值，先修正再显示，避免闪烁。
        QrProgressVisibility = Visibility.Collapsed;
        QrRefreshVisibility = Visibility.Collapsed;
        UpdateAccountState();
        UpdatePanes();
        this.Loaded += AccountCenterPanel_Loaded;
        this.Unloaded += AccountCenterPanel_Unloaded;
    }




    private void AccountCenterPanel_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 账号可能在别处被改变（比如工具箱里登录），这里跟着刷新账号列表。
            WeakReferenceMessenger.Default.Register<PassportAccountChangedMessage>(this, (_, _) => UpdateAccountState());
            UpdatePanelWidth();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load account center");
        }
    }




    /// <summary>
    /// 已登录那部分面板的宽度跟着底部两个按钮走：两个按钮各自正好等于自己的文字，
    /// 这一行的宽度就是面板需要的宽度，昵称和提示文字再长也只在这个宽度里省略或换行。
    /// 按钮文字随语言变化，所以宽度要现算，不能写死。
    /// </summary>
    private void UpdatePanelWidth()
    {
        try
        {
            Grid_AccountActions.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = Math.Ceiling(Grid_AccountActions.DesiredSize.Width);
            if (width <= 0)
            {
                // 未登录或正在添加账号时按钮是收起的，量不到宽度，沿用上一次算出来的宽度。
                return;
            }
            width = Math.Clamp(width, MinAccountPaneWidth, MaxAccountPaneWidth);
            Panel_AccountList.Width = width;
            Panel_AccountFooter.Width = width;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update account center panel width");
        }
    }




    /// <summary>
    /// 让宿主浮窗重新量一次内容。
    /// <para>
    /// 卡片在「账号列表」和「登录表单」之间切换时高度差很大，而浮窗的大小是在 ShowAt 那一刻
    /// 按当时的内容定死的，之后内容变矮不会自动缩回去，所以要主动作废一次测量。
    /// </para>
    /// </summary>
    public void RefreshLayout()
    {
        try
        {
            RootGrid.InvalidateMeasure();
            // 根元素自己作废不够，浮窗的 presenter 也要跟着重算。
            if (Host is Flyout flyout && flyout.Content is FrameworkElement content)
            {
                content.InvalidateMeasure();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh account center layout");
        }
    }




    private void AccountCenterPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }




    /// <summary>浮窗每次打开时刷新一次状态，上次没做完的「添加账号」不带到这一次。</summary>
    public void Refresh()
    {
        try
        {
            _addingAccount = false;
            StatusMessage = null;
            AccountErrorMessage = null;
            UpdateAccountState();
            UpdatePanes();
            UpdatePanelWidth();
            // 这里还在 Opening 阶段，浮窗尚未按内容量出大小，所以排到下一帧再作废测量。
            DispatcherQueue.TryEnqueue(RefreshLayout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh account center");
        }
    }




    /// <summary>浮窗关闭后停止轮询、取消人机验证，避免继续发请求。</summary>
    public void Cleanup()
    {
        try
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            Panel_Captcha.Cancel();
            StopQrLogin();
            _countdownTimer?.Stop();
            _countdownTimer = null;
        }
        catch { }
    }




    #region 界面状态




    /// <summary>
    /// 说明：面板不是 <c>Window</c>，但这里的 <c>x:Bind</c> 一律使用预转换好的值，
    /// 免得依赖只能在 <c>FrameworkElement</c> 之外使用的转换器。
    /// </summary>
    public string? StatusMessage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(StatusVisibility));
                OnPropertyChanged(nameof(AccountStatusVisibility));
            }
        }
    }


    public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusMessage) || IsSignedIn ? Visibility.Collapsed : Visibility.Visible;


    /// <summary>已登录时状态信息显示在底部按钮上方。</summary>
    public Visibility AccountStatusVisibility => string.IsNullOrWhiteSpace(StatusMessage) || !IsSignedIn ? Visibility.Collapsed : Visibility.Visible;


    public bool IsSignedIn
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                NotifyPaneVisibilityChanged();
            }
        }
    }


    /// <summary>正在卡片里做人机验证时，登录界面和账号列表都让位给验证码控件。</summary>
    public bool IsCaptchaShown
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                NotifyPaneVisibilityChanged();
            }
        }
    }


    private void NotifyPaneVisibilityChanged()
    {
        OnPropertyChanged(nameof(SignInVisibility));
        OnPropertyChanged(nameof(AccountVisibility));
        OnPropertyChanged(nameof(AccountCancelVisibility));
        OnPropertyChanged(nameof(CaptchaVisibility));
        OnPropertyChanged(nameof(FooterVisibility));
        OnPropertyChanged(nameof(StatusVisibility));
        OnPropertyChanged(nameof(AccountStatusVisibility));
        // 登录状态变了，账号列表和按钮会显示出来，宽度要重新按按钮算一次。
        UpdatePanelWidth();
        // 卡片从「登录表单」换成「账号列表」时矮了一大截，浮窗也要跟着缩回去。
        RefreshLayout();
    }


    /// <summary>未登录时显示登录方式；从账号中心里添加账号时也显示。</summary>
    public Visibility SignInVisibility => (!IsSignedIn || _addingAccount) && !IsCaptchaShown ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>已登录且不在添加账号时显示账号列表。</summary>
    public Visibility AccountVisibility => IsSignedIn && !_addingAccount && !IsCaptchaShown ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>从账号中心进入登录界面时，提供退回账号列表的入口。</summary>
    public Visibility AccountCancelVisibility => IsSignedIn && _addingAccount && !IsCaptchaShown ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>人机验证面板。</summary>
    public Visibility CaptchaVisibility => IsCaptchaShown ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>
    /// 卡片底部那一行（已登录时的状态与操作按钮）。人机验证时要收起来：
    /// 它带外边距，即使里面的内容都是收起的也会占掉几十像素高度，把验证码控件裁掉一截。
    /// </summary>
    public Visibility FooterVisibility => IsCaptchaShown ? Visibility.Collapsed : Visibility.Visible;


    /// <summary>账号池里保存的全部账号，可以拖动排序，所以是可变集合。</summary>
    public ObservableCollection<PassportAccountItem> AccountList { get; } = new();


    /// <summary>当前账号的令牌已经失效之类的提示。</summary>
    public string? AccountErrorMessage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AccountErrorVisibility));
            }
        }
    }


    public Visibility AccountErrorVisibility => string.IsNullOrWhiteSpace(AccountErrorMessage) ? Visibility.Collapsed : Visibility.Visible;


    public ImageSource? QrCodeImage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(QrCodeVisibility));
            }
        }
    }


    public Visibility QrCodeVisibility => QrCodeImage is null ? Visibility.Collapsed : Visibility.Visible;


    /// <summary>正在获取二维码时显示的进度环。</summary>
    public Visibility QrProgressVisibility { get; set => SetProperty(ref field, value); }


    /// <summary>二维码失效或获取失败时显示的重新获取入口。</summary>
    public Visibility QrRefreshVisibility { get; set => SetProperty(ref field, value); }


    public string? QrCodeStatus { get; set => SetProperty(ref field, value); }


    /// <summary>登录方式对应的内容面板，同一时间只显示一个。</summary>
    public Visibility PasswordPaneVisibility { get; set => SetProperty(ref field, value); }


    public Visibility SmsPaneVisibility { get; set => SetProperty(ref field, value); }


    public Visibility QrPaneVisibility { get; set => SetProperty(ref field, value); }


    public Visibility TokenPaneVisibility { get; set => SetProperty(ref field, value); }


    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value))
            {
                UpdateInteraction();
            }
        }
    }




    private void UpdateInteraction()
    {
        try
        {
            bool enabled = !IsBusy;
            // 倒计时期间不允许重新发送验证码。
            Button_SendSmsCode.IsEnabled = enabled && _countdownTimer is null;
            Button_SaveQrCode.IsEnabled = enabled;
            Button_RefreshInfo.IsEnabled = enabled;
            Button_UpdateDeviceFp.IsEnabled = enabled;
        }
        catch { }
    }




    private void UpdateAccountState()
    {
        try
        {
            var tokenSet = _accountService.TokenSet;
            IsSignedIn = tokenSet?.HasStoken ?? false;
            if (!IsSignedIn)
            {
                _addingAccount = false;
            }
            AccountList.Clear();
            if (IsSignedIn)
            {
                foreach (var item in _accountService.GetAccounts())
                {
                    AccountList.Add(item);
                }
            }
            NotifyPaneVisibilityChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update account state");
        }
    }




    #endregion




    #region 登录方式：账号密码与短信验证码




    private async void Button_PasswordLogin_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            string account = TextBox_Account.Text.Trim();
            string password = PasswordBox_Password.Password;
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            {
                StatusMessage = Lang.PassportLogin_InputRequired;
                return;
            }
            IsBusy = true;
            StatusMessage = null;
            PassportCaptcha? captcha = null;
            for (int i = 0; i < MaxLoginRetry; i++)
            {
                var outcome = await _accountService.LoginByPasswordAsync(account, password, captcha);
                if (await HandleOutcomeAsync(outcome))
                {
                    return;
                }
                captcha = _pendingCaptcha;
            }
            StatusMessage = Lang.PassportLogin_TooManyAttempts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password login");
            StatusMessage = string.Format(Lang.PassportLogin_NetworkError, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }




    private async void Button_SendSmsCode_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            string mobile = TextBox_Mobile.Text.Trim();
            if (string.IsNullOrWhiteSpace(mobile))
            {
                StatusMessage = Lang.PassportLogin_InputRequired;
                return;
            }
            IsBusy = true;
            StatusMessage = null;
            _smsActionType = null;
            PassportCaptcha? captcha = null;
            for (int i = 0; i < MaxLoginRetry; i++)
            {
                var outcome = await _accountService.SendSmsCaptchaAsync(mobile, captcha);
                if (outcome.State is PassportLoginState.NeedCaptcha)
                {
                    captcha = await ShowCaptchaAsync(outcome.Aigis);
                    if (captcha is null)
                    {
                        StatusMessage = Lang.PassportLogin_CaptchaCancelled;
                        return;
                    }
                    continue;
                }
                if (outcome.IsSuccess)
                {
                    _smsActionType = outcome.ActionType;
                    _smsCodeSendTime = DateTimeOffset.Now;
                    StartSmsCountdown(outcome.Countdown);
                    StatusMessage = Lang.PassportLogin_SmsCodeSent;
                    // 短信已经发出去，把焦点放到验证码上。
                    PasswordBox_SmsCode.Focus(FocusState.Programmatic);
                }
                else
                {
                    StatusMessage = outcome.Message ?? Lang.PassportLogin_ServiceError;
                }
                return;
            }
            StatusMessage = Lang.PassportLogin_TooManyAttempts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send sms code");
            StatusMessage = string.Format(Lang.PassportLogin_NetworkError, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }




    private async void Button_SmsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            string mobile = TextBox_Mobile.Text.Trim();
            string code = PasswordBox_SmsCode.Password.Trim();
            // 验证码固定六位数字。
            if (string.IsNullOrWhiteSpace(mobile) || code.Length != 6)
            {
                StatusMessage = Lang.PassportLogin_InputRequired;
                return;
            }
            IsBusy = true;
            StatusMessage = null;
            PassportCaptcha? captcha = null;
            for (int i = 0; i < MaxLoginRetry; i++)
            {
                var outcome = await _accountService.LoginBySmsCaptchaAsync(mobile, code, _smsActionType, captcha);
                if (await HandleOutcomeAsync(outcome))
                {
                    return;
                }
                captcha = _pendingCaptcha;
            }
            StatusMessage = Lang.PassportLogin_TooManyAttempts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sms login");
            StatusMessage = string.Format(Lang.PassportLogin_NetworkError, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }




    private async void Button_ImportToken_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            string stoken = TextBox_Stoken.Text.Trim();
            string mid = TextBox_Mid.Text.Trim();
            long aid = 0;
            if (string.IsNullOrWhiteSpace(stoken) || string.IsNullOrWhiteSpace(mid))
            {
                // 只填了一个输入框：把这段文本当成 Cookie / JSON / 裸令牌来解析。
                var parsed = PassportCookieParser.Parse(string.IsNullOrWhiteSpace(stoken) ? mid : stoken);
                aid = parsed.Aid;
                if (!string.IsNullOrWhiteSpace(parsed.Stoken))
                {
                    stoken = parsed.Stoken;
                }
                if (!string.IsNullOrWhiteSpace(parsed.Mid))
                {
                    mid = parsed.Mid;
                }
                // 解析结果填回输入框，方便用户核对。
                TextBox_Stoken.Text = stoken;
                TextBox_Mid.Text = mid;
            }
            if (string.IsNullOrWhiteSpace(stoken))
            {
                StatusMessage = Lang.PassportLogin_ImportCookieNotFound;
                return;
            }
            IsBusy = true;
            StatusMessage = null;
            var outcome = await _accountService.ImportStokenAsync(stoken, mid, aid);
            if (outcome.IsSuccess)
            {
                OnSignedIn();
            }
            else
            {
                StatusMessage = outcome.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import token");
            StatusMessage = string.Format(Lang.PassportLogin_NetworkError, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }




    /// <summary>
    /// 处理登录结果。返回 true 表示本次登录流程已经结束。
    /// </summary>
    private async Task<bool> HandleOutcomeAsync(PassportLoginOutcome outcome)
    {
        _pendingCaptcha = null;
        switch (outcome.State)
        {
            case PassportLoginState.Success:
                OnSignedIn();
                return true;
            case PassportLoginState.NeedCaptcha:
                _pendingCaptcha = await ShowCaptchaAsync(outcome.Aigis);
                if (_pendingCaptcha is null)
                {
                    StatusMessage = Lang.PassportLogin_CaptchaCancelled;
                    return true;
                }
                return false;
            case PassportLoginState.NeedDeviceVerify:
                return await HandleDeviceVerifyAsync(outcome);
            default:
                StatusMessage = outcome.Message;
                return true;
        }
    }




    /// <summary>
    /// 新设备登录：发送短信验证码并校验。
    /// </summary>
    private async Task<bool> HandleDeviceVerifyAsync(PassportLoginOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.ActionTicket) && string.IsNullOrWhiteSpace(outcome.VerifyTicket))
        {
            StatusMessage = outcome.Message;
            return true;
        }
        StatusMessage = outcome.Message ?? Lang.PassportLogin_DeviceVerifyTip;
        // 实测确认：申请校验短信的接口需要 x-rpc-verify 中的 verify_str.ticket，
        // 传入响应头里的 action_ticket 会返回 -3003，因此优先使用前者并保留回退。
        string sendTicket = outcome.VerifyTicket ?? outcome.ActionTicket;
        var send = await _accountService.SendDeviceVerifyCaptchaAsync(sendTicket);
        if (send.State is PassportLoginState.NeedCaptcha)
        {
            var captcha = await ShowCaptchaAsync(send.Aigis);
            if (captcha is null)
            {
                StatusMessage = Lang.PassportLogin_CaptchaCancelled;
                return true;
            }
            send = await _accountService.SendDeviceVerifyCaptchaAsync(sendTicket, captcha);
        }
        if (!send.IsSuccess && !string.IsNullOrWhiteSpace(outcome.ActionTicket) && outcome.ActionTicket != sendTicket)
        {
            send = await _accountService.SendDeviceVerifyCaptchaAsync(outcome.ActionTicket);
        }
        if (!send.IsSuccess)
        {
            StatusMessage = send.Message ?? Lang.PassportLogin_DeviceVerifyTip;
            return true;
        }
        string? code = await ShowInputDialogAsync(Lang.PassportLogin_DeviceVerifyTip, Lang.PassportLogin_DeviceVerifyCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            return true;
        }
        var verify = await _accountService.CompleteDeviceVerifyAsync(outcome.ActionTicket, outcome.VerifyTicket ?? outcome.ActionTicket, code);
        if (verify.IsSuccess)
        {
            OnSignedIn();
        }
        else
        {
            StatusMessage = verify.Message;
        }
        return true;
    }




    /// <summary>
    /// 登录成功：提示用户，登录界面收起；从账号池里添加账号时留在原地，方便继续管理。
    /// </summary>
    private void OnSignedIn()
    {
        StopQrLogin();
        PasswordBox_Password.Password = "";
        TextBox_Stoken.Text = "";
        TextBox_Mid.Text = "";
        string? displayName = _accountService.DisplayName;
        if (_addingAccount)
        {
            _addingAccount = false;
            StatusMessage = null;
            UpdateAccountState();
            InAppToast.MainWindow?.Success(Lang.PassportLogin_SignInSuccess, displayName, 3000);
            return;
        }
        UpdateAccountState();
        InAppToast.MainWindow?.Success(Lang.PassportLogin_SignInSuccess, displayName, 3000);
        Host?.Hide();
    }




    #endregion




    #region 人机验证




    private async Task<PassportCaptcha?> ShowCaptchaAsync(PassportAigisHeader? aigis)
    {
        var challenge = aigis?.GetChallenge();
        if (aigis is null || challenge is null)
        {
            StatusMessage = Lang.PassportLogin_CaptchaUnavailable;
            return null;
        }
        // 人机验证直接内嵌在卡片里：隐藏登录界面，验证码控件自己决定浮窗的大小。
        try
        {
            IsCaptchaShown = true;
            return await Panel_Captcha.RunAsync(aigis, challenge);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show captcha");
            return null;
        }
        finally
        {
            IsCaptchaShown = false;
        }
    }




    private async Task<string?> ShowInputDialogAsync(string title, string placeholder)
    {
        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            IsSpellCheckEnabled = false,
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = Lang.PassportLogin_DeviceVerifySubmit,
            CloseButtonText = Lang.PassportLogin_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            return textBox.Text.Trim();
        }
        return null;
    }




    #endregion




    #region 登录方式切换、验证码输入与扫码登录




    /// <summary>
    /// 切换登录方式：切到扫码页时自动获取二维码，切走时停止轮询，避免无意义的请求。
    /// </summary>
    private void SelectorBar_Login_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        try
        {
            UpdatePanes();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change login method");
        }
    }




    private void UpdatePanes()
    {
        bool password = ReferenceEquals(SelectorBar_Login.SelectedItem, SelectorBarItem_Password);
        bool sms = ReferenceEquals(SelectorBar_Login.SelectedItem, SelectorBarItem_Sms);
        bool qrCode = ReferenceEquals(SelectorBar_Login.SelectedItem, SelectorBarItem_QrCode);
        bool token = ReferenceEquals(SelectorBar_Login.SelectedItem, SelectorBarItem_Token);
        if (!password && !sms && !qrCode && !token)
        {
            password = true;
        }
        PasswordPaneVisibility = password ? Visibility.Visible : Visibility.Collapsed;
        SmsPaneVisibility = sms ? Visibility.Visible : Visibility.Collapsed;
        QrPaneVisibility = qrCode ? Visibility.Visible : Visibility.Collapsed;
        TokenPaneVisibility = token ? Visibility.Visible : Visibility.Collapsed;
        if (IsSignedIn && !_addingAccount)
        {
            return;
        }
        if (qrCode)
        {
            if (_qrSession is null || IsQrCodeExpired(_qrSession))
            {
                _ = StartQrLoginAsync();
            }
            else
            {
                StartQrTimer();
            }
        }
        else
        {
            StopQrPolling();
            FocusSelectedPane(password, sms, token);
        }
    }




    /// <summary>把输入焦点放到当前登录方式的第一个输入框，省掉一次点击。</summary>
    private void FocusSelectedPane(bool password, bool sms, bool token)
    {
        try
        {
            if (!RootGrid.IsLoaded)
            {
                return;
            }
            if (password)
            {
                TextBox_Account.Focus(FocusState.Programmatic);
            }
            else if (sms)
            {
                TextBox_Mobile.Focus(FocusState.Programmatic);
            }
            else if (token)
            {
                TextBox_Stoken.Focus(FocusState.Programmatic);
            }
        }
        catch { }
    }




    private static bool IsQrCodeExpired(PassportQrSession session)
    {
        return session.IsExpired || DateTimeOffset.Now - session.CreateTime > QrCodeLifetime;
    }




    // ---- 六位验证码输入框 ----




    /// <summary>验证码固定六位数字。</summary>
    private const int SmsCodeLength = 6;


    private bool _updatingSmsCode;




    /// <summary>
    /// 只保留数字，最多六位。
    /// </summary>
    private void PasswordBox_SmsCode_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingSmsCode)
        {
            return;
        }
        try
        {
            string digits = new(PasswordBox_SmsCode.Password.Where(char.IsDigit).Take(SmsCodeLength).ToArray());
            if (digits != PasswordBox_SmsCode.Password)
            {
                _updatingSmsCode = true;
                PasswordBox_SmsCode.Password = digits;
                _updatingSmsCode = false;
            }
        }
        catch
        {
            _updatingSmsCode = false;
        }
    }




    // ---- 扫码登录 ----




    private async void Button_RefreshQrCode_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        await StartQrLoginAsync();
    }




    private async Task StartQrLoginAsync(bool expired = false)
    {
        try
        {
            StopQrLogin();
            IsBusy = true;
            QrCodeImage = null;
            QrProgressVisibility = Visibility.Visible;
            QrRefreshVisibility = Visibility.Collapsed;
            QrCodeStatus = expired ? Lang.PassportLogin_QrCodeRefreshing : Lang.PassportLogin_QrCodeLoading;
            var session = await _accountService.StartQrLoginAsync();
            if (session is null)
            {
                ShowQrCodeError(Lang.PassportLogin_QrCodeFailed);
                return;
            }
            _qrSession = session;
            // 二维码以图片形式输出，背景透明，深色主题下改用白色模块保证可扫描。
            var colorMode = RootGrid.ActualTheme is ElementTheme.Dark ? QrCodeColorMode.TransparentLightModules : QrCodeColorMode.TransparentDarkModules;
            _currentQrCode = await QrCodeImageGenerator.GenerateAsync(session.Url, 8, 2, QrCodeEcc.Medium, colorMode);
            QrCodeImage = _currentQrCode.ImageSource;
            QrCodeStatus = Lang.PassportLogin_QrCodeTip;
            StartQrTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start qr login");
            ShowQrCodeError(string.Format(Lang.PassportLogin_NetworkError, ex.Message));
        }
        finally
        {
            QrProgressVisibility = Visibility.Collapsed;
            IsBusy = false;
        }
    }




    private void ShowQrCodeError(string message)
    {
        _qrSession = null;
        QrCodeImage = null;
        QrCodeStatus = message;
        QrRefreshVisibility = Visibility.Visible;
    }




    private void StartQrTimer()
    {
        if (_qrTimer is not null)
        {
            return;
        }
        _qrCancellation = new CancellationTokenSource();
        _qrTimer = DispatcherQueue.CreateTimer();
        _qrTimer.Interval = TimeSpan.FromSeconds(2);
        _qrTimer.Tick += QrTimer_Tick;
        _qrTimer.Start();
    }




    private async void QrTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var session = _qrSession;
        if (session is null || IsBusy)
        {
            return;
        }
        if (IsQrCodeExpired(session))
        {
            // 二维码过期后自动换一张，用户不需要手动点击。
            await StartQrLoginAsync(expired: true);
            return;
        }
        try
        {
            var outcome = await _accountService.PollQrLoginAsync(session, _qrCancellation?.Token ?? default);
            switch (outcome.State)
            {
                case PassportLoginState.Pending:
                    if (session.IsScanned)
                    {
                        QrCodeStatus = Lang.PassportLogin_QrCodeScanned;
                    }
                    break;
                case PassportLoginState.Success:
                    StopQrLogin();
                    QrCodeStatus = Lang.PassportLogin_QrCodeConfirmed;
                    OnSignedIn();
                    break;
                case PassportLoginState.Failed:
                    if (session.IsExpired)
                    {
                        await StartQrLoginAsync(expired: true);
                    }
                    else
                    {
                        ShowQrCodeError(session.IsCancelled ? Lang.PassportLogin_QrCodeCancelled : outcome.Message ?? Lang.PassportLogin_QrCodeFailed);
                    }
                    break;
                case PassportLoginState.NeedCaptcha:
                    StopQrLogin();
                    ShowQrCodeError(outcome.Message ?? Lang.PassportLogin_CaptchaRequired);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll qr login");
        }
    }




    /// <summary>停止轮询，保留当前二维码。</summary>
    private void StopQrPolling()
    {
        try
        {
            if (_qrTimer is not null)
            {
                _qrTimer.Tick -= QrTimer_Tick;
                _qrTimer.Stop();
                _qrTimer = null;
            }
            _qrCancellation?.Cancel();
            _qrCancellation?.Dispose();
            _qrCancellation = null;
        }
        catch { }
    }




    /// <summary>停止轮询并丢弃当前二维码。</summary>
    private void StopQrLogin()
    {
        StopQrPolling();
        _qrSession = null;
        _currentQrCode = null;
        QrCodeImage = null;
        QrRefreshVisibility = Visibility.Collapsed;
    }




    private async void Button_SaveQrCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var qrCode = _currentQrCode;
            if (qrCode is null)
            {
                return;
            }
            string? path = await FileDialogHelper.OpenSaveFileDialogAsync(RootGrid.XamlRoot.GetWindowHandle(), $"passport_qrcode_{DateTime.Now:yyyyMMddHHmmss}", (Lang.PassportLogin_QrCodeImage, ".png"));
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            await System.IO.File.WriteAllBytesAsync(path, qrCode.PngBytes);
            StatusMessage = Lang.PassportLogin_QrCodeSaved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save qr code image");
            StatusMessage = ex.Message;
        }
    }




    #endregion




    #region 短信倒计时




    private void StartSmsCountdown(int seconds)
    {
        try
        {
            _countdownTimer?.Stop();
            int remain = Math.Max(seconds, 30);
            Button_SendSmsCode.IsEnabled = false;
            _countdownTimer = DispatcherQueue.CreateTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += (_, _) =>
            {
                remain--;
                if (remain <= 0)
                {
                    _countdownTimer?.Stop();
                    _countdownTimer = null;
                    Button_SendSmsCode.Content = Lang.PassportLogin_SendSmsCode;
                    UpdateInteraction();
                }
                else
                {
                    Button_SendSmsCode.Content = string.Format(Lang.PassportLogin_ResendIn, remain);
                }
            };
            _countdownTimer.Start();
            Button_SendSmsCode.Content = string.Format(Lang.PassportLogin_ResendIn, remain);
        }
        catch { }
    }




    #endregion




    #region 账号操作




    /// <summary>在账号中心里添加一个账号：切到登录界面，但不退出当前账号。</summary>
    private void Button_AddAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _addingAccount = true;
            AccountErrorMessage = null;
            StatusMessage = null;
            NotifyPaneVisibilityChanged();
            UpdatePanes();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add passport account");
        }
    }




    /// <summary>放弃添加账号，回到账号列表。</summary>
    private void Button_CancelAddAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _addingAccount = false;
            StatusMessage = null;
            NotifyPaneVisibilityChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel adding passport account");
        }
    }




    /// <summary>删除一个保存过的账号。</summary>
    private void MenuFlyoutItem_RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string key)
            {
                return;
            }
            if (_accountService.RemoveAccount(key))
            {
                UpdateAccountState();
                InAppToast.MainWindow?.Success(Lang.PassportLogin_AccountRemoved);
            }
            else
            {
                AccountErrorMessage = Lang.PassportLogin_SwitchAccountFailed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove passport account");
        }
    }




    /// <summary>账号列表拖动排序后保存新的顺序。</summary>
    private void ListView_Accounts_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            _accountService.SaveAccountOrder(AccountList.Select(x => x.Key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save passport account order");
        }
    }




    /// <summary>
    /// 刷新信息：刷新账号池里到期账号的凭据、米游社账号的昵称头像，以及已经 pin 的游戏的工具箱数据。
    /// <para>
    /// 凭据本来就会按需自动刷新（启动游戏时校验 SToken、派生令牌过期后重新换取），
    /// 工具箱数据平时由各个页面自己按需拉取，这里是给用户的一个兜底入口。
    /// </para>
    /// </summary>
    private async void Button_RefreshInfo_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            IsBusy = true;
            AccountErrorMessage = null;
            StatusMessage = Lang.PassportLogin_Refreshing;
            var (refreshed, expired) = await _accountService.RefreshAllAccountsAsync();
            int users = await _gameRecordService.RefreshMiyousheAccountInfoAsync();
            _accountService.SyncAccountNamesFromToolbox();
            var (games, succeeded, failed) = await _gameRecordService.RefreshPinnedGamesToolboxDataAsync();
            UpdateAccountState();
            WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(_accountService.IsLoggedIn));
            if (succeeded > 0 || failed > 0)
            {
                // 工具箱页正开着的话，让它重新加载一次，显示的就是刚刷新下来的记录。
                WeakReferenceMessenger.Default.Send(new GameRecordToolboxRefreshedMessage());
            }
            if (games > 0)
            {
                InAppToast.MainWindow?.Success(string.Format(Lang.PassportLogin_ToolboxRefreshed, games, succeeded));
            }
            else if (users > 0 || refreshed > 0)
            {
                InAppToast.MainWindow?.Success(string.Format(Lang.PassportLogin_CredentialsRefreshed, refreshed));
            }
            else
            {
                InAppToast.MainWindow?.Success(Lang.PassportLogin_CredentialsAreFresh);
            }
            if (failed > 0)
            {
                AccountErrorMessage = string.Format(Lang.PassportLogin_ToolboxRefreshFailed, failed);
            }
            if (expired > 0)
            {
                AccountErrorMessage = string.Format(Lang.PassportLogin_CredentialsExpired, expired);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh passport credentials");
            AccountErrorMessage = Lang.PassportLogin_SwitchAccountFailed;
        }
        finally
        {
            StatusMessage = null;
            IsBusy = false;
        }
    }




    /// <summary>
    /// 更新设备指纹。和米游社工具箱共用同一份指纹，两边界面上的鉴权信息保持一致。
    /// </summary>
    private async void Button_UpdateDeviceFp_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            IsBusy = true;
            await _gameRecordService.UpdateDeviceFpAsync(true);
            InAppToast.MainWindow?.Success(Lang.HoyolabToolboxPage_TheDeviceFingerprintIsAlreadyUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update device info");
            InAppToast.MainWindow?.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }




    #endregion




}




/// <summary>
/// 账号中心的入口。入口在左侧导航栏的账号按钮上，这里只负责把浮窗弹出来。
/// </summary>
internal static class AccountCenter
{


    private static Flyout? _flyout;


    public static void Show(FrameworkElement anchor)
    {
        try
        {
            if (anchor.XamlRoot is null)
            {
                return;
            }
            Hide();
            var panel = new AccountCenterPanel();
            var flyout = new Flyout
            {
                Content = panel,
                Placement = FlyoutPlacementMode.Right,
                FlyoutPresenterStyle = CreatePresenterStyle(),
            };
            panel.Host = flyout;
            flyout.Closed += (_, _) =>
            {
                panel.Cleanup();
                _flyout = null;
            };
            _flyout = flyout;
            flyout.ShowAt(anchor);
            // ShowAt 是同步的，浮窗在这一刻就按当前内容把大小定死了；
            // 登录成功收起登录表单后卡片会矮一大截，所以排一次重算，让高度跟着内容走。
            panel.DispatcherQueue.TryEnqueue(panel.RefreshLayout);
        }
        catch
        {
            _flyout = null;
        }
    }




    public static void Hide()
    {
        try
        {
            _flyout?.Hide();
        }
        catch { }
        _flyout = null;
    }




    /// <summary>和米游社工具箱里的弹出层一致：亚克力背景、没有边框和内边距。</summary>
    private static Style CreatePresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        if (Application.Current.Resources.TryGetValue("CustomAcrylicBrush", out object? brush) && brush is Brush acrylic)
        {
            style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, acrylic));
        }
        style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(12)));
        return style;
    }


}
