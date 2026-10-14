using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Features.ViewHost;
using Starward.Frameworks;
using System;
using System.Globalization;
using Windows.System;


namespace Starward.Features.Setting;

public sealed partial class GeneralSetting : PageBase
{

    private readonly ILogger<GeneralSetting> _logger = AppConfig.GetLogger<GeneralSetting>();


    public GeneralSetting()
    {
        this.InitializeComponent();
    }



    protected override void OnLoaded()
    {
        InitializeLanguageSelector();
        InitializeCloseWindowOption();
        if (_pendingScrollToStartAtLogin)
        {
            _pendingScrollToStartAtLogin = false;
            BringStartAtLoginIntoView();
        }
    }



    /// <summary>深链要求滚动到「开机自启」一节，但页面还没 Loaded：留到 OnLoaded 再滚。</summary>
    private bool _pendingScrollToStartAtLogin;


    /// <summary>
    /// 把「开机自启」一节滚入可视区并聚焦开关，供 <see cref="SettingPage.StartAtLoginSection"/> 深链调用。
    /// 页面尚未 Loaded 时等 Loaded 之后再滚。
    /// </summary>
    public void ScrollToStartAtLogin()
    {
        if (IsLoaded)
        {
            BringStartAtLoginIntoView();
        }
        else
        {
            _pendingScrollToStartAtLogin = true;
        }
    }



    /// <summary>
    /// 等布局量完再 BringIntoView，避免刚导航过来时滚动无效；不做动画，静默定位。
    /// </summary>
    private void BringStartAtLoginIntoView()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                ToggleSwitch_StartAtLogin.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0.22,
                });
                ToggleSwitch_StartAtLogin.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scroll to start at login");
            }
        });
    }




    #region 语言



    private bool _languageInitialized;


    /// <summary>
    /// 语言
    /// </summary>
    private void InitializeLanguageSelector()
    {
        try
        {
            var lang = AppConfig.Language;
            ComboBox_Language.Items.Clear();
            ComboBox_Language.Items.Add(new ComboBoxItem
            {
                Content = Lang.ResourceManager.GetString(nameof(Lang.SettingPage_FollowSystem), CultureInfo.InstalledUICulture),
                Tag = "",
            });
            ComboBox_Language.SelectedIndex = 0;
            foreach (var (Title, LangCode) in Localization.LanguageList)
            {
                var box = new ComboBoxItem
                {
                    Content = Title,
                    Tag = LangCode,
                };
                ComboBox_Language.Items.Add(box);
                if (LangCode == lang)
                {
                    ComboBox_Language.SelectedItem = box;
                }
            }
        }
        finally
        {
            _languageInitialized = true;
        }
    }



    /// <summary>
    /// 语言切换
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ComboBox_Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ComboBox_Language.SelectedItem is ComboBoxItem item)
            {
                if (_languageInitialized)
                {
                    var lang = item.Tag as string;
                    _logger.LogInformation("Language change to {lang}", lang);
                    AppConfig.SetLanguage(lang);
                    this.Bindings.Update();
                    WeakReferenceMessenger.Default.Send(new LanguageChangedMessage());
                    AppConfig.SaveConfiguration();
                }
            }
        }
        catch (CultureNotFoundException)
        {
            AppConfig.SetLanguage(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change Language");
        }
    }



    #endregion



    #region 关闭窗口选项



    private bool _closeWindowOptionInitialized;



    /// <summary>
    /// 初始化关闭窗口选项
    /// </summary>
    private void InitializeCloseWindowOption()
    {
        try
        {
            var option = AppConfig.CloseWindowOption;
            if (option is MainWindowCloseOption.Hide)
            {
                RadioButton_CloseWindowOption_Hide.IsChecked = true;
            }
            else if (option is MainWindowCloseOption.Exit)
            {
                RadioButton_CloseWindowOption_Exit.IsChecked = true;
            }
            _closeWindowOptionInitialized = true;
        }
        catch { }
    }



    /// <summary>
    /// 关闭窗口选项切换
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void RadioButton_CloseWindowOption_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_closeWindowOptionInitialized)
            {
                if (sender is FrameworkElement fe)
                {
                    AppConfig.CloseWindowOption = fe.Tag switch
                    {
                        MainWindowCloseOption option => option,
                        _ => 0,
                    };
                }
            }
        }
        catch { }
    }



    #endregion



    #region 开机自启



    /// <summary>可移动存储上不提供开机自启：盘符换了快捷方式就失效。</summary>
    public bool StartAtLoginAvailable { get; } = AutoStartService.IsAvailable;


    /// <summary>
    /// 开机自启。实现方式是在用户「启动」文件夹放一个快捷方式（不写注册表），
    /// 用户可以在任务管理器的启动页里直接关掉。
    /// </summary>
    public bool StartAtLogin
    {
        get; set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }
            try
            {
                if (value)
                {
                    AutoStartService.Enable();
                }
                else
                {
                    AutoStartService.Disable();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set start at login");
                // 失败时把开关拨回去，别让界面显示一个没生效的状态。
                field = !value;
                OnPropertyChanged(nameof(StartAtLogin));
            }
        }
    } = AutoStartService.IsAvailable && AutoStartService.IsEnabled();



    #endregion



    #region 游戏账号切换



    public bool EnableRegistryGameAccountSwitcher
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.EnableRegistryGameAccountSwitcher = value;
            }
        }
    } = AppConfig.EnableRegistryGameAccountSwitcher;



    #endregion



    #region 系统视觉效果



    /// <summary>
    /// 透明/动画效果
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_VisualEffects_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-visualeffects"));
    }



    /// <summary>
    /// 打开 Windows 的「启动应用」页，用户可以在那里看到并关掉开机自启。
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_StartupApps_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));
    }



    #endregion



}
