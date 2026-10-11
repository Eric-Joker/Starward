using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starward.Core.Passport;

/// <summary>
/// 米哈游通行证（国服）接口的返回载体。
/// 不同接口的返回结构略有差异（<c>retcode</c> 可能是数字或字符串，错误信息可能叫 <c>message</c> 或 <c>msg</c>），
/// 这里统一处理，避免每个接口都写一份解析代码。
/// </summary>
public class PassportResult
{
    /// <summary>返回码，0 表示成功。</summary>
    public int Retcode { get; set; }

    /// <summary>返回信息。</summary>
    public string? Message { get; set; }

    /// <summary>原始 data 节点，未做类型化。</summary>
    public JsonElement Data { get; set; }

    /// <summary>响应头 <c>x-rpc-verify</c>，新设备登录校验（retcode = -3235）时返回。</summary>
    public string? VerifyHeader { get; set; }

    /// <summary>响应头 <c>x-rpc-aigis</c>，触发人机验证（retcode = -3101）时返回。</summary>
    public string? AigisHeader { get; set; }

    /// <summary>响应头 <c>set-cookie</c> 中解析出的 Cookie 键值。</summary>
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSuccess => Retcode == 0;

    /// <summary>未通过人机验证。</summary>
    public bool IsCaptchaRequired => Retcode is -3101 or -3102 or -2016;

    /// <summary>新设备登录，需要短信校验。</summary>
    public bool IsNewDevice => Retcode is -3235 or -3154;

    public T? Deserialize<T>() where T : class
    {
        if (Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }
        return Data.Deserialize<T>(PassportJson.Options);
    }

    public void EnsureSuccess()
    {
        if (!IsSuccess)
        {
            throw new miHoYoApiException(Retcode, Message);
        }
    }
}


/// <summary>
/// 用户信息（<c>data.user_info</c>）。
/// </summary>
public class PassportUserInfo
{
    [JsonPropertyName("aid")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long Aid { get; set; }

    [JsonPropertyName("mid")]
    public string? Mid { get; set; }

    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("area_code")]
    public string? AreaCode { get; set; }

    [JsonPropertyName("realname")]
    public string? Realname { get; set; }

