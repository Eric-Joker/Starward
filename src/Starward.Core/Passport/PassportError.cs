namespace Starward.Core.Passport;

/// <summary>
/// 通行证接口返回码到本地化提示信息的映射。
/// <para>
/// 返回码来源：国服账号相关接口的公开资料与常见实现，未收录的返回码直接使用服务端信息。
/// </para>
/// </summary>
public static class PassportError
{


    public static string GetMessage(int retcode, string? serverMessage = null)
    {
        string? message = retcode switch
        {
            // 账号或密码错误
            -3004 or -3208 or 1008 => CoreLang.PassportError_LoginFailed,
            // 账号不存在
            -3203 => CoreLang.PassportError_AccountNotFound,
            // 密码错误次数过多，账号被临时锁定
            -3202 => CoreLang.PassportError_AccountLocked,
            // 短信验证码错误
            -3205 => CoreLang.PassportError_SmsCodeWrong,
            // 人机验证未通过
            -3101 or -3102 or 1034 => CoreLang.PassportError_CaptchaFailed,
            // 请求过于频繁，或验证码发送次数达到上限
            -3006 or -3206 or -119 => CoreLang.PassportError_TooFrequent,
            // 新设备登录，需要短信校验
            -3235 or -3154 => CoreLang.PassportError_DeviceVerifyRequired,
            // 登录状态失效
            -100 or -111 or 10001 or -10001 => CoreLang.PassportError_TokenExpired,
            // 需要邮箱验证
            -3239 => CoreLang.PassportError_EmailVerifyRequired,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }
        if (string.IsNullOrWhiteSpace(serverMessage))
        {
            return string.Format(CoreLang.PassportError_Unknown, retcode);
        }
        return $"{serverMessage} ({retcode})";
    }


}
