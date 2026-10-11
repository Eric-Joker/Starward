using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
// WinRT 异步操作的 GetAwaiter 扩展位于 System 命名空间。
using System;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Starward.Helpers;

/// <summary>
/// 二维码纠错等级。取值与 QRCoder 的 ECCLevel 对应。
/// </summary>
public enum QrCodeEcc
{
    Low,
    Medium,
    Quartile,
    High,
}



/// <summary>
/// 二维码在界面上显示时的配色。
/// </summary>
public enum QrCodeColorMode
{
    /// <summary>白底黑块，适合保存成图片后在其它设备上扫描。</summary>
    White,

    /// <summary>透明底黑块，适合放在浅色的半透明背景上显示。</summary>
    TransparentDarkModules,

    /// <summary>透明底白块，适合放在深色的半透明背景上显示。</summary>
    TransparentLightModules,
}



/// <summary>
/// 生成好的二维码图片。
/// </summary>
public class QrCodeImageResult
{

    /// <summary>可直接绑定到 <c>Image</c> 的图像源。</summary>
    public required ImageSource ImageSource { get; init; }


    /// <summary>PNG 文件数据，用于保存到磁盘。</summary>
    public required byte[] PngBytes { get; init; }


}



/// <summary>
/// 二维码图片生成工具。编码与 PNG 输出由 QRCoder 完成，这里只负责配色和转成 WinUI 图像源。
/// </summary>
public static class QrCodeImageGenerator
{

    /// <summary>不透明黑，用于白底二维码。</summary>
    private static readonly byte[] OpaqueDark = [0, 0, 0, 255];


    /// <summary>不透明白，作为二维码的底色。</summary>
    private static readonly byte[] OpaqueLight = [255, 255, 255, 255];


    /// <summary>透明，用于把底色透出去。</summary>
    private static readonly byte[] Transparent = [0, 0, 0, 0];


    /// <summary>不透明白，用于深色背景上反色显示的码点。</summary>
    private static readonly byte[] OpaqueLightModules = [255, 255, 255, 255];



    /// <summary>
    /// 把文本编码成二维码图片。
    /// </summary>
    /// <param name="text">二维码内容，例如登录链接。</param>
    /// <param name="moduleSize">每个模块的像素大小。</param>
    /// <param name="quietZone">静默区宽度（模块数）。大于 0 时由 QRCoder 画出标准静默区（4 个模块），
    /// 为 0 时不画；QRCoder 不支持指定任意宽度，这里只区分有 / 无。</param>
    /// <param name="ecc">纠错等级。</param>
    /// <param name="displayColorMode">界面上显示时使用的颜色模式，背景可以透明。</param>
    public static async Task<QrCodeImageResult> GenerateAsync(string text, int moduleSize = 8, int quietZone = 3, QrCodeEcc ecc = QrCodeEcc.Medium, QrCodeColorMode displayColorMode = QrCodeColorMode.White)
    {
        // 界面上可以透明背景；保存到磁盘的图片固定用白底，方便在其它设备上扫描。
        byte[] displayPng = Encode(text, moduleSize, quietZone, ecc, displayColorMode);
        byte[] savePng = displayColorMode is QrCodeColorMode.White ? displayPng : Encode(text, moduleSize, quietZone, ecc, QrCodeColorMode.White);
        var bitmap = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(displayPng);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        return new QrCodeImageResult
        {
            ImageSource = bitmap,
            PngBytes = savePng,
        };
    }




    /// <summary>
    /// 按颜色模式编码成 PNG。底色透出去，亮色码点用在深色背景上。
    /// </summary>
    private static byte[] Encode(string text, int moduleSize, int quietZone, QrCodeEcc ecc, QrCodeColorMode colorMode)
    {
        if (moduleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleSize), "Module size must be at least 1 pixel.");
        }
        if (quietZone < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quietZone), "Quiet zone must not be negative.");
        }
        (byte[] dark, byte[] light) = colorMode switch
        {
            QrCodeColorMode.TransparentLightModules => (OpaqueLightModules, Transparent),
            QrCodeColorMode.TransparentDarkModules => (OpaqueDark, Transparent),
            _ => (OpaqueDark, OpaqueLight),
        };
        QRCodeGenerator.ECCLevel level = ecc switch
        {
            QrCodeEcc.Low => QRCodeGenerator.ECCLevel.L,
            QrCodeEcc.Quartile => QRCodeGenerator.ECCLevel.Q,
            QrCodeEcc.High => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.M,
        };
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, level);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(moduleSize, dark, light, drawQuietZones: quietZone > 0);
    }


}
