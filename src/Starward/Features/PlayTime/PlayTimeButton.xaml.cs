using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Features.Database;
using Starward.Helpers;
using System;
using System.Threading.Tasks;


namespace Starward.Features.PlayTime;

[INotifyPropertyChanged]
public sealed partial class PlayTimeButton : UserControl
{


    public GameBiz CurrentGameBiz { get; set; }


    private readonly ILogger<PlayTimeButton> _logger = AppConfig.GetLogger<PlayTimeButton>();



    public PlayTimeButton()
    {
        this.InitializeComponent();
    }



    public TimeSpan PlayTimeTotal { get; set => SetProperty(ref field, value); }



    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        InitializePlayTime();
    }



    private void InitializePlayTime()
    {
        try
        {
            PlayTimeTotal = DatabaseService.GetValue<TimeSpan>(PlayTimeStatsService.TotalPlayTimeKey(CurrentGameBiz), out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize play time");
        }
    }



    public static string TimeSpanToString(TimeSpan timeSpan)
    {
        int totalMinutes = (int)Math.Round(timeSpan.TotalMinutes);
        int hours = totalMinutes / 60, minutes = totalMinutes % 60;
        return $"{hours}h {minutes}m";
    }


    [RelayCommand]
    private async Task OpenStatsDialogAsync()
    {
        try
        {
            await new PlayTimeStatsDialog
            {
                CurrentGameBiz = CurrentGameBiz,
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            // 对话框构造/加载失败不应该让整个应用崩掉
            _logger.LogError(ex, "Open play time stats dialog: GameBiz {biz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
        InitializePlayTime();
    }


}
