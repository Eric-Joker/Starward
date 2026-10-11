using System.Text.Json.Serialization;

namespace Starward.Core.Passport;

/// <summary>
/// 米哈游通行证（国服）的令牌集合。
/// <para>
/// 令牌的失效特性（静态区分，决定了刷新策略）：
/// </para>
/// <list type="table">
/// <item>
///   <term>LoginTicket</term>
///   <description>有效约 30 分钟，且只能使用一次，仅用于换取 LToken / SToken，不保存。</description>
/// </item>
/// <item>
///   <term>GameToken</term>
///   <description>扫码登录产生的一次性令牌，只能换取一次 SToken，不保存。</description>
/// </item>
/// <item>
///   <term>Stoken</term>
///   <description>
///   长期有效，不会自然过期；修改密码后失效。V1 与 stuid 搭配，V2 与 mid 搭配。
///   可通过 <c>ma-cn-session/app/verify</c>（refresh = true）校验并续期，也可换取 LToken / CookieToken / 游戏授权票据。
///   </description>
/// </item>
/// <item>
///   <term>LToken / CookieToken</term>
///   <description>
///   用于米游社（工具箱）Cookie，会随服务端策略失效，没有公开的有效期。
///   只要 SToken 仍然有效就可以随时重新换取，因此把它们当作可再生的派生令牌，按时间或鉴权失败触发刷新。
///   </description>
/// </item>
/// </list>
/// </summary>
public class PassportTokenSet
{

    /// <summary>SToken（V1 或 V2）。</summary>
    [JsonPropertyName("stoken")]
    public string? Stoken { get; set; }


    /// <summary>SToken 的版本，1 或 2。V2 必须与 <see cref="Mid"/> 一起使用。</summary>
    [JsonPropertyName("stoken_version")]
    public int StokenVersion { get; set; }


    /// <summary>米游社 MiHoYo ID，与 SToken（V2）搭配使用。</summary>
    [JsonPropertyName("mid")]
    public string? Mid { get; set; }


    /// <summary>通行证账号 ID（stuid / ltuid / account_id）。</summary>
    [JsonPropertyName("aid")]
    public long Aid { get; set; }


    /// <summary>派生令牌（LToken / CookieToken）所属的账号 ID，用于校验是否与 SToken 匹配。</summary>
    [JsonPropertyName("ltoken_uid")]
    public long LtokenUid { get; set; }


    /// <summary>LToken（V1），与 <see cref="LtokenUid"/> 搭配用于米游社接口。</summary>
    [JsonPropertyName("ltoken")]
    public string? Ltoken { get; set; }


    /// <summary>CookieToken，用于米游社接口。</summary>
    [JsonPropertyName("cookie_token")]
    public string? CookieToken { get; set; }


    /// <summary>用于界面展示的账号名（手机号或邮箱，已由服务端脱敏）。</summary>
    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }


    /// <summary>米游社昵称，登录后随工具箱账号信息一起获得，仅用于界面展示。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }


    /// <summary>最近一次校验 / 续期 SToken 的时间。</summary>
    [JsonPropertyName("stoken_verify_time")]
    public DateTimeOffset StokenVerifyTime { get; set; }


    /// <summary>最近一次获取派生令牌的时间。</summary>
    [JsonPropertyName("derived_token_time")]
    public DateTimeOffset DerivedTokenTime { get; set; }


    /// <summary>登录时间。</summary>
    [JsonPropertyName("login_time")]
    public DateTimeOffset LoginTime { get; set; }


    /// <summary>登录方式，仅用于展示。</summary>
    [JsonPropertyName("login_method")]
    public string? LoginMethod { get; set; }


    [JsonIgnore]
    public bool HasStoken => !string.IsNullOrWhiteSpace(Stoken) && (StokenVersion == 1 || !string.IsNullOrWhiteSpace(Mid));


    [JsonIgnore]
    public bool HasDerivedToken => !string.IsNullOrWhiteSpace(Ltoken) && LtokenUid == Aid;


    /// <summary>
    /// 界面上显示的账号名称：优先米游社昵称，其次是手机号，最后退回到通行证 ID。
    /// </summary>
    [JsonIgnore]
    public string? DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Nickname))
            {
                return Nickname;
            }
            if (!string.IsNullOrWhiteSpace(AccountName))
            {
                return AccountName;
            }
            if (Aid > 0)
            {
                return Aid.ToString();
            }
            return string.IsNullOrWhiteSpace(Mid) ? null : Mid;
        }
    }


    /// <summary>SToken 校验的间隔。校验接口会返回新的 SToken，是最可靠的续期方式。</summary>
    public static TimeSpan StokenVerifyInterval { get; } = TimeSpan.FromHours(6);


    /// <summary>派生令牌的强制刷新间隔。派生令牌会随时间失效，过期后由服务端返回鉴权失败。</summary>
    public static TimeSpan DerivedTokenInterval { get; } = TimeSpan.FromDays(30);


    /// <summary>SToken 是否到了需要校验 / 续期的时间。</summary>
    public bool NeedVerifyStoken(DateTimeOffset now)
    {
        return !HasStoken || now - StokenVerifyTime > StokenVerifyInterval;
    }


    /// <summary>派生令牌（LToken / CookieToken）是否缺失或已经过期。</summary>
    public bool NeedRefreshDerivedToken(DateTimeOffset now)
    {
        return !HasDerivedToken || string.IsNullOrWhiteSpace(CookieToken) || now - DerivedTokenTime > DerivedTokenInterval;
    }


    /// <summary>
    /// 生成米游社（工具箱）使用的 Cookie。
    /// LToken（V1）必须与 ltuid 搭配，因此这里同时写入 account_id 与 ltuid。
    /// </summary>
    public string? CreateToolboxCookie()
    {
        if (!HasDerivedToken)
        {
            return null;
        }
        var items = new List<string>(4)
        {
            $"account_id={LtokenUid}",
            $"ltuid={LtokenUid}",
            $"ltoken={Ltoken}",
        };
        if (!string.IsNullOrWhiteSpace(CookieToken))
        {
            items.Add($"cookie_token={CookieToken}");
        }
        return string.Join("; ", items);
    }


    public PassportTokenSet Clone()
    {
        return (PassportTokenSet)MemberwiseClone();
    }


}
