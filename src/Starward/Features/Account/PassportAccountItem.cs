using Microsoft.UI.Xaml;
using Starward.Core;

namespace Starward.Features.Account;

/// <summary>
/// 账号中心里的一条账号记录（只用于界面展示）。
/// <para>
/// 账号中心是全局的账号池，只显示米游社身份：昵称、米游社 ID、米游社头像。
/// 游戏内的昵称、UID 和头像在游戏账号切换器里显示。
/// </para>
/// <para>
/// 属性都是可读可写的：这个类型用在了 <c>x:Bind</c> 的数据模板里，
/// 生成 XAML 类型信息时需要它能被直接赋值。
/// </para>
/// </summary>
public class PassportAccountItem
{

    /// <summary>取不到米游社头像时使用的通用图标。</summary>
    public const string DefaultAvatarUrl = "ms-appx:///Assets/Image/icon_hyperion.png";


    public string Key { get; set; } = "";


    /// <summary>米游社昵称，取不到时依次退到手机号、通行证 ID。</summary>
    public string DisplayName { get; set; } = "";


    /// <summary>米游社 ID，格式为“ID：81107513”。</summary>
    public string? Detail { get; set; }


    /// <summary>米游社头像，取不到时是通用图标，不会为空。</summary>
    public string AvatarUrl { get; set; } = DefaultAvatarUrl;


    /// <summary>米游社头像框，没有时为空，界面不会显示。</summary>
    public string? Pendant { get; set; }


    /// <summary>服务器区域，见 <see cref="ServerRegion"/>：国服或国际服。</summary>
    public ServerRegion Region { get; set; } = ServerRegion.miHoYo;


    /// <summary>登录时使用的账号名（手机号 / 通行证 ID），重新登录时用来预填输入框。</summary>
    public string? LoginName { get; set; }


    /// <summary>本地保存的登录凭据已经失效，需要重新登录；界面上显示警告红点。</summary>
    public bool IsExpired { get; set; }


    /// <summary>头像上的警告红点，登录失效时显示。</summary>
    public Visibility AlertVisibility => IsExpired ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>账号行里的“重新登录”入口，登录失效时显示。</summary>
    public Visibility ExpiredRowVisibility => IsExpired ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>头像框，没有时不显示。</summary>
    public Visibility PendantVisibility => string.IsNullOrWhiteSpace(Pendant) ? Visibility.Collapsed : Visibility.Visible;


    /// <summary>区域标签只在国际服账号上显示，国服账号不占位置。</summary>
    public Visibility IntlTagVisibility => Region is ServerRegion.HoYoverse ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>区域文字：国服 / 国际服。</summary>
    public string RegionText => Region is ServerRegion.HoYoverse ? Lang.PassportLogin_RegionGlobal : Lang.PassportLogin_RegionChina;


}
