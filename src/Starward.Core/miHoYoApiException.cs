namespace Starward.Core;

public class miHoYoApiException : Exception
{

    public int ReturnCode { get; init; }


    /// <summary>
    /// 接口响应中的原始消息；可为 null。
    /// </summary>
    /// <remarks>
    /// UI 层使用该值在未知错误时保留服务端诊断信息，避免从 <see cref="Exception.Message"/> 反向解析状态码。
    /// </remarks>
    public string? ResponseMessage { get; }


    public miHoYoApiException(int returnCode, string? message) : base($"{message} ({returnCode})")
    {
        ReturnCode = returnCode;
        ResponseMessage = message;
    }

}
