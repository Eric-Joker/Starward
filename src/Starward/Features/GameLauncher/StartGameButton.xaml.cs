using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Features.GameInstall;
using Starward.GameInstall;
using System;
using System.Windows.Input;


namespace Starward.Features.GameLauncher;

[INotifyPropertyChanged]
public sealed partial class StartGameButton : UserControl
{


    private static Brush AccentFillColorDefaultBrush => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private static Brush TextOnAccentFillColorDisabled => (Brush)Application.Current.Resources["TextOnAccentFillColorDisabledBrush"];
    private static Brush TextOnAccentFillColorPrimaryBrush => (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
    private static Brush TextOnAccentFillColorSecondaryBrush => (Brush)Application.Current.Resources["TextOnAccentFillColorSecondaryBrush"];
    private static Brush TextFillColorDisabledBrush => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];


    public StartGameButton()
    {
        this.InitializeComponent();
        // 预设名和「开始游戏」同格同心居中，位置完全靠这个 RenderTransform 扛，
        // 所以它一点布局都不占，主文案的测量、居中和原来完全一样。
        TextBlock_LaunchProfileName.RenderTransform = _launchProfileNameTransform;
        this.LayoutUpdated += StartGameButton_LayoutUpdated;
        // Loaded 时再同步跑一次布局并重算：保证第一次显示就用实测宽度定位，不依赖某一帧的时序。
        this.Loaded += StartGameButton_Loaded;
        this.ActualThemeChanged += StartGameButton_ActualThemeChanged;
    }


    public ICommand GameCommand { get; set => SetProperty(ref field, value); }


    public ICommand SettingCommand { get; set => SetProperty(ref field, value); }


    public string? RunningGameTime { get; set { field = value; OnPropertyChanged(nameof(StartGameButtonText)); OnPropertyChanged(nameof(GameActionAccessibilityText)); } }


