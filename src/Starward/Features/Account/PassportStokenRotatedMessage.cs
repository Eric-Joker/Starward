using Starward.Core;

namespace Starward.Features.Account;

/// <summary>
/// 上游账号中心校验会话时服务端换发了新的 SToken。
/// <para>
/// SToken 在本机存了两份：账号池（<c>PassportAccount</c> 表，main 的登录 / 游戏授权票据用它）
/// 和上游的 <c>PassportUserInfo</c> 表（账号中心浮窗、战绩、自动登录用它）。
/// 服务端换发之后旧的那枚随时会被作废，两份副本只要有一份没跟上，
/// 另一处就会用着死令牌，表现出来的就是「米游社功能都正常，账号中心却说登录已过期」。
/// </para>
/// <para>
/// 所以上游换票成功后发这条消息，<see cref="PassportAccountService"/> 收到后把账号池里的副本一起换掉。
/// </para>
/// </summary>
public class PassportStokenRotatedMessage
{

    public PassportStokenRotatedMessage(ServerRegion region, long aid, string? mid, string stoken)
    {
        Region = region;
        Aid = aid;
        Mid = mid;
        Stoken = stoken;
    }


    /// <summary>服务器区域。</summary>
    public ServerRegion Region { get; }


    /// <summary>通行证账号 ID。</summary>
    public long Aid { get; }


    /// <summary>通行证 Mid，与 SToken（V2）搭配。</summary>
    public string? Mid { get; }


    /// <summary>服务端换发的新 SToken。</summary>
    public string Stoken { get; }

}
