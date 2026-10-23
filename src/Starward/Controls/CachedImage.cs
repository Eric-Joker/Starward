using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Scighost.WinUI.ImageEx;
using Starward.Features.Background;
using Starward.Features.Codec;
using Starward.Helpers;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Controls;

public sealed partial class CachedImage : ImageEx
{



    public bool IsThumbnail
    {
        get { return (bool)GetValue(IsThumbnailProperty); }
        set { SetValue(IsThumbnailProperty, value); }
    }

    public static readonly DependencyProperty IsThumbnailProperty =
        DependencyProperty.Register("IsThumbnail", typeof(bool), typeof(CachedImage), new PropertyMetadata(false));


    public bool PngThumbnail
    {
        get { return (bool)GetValue(PngThumbnailProperty); }
        set { SetValue(PngThumbnailProperty, value); }
    }

    public static readonly DependencyProperty PngThumbnailProperty =
        DependencyProperty.Register(nameof(PngThumbnail), typeof(bool), typeof(CachedImage), new PropertyMetadata(false));



    protected override async Task<ImageSource?> ProvideCachedResourceAsync(Uri imageUri, CancellationToken token)
    {
        try
        {
            if (imageUri.Scheme is "ms-appx")
            {
                return new BitmapImage(imageUri);
            }
            else if (imageUri.Scheme is "file")
            {
                if (IsThumbnail)
                {
                    if (BackgroundService.FileIsSupportedVideo(imageUri.OriginalString))
                    {
                        return await ImageThumbnail.GetVideoThumbnailAsync(imageUri.LocalPath, token);
                    }
                    else
                    {
                        return await ImageThumbnail.GetImageThumbnailAsync(imageUri.LocalPath, PngThumbnail, token);
                    }
                }
                else
                {
                    return new BitmapImage(imageUri);
                }
            }
            else
            {
                var file = await FileCache.GetFromCacheAsync(imageUri.ToString(), false, token);
                if (token.IsCancellationRequested)
                {
                    throw new TaskCanceledException("Image source has changed.");
                }
                if (file is null)
                {
                    throw new FileNotFoundException(imageUri.ToString());
                }
                string uriKey = imageUri.ToString();
                var bitmap = new BitmapImage();
                // 本次运行里坏过的图：XAML 图片缓存按文件路径记着那次解码失败，重新下载到同一路径后仍会直接判失败，
                // 不绕过缓存就会陷入「下载→判失败→删除」的循环。CreateOptions 必须在设置 UriSource 之前赋值。
                if (_refreshedUris.ContainsKey(uriKey))
                {
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                }
                bitmap.UriSource = new Uri(file);
                void OnOpened(object sender, RoutedEventArgs e)
                {
                    if (sender is BitmapImage image)
                    {
                        image.ImageOpened -= OnOpened;
                        image.ImageFailed -= OnFailed;
                    }
                }
                void OnFailed(object sender, ExceptionRoutedEventArgs e)
                {
                    if (sender is BitmapImage image)
                    {
                        image.ImageOpened -= OnOpened;
                        image.ImageFailed -= OnFailed;
                    }
                    // 每个地址每次运行只删一次：重新下载的文件仍解不出来（如缺少解码器）时保留，免得每次显示都重下。
                    // 缓存文件名按远程地址计算；bitmap.UriSource 已是本地缓存文件路径，传它会算错文件名、什么也删不掉。
                    if (_refreshedUris.TryAdd(uriKey, 0))
                    {
                        FileCache.DeleteCacheFile(uriKey);
                    }
                }
                bitmap.ImageOpened += OnOpened;
                bitmap.ImageFailed += OnFailed;
                return bitmap;
            }
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (Exception)
        {
            throw;
        }
    }



    /// <summary>
    /// 本次运行中解码失败过、已删除磁盘缓存并会重新下载的远程地址（<see cref="Uri.ToString"/>）。
    /// 用于两件事：之后加载这些地址时绕过 XAML 图片缓存；同一地址只删一次磁盘缓存。
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> _refreshedUris = new();


}
