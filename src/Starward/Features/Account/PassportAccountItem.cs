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


}
