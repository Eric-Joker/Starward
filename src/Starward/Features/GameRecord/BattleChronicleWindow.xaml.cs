using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Starward.Core.GameRecord;
using Starward.Frameworks;
using System;
using Windows.Graphics;


namespace Starward.Features.GameRecord;

[INotifyPropertyChanged]
public sealed partial class BattleChronicleWindow : WindowEx
{


    public BattleChronicleWindow()
    {
        this.InitializeComponent();
        InitializeWindow();
        // 网页里的登录按钮点了没反应：桥收下消息后没有回话，这里往上抛给工具箱页处理。
        bbsWebBridge.LoginRequested += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
    }



    private void InitializeWindow()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        Title = Lang.HoyolabToolboxPage_BattleChronicle;
        RootGrid.RequestedTheme = ShouldAppsUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
        AdaptTitleBarButtonColorToActuallTheme();
        SetIcon();
        CenterInScreen();
    }




    private void CenterInScreen()
    {
        RectInt32 workArea = DisplayArea.GetFromWindowId(MainWindowId, DisplayAreaFallback.Nearest).WorkArea;
        int h = (int)(workArea.Height * 0.95);
        int w = (int)(h / 16.0 * 9.0);
        if (w > workArea.Width)
        {
            w = (int)(workArea.Width * 0.95);
            h = (int)(w * 16.0 / 9.0);
        }
        int x = workArea.X + (workArea.Width - w) / 2;
        int y = workArea.Y + (workArea.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }




    public GameRecordRole? CurrentRole { get; set => SetProperty(ref field, value); }




    /// <summary>网页里请求登录，由工具箱页决定打开哪个登录入口。</summary>
    public event EventHandler? LoginRequested;




    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _ = bbsWebBridge.LoadPageAsync();
    }




}
