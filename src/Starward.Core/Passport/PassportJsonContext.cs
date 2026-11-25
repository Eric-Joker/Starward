using Starward.Core.GameRecord;
using System.Text.Json.Serialization;

namespace Starward.Core.Passport;


/// <summary>
/// 通行证协议的 source-generated JSON 上下文。
/// <para>
/// 上游强类型接口通过 <see cref="PassportJsonContext.Default"/> 反序列化统一返回结构；
/// 国服登录协议（原 Moonward 分支）新增的模型也在这里注册，避免以后关闭反射序列化时出现遗漏。
/// </para>
/// <para>
/// 这些国服模型目前由 <see cref="PassportJson.Options"/>（反射序列化）反序列化：
/// <c>Starward.csproj</c> 显式设置了 <c>JsonSerializerIsReflectionEnabledByDefault=true</c>，
/// 因此反射通道可用；注册到上下文只是为后续裁剪/AOT 预留完整的元数据。
/// </para>
/// </summary>
[JsonSerializable(typeof(miHoYoApiWrapper<SessionVerifyResult>))]
[JsonSerializable(typeof(miHoYoApiWrapper<LogoutResult>))]
[JsonSerializable(typeof(miHoYoApiWrapper<CrossLoginTokenResult>))]
[JsonSerializable(typeof(miHoYoApiWrapper<AuthTicketResult>))]
[JsonSerializable(typeof(miHoYoApiWrapper<GameRecordUserWrapper>))]
[JsonSerializable(typeof(miHoYoApiWrapper<GameRecordRoleWrapper>))]
[JsonSerializable(typeof(SessionVerifyRequest))]
[JsonSerializable(typeof(VerifySTokenRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(CreateAuthTicketByGameBizRequest))]
[JsonSerializable(typeof(CreateAuthTicketBySTokenRequest))]
[JsonSerializable(typeof(CreateCrossLoginTokenRequest))]
[JsonSerializable(typeof(GetCrossTokenUrlBySTokenRequest))]
[JsonSerializable(typeof(ExchangeTokenRequest))]
[JsonSerializable(typeof(miHoYoApiWrapper<ExchangeTokenResponse>))]
[JsonSerializable(typeof(GetBySTokenRequest))]
[JsonSerializable(typeof(miHoYoApiWrapper<GetBySTokenResponse>))]
// 国服登录协议（main）新增的序列化模型
[JsonSerializable(typeof(PassportResult))]
[JsonSerializable(typeof(PassportLoginData))]
[JsonSerializable(typeof(PassportExchangeData))]
[JsonSerializable(typeof(PassportTokenInfo))]
[JsonSerializable(typeof(PassportMultiTokenData))]
[JsonSerializable(typeof(PassportMobileCaptchaData))]
[JsonSerializable(typeof(PassportQrTicket))]
[JsonSerializable(typeof(PassportQrStatus))]
[JsonSerializable(typeof(PassportQrStatusData))]
[JsonSerializable(typeof(PassportQrPayload))]
[JsonSerializable(typeof(PassportQrRawToken))]
[JsonSerializable(typeof(PassportVerifyHeader))]
[JsonSerializable(typeof(PassportVerifyStr))]
[JsonSerializable(typeof(PassportAigisHeader))]
[JsonSerializable(typeof(PassportGeetestChallenge))]
[JsonSerializable(typeof(PassportCaptcha))]
[JsonSerializable(typeof(PassportGeetestV4Data))]
[JsonSerializable(typeof(PassportAuthTicket))]
[JsonSerializable(typeof(PassportDeviceFp))]
[JsonSerializable(typeof(PassportVerifyData))]
[JsonSerializable(typeof(PassportTokenSet))]
[JsonSerializable(typeof(PassportUserInfo))]
[JsonSerializable(typeof(PassportToken))]
internal partial class PassportJsonContext : JsonSerializerContext
{

}
