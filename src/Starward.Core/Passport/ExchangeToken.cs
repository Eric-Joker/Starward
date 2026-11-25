using System.Text.Json.Serialization;

namespace Starward.Core.Passport;

public class ExchangeTokenRequest
{
    [JsonPropertyName("dst_token_type")]
    public TokenType DestTokenType { get; set; }

    [JsonPropertyName("mid")]
    public string Mid { get; set; }

    [JsonPropertyName("src_token")]
    public PassportToken SourceToken { get; set; }

    public static ExchangeTokenRequest FromStoken(string stoken, string mid)
    {
        return new ExchangeTokenRequest { SourceToken = new PassportToken { TokenType = TokenType.SToken, Token = stoken }, Mid = mid };
    }
}


/// <summary>
/// 国服 V2 换票（<c>account/ma-cn-session/app/exchange</c>）的强类型返回。
/// <para>
/// 与国服登录协议里的 <see cref="PassportExchangeData"/> 是同一个响应：
/// 后者作为基类保留 <c>token</c> / <c>user_info</c>，这里只补上 <c>expire_in_seconds</c>，
/// 两个类型不会各自维护一份重复的字段。
/// </para>
/// </summary>
public class ExchangeTokenResponse : PassportExchangeData
{

    [JsonPropertyName("expire_in_seconds")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ExpireInSeconds { get; set; }
}
