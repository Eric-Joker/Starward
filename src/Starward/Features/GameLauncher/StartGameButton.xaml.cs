using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.RPC.GameInstall;
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


    public string? RunningGameTime { get; set { field = value; OnPropertyChanged(nameof(StartGameButtonText)); } }


    public string? RunningGameInfo { get; set => SetProperty(ref field, value); }


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
        GameState.GameIsRunning => RunningGameTime ?? "",
        GameState.InstallGame => Lang.LauncherPage_InstallGame,
        GameState.UpdateGame => Lang.LauncherPage_UpdateGame,
        GameState.UpdatePlugin => "Update Plugins",
        GameState.Installing => "",
        GameState.ResumeDownload => Lang.StartGameButton_ResumeDownload,
        GameState.ComingSoon => "Coming Soon",
        _ => "",
    };


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
    /// 名字也必须是**同一态的**次要色，即页面表面的禁用色。
    /// 之前这里按「是不是强调色的主文字色」来二选一，禁用态被算进了「非强调色底」，
    /// 于是主文案暗下去了、名字还是 TextFillColorSecondary（深色主题下 77% 白，比主文案的 53% 还亮），
    /// 看上去就是"名字没跟着变色"。
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





    public GameInstallState InstallState { get; set { if (SetProperty(ref field, value)) UpdateActionButtonStateWhenInstalling(); } }


    public bool InstallStateIsPending => InstallState is GameInstallState.Waiting;

    public bool InstallStateIsDownloading => InstallState is GameInstallState.Downloading;

    public double Button_GameAction_DownloadingRemainTime_Opacity => !ActionButtonPointerOver && InstallState is GameInstallState.Downloading ? 1 : 0;

    public double TextBlock_GameAction_InstallState_Opacity => !ActionButtonPointerOver && InstallState is not GameInstallState.Downloading ? 1 : 0;

    public double TextBlock_GameAction_PointerOver_Opacity => ActionButtonPointerOver ? 1 : 0;

    public string TextBlock_GameAction_InstallState_Text => (ActionButtonPointerOver, InstallState) switch
    {
        (false, GameInstallState.Waiting) => Lang.StartGameButton_Waiting,
        (false, GameInstallState.Decompressing) => Lang.DownloadGamePage_Decompressing,
        (false, GameInstallState.Merging) => Lang.DownloadGamePage_Merging,
        (false, GameInstallState.Verifying) => Lang.DownloadGamePage_Verifying,
        (false, GameInstallState.Paused) => Lang.DownloadGamePage_Paused,
        (false, GameInstallState.Error) => Lang.Common_Error,
        (false, GameInstallState.Queueing) => Lang.StartGameButton_InQueue,
        _ => ""
    };

    public string TextBlock_GameAction_PointerOver_Text => (ActionButtonPointerOver, InstallState) switch
    {
        (true, GameInstallState.Waiting or GameInstallState.Downloading) => Lang.DownloadGamePage_Pause,
        (true, GameInstallState.Paused or GameInstallState.Error or GameInstallState.Queueing) => Lang.Common_Continue,
        (true, GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying) => Lang.Common_Cancel,
        _ => ""

    };

    public string InstallStateText => InstallState switch
    {
        GameInstallState.Waiting => Lang.StartGameButton_Waiting,
        GameInstallState.Downloading => Lang.DownloadGamePage_Downloading,
        GameInstallState.Decompressing => Lang.DownloadGamePage_Decompressing,
        GameInstallState.Merging => Lang.DownloadGamePage_Merging,
        GameInstallState.Verifying => Lang.DownloadGamePage_Verifying,
        GameInstallState.Paused => Lang.DownloadGamePage_Paused,
        GameInstallState.Finish => Lang.DownloadGamePage_Finished,
        GameInstallState.Error => Lang.DownloadGamePage_SomethingError,
        GameInstallState.Queueing => Lang.StartGameButton_InQueue,
        _ => "State Error"
    };


    public void UpdateActionButtonStateWhenInstalling()
    {
        OnPropertyChanged(nameof(InstallState));
        OnPropertyChanged(nameof(InstallStateIsPending));
        OnPropertyChanged(nameof(InstallStateIsDownloading));
        OnPropertyChanged(nameof(Button_GameAction_DownloadingRemainTime_Opacity));
        OnPropertyChanged(nameof(TextBlock_GameAction_InstallState_Opacity));
        OnPropertyChanged(nameof(TextBlock_GameAction_PointerOver_Opacity));
        OnPropertyChanged(nameof(TextBlock_GameAction_InstallState_Text));
        OnPropertyChanged(nameof(TextBlock_GameAction_PointerOver_Text));
        OnPropertyChanged(nameof(InstallStateText));
    }




    public int ProgressRingValue { get; set => SetProperty(ref field, value); }

    public string ProgressPercentText { get; set => SetProperty(ref field, value); }

    public string? DownloadBytesText { get; set => SetProperty(ref field, value); }

    public string? DownloadSpeedText { get; set => SetProperty(ref field, value); }

    public string? InstallBytesText { get; set => SetProperty(ref field, value); }

    public string? InstallSpeedText { get; set => SetProperty(ref field, value); }

    public string? VerifySpeedText { get; set => SetProperty(ref field, value); }

    public string? RemainTimeText { get; set => SetProperty(ref field, value); }

    public string? ErrorMessage { get; set => SetProperty(ref field, value); }



    public void UpdateGameInstallTaskState(GameInstallContext task)
    {
        InstallState = task.State;
        DownloadBytesText = ToBytesText(task.Progress_DownloadFinishBytes, task.Progress_DownloadTotalBytes);
        InstallBytesText = ToBytesText(task.Progress_WriteFinishBytes, task.Progress_WriteTotalBytes);
        ErrorMessage = task.ErrorMessage;
        if (InstallState is GameInstallState.Downloading)
        {
            long total = task.Progress_DownloadTotalBytes;
            long finish = task.Progress_DownloadFinishBytes;
            double progress = (double)finish / total;
            DownloadSpeedText = ToSpeedText(task.NetworkDownloadSpeed);
            InstallSpeedText = ToSpeedText(task.StorageWriteSpeed);
            VerifySpeedText = ToSpeedText(task.StorageReadSpeed);
            RemainTimeText = ToRemainTimeText(task.RemainTimeSeconds);
            if (task.Operation is GameInstallOperation.Update && task.DownloadMode is GameInstallDownloadMode.Chunk)
            {
                progress = (double)task.Progress_WriteFinishBytes / task.Progress_WriteTotalBytes;
                ProgressRingValue = (int)(progress * 100);
                ProgressPercentText = $"{progress:P1}";
            }
            else
            {
                ProgressRingValue = (int)(progress * 100);
                ProgressPercentText = $"{progress:P1}";
            }
        }
        else if (InstallState is GameInstallState.Decompressing or GameInstallState.Merging)
        {
            DownloadSpeedText = null;
            InstallSpeedText = null;
            VerifySpeedText = null;
            RemainTimeText = "--:--:--";
            ProgressRingValue = (int)(task.Progress_Percent * 100);
            ProgressPercentText = $"{task.Progress_Percent:P1}";
        }
        else if (InstallState is GameInstallState.Finish)
        {
            DownloadSpeedText = null;
            InstallBytesText = null;
            DownloadSpeedText = null;
            InstallSpeedText = null;
            VerifySpeedText = null;
            RemainTimeText = null;
            ErrorMessage = null;
            ProgressRingValue = 100;
            ProgressPercentText = "100%";
            Popup_GameInfoOrDownloadProgress.IsOpen = false;
        }
        else
        {
            DownloadSpeedText = null;
            InstallSpeedText = null;
            VerifySpeedText = null;
            RemainTimeText = "--:--:--";
        }
    }


    private static string? ToBytesText(long finish, long total)
    {
        const double MB = 1 << 20;
        const double GB = 1 << 30;
        if (total == 0)
        {
            return null;
        }
        if (total >= GB)
        {
            return $"{finish / GB:F2}/{total / GB:F2} GB";
        }
        else
        {
            return $"{finish / MB:F2}/{total / MB:F2} MB";
        }
    }


    private static string ToSpeedText(long bytes)
    {
        const double KB = 1 << 10;
        const double MB = 1 << 20;
        if (bytes >= MB)
        {
            return $"{bytes / MB:F2} MB/s";
        }
        else
        {
            return $"{bytes / KB:F2} KB/s";
        }
    }


    private static string? ToRemainTimeText(long seconds)
    {
        if (seconds == 0)
        {
            return "--:--:--";
        }
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    }



    private void StartGameButton_ActualThemeChanged(FrameworkElement sender, object args)
    {
        OnPropertyChanged(nameof(ActionButtonForeground));
        OnPropertyChanged(nameof(SettingButtonForeground));
        OnPropertyChanged(nameof(LaunchProfileNameForeground));
    }




    /// <summary>
    /// 当前预设名的位移（水平贴标题右缘 +4px，垂直底边对齐标题底边）。
    /// 名字和主文案同格同心居中，所以主文案右边缘相对这一格的中心就是「主文案宽度 / 2」；
    /// 水平位移 = 「主文案宽度 / 2 + 间距 + 预设名宽度 / 2」，名字居中后减去自己的半宽再平移，
    /// 左边缘正好落在主文案右边缘右侧 <see cref="LaunchProfileNameGap"/>。
    /// 垂直方向两者都居中，底边差就是半高差，位移 Y = 「(主文案高 − 预设名高) / 2」。
    /// 用 TranslateTransform 而不是 Margin：RenderTransform 不参与布局，
    /// 改它不会触发重新测量，标题的测量和位置一点都不会被动到。
    /// </summary>
    private readonly TranslateTransform _launchProfileNameTransform = new();


    /// <summary>
    /// 预设名和「开始游戏」之间留出的水平间距。
    /// 取 1：名字是 11pt 的小字、贴着标题右下角当注脚，再紧就会和标题右缘粘在一起。
    /// 这个名字只用于水平位移 <see cref="UpdateLaunchProfileNameTransform"/>，没有别的使用点。
    /// </summary>
    private const double LaunchProfileNameGap = 1;


    /// <summary>
    /// 上一次用过的「主文案宽 / 预设名宽 / 主文案高 / 预设名高」缓存，只在真的变了的时候才写
    /// RenderTransform，避免每个布局帧都无谓地重设。
    /// </summary>
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


    /// <summary>
    /// 主文案大小变了（换状态导致文字变长变短、字号变化）：重新定位。
    /// </summary>
    private void TextBlock_StartGame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>
    /// 预设名所在的那一格大小变了（按钮随内容/窗口变宽变窄）：重新定位。
    /// </summary>
    private void Grid_StartGameCaption_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateLaunchProfileNameTransform();
        }
        catch { }
    }


    /// <summary>
    /// 预设名自己大小变了（换预设、名字变长变短）：重新定位。
    /// </summary>
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
    /// <list type="bullet">
    /// <item>水平：名字和标题同格同心居中，标题右缘相对格心是「标题宽 / 2」，所以
    /// X = 标题宽 / 2 + 间距 + 名字宽 / 2，名字左边缘正好落在标题右边缘右侧
    /// <see cref="LaunchProfileNameGap"/>（4px）。</item>
    /// <item>垂直：两者都居中，底边差 = (标题高 − 名字高) / 2，所以 Y 取这个值，
    /// 名字的底边就和标题的底边齐平（不是靠 Margin 猜的，是两个实测高度算出来的）。</item>
    /// </list>
    /// 宽高拿不到时两者都归零（保持居中），下一帧布局量出来就会重新摆放。
    /// 名字的宽度上限由 XAML 的 MaxWidth=200 控制，常见预设名不会被截断，按钮随内容自适应加宽。
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
        // 水平：左边缘贴在标题右边缘右侧 6px。
        _launchProfileNameTransform.X = captionWidth / 2 + LaunchProfileNameGap + nameWidth / 2;
        // 垂直：底边对齐标题底边（两者都居中，差的就是半高差）。
        _launchProfileNameTransform.Y = (captionHeight - nameHeight) / 2;
    }


}
