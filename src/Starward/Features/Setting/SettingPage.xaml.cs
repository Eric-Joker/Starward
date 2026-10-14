using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Starward.Frameworks;
using System;
using System.Linq;


namespace Starward.Features.Setting;

public sealed partial class SettingPage : PageBase
{

    /// <summary>导航参数：滚动到「常规」页的开机启动一节。</summary>
    public const string StartAtLoginSection = "StartAtLogin";


    private readonly ILogger<SettingPage> _logger = AppConfig.GetLogger<SettingPage>();


    public SettingPage()
    {
        this.InitializeComponent();
        Frame_Setting.Navigate(typeof(AboutSetting));
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.Bindings.Update());
    }



    /// <summary>
    /// 处理来自其它页面的深链：例如签到提示跳转到开机启动设置。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string section && section == StartAtLoginSection)
        {
            ShowStartAtLoginSection();
        }
    }



    /// <summary>
    /// 切到「常规」子页并滚动到开机启动一节。
    /// 按 Tag 找导航项而不是按下标：本分支「关于」才是首项，默认落地页不变。
    /// </summary>
    private void ShowStartAtLoginSection()
    {
        if (Frame_Setting.CurrentSourcePageType != typeof(GeneralSetting))
        {
            Frame_Setting.Navigate(typeof(GeneralSetting));
            if (NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(x => x.Tag as string is nameof(GeneralSetting)) is { } generalItem)
            {
                NavView.SelectedItem = generalItem;
            }
        }

        if (Frame_Setting.Content is GeneralSetting general)
        {
            general.ScrollToStartAtLogin();
        }
    }



    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            Type? type = args.InvokedItemContainer?.Tag switch
            {
                nameof(AboutSetting) => typeof(AboutSetting),
                nameof(GeneralSetting) => typeof(GeneralSetting),
                nameof(DownloadSetting) => typeof(DownloadSetting),
                nameof(FileManageSetting) => typeof(FileManageSetting),
                nameof(ScreenshotSetting) => typeof(ScreenshotSetting),
                nameof(AdvancedSetting) => typeof(AdvancedSetting),
                nameof(ToolboxSetting) => typeof(ToolboxSetting),
                nameof(HotkeySetting) => typeof(HotkeySetting),
                nameof(GamepadControlSetting) => typeof(GamepadControlSetting),
                _ => null,
            };
            if (type is not null)
            {
                Frame_Setting.Navigate(type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setting page navigate.");
        }
    }



    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }



}
