using System.Text.Json.Serialization;

namespace Starward.Core.Passport;


/// <summary>
/// 通行证令牌，<see cref="TokenType"/> 表示令牌的类型。
/// <para>
/// 国服登录协议（原 Moonward 分支的 <c>PassportModels.cs</c>）里 <c>token_type</c> 可能是数字，
/// 也可能是数字字符串，这里统一用 <see cref="LenientTokenTypeConverter"/> 宽容读取；
/// 国际服/国服的强类型接口使用同一个类型，序列化时仍然输出数字。
/// </para>
/// </summary>
public class PassportToken
{

    [JsonPropertyName("token_type")]
    [JsonConverter(typeof(LenientTokenTypeConverter))]
    public TokenType TokenType { get; set; }


    [JsonPropertyName("token")]
    public string? Token { get; set; }

}