    public string? RunningGameProcessInfo { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 当前启动预设名。显示为「开始游戏」主文案右侧偏下的一行小字，没有自己的背景和边框，
    /// 位置由 <see cref="StartGameButton_LayoutUpdated"/> 按主文案实测宽度算出来；
    /// 预设只有一个时由 <see cref="ProfileNameVisible"/> 把它整块隐藏。
    /// </summary>
    public string? ProfileName { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否显示当前启动预设名。区服不止一个预设时才为 true。
    /// </summary>
    public bool ProfileNameVisible { get; set => SetProperty(ref field, value); }



    public GameState GameState { get; set { if (SetProperty(ref field, value)) UpdateActionButtonState(); } }


    public bool ActionButtonPointerOver { get; set { if (SetProperty(ref field, value)) UpdateActionButtonState(); } }


    public bool SettingButtonPointerOver { get; set { if (SetProperty(ref field, value)) UpdateButtonForeground(); } }


    public bool GameStateIsInstalling => GameState is GameState.Installing;


    public bool IsAccentColorBackgroundVisible => Button_GameAction.IsEnabled && GameState is not GameState.Installing;


    public bool IsGameActionCommandRunning => !Button_GameAction.IsEnabled && GameState is not GameState.GameIsRunning;


    public string StartGameButtonText => GameState switch
    {
        GameState.StartGame => Lang.LauncherPage_StartGame,
        GameState.GameIsRunning => RunningGameTime ?? Lang.LauncherPage_GameIsRunning,
        GameState.InstallGame => Lang.LauncherPage_InstallGame,
        GameState.UpdateGame => Lang.LauncherPage_UpdateGame,
        GameState.UpdatePlugin => Lang.StartGameButton_UpdatePlugin,
        GameState.Installing => "",
        GameState.ResumeDownload => Lang.StartGameButton_ResumeDownload,
        GameState.ComingSoon => Lang.StartGameButton_ComingSoon,
        GameState.Reservation => Lang.GameReservationView_PreRegister,
        GameState.HotUpdate => Lang.StartGameButton_HotUpdate,
        _ => "",
    };


    public string GameActionAccessibilityText
    {
        get
        {
            if (!GameStateIsInstalling)
            {
                return StartGameButtonText;
            }
            string text = InstallStateText;
            if (!string.IsNullOrWhiteSpace(ProgressPercentText))
            {
                text = $"{text} {ProgressPercentText}";
            }
            if (!string.IsNullOrWhiteSpace(InstallSecondaryText))
            {
                text = $"{text} {InstallSecondaryText}";
            }
            if (!string.IsNullOrWhiteSpace(InstallActionText))
            {
                text = $"{text} {InstallActionText}";
            }
            return text;
        }
    }


    public Brush ActionButtonForeground => (Button_GameAction.IsEnabled, IsAccentColorBackgroundVisible, ActionButtonPointerOver) switch
    {
        (false, _, _) => TextOnAccentFillColorDisabled,
        (true, false, true) => AccentFillColorDefaultBrush,
        (true, false, false) => TextOnAccentFillColorDisabled,
        _ => TextOnAccentFillColorPrimaryBrush
    };


    public Brush SettingButtonForeground => (IsAccentColorBackgroundVisible, SettingButtonPointerOver) switch
    {
        (false, true) => AccentFillColorDefaultBrush,
        (false, false) => TextOnAccentFillColorDisabled,
        _ => TextOnAccentFillColorPrimaryBrush
    };


    /// <summary>
    /// 预设名的颜色：必须和主文案的 <see cref="ActionButtonForeground"/> 逐态对应，否则会出现
    /// 「标题看得见、名字看不见」——强调色底上主文案是亮色，名字用「强调色表面上的次要文字色」；
    /// 一旦按钮不再是强调色底（禁用：启动中、游戏运行中），主文案会切成禁用色，
    /// 名字也必须是**同一态**的次要色，即页面表面的禁用色。
    /// </summary>
    public Brush LaunchProfileNameForeground => (Button_GameAction.IsEnabled, IsAccentColorBackgroundVisible, ActionButtonPointerOver) switch
    {
        (false, _, _) => TextFillColorDisabledBrush,
        (true, false, true) => AccentFillColorDefaultBrush,
        (true, false, false) => TextFillColorDisabledBrush,
        _ => TextOnAccentFillColorSecondaryBrush,
    };



    private void UpdateActionButtonState()
    {
        Button_GameAction.IsEnabled = GameState is not GameState.GameIsRunning and not GameState.ComingSoon;
        OnPropertyChanged(nameof(GameStateIsInstalling));
        if (GameStateIsInstalling)
        {
            UpdateActionButtonStateWhenInstalling();
        }
        OnPropertyChanged(nameof(IsAccentColorBackgroundVisible));
        OnPropertyChanged(nameof(IsGameActionCommandRunning));
        OnPropertyChanged(nameof(StartGameButtonText));
        OnPropertyChanged(nameof(GameActionAccessibilityText));
        UpdateButtonForeground();
    }



    private void UpdateButtonForeground()
    {
        OnPropertyChanged(nameof(ActionButtonForeground));
        OnPropertyChanged(nameof(SettingButtonForeground));
        OnPropertyChanged(nameof(LaunchProfileNameForeground));
    }



    private void Button_GameAction_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsAccentColorBackgroundVisible));
        OnPropertyChanged(nameof(IsGameActionCommandRunning));
        OnPropertyChanged(nameof(ActionButtonForeground));
        OnPropertyChanged(nameof(SettingButtonForeground));
        OnPropertyChanged(nameof(LaunchProfileNameForeground));
    }



    private void Control_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender as Grid == Grid_Root)
        {
            if (GameState is GameState.GameIsRunning or GameState.Installing)
            {
                Popup_GameInfoOrDownloadProgress.IsOpen = true;
            }
        }
        else if (sender as Button == Button_GameAction)
        {
            ActionButtonPointerOver = true;
        }
        else if (sender as Button == Button_Setting)
        {
            SettingButtonPointerOver = true;
        }
    }


    private void Control_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender as Grid == Grid_Root)
        {
            Popup_GameInfoOrDownloadProgress.IsOpen = false;
        }
        else if (sender as Button == Button_GameAction)
        {
            ActionButtonPointerOver = false;
        }
        else if (sender as Button == Button_Setting)
        {
            SettingButtonPointerOver = false;
        }

    }





    public RunnerState InstallState { get; set { if (SetProperty(ref field, value)) UpdateActionButtonStateWhenInstalling(); } }

    public RunnerStep InstallStep { get; set { if (SetProperty(ref field, value)) UpdateActionButtonStateWhenInstalling(); } }

    public GameInstallOperation InstallOperation { get; set => SetProperty(ref field, value); }


    public bool InstallProgressIsIndeterminate { get; set => SetProperty(ref field, value); }

    public double InstallInfoOpacity { get; set => SetProperty(ref field, value); }

    public double InstallActionTextOpacity { get; set => SetProperty(ref field, value); }

    public string InstallPhaseText { get; set => SetProperty(ref field, value); } = "";

    public string InstallOperationText { get; set => SetProperty(ref field, value); } = "";

    public string InstallStepPhaseText { get; set => SetProperty(ref field, value); } = "";

    public string? InstallSecondaryText { get; set => SetProperty(ref field, value); }

    public string InstallActionText { get; set => SetProperty(ref field, value); } = "";

    public double InstallPhaseFontSize { get; set => SetProperty(ref field, value); } = 12;

    public TextAlignment InstallPhaseTextAlignment { get; set => SetProperty(ref field, value); } = TextAlignment.Left;

    public string InstallStateText { get; set => SetProperty(ref field, value); } = "";

    public string InstallBytesLabel { get; set => SetProperty(ref field, value); } = "";


    public void UpdateActionButtonStateWhenInstalling()
    {
        bool showInfo = !ActionButtonPointerOver && InstallState is not RunnerState.Finish;
        InstallInfoOpacity = showInfo ? 1 : 0;
        InstallActionTextOpacity = !showInfo && InstallState is not RunnerState.Finish ? 1 : 0;
        bool isLargeCentered = InstallState is RunnerState.Paused or RunnerState.Error
            || (InstallState is RunnerState.Running && (InstallStep is RunnerStep.Pending or RunnerStep.None));
        InstallPhaseFontSize = isLargeCentered ? 14 : 12;
        InstallPhaseTextAlignment = isLargeCentered ? TextAlignment.Center : TextAlignment.Left;
        OnPropertyChanged(nameof(InstallState));
        OnPropertyChanged(nameof(InstallPhaseText));
        OnPropertyChanged(nameof(InstallOperationText));
        OnPropertyChanged(nameof(InstallStepPhaseText));
        OnPropertyChanged(nameof(InstallSecondaryText));
        OnPropertyChanged(nameof(InstallActionText));
        OnPropertyChanged(nameof(InstallStateText));
        OnPropertyChanged(nameof(InstallBytesLabel));
        OnPropertyChanged(nameof(InstallInfoOpacity));
        OnPropertyChanged(nameof(InstallActionTextOpacity));
        OnPropertyChanged(nameof(GameActionAccessibilityText));
    }




    public int ProgressRingValue { get; set => SetProperty(ref field, value); }

    public string ProgressRingText { get; set => SetProperty(ref field, value); } = "";

    public string? ProgressPercentText { get; set => SetProperty(ref field, value); }

    public string? DownloadBytesText { get; set => SetProperty(ref field, value); }

    public string? DownloadSpeedText { get; set => SetProperty(ref field, value); }

    public string? InstallBytesText { get; set => SetProperty(ref field, value); }

    public string? RemainTimeText { get; set => SetProperty(ref field, value); }

    public string? ErrorMessage { get; set => SetProperty(ref field, value); }

    public string? HardLinkBytesText { get; set => SetProperty(ref field, value); }



    public void UpdateGameInstallTaskState(GameInstallProgress task)
    {
        InstallOperation = task.Operation;
        InstallState = task.State;
        InstallStep = task.Step;

        InstallPhaseText = GameInstallProgressDisplay.GetPhaseText(task.State, task.Step);
        InstallStateText = GameInstallProgressDisplay.GetOperationPhaseText(task);
        InstallActionText = GameInstallProgressDisplay.GetActionText(task.State);

        string operationText = GameInstallProgressDisplay.GetOperationText(task.Operation);
        InstallOperationText = string.IsNullOrWhiteSpace(operationText) ? InstallPhaseText : operationText;

        string? stepText = GameInstallProgressDisplay.GetStepText(task);
        InstallStepPhaseText = string.IsNullOrWhiteSpace(stepText) ? InstallPhaseText : $"{stepText} · {InstallPhaseText}";

        bool hasPercent = GameInstallProgressDisplay.TryGetPercent(task, out double percent);
        InstallProgressIsIndeterminate = task.State is RunnerState.Running && !hasPercent;
        int ringValue = hasPercent ? (int)Math.Floor(percent) : 0;
        if (hasPercent && ringValue == 0 && percent > 0)
        {
            ringValue = 1;
        }
        ProgressRingValue = ringValue;
        ProgressRingText = hasPercent ? ringValue.ToString() : "";
        ProgressPercentText = hasPercent ? GameInstallProgressDisplay.ToPercentText(percent) : null;

        DownloadBytesText = null;
        DownloadSpeedText = null;
        InstallBytesText = null;
        InstallBytesLabel = "";
        RemainTimeText = null;
        ErrorMessage = null;
        HardLinkBytesText = null;

        if (task.State is RunnerState.Running or RunnerState.Paused or RunnerState.Error)
        {
            if (task.ProgressDisplay.HasFlag(ProgressDisplay.NetworkBytes))
            {
                DownloadBytesText = GameInstallProgressDisplay.ToBytesText(task.NetworkFinishedBytes, task.NetworkTotalBytes);
            }
            if (task.State is RunnerState.Running
                && task.Step is RunnerStep.Downloading or RunnerStep.Patching
                && task.ProgressDisplay.HasFlag(ProgressDisplay.NetworkSpeed))
            {
                DownloadSpeedText = GameInstallProgressDisplay.ToSpeedText(task.NetworkDownloadSpeed);
            }
            if (task.ProgressDisplay.HasFlag(ProgressDisplay.StorageBytes))
            {
                InstallBytesText = GameInstallProgressDisplay.ToBytesText(task.StorageFinishedBytes, task.StorageTotalBytes);
                InstallBytesLabel = task.Step is RunnerStep.Verifying ? Lang.StartGameButton_Verify : Lang.StartGameButton_Install;
            }
            else
            {
                InstallBytesLabel = "";
            }
            if (task.HardLinkBytes > 0)
            {
                HardLinkBytesText = GameInstallProgressDisplay.FormatBytes(task.HardLinkBytes);
            }
            if (task.State is RunnerState.Running
                && task.Step is RunnerStep.Downloading
                && task.ProgressDisplay.HasFlag(ProgressDisplay.NetworkSpeed))
            {
                RemainTimeText = GameInstallProgressDisplay.ToRemainTimeText(task.RemainTimeSeconds);
            }
        }

        if (task.State is RunnerState.Error)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(task.ErrorMessage) ? Lang.DownloadGamePage_UnknownError : task.ErrorMessage;
        }

        string? secondaryText = null;
        if (task.State is RunnerState.Running or RunnerState.Error)
        {
            // 第二行只显示剩余时间、网络字节或存储字节；暂停时不显示第二行。
            if (task.State is RunnerState.Running
                && task.Step is RunnerStep.Downloading
                && task.ProgressDisplay.HasFlag(ProgressDisplay.NetworkSpeed)
                && task.RemainTimeSeconds > 0)
            {
                secondaryText = GameInstallProgressDisplay.ToRemainTimeText(task.RemainTimeSeconds);
            }
            else if (task.ProgressDisplay.HasFlag(ProgressDisplay.StorageBytes)
                     && GameInstallProgressDisplay.ToBytesText(task.StorageFinishedBytes, task.StorageTotalBytes, 1) is { } storageText)
            {
                secondaryText = storageText;
            }
            else if (task.ProgressDisplay.HasFlag(ProgressDisplay.NetworkBytes)
                     && GameInstallProgressDisplay.ToBytesText(task.NetworkFinishedBytes, task.NetworkTotalBytes, 1) is { } networkText)
            {
                secondaryText = networkText;
            }
        }

        InstallSecondaryText = secondaryText;
        OnPropertyChanged(nameof(GameActionAccessibilityText));

        if (task.State is RunnerState.Finish)
        {
            Popup_GameInfoOrDownloadProgress.IsOpen = false;
        }
    }



    private void StartGameButton_ActualThemeChanged(FrameworkElement sender, object args)
    {
        OnPropertyChanged(nameof(ActionButtonForeground));
        OnPropertyChanged(nameof(SettingButtonForeground));
        OnPropertyChanged(nameof(LaunchProfileNameForeground));
    }




    /// <summary>
    /// 当前预设名的位移（水平贴标题右缘，垂直底边对齐标题底边）。
    /// 名字和主文案同格同心居中，所以主文案右边缘相对这一格的中心就是「主文案宽度 / 2」；
    /// 水平位移 = 「主文案宽度 / 2 + 间距 + 预设名宽度 / 2」，名字居中后减去自己的半宽再平移，
    /// 左边缘正好落在主文案右边缘右侧 <see cref="LaunchProfileNameGap"/>。
    /// 用 TranslateTransform 而不是 Margin：RenderTransform 不参与布局，
    /// 改它不会触发重新测量，标题的测量和位置一点都不会被动到。
    /// </summary>
    private readonly TranslateTransform _launchProfileNameTransform = new();


    /// <summary>预设名和「开始游戏」之间留出的水平间距（DIP）。</summary>
    private const double LaunchProfileNameGap = 1;


    /// <summary>上一次用过的实测宽高缓存，只在真的变了的时候才写 RenderTransform。</summary>
    private double _launchProfileNameCaptionWidth = double.NaN;

    private double _launchProfileNameNameWidth = double.NaN;

    private double _launchProfileNameCaptionHeight = double.NaN;

    private double _launchProfileNameNameHeight = double.NaN;


    /// <summary>
    /// 第一次显示时先强制跑一遍布局，拿到标题和预设名的实测宽高，再定位一次；
    /// 然后不依赖任何一帧的时序，后面的 SizeChanged / LayoutUpdated 会继续跟着更新。
    /// </summary>
    private void StartGameButton_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.UpdateLayout();
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>
    /// 主文案的文字会跟着游戏状态变（开始游戏 / 正在运行的时间 / 安装游戏 / 更新游戏 …），
    /// 预设名也会变，按钮本身还会随内容和窗口变化，所以每次布局之后都重新摆一次。
    /// 这里只写 RenderTransform（不参与布局），显隐只由 XAML 的 Visibility 绑定决定。
    /// </summary>
    private void StartGameButton_LayoutUpdated(object? sender, object e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>主文案大小变了（换状态导致文字变长变短、字号变化）：重新定位。</summary>
    private void TextBlock_StartGame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>预设名所在的那一格大小变了（按钮随内容/窗口变宽变窄）：重新定位。</summary>
    private void Grid_StartGameCaption_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>预设名自己大小变了（换预设、名字变长变短）：重新定位。</summary>
    private void TextBlock_LaunchProfileName_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>
    /// 按实测宽高摆放预设名（只用 RenderTransform，绝不参与布局，所以标题一点都不会被动到）：
    /// 水平把名字左边缘放到标题右边缘右侧 <see cref="LaunchProfileNameGap"/>，
    /// 垂直让两者底边齐平。宽高拿不到时归零（保持居中），下一帧布局量出来就会重新摆放。
    /// </summary>
    private void UpdateLaunchProfileNameTransform()
    {
        double captionWidth = TextBlock_StartGame.ActualWidth;
        double captionHeight = TextBlock_StartGame.ActualHeight;
        double nameWidth = TextBlock_LaunchProfileName.ActualWidth;
        double nameHeight = TextBlock_LaunchProfileName.ActualHeight;
        if (captionWidth <= 0 || captionHeight <= 0 || nameWidth <= 0 || nameHeight <= 0)
        {
            _launchProfileNameCaptionWidth = 0;
            _launchProfileNameNameWidth = 0;
            _launchProfileNameCaptionHeight = 0;
            _launchProfileNameNameHeight = 0;
            _launchProfileNameTransform.X = 0;
            _launchProfileNameTransform.Y = 0;
            return;
        }
        if (captionWidth == _launchProfileNameCaptionWidth
            && nameWidth == _launchProfileNameNameWidth
            && captionHeight == _launchProfileNameCaptionHeight
            && nameHeight == _launchProfileNameNameHeight)
        {
            return;
        }
        _launchProfileNameCaptionWidth = captionWidth;
        _launchProfileNameNameWidth = nameWidth;
        _launchProfileNameCaptionHeight = captionHeight;
        _launchProfileNameNameHeight = nameHeight;
        // 水平：左边缘贴在标题右边缘右侧（两者都居中，所以差的是半宽和）。
        _launchProfileNameTransform.X = captionWidth / 2 + LaunchProfileNameGap + nameWidth / 2;
        // 垂直：底边对齐标题底边（两者都居中，差的就是半高差）。
        _launchProfileNameTransform.Y = (captionHeight - nameHeight) / 2;
    }


}
