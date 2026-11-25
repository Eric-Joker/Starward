using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.Passport;
using Starward.Features.GameRecord;
using Starward.Features.Passport;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace Starward.Features.Account;

/// <summary>
/// 原账号中心。【已停用】账号中心已经换成上游 0.19 的 HYP 账号浮窗（<c>MainView</c> 左侧栏），
/// 这个面板不再挂到任何界面上；代码保留备查（登录方式本身照旧可用）。
/// <para>
/// 原行为：挂在左侧导航栏的账号入口上，以弹出的浮窗形式显示。
/// </para>
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


    private readonly GameRecordRoleService _gameRecordRoleService = AppConfig.GetService<GameRecordRoleService>();


    /// <summary>一次登录流程中允许的最大重试次数（含人机验证）。短信登录的重试交给会话状态机。</summary>
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

    private PassportCaptcha? _pendingCaptcha;

    private bool _busy;


    /// <summary>
    /// 短信验证码登录的会话状态。放在面板之外，工具箱入口重建面板、窗口进托盘后回来都能接着登录，
    /// 不用重新过一次验证码。用户显式取消或登录成功时才清空。
    /// </summary>
    private readonly PassportLoginSession _loginSession = AppConfig.GetService<PassportLoginSession>();


    /// <summary>正在账号中心里添加新账号：此时显示登录界面，但当前账号仍然是登录状态。</summary>
    private bool _addingAccount;


    /// <summary>
    /// 登录新账号之前的当前账号 key。账号中心只是往账号池里加一条记录，登录成功后要把会话恢复成它，
    /// 否则预设 / 账号选择器里显示的账号会跟着变成刚登录的新账号（见 <see cref="OnSignedIn"/>）。
    /// </summary>
    private string? _accountKeyBeforeLogin;


    /// <summary>承载这个面板的浮窗，登录成功后用它收起界面。</summary>
    public FlyoutBase? Host { get; private set; }


    /// <summary>
    /// 本次是否允许宿主浮窗关闭。登录成功、用户显式取消、窗口收托盘等程序化关闭路径必须置为 true，
    /// 否则浮窗会被“登录中禁止自动收起”的逻辑挡住，出现关不掉的情况。
    /// </summary>
    private bool _allowHostHide;


    /// <summary>
    /// 登录过程正在进行（请求中 / 人机验证 / 等短信验证码 / 等扫码 / 新设备校验），
    /// 这时浮窗不能因为点击外部或切换窗口而 light-dismiss，否则登录会被打断。
    /// </summary>
    public bool IsLoginInProgress
    {
        get
        {
            try
            {
                return IsBusy
                    || IsCaptchaShown
                    || _countdownTimer is not null
                    // 短信已经发出去、在等用户填验证码：这段状态在会话里，重开面板也要算登录中。
                    || _loginSession.HasSentSmsCode
                    || (QrPaneVisibility == Visibility.Visible && _qrSession is not null);
            }
            catch
            {
                return false;
            }
        }
    }


    /// <summary>登录过程中的显式取消入口是否显示。</summary>
    public Visibility CancelLoginVisibility => IsLoginInProgress ? Visibility.Visible : Visibility.Collapsed;




    /// <summary>
    /// 把面板挂到承载它的浮窗上。两个入口都要挂：工具箱入口每次新建一个 <see cref="Flyout"/>，
    /// 主界面入口是 <see cref="MainView"/> 里 XAML 常驻的那个。
    /// </summary>
    public void AttachHost(FlyoutBase host)
    {
        try
        {
            if (ReferenceEquals(Host, host))
            {
                return;
            }
            DetachHost();
            Host = host;
            host.Closing += Host_Closing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attach account center host");
        }
    }




    private void DetachHost()
    {
        try
        {
            if (Host is not null)
            {
                Host.Closing -= Host_Closing;
            }
        }
        catch { }
        Host = null;
    }




    /// <summary>
    /// 浮窗即将关闭：登录过程中拦下自动收起（点外部、切窗口、WebView2 抢焦点都会触发 light-dismiss），
    /// 其他情况保持原来的“点外面关闭”行为，清理交给 Closed。
    /// </summary>
    private void Host_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        try
        {
            if (!CanCloseHost())
            {
                args.Cancel = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account center host closing");
        }
    }




    /// <summary>当前是否允许宿主浮窗关闭：非登录态，或者已经走了显式取消 / 程序化关闭。</summary>
    public bool CanCloseHost()
    {
        return _allowHostHide || !IsLoginInProgress;
    }




    /// <summary>允许宿主浮窗在下一次关闭时正常收起，用于登录成功、显式取消、窗口收托盘等路径。</summary>
    public void AllowHostHide()
    {
        _allowHostHide = true;
    }




    /// <summary>
    /// 用户显式取消登录（面板里的「取消登录」/「取消验证」按钮或 Esc）：
    /// 结束所有进行中的登录会话，然后允许并收起宿主浮窗。
    /// </summary>
    public void CancelLoginAndClose()
    {
        try
        {
            _logger.LogInformation("Account center sign-in cancelled by the user.");
            _pendingCaptcha = null;
            // 用户显式取消：短信登录的 action_type / 验证码 / 倒计时全部作废，下次重新发码。
            _loginSession.Reset();
            Panel_Captcha.Cancel();
            StopQrLogin();
            StopSmsCountdown();
            StatusMessage = null;
            AllowHostHide();
            Host?.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel account center sign-in");
        }
    }




    private void Button_CancelLogin_Click(object sender, RoutedEventArgs e)
    {
        CancelLoginAndClose();
    }




    private void KeyboardAccelerator_Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            args.Handled = true;
            CancelLoginAndClose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel account center sign-in with escape");
        }
    }




    /// <summary>登录过程状态变了，通知界面刷新“取消登录”入口。</summary>
    private void NotifyLoginProgressChanged()
    {
        OnPropertyChanged(nameof(IsLoginInProgress));
        OnPropertyChanged(nameof(CancelLoginVisibility));
    }




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
        // 验证码面板自己的“取消验证”按钮：结束整个登录流程并收起卡片。
        Panel_Captcha.CancelRequested += Panel_Captcha_CancelRequested;
        // 工具箱入口每次都是新面板，打开时要把上次没做完的短信登录状态接回来。
        RestoreLoginSessionState();
    }




    /// <summary>
    /// 重开面板时恢复短信验证码登录的界面状态：手机号、发送时间对应的倒计时与按钮文案。
    /// 会话还在有效期内时不应该让用户重新发一遍短信、重新过一次验证码。
    /// </summary>
    private void RestoreLoginSessionState()
    {
        try
        {
            string? mobile = _loginSession.Mobile;
            if (string.IsNullOrWhiteSpace(mobile))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(TextBox_Mobile.Text))
            {
                TextBox_Mobile.Text = mobile;
            }
            int remaining = _loginSession.GetRemainingCountdownSeconds();
            if (remaining > 0)
            {
                StartSmsCountdown(remaining);
                if (string.IsNullOrWhiteSpace(StatusMessage))
                {
                    StatusMessage = Lang.PassportLogin_SmsCodeSent;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore passport login session state");
        }
    }




    private void Panel_Captcha_CancelRequested(object? sender, EventArgs e)
    {
        CancelLoginAndClose();
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
            _allowHostHide = false;
            _addingAccount = false;
            _accountKeyBeforeLogin = null;
            StatusMessage = null;
            AccountErrorMessage = null;
            UpdateAccountState();
            if (IsSignedIn)
            {
                // 已经登录时账号中心显示的是账号列表，上一次「添加账号」没做完的短信登录状态
                // 在这一轮不会再用到；留着会让面板一直算“登录中”，反而关不掉。
                _loginSession.Reset();
            }
            UpdatePanes();
            UpdatePanelWidth();
            // 会话可能还留着上一次发出去的验证码与倒计时，重开面板时接着用。
            RestoreLoginSessionState();
            NotifyLoginProgressChanged();
            // 账号池里的登录状态在后台校验一次，失效的账号直接在列表里显示红点。
            _ = VerifyAccountSessionsAsync();
            // 这里还在 Opening 阶段，浮窗尚未按内容量出大小，所以排到下一帧再作废测量。
            DispatcherQueue.TryEnqueue(RefreshLayout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh account center");
        }
    }




    /// <summary>上一次后台校验账号登录状态的时间。</summary>
    private DateTimeOffset _lastVerifyAccountTime;


    /// <summary>
    /// 后台校验账号池里所有账号的登录状态，让失效的账号在列表里显示警告红点。
    /// <para>
    /// 五分钟内不重复校验，避免每开一次浮窗都打一轮请求；校验期间不打断用户正在做的事。
    /// </para>
    /// </summary>
    private async Task VerifyAccountSessionsAsync()
    {
        try
        {
            if (IsSignedIn is false || !(_accountService.GetAccounts().Count > 0))
            {
                return;
            }
            if (DateTimeOffset.Now - _lastVerifyAccountTime < TimeSpan.FromMinutes(5))
            {
                return;
            }
            _lastVerifyAccountTime = DateTimeOffset.Now;
            await _accountService.VerifyAllAccountsAsync();
            if (!_addingAccount && !IsCaptchaShown)
            {
                UpdateAccountState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verify passport account sessions");
        }
    }




    /// <summary>
    /// 浮窗关闭后停止轮询、取消人机验证，避免继续发请求。
    /// <para>
    /// 只会在用户显式关闭/取消，或者登录成功这类程序化收起时执行；
    /// 被 <see cref="Host_Closing"/> 拦下的 light-dismiss 不会走到这里，进行中的验证码/扫码不会被误清理。
    /// </para>
    /// </summary>
    public void Cleanup()
    {
        try
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            _pendingCaptcha = null;
            _accountKeyBeforeLogin = null;
            Panel_Captcha.Cancel();
            StopQrLogin();
            StopSmsCountdown();
            NotifyLoginProgressChanged();
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
        NotifyLoginProgressChanged();
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
            // Button_RefreshInfo 已删除（与"刷新信息"按钮重复）；其槽位现在是 Button_UpdateDeviceFp。
            Button_UpdateDeviceFp.IsEnabled = enabled;
            NotifyLoginProgressChanged();
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
            RememberAccountBeforeLogin();
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
            // 会话状态放在 PassportLoginSession 里：验证码结果一解出来就自动带 x-rpc-aigis 重发，
            // 面板重建/收起也不会丢 action_type 和验证码。
            _loginSession.Begin(mobile);
            var outcome = await _loginSession.SendSmsCodeAsync(
                mobile,
                captcha => _accountService.SendSmsCaptchaAsync(mobile, captcha, _loginSession.CancellationToken),
                aigis => ShowCaptchaAsync(aigis));
            if (outcome.IsSuccess)
            {
                StartSmsCountdown(_loginSession.GetRemainingCountdownSeconds());
                StatusMessage = Lang.PassportLogin_SmsCodeSent;
                // 短信已经发出去，把焦点放到验证码上。
                PasswordBox_SmsCode.Focus(FocusState.Programmatic);
            }
            else
            {
                StatusMessage = outcome.Message ?? Lang.PassportLogin_ServiceError;
            }
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
            // action_type 只从会话里取：重开面板之后也能接着登录，不用重新过一次验证码。
            // Begin 会检查手机号：换了号码就清掉旧会话，避免拿着上一个号码的 action_type 去登录。
            _loginSession.Begin(mobile);
            string? actionType = _loginSession.SmsActionType;
            if (string.IsNullOrWhiteSpace(actionType))
            {
                StatusMessage = Lang.PassportLogin_SendSmsCodeFirst;
                return;
            }
            IsBusy = true;
            StatusMessage = null;
            RememberAccountBeforeLogin();
            var outcome = await _loginSession.LoginAsync(
                mobile,
                captcha => _accountService.LoginBySmsCaptchaAsync(mobile, code, actionType, captcha, _loginSession.CancellationToken),
                aigis => ShowCaptchaAsync(aigis));
            await HandleOutcomeAsync(outcome);
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
            // 设备标识（x-rpc-device_id）是可选项：留空就完全不动现有值。
            // 实测服务端只认这一枚"已登记"的值，所以只接受用户从浏览器请求头抄来的 GUID，不做任何自动获取/登记。
            string deviceIdInput = TextBox_DeviceId.Text.Trim();
            if (!string.IsNullOrEmpty(deviceIdInput))
            {
                if (!Guid.TryParse(deviceIdInput, out _))
                {
                    // 格式不合法：只提示，不写入，也不继续后面的导入。
                    StatusMessage = Lang.PassportLogin_DeviceIdInvalid;
                    return;
                }
                if (!_accountService.SaveDeviceId(deviceIdInput))
                {
                    StatusMessage = Lang.PassportLogin_DeviceIdInvalid;
                    return;
                }
                // 写入成功不额外提示：解析/导入本身的结局消息会覆盖这里，且成功的导入会直接收起面板。
            }
            IsBusy = true;
            RememberAccountBeforeLogin();
            var outcome = await _accountService.ImportStokenAsync(stoken, mid, aid);
            if (outcome.IsSuccess)
            {
                OnSignedIn(outcome.DisplayName);
            }
            else if (outcome.Aid > 0)
            {
                ShowSignedInFailure(outcome);
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
                OnSignedIn(outcome.DisplayName);
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
            case PassportLoginState.Failed:
                if (outcome.Aid > 0)
                {
                    // 登录本身已经完成（SToken 已入库），只是米游社工具箱的派生凭据没换到。
                    // 这时不能提示“登录成功”、也不能收起面板，把原因显示在账号列表上。
                    ShowSignedInFailure(outcome);
                }
                else
                {
                    StatusMessage = outcome.Message;
                }
                return true;
            default:
                StatusMessage = outcome.Message;
                return true;
        }
    }




    /// <summary>
    /// 账号已经登录成功、但派生凭据（工具箱 Cookie）获取失败：切到账号列表并显示原因，面板保持打开。
    /// </summary>
    private void ShowSignedInFailure(PassportLoginOutcome outcome)
    {
        _addingAccount = false;
        // 账号已经登进去了，短信登录的临时凭据没用了。
        _loginSession.Reset();
        UpdateAccountState();
        StatusMessage = outcome.Message;
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
            OnSignedIn(verify.DisplayName);
        }
        else if (verify.Aid > 0)
        {
            ShowSignedInFailure(verify);
        }
        else
        {
            StatusMessage = verify.Message;
        }
        return true;
    }




    /// <summary>
    /// 登录成功：提示用户，登录界面收起；从账号池里添加账号时留在原地，方便继续管理。
    /// <para>
    /// 添加账号时登录流程会把当前会话换成新账号，这里再恢复成登录前的账号：账号中心只是往账号池里
    /// 加一条记录，用户正在用的账号（以及各个启动预设记住的账号）不应该因为「加了个账号」而改变。
    /// 新账号已经完成入库和工具箱同步，账号列表里仍然能看到它。
    /// </para>
    /// </summary>
    private void OnSignedIn(string? displayName)
    {
        StopQrLogin();
        // 登录成功，短信登录的 action_type / 倒计时不再需要。
        _loginSession.Reset();
        PasswordBox_Password.Password = "";
        TextBox_Stoken.Text = "";
        TextBox_Mid.Text = "";
        // 登录结果里带着新账号的昵称；没有时退回当前会话。此时会话可能已经被预设流程换回旧账号，
        // 所以不能直接读 _accountService.DisplayName。
        displayName ??= _accountService.DisplayName;
        RestoreAccountBeforeLogin();
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
        // 登录成功是程序化收起：此时 IsBusy 可能还没在 finally 里复位，必须放行。
        AllowHostHide();
        Host?.Hide();
    }




    /// <summary>
    /// 记下登录新账号之前的当前账号。只在第一次登录尝试前记一次，验证码重试不会把它覆盖成新账号。
    /// </summary>
    private void RememberAccountBeforeLogin()
    {
        _accountKeyBeforeLogin ??= _accountService.CurrentAccountKey;
    }




    /// <summary>
    /// 登录新账号后恢复登录前的会话。旧账号已经不在账号池（或没有有效令牌）时保持新账号登录，
    /// 不会把用户留在未登录状态。
    /// </summary>
    private void RestoreAccountBeforeLogin()
    {
        string? key = _accountKeyBeforeLogin;
        _accountKeyBeforeLogin = null;
        if (string.IsNullOrWhiteSpace(key) || key == _accountService.CurrentAccountKey)
        {
            return;
        }
        _accountService.RestoreAccountSession(key);
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
        NotifyLoginProgressChanged();
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
            NotifyLoginProgressChanged();
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
        NotifyLoginProgressChanged();
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
                    OnSignedIn(outcome.DisplayName);
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
        NotifyLoginProgressChanged();
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




    /// <summary>
    /// 开始短信倒计时。<paramref name="seconds"/> 是“从现在起还能等多少秒”，
    /// 重开面板时由会话里的发送时间算出来，所以不再额外抬高下限；服务端没给倒计时时会话会按 60 秒算。
    /// </summary>
    private void StartSmsCountdown(int seconds)
    {
        try
        {
            StopSmsCountdown();
            int remain = Math.Max(seconds, 1);
            Button_SendSmsCode.IsEnabled = false;
            _countdownTimer = DispatcherQueue.CreateTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += (_, _) =>
            {
                remain--;
                if (remain <= 0)
                {
                    StopSmsCountdown();
                }
                else
                {
                    Button_SendSmsCode.Content = string.Format(Lang.PassportLogin_ResendIn, remain);
                }
            };
            _countdownTimer.Start();
            Button_SendSmsCode.Content = string.Format(Lang.PassportLogin_ResendIn, remain);
            NotifyLoginProgressChanged();
        }
        catch { }
    }




    /// <summary>停止短信倒计时，把发送按钮恢复成可再次发送的状态。</summary>
    private void StopSmsCountdown()
    {
        try
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
            Button_SendSmsCode.Content = Lang.PassportLogin_SendSmsCode;
            UpdateInteraction();
        }
        catch { }
        NotifyLoginProgressChanged();
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
            // 放弃添加账号等于放弃这次短信登录，action_type / 验证码 / 倒计时一起作废。
            _loginSession.Reset();
            StopSmsCountdown();
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
    /// 刷新信息：刷新账号池里到期账号的凭据、米游社账号的昵称头像，以及当前游戏的角色资料。
    /// <para>
    /// 凭据本来就会按需自动刷新（启动游戏时校验 SToken、派生令牌过期后重新换取），
    /// 工具箱数据平时由各个页面自己按需拉取，这里是给用户的一个兜底入口。
    /// 失效的账号会在列表里显示警告红点，点它上面的“重新登录”即可重新登录。
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
            var (refreshed, expired, expiredKeys) = await _accountService.RefreshAllAccountsAsync();
            int users = await _accountService.RefreshAccountUserInfosAsync();
            _accountService.SyncAccountNamesFromToolbox();
            UpdateAccountState();
            WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(_accountService.IsLoggedIn));
            if (users > 0 || refreshed > 0)
            {
                InAppToast.MainWindow?.Success(string.Format(Lang.PassportLogin_CredentialsRefreshed, refreshed));
            }
            else
            {
                InAppToast.MainWindow?.Success(Lang.PassportLogin_CredentialsAreFresh);
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
    /// 国际服账号登录：走上游的 HYP 网页登录（<see cref="HypLoginWindow"/>），成功后把账号写进账号池。
    /// <para>
    /// 账号中心本身只做国服通行证的密码 / 短信 / 扫码 / 令牌导入；国际服没有这些接口，
    /// 统一交给 HYP 网页登录，登录结果由 <see cref="PassportAccountService.ImportPassportUserInfoAsync"/> 落库。
    /// </para>
    /// </summary>
    private async void Button_HypLogin_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            IsBusy = true;
            StatusMessage = Lang.PassportLogin_HypLoginOpening;
            // 【已停用】账号中心已换成上游 MainView 的 HYP 浮窗；这里保留面板内的国际服登录实现备查，
            // 不再依赖 MainView 的扩展方法，直接开上游的 HYP 网页登录。
            var model = await new HypLoginWindow
            {
                ParentWindowHandle = MainWindow.Current.WindowHandle,
                ServerRegion = ServerRegion.HoYoverse,
            }.LoginAsync();
            if (model is not null)
            {
                PassportService.SavePassportUserInfo(model);
            }
            if (model is null)
            {
                StatusMessage = null;
                return;
            }
            await _accountService.ImportPassportUserInfoAsync(model);
            string? displayName = string.IsNullOrWhiteSpace(model.Nickname) ? model.Aid.ToString() : model.Nickname;
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
            // 登录成功是程序化收起：此时 IsBusy 还没在 finally 里复位，必须放行。
            AllowHostHide();
            Host?.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HYP login from the account center");
            StatusMessage = Lang.PassportLogin_ServiceError;
        }
        finally
        {
            IsBusy = false;
        }
    }




    /// <summary>
    /// 登录已失效的账号点“重新登录”：切到登录界面，预填上次登录用的手机号 / 通行证 ID。
    /// 登录成功后同一个账号会覆盖账号池里那条记录，红点随之消失。
    /// </summary>
    private void Button_ReLoginAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string key)
            {
                return;
            }
            var item = AccountList.FirstOrDefault(x => x.Key == key);
            if (item is null)
            {
                return;
            }
            _addingAccount = true;
            AccountErrorMessage = null;
            StatusMessage = string.Format(Lang.PassportLogin_AccountExpiredHint, item.DisplayName);
            NotifyPaneVisibilityChanged();
            UpdatePanes();
            if (!string.IsNullOrWhiteSpace(item.LoginName))
            {
                TextBox_Account.Text = item.LoginName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-login an expired passport account");
        }
    }




    /// <summary>
    /// 【已改为"刷新信息"】原"更新指纹"按钮已删除（产品方要求）：它原来强制重申请一枚指纹，
    /// 而重申请得到的必然是未被账号登记的设备指纹，只会把可用状态换成永久 10041。
    /// 设备指纹现在只保留"首次运行没有指纹时申请一次"这一个自动入口。
    /// 该槽位改为刷新账号信息，复用既有的刷新逻辑（<see cref="Button_RefreshInfo_Click"/>）。
    /// </summary>
    private void Button_UpdateDeviceFp_Click(object sender, RoutedEventArgs e)
    {
        Button_RefreshInfo_Click(sender, e);
    }




    #endregion




}




/// <summary>
/// 账号中心的入口。入口在左侧导航栏的账号按钮上，这里只负责把浮窗弹出来。
/// </summary>
internal static class AccountCenter
{


    private static Flyout? _flyout;

    private static AccountCenterPanel? _panel;


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
            // 挂上宿主：登录过程中拦下 light-dismiss，显式取消/登录成功时再放行。
            panel.AttachHost(flyout);
            flyout.Closed += (_, _) =>
            {
                panel.Cleanup();
                _flyout = null;
                _panel = null;
            };
            _flyout = flyout;
            _panel = panel;
            flyout.ShowAt(anchor);
            // ShowAt 是同步的，浮窗在这一刻就按当前内容把大小定死了；
            // 登录成功收起登录表单后卡片会矮一大截，所以排一次重算，让高度跟着内容走。
            panel.DispatcherQueue.TryEnqueue(panel.RefreshLayout);
        }
        catch
        {
            _flyout = null;
            _panel = null;
        }
    }




    public static void Hide()
    {
        try
        {
            // 程序化收起：登录过程中也要放行，避免卡片关不掉。
            _panel?.AllowHostHide();
            _flyout?.Hide();
        }
        catch { }
        _flyout = null;
        _panel = null;
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


