using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core.HoYoPlay;
using System;
using System.Threading.Tasks;

namespace Starward.Features.Background;

[ObservableObject]
public sealed partial class BackgroundSwitcher : UserControl
{

    private readonly ILogger<BackgroundSwitcher> _logger = AppConfig.GetLogger<BackgroundSwitcher>();

    private const string PlayIcon = "\uF5B0";

    private const string PauseIcon = "\uE62E";



    public BackgroundSwitcher()
    {
        InitializeComponent();
        OnGameBackgroundsChanged();
        OnCurrentGameBackgroundChanged();
        OnVideoPlayingChanged();
        AppBackground.Current.PropertyChanged += AppBackground_PropertyChanged;
        this.Unloaded += BackgroundSwitcher_Unloaded;
    }


    private void BackgroundSwitcher_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppBackground.Current.PropertyChanged -= AppBackground_PropertyChanged;
        this.Unloaded -= BackgroundSwitcher_Unloaded;
    }

    private void AppBackground_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppBackground.GameBackgrounds):
                OnGameBackgroundsChanged();
                break;
            case nameof(AppBackground.CurrentGameBackground):
                OnCurrentGameBackgroundChanged();
                break;
            case nameof(AppBackground.IsVideoPlaying):
                OnVideoPlayingChanged();
                break;
        }
    }



    private GameId? _currentGameId;

    /// <summary>当前游戏；背景切换条按它决定要不要显示好感壁纸入口。</summary>
    public GameId? CurrentGameId
    {
        get => _currentGameId;
        set
        {
            _currentGameId = value;
            ShowFavorWallpaper = GameFeatureConfig.FromGameId(value).SupportFavorWallpaper;
        }
    }

    /// <summary>当前游戏是否支持好感壁纸（只有绝区零有）。</summary>
    public bool ShowFavorWallpaper { get; set => SetProperty(ref field, value); }

    public int GameBackgroundCount { get; set => SetProperty(ref field, value); }

    [ObservableProperty]
    public partial int CurrentGameBackgroundIndex { get; set; }

    public bool IsCopySaveEnabled { get; set => SetProperty(ref field, value); }

    public bool IsVideoGameBackground { get; set => SetProperty(ref field, value); }

    public string StartStopButtonIcon { get; set => SetProperty(ref field, value); }


    private void OnGameBackgroundsChanged()
    {
        try
        {
            if (AppBackground.Current.GameBackgrounds is null)
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Collapsed;
                GameBackgroundCount = 0;
            }
            else
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Visible;
                GameBackgroundCount = AppBackground.Current.GameBackgrounds.Count;
            }
        }
        catch { }
    }


    private void OnCurrentGameBackgroundChanged()
    {
        try
        {
            if (AppBackground.Current.CurrentGameBackground is null)
            {
                IsCopySaveEnabled = false;
                IsVideoGameBackground = false;
            }
            else
            {
                IsCopySaveEnabled = true;
                IsVideoGameBackground = AppBackground.Current.CurrentGameBackground.GameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO;
                if (AppBackground.Current.GameBackgrounds is not null)
                {
                    CurrentGameBackgroundIndex = AppBackground.Current.GameBackgrounds.IndexOf(AppBackground.Current.CurrentGameBackground);
                }
            }
        }
        catch { }
    }


    private void OnVideoPlayingChanged()
    {
        try
        {
            StartStopButtonIcon = AppBackground.Current.IsVideoPlaying ? PauseIcon : PlayIcon;
        }
        catch { }
    }


    partial void OnCurrentGameBackgroundIndexChanged(int value)
    {
        try
        {
            _ = AppBackground.Current.ChangeCurrentBackgroundIndexAsync(value);
        }
        catch { }
    }


    private void Border_SwitchBackgroundImage_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 1;
    }


    private void Border_SwitchBackgroundImage_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 0;
    }


    int _switchBackgroundTotalDelta = 0;

    private void Border_SwitchBackgroundImage_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _switchBackgroundTotalDelta += delta;
        if (_switchBackgroundTotalDelta <= -120)
        {
            CurrentGameBackgroundIndex++;
            _switchBackgroundTotalDelta = 0;
        }
        else if (_switchBackgroundTotalDelta >= 120)
        {
            CurrentGameBackgroundIndex--;
            _switchBackgroundTotalDelta = 0;
        }
    }


    [RelayCommand]
    public void OpenBackgroundViewWindow()
    {
        try
        {
            new BackgroundViewWindow().Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open background view window.");
        }
    }


    /// <summary>
    /// 打开好感壁纸对话框：从米游社百科取绝区零的好感/满影画壁纸，可预览、下载、设为自定义背景。
    /// </summary>
    [RelayCommand]
    private async Task OpenFavorWallpaperDialogAsync()
    {
        try
        {
            if (CurrentGameId is null)
            {
                return;
            }
            await new FavorWallpaperDialog
            {
                CurrentGameId = CurrentGameId,
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open favor wallpaper dialog.");
        }
    }


    [RelayCommand]
    private async Task CopyCurrentBackgroundAsync()
    {
        await AppBackground.Current.CopyCurrentBackgroundAsync();
    }


    [RelayCommand]
    private async Task SaveCurrentBackgroundAsync()
    {
        await AppBackground.Current.SaveCurrentBackgroundAsync();
    }


    [RelayCommand]
    private void StartOrStopVideoBackground()
    {
        try
        {
            AppBackground.Current.PlayPauseVideo();
        }
        catch { }
    }





}
