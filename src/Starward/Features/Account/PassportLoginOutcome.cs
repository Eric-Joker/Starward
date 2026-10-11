using Starward.Core;
using Starward.Core.Passport;
using System;

namespace Starward.Features.Account;

/// <summary>
/// 通行证登录流程的状态。
/// </summary>
public enum PassportLoginState
{
    /// <summary>操作成功。</summary>
    Success,

    /// <summary>等待用户操作（例如等待扫码）。</summary>
    Pending,

    /// <summary>需要完成人机验证。</summary>
    NeedCaptcha,

    /// <summary>新设备登录，需要短信校验。</summary>
    NeedDeviceVerify,

    /// <summary>失败。</summary>
    Failed,
}


/// <summary>
/// 通行证登录结果。风控人机验证（-3101）与新设备校验（-3235）都会作为正常结果返回，
/// 由界面引导用户完成后续步骤，而不是直接报错。
/// </summary>
public class PassportLoginOutcome
{


    public PassportLoginState State { get; private init; }


    /// <summary>已经本地化的提示信息。</summary>
    public string? Message { get; private init; }


    /// <summary>人机验证任务，<see cref="PassportLoginState.NeedCaptcha"/> 时有效。</summary>
    public PassportAigisHeader? Aigis { get; private init; }


    /// <summary>新设备校验票据，<see cref="PassportLoginState.NeedDeviceVerify"/> 时有效。</summary>
    public string? ActionTicket { get; private init; }


    /// <summary>提交短信验证码时需要的票据。</summary>
    public string? VerifyTicket { get; private init; }


    /// <summary>发送短信验证码后返回的操作类型，登录时需要原样带回。</summary>
    public string? ActionType { get; set; }


    /// <summary>重新发送短信验证码的倒计时（秒）。</summary>
    public int Countdown { get; set; } = 60;


    public long Aid { get; set; }


    public string? Mid { get; set; }


    public string? AccountName { get; set; }


    public bool IsSuccess => State is PassportLoginState.Success;




    public static PassportLoginOutcome Succeeded() => new() { State = PassportLoginState.Success };


    public static PassportLoginOutcome Pending() => new() { State = PassportLoginState.Pending };


    public static PassportLoginOutcome Failed(string message) => new() { State = PassportLoginState.Failed, Message = message };


    public static PassportLoginOutcome Failed(Exception ex)
    {
        string message = ex is miHoYoApiException api ? PassportError.GetMessage(api.ReturnCode, ex.Message) : ex.Message;
        return new PassportLoginOutcome { State = PassportLoginState.Failed, Message = message };
    }


    public static PassportLoginOutcome Failed(PassportResult result)
    {
        return new PassportLoginOutcome
        {
            State = PassportLoginState.Failed,
            Message = PassportError.GetMessage(result.Retcode, result.Message),
        };
    }


    /// <summary>需要人机验证。</summary>
    public static PassportLoginOutcome Captcha(PassportResult result)
    {
        return new PassportLoginOutcome
        {
            State = PassportLoginState.NeedCaptcha,
            Aigis = PassportClient.ParseAigisHeader(result.AigisHeader),
            Message = Lang.PassportLogin_CaptchaRequired,
        };
    }


    /// <summary>新设备登录校验。</summary>
    public static PassportLoginOutcome DeviceVerify(PassportResult result)
    {
        var verify = PassportClient.ParseVerifyHeader(result.VerifyHeader);
        return new PassportLoginOutcome
        {
            State = PassportLoginState.NeedDeviceVerify,
            ActionTicket = verify?.ActionTicket,
            VerifyTicket = verify?.GetVerifyStr()?.Ticket,
            Message = PassportError.GetMessage(result.Retcode, result.Message),
        };
    }


}




/// <summary>
/// 一次扫码登录会话。
/// </summary>
public class PassportQrSession
{


    /// <summary>二维码内容。</summary>
    public required string Url { get; init; }


    public required string Ticket { get; init; }


    /// <summary>有效期（秒），0 表示未知。</summary>
    public int ExpiresIn { get; init; }


    public DateTimeOffset CreateTime { get; init; } = DateTimeOffset.Now;


    /// <summary>二维码是否已经被扫描。</summary>
    public bool IsScanned { get; set; }


    /// <summary>二维码是否已经失效。</summary>
    public bool IsExpired { get; set; }


    /// <summary>用户是否取消了登录。</summary>
    public bool IsCancelled { get; set; }


}