    [JsonPropertyName("is_email_verify")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? IsEmailVerify { get; set; }
}


/// <summary>
/// 令牌（<c>data.token</c>）。
/// </summary>
public class PassportToken
{
    [JsonPropertyName("token_type")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int TokenType { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}


/// <summary>
/// 登录结果（<c>data</c> 节点），密码登录 / 验证码登录 / 检查风控 均返回该结构。
/// </summary>
public class PassportLoginData
{
    [JsonPropertyName("login_ticket")]
    public string? LoginTicket { get; set; }

    [JsonPropertyName("token")]
    public PassportToken? Token { get; set; }

    [JsonPropertyName("user_info")]
    public PassportUserInfo? UserInfo { get; set; }

    [JsonPropertyName("need_realperson")]
    public JsonElement NeedRealperson { get; set; }
}


/// <summary>
/// 令牌查询结果（<c>TokenInfo</c>），部分接口会返回其中的若干个字段。
/// </summary>
public class PassportTokenInfo
{
    [JsonPropertyName("account_id")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long AccountId { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("mid")]
    public string? Mid { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("stoken")]
    public string? Stoken { get; set; }

    [JsonPropertyName("ltoken")]
    public string? Ltoken { get; set; }

    [JsonPropertyName("cookie_token")]
    public string? CookieToken { get; set; }

    [JsonPropertyName("game_token")]
    public string? GameToken { get; set; }
}


/// <summary>
/// 登录票据换取令牌的返回项（<c>data.list[]</c>）。
/// </summary>
public class PassportMultiTokenItem
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}


public class PassportMultiTokenData
{
    [JsonPropertyName("list")]
    public List<PassportMultiTokenItem>? List { get; set; }
}


/// <summary>
/// 发送短信验证码的返回结果。
/// </summary>
public class PassportMobileCaptchaData
{
    /// <summary>后续登录需要带回的操作类型。</summary>
    [JsonPropertyName("action_type")]
    public string? ActionType { get; set; }

    /// <summary>重新发送的倒计时（秒）。</summary>
    [JsonPropertyName("countdown")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int Countdown { get; set; }

    [JsonPropertyName("sent_new")]
    public bool SentNew { get; set; }
}


/// <summary>
/// 扫码登录票据。
/// </summary>
public class PassportQrTicket
{
    [JsonPropertyName("ticket")]
    public string? Ticket { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("expires_in")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int ExpiresIn { get; set; }
}


/// <summary>
/// 扫码状态。
/// </summary>
public class PassportQrStatus
{
    /// <summary>二维码状态：未扫描 / 已扫描 / 已确认 等。</summary>
    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    /// <summary>部分接口把状态放在该字段。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>确认登录后的载荷。</summary>
    [JsonPropertyName("payload")]
    public PassportQrPayload? Payload { get; set; }

    public string? State => Stat ?? Status;
}


public class PassportQrPayload
{
    [JsonPropertyName("proto")]
    public string? Proto { get; set; }

    /// <summary>确认登录后的 JSON 字符串，包含 <c>uid</c> 与 <c>token</c>（GameToken）。</summary>
    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    [JsonPropertyName("ext")]
    public string? Ext { get; set; }
}


/// <summary>
/// 通行证扫码状态查询的返回数据。
/// <para>
/// 扫描确认后，米游社 App 版接口会直接在 <see cref="Tokens"/> 中返回 SToken，
/// 因此不需要额外的换取请求；网页版接口则总是返回空的 <c>tokens</c>，需要改用 Cookie。
/// </para>
/// </summary>
public class PassportQrStatusData
{
    /// <summary>二维码状态：<c>Init</c> 未扫描、<c>Scanned</c> 已扫描、<c>Confirmed</c> 已确认。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>部分接口把状态放在该字段。</summary>
    [JsonPropertyName("stat")]
    public string? Stat { get; set; }

    /// <summary>确认登录后返回的令牌。</summary>
    [JsonPropertyName("tokens")]
    public List<PassportToken>? Tokens { get; set; }

    [JsonPropertyName("user_info")]
    public PassportUserInfo? UserInfo { get; set; }

    [JsonPropertyName("payload")]
    public PassportQrPayload? Payload { get; set; }

    public string? State => Status ?? Stat;

    public bool IsScanned => string.Equals(State, "Scanned", StringComparison.OrdinalIgnoreCase);

    public bool IsConfirmed => string.Equals(State, "Confirmed", StringComparison.OrdinalIgnoreCase);
}


/// <summary>
/// <c>payload.raw</c> 反序列化后的内容。
/// </summary>
public class PassportQrRawToken
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}


/// <summary>
/// 新设备校验（<c>x-rpc-verify</c> 响应头）。
/// </summary>
public class PassportVerifyHeader
{
    [JsonPropertyName("action_ticket")]
    public string? ActionTicket { get; set; }

    /// <summary>该字段在不同版本中可能是对象或 JSON 字符串。</summary>
    [JsonPropertyName("verify_str")]
    public JsonElement VerifyStr { get; set; }

    public PassportVerifyStr? GetVerifyStr()
    {
        if (VerifyStr.ValueKind is JsonValueKind.String)
        {
            string? text = VerifyStr.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            return JsonSerializer.Deserialize<PassportVerifyStr>(text, PassportJson.Options);
        }
        if (VerifyStr.ValueKind is JsonValueKind.Object)
        {
            return VerifyStr.Deserialize<PassportVerifyStr>(PassportJson.Options);
        }
        return null;
    }
}


public class PassportVerifyStr
{
    [JsonPropertyName("ticket")]
    public string? Ticket { get; set; }

    [JsonPropertyName("verify_type")]
    public string? VerifyType { get; set; }
}


/// <summary>
/// 风控人机验证（<c>x-rpc-aigis</c> 响应头）。
/// </summary>
public class PassportAigisHeader
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("mmt_type")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int MmtType { get; set; }

    /// <summary>
    /// 极验验证任务数据。服务端可能返回对象，也可能返回 JSON 字符串，
    /// 因此这里保留为 <see cref="JsonElement"/>，由 <see cref="GetChallenge"/> 统一解析。
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    public PassportGeetestChallenge? GetChallenge()
    {
        if (Data.ValueKind is JsonValueKind.String)
        {
            string? text = Data.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<PassportGeetestChallenge>(text, PassportJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        if (Data.ValueKind is JsonValueKind.Object)
        {
            return Data.Deserialize<PassportGeetestChallenge>(PassportJson.Options);
        }
        return null;
    }
}


/// <summary>
/// 极验验证任务（第三代 / 第四代通用）。
/// </summary>
public class PassportGeetestChallenge
{
    /// <summary>验证 ID（第四代中即 captchaId）。</summary>
    [JsonPropertyName("gt")]
    public string? Gt { get; set; }

    /// <summary>验证流水号，第三代行为验使用。</summary>
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    [JsonPropertyName("success")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int Success { get; set; }

    [JsonPropertyName("new_captcha")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int NewCaptcha { get; set; }

    /// <summary>是否使用极验第四代适应性验证。</summary>
    [JsonPropertyName("use_v4")]
    public bool UseV4 { get; set; }

    /// <summary>风控指定的验证形式，例如 <c>slide</c>。</summary>
    [JsonPropertyName("risk_type")]
    public string? RiskType { get; set; }
}


/// <summary>
/// 人机验证结果。第三代与第四代只会用到其中一组字段。
/// </summary>
public class PassportCaptcha
{
    /// <summary>极验第四代验证结果（<c>getValidate()</c> 的返回值）。</summary>
    [JsonPropertyName("geetest_v4_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PassportGeetestV4Data? GeetestV4Data { get; set; }

    /// <summary>极验第三代验证流水号。</summary>
    [JsonPropertyName("geetest_challenge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeetestChallenge { get; set; }

    /// <summary>极验第三代验证结果。</summary>
    [JsonPropertyName("geetest_validate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeetestValidate { get; set; }

    /// <summary>极验第三代验证校验码。</summary>
    [JsonPropertyName("geetest_seccode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeetestSeccode { get; set; }

    /// <summary>风控会话标识（<c>x-rpc-aigis</c> 的 <c>session_id</c>）。</summary>
    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    [JsonIgnore]
    public bool IsEmpty => GeetestV4Data is null && string.IsNullOrWhiteSpace(GeetestChallenge);


    /// <summary>
    /// 生成风控校验请求头 <c>x-rpc-aigis</c> 的值：<c>{session_id};{base64(极验验证结果)}</c>。
    /// 第四代传 <c>getValidate()</c> 的结果，第三代传 challenge / validate / seccode。
    /// </summary>
    public string ToAigisHeaderValue()
    {
        object payload;
        if (GeetestV4Data is not null)
        {
            payload = GeetestV4Data;
        }
        else
        {
            payload = new Dictionary<string, string?>
            {
                ["geetest_challenge"] = GeetestChallenge,
                ["geetest_validate"] = GeetestValidate,
                ["geetest_seccode"] = GeetestSeccode,
            };
        }
        string json = JsonSerializer.Serialize(payload, PassportJson.Options);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"{SessionId};{base64}";
    }
}


/// <summary>
/// 极验第四代验证结果。
/// </summary>
public class PassportGeetestV4Data
{
    [JsonPropertyName("lot_number")]
    public string? LotNumber { get; set; }

    [JsonPropertyName("captcha_output")]
    public string? CaptchaOutput { get; set; }

    [JsonPropertyName("pass_token")]
    public string? PassToken { get; set; }

    [JsonPropertyName("gen_time")]
    public string? GenTime { get; set; }

    [JsonPropertyName("captcha_id")]
    public string? CaptchaId { get; set; }

    [JsonPropertyName("sign_token")]
    public string? SignToken { get; set; }
}


/// <summary>
/// 游戏授权票据。不同版本的接口把票据放在 <c>ticket</c> 或 <c>token</c> 字段中。
/// </summary>
public class PassportAuthTicket
{
    [JsonPropertyName("ticket")]
    public string? Ticket { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    public string? Value => string.IsNullOrWhiteSpace(Ticket) ? Token : Ticket;
}


/// <summary>
/// 用户的设备指纹。
/// </summary>
public class PassportDeviceFp
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Message { get; set; }

    [JsonPropertyName("device_fp")]
    public string? DeviceFp { get; set; }
}


/// <summary>
/// <c>ma-cn-session/app/verify</c> 的返回数据。
/// </summary>
public class PassportVerifyData
{
    [JsonPropertyName("user_info")]
    public PassportUserInfo? UserInfo { get; set; }

    [JsonPropertyName("new_token")]
    public PassportToken? NewToken { get; set; }
}


public static class PassportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
