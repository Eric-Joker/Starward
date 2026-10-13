namespace Starward.Core.Gacha;

/// <summary>
/// 表示通过 authkey 调用抽卡记录接口时返回的 API 错误。
/// </summary>
/// <remarks>
/// <para>
/// 抽卡记录 URL 的 authkey 失效（原神 / 星穹铁道 -101，绝区零 -1）与米游社 Cookie 失效共用 -101，
/// 因此这里提供 <see cref="IsAuthkeyExpired"/> 让调用方区分「authkey 过期 → 引导用户重新获取抽卡链接」
/// 与其他失败，而不是把状态码当作登录态解释。
/// </para>
/// <para>
/// 选择继承 <see cref="miHoYoApiException"/> 而不是直接继承 <see cref="Exception"/>：
/// 既有的抽卡调用点都以 <c>catch (miHoYoApiException)</c> 捕获抽卡接口错误
/// （<c>GachaLogPage.xaml.cs</c> 的更新与米游社同步、<c>GenshinBeyondGachaPage.xaml.cs</c>），
/// 继承可以让这些捕获点无需改动就继续生效，保持现有用户可见行为；
/// <see cref="miHoYoApiException.ReturnCode"/>、<see cref="miHoYoApiException.ResponseMessage"/>
/// 与 <see cref="miHoYoApiException.Message"/> 的格式（<c>{message} ({returnCode})</c>）
/// 也与原先直接抛出 <see cref="miHoYoApiException"/> 时完全一致。
/// 另外 <see cref="miHoYoApiException.IsLoginExpired"/> 不包含 -101 / -1，不会把抽卡 authkey 过期误报为登录失效。
/// </para>
/// </remarks>
public class GachaApiException : miHoYoApiException
{


    /// <summary>
    /// 获取当前错误是否表示抽卡记录 URL 中的 authkey 已失效。
    /// 原神和星穹铁道使用 -101，绝区零使用 -1。
    /// </summary>
    public bool IsAuthkeyExpired => ReturnCode is -101 or -1;


    /// <summary>
    /// 创建不含接口状态码的抽卡 API 异常。
    /// </summary>
    public GachaApiException() : base(0, null)
    {
    }


    /// <summary>
    /// 创建不含接口状态码、带指定消息的抽卡 API 异常。
    /// </summary>
    /// <param name="message">描述异常原因的消息；可为 null。</param>
    public GachaApiException(string? message) : base(0, message)
    {
    }


    /// <summary>
    /// 创建包含抽卡记录接口状态码的异常。
    /// </summary>
    /// <param name="returnCode">接口返回的 retcode。</param>
    /// <param name="message">接口返回的原始消息；可为 null。</param>
    public GachaApiException(int returnCode, string? message) : base(returnCode, message)
    {
    }


}
