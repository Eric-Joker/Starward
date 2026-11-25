using System.Text.Json.Serialization;

namespace Starward.Core.Passport;


/// <summary>
/// 通行证账号信息。
/// <para>
/// 这是 <see cref="UserInfo"/>（接口返回的 <c>user_info</c>，账号资料）与会话字段
/// （<see cref="Region"/> / <see cref="Stoken"/>）的并集：
/// 上游的强类型接口用它的会话字段完成校验、登出、换票与授权登录，
/// 国服登录协议（原 Moonward 分支）则直接把它作为 <c>data.user_info</c> 的反序列化目标。
/// </para>
/// <para>
/// 注意：从 <c>user_info</c> 反序列化出来时只有账号资料字段有值，
/// <see cref="Region"/> 与 <see cref="Stoken"/> 属于会话上下文，必须由调用方显式赋值。
/// </para>
/// </summary>
public class PassportUserInfo : UserInfo
{

    /// <summary>
    /// 服务器区域（<see cref="ServerRegion.miHoYo"/> 国服 / <see cref="ServerRegion.HoYoverse"/> 国际服）。
    /// </summary>
    [JsonIgnore]
    public ServerRegion Region { get; set; }


    /// <summary>
    /// 会话 SToken（V2 必须与 <see cref="UserInfo.Mid"/> 搭配使用）。
    /// </summary>
    [JsonIgnore]
    public string Stoken { get; set; } = "";


    [JsonIgnore]
    public bool IsChinaServer => Region is ServerRegion.miHoYo;


    [JsonIgnore]
    public bool IsGlobalServer => Region is ServerRegion.HoYoverse;

}
