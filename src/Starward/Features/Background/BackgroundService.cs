using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Starward.Core.HoYoPlay;
using Starward.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Storage;

namespace Starward.Features.Background;

public class BackgroundService
{

    private readonly ILogger<BackgroundService> _logger;

    private readonly HoYoPlayService _hoYoPlayService;

    private readonly HttpClient _httpClient;



    public BackgroundService(ILogger<BackgroundService> logger, HoYoPlayService hoYoPlayService, HttpClient httpClient)
    {
        _logger = logger;
        _hoYoPlayService = hoYoPlayService;
        _httpClient = httpClient;
    }



    /// <summary>
    /// 获取自定义背景图文件路径
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="path"></param>
    /// <returns></returns>

    /// <summary>
    /// 当前预设正在使用的背景文件名；预设存储不可用或没记过时返回 null（调用方回退旧键）。
    /// </summary>
    private static string? GetProfileBgName(GameId gameId)
    {
        try
        {
            return GameLaunchProfileStore.GetCurrentBackground(gameId.GameBiz);
        }
        catch
        {
            return null;
        }
    }


    public static bool TryGetCustomBgFilePath(GameId? gameId, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (gameId is null)
        {
            return false;
        }
        try
        {
            // 自定义背景属于启动预设，预设是文件名和开关唯一的 owner。
            if (!GameLaunchProfileStore.GetCurrentEnableCustomBackground(gameId.GameBiz))
            {
                return false;
            }
            string? name = GameLaunchProfileStore.GetCurrentCustomBackground(gameId.GameBiz);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            // 0.18 把自定义背景放在缓存目录，0.19 改成了用户数据目录且没有搬文件：两个目录都找。
            foreach (string folder in new[] { Path.Join(AppConfig.UserDataFolder, "bg"), Path.Join(AppConfig.CacheFolder, "bg") })
            {
                string candidate = Path.Join(folder, name);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 自定义背景文件的完整路径（用户数据目录优先，回退到 main 时代的缓存目录）。
    /// 只用于「打开 / 删除」这类需要路径的界面操作。
    /// </summary>
    public static string? GetCustomBgFilePath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        foreach (string folder in new[] { Path.Join(AppConfig.UserDataFolder, "bg"), Path.Join(AppConfig.CacheFolder, "bg") })
        {
            string candidate = Path.Join(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }


    /// <summary>
    /// 文件是否是支持的视频格式，支持 mp4、mkv、flv、webm
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static bool FileIsSupportedVideo(string? name)
    {
        return Path.GetExtension(name) switch
        {
            ".mp4" or ".mkv" or ".webm" => true,
            _ => false,
        };
    }


    /// <summary>
    /// 获取已缓存的背景图文件路径
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public static string? GetLastAppBackgroundFile(GameId? gameId)
    {
        if (gameId is null)
        {
            return null;
        }
        // 当前背景属于启动预设（唯一 owner），0.19 上游按 GameId 的 Bg 只在迁移时读过。
        string? name = GetProfileBgName(gameId);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string? path = Path.Join(AppConfig.CacheFolder, "bg", name);
        if (File.Exists(path))
        {
            return path;
        }
        path = Path.Join(AppConfig.UserDataFolder, "bg", name);
        if (File.Exists(path))
        {
            return path;
        }
        return null;
    }



    /// <summary>
    /// 获取背景图文件路径，保存在 CacheFolder\bg。
    /// <para>从主分支迁移过来的好感壁纸代码使用；0.19 原本只有按 GameId 取当前背景的入口。</para>
    /// </summary>
    /// <param name="name">背景文件名。</param>
    /// <returns>完整路径；<paramref name="name"/> 为 null 时返回 null。</returns>
    [return: NotNullIfNotNull(nameof(name))]
    public static string? GetBgFilePath(string? name)
    {
        return Path.Join(AppConfig.CacheFolder, "bg", name);
    }



    /// <summary>
    /// 游戏背景图
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<(List<GameBackgroundModel> List, GameBackgroundModel? Selected)> GetGameBackgroundsAsync(GameId gameId, CancellationToken cancellationToken = default)
    {
        GameBackgroundInfo? backgroundInfo = await _hoYoPlayService.GetGameBackgroundInfoAsync(gameId, cancellationToken);
        List<GameBackground> backgrounds = backgroundInfo?.Backgrounds?.ToList() ?? [];
        if (await _hoYoPlayService.GetGameInfoAsync(gameId, cancellationToken) is GameInfo gameInfo)
        {
            backgrounds.Add(new GameBackground
            {
                Id = gameInfo.Id,
                Background = new GameImage { Url = gameInfo.Display.Background?.Url! },
                Icon = new(),
                Video = new(),
                Theme = new(),
                Type = GameBackground.BACKGROUND_TYPE_UNSPECIFIED,
            });
        }
        List<GameBackgroundModel> models = backgrounds.Select(x => new GameBackgroundModel(x)).ToList();
        if (models.Count is 0)
        {
            return (models, null);
        }

        // 这个设置的格式为：$"{index};{id1},{type1};{id2},{type2}"
        string? last = AppConfig.GetGameBackground(gameId);
        if (string.IsNullOrWhiteSpace(last))
        {
            return (models, models?.FirstOrDefault());
        }

        int index = 0;
        int i = last.IndexOf(';');
        if (i >= 0)
        {
            _ = int.TryParse(last.AsSpan().Slice(0, i), out index);
        }

        List<(string Id, int Type)> lastIds = new();
        foreach (Match match in Regex.Matches(last, @"([0-9A-Za-z]+),(\d)"))
        {
            string id = match.Groups[1].Value;
            int.TryParse(match.Groups[2].Value, out int type);
            lastIds.Add((id, type));
        }

        foreach (var model in models)
        {
            if (lastIds.FirstOrDefault(x => x.Id == model.Id) is (string id, int type))
            {
                model.Last = true;
                model.Type = Math.Clamp(type, 0, 1);
            }
        }

        if (models.FirstOrDefault(x => !x.Last) is GameBackgroundModel model1)
        {
            return (models, model1);
        }
        else if (models.Count > index)
        {
            return (models, models[index]);
        }
        else
        {
            return (models, models.FirstOrDefault());
        }
    }




    public async Task<string> GetBackgroundFileAsync(string url, CancellationToken cancellationToken = default)
    {
        string file = Path.Combine(AppConfig.CacheFolder, "bg", Path.GetFileName(url));
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.Combine(AppConfig.CacheFolder, "bg"));
            string tmp = file + ".tmp";
            using var fs = File.Open(tmp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(fs.Length, null);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            fs.Position = response.Content.Headers.ContentRange?.From ?? 0;
            using var hs = await response.Content.ReadAsStreamAsync(cancellationToken);
            await hs.CopyToAsync(fs, cancellationToken);
            fs.Dispose();
            File.Move(tmp, file, true);
        }
        return file;
    }




    /// <summary>
    /// 获取默认的背景图文件路径
    /// </summary>
    /// <returns></returns>
    public static string? GetFallbackBackgroundImage()
    {
        string baseFolder = AppContext.BaseDirectory;
        string path = Path.Combine(baseFolder, @"Assets\Image\UI_CutScene_1130320101A.png");
        return File.Exists(path) ? path : null;
    }



    #region Pick Custom Background


    /// <summary>
    /// 更改自定义背景图文件，保存在 UserData\bg，返回文件名
    /// </summary>
    /// <param name="xamlRoot"></param>
    /// <returns></returns>
    public async Task<string?> ChangeCustomBackgroundFileAsync(XamlRoot xamlRoot)
    {
        string? file = await PickBackgroundFileAsync(xamlRoot);
        if (file is null)
        {
            return null;
        }
        await CheckBackgroundFileAvailableAsync(file);
        string bg = Path.Join(AppConfig.UserDataFolder, "bg");
        Directory.CreateDirectory(bg);
        string name = Path.GetFileName(file);
        string path = Path.Combine(bg, name);
        if (path != file)
        {
            File.Copy(file, path, true);
        }
        return name;
    }



    /// <summary>
    /// 更改自定义背景图文件，保存在 UserData\bg，返回文件名
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static async Task<string?> ChangeCustomBackgroundFileAsync(StorageFile file)
    {
        string bg = Path.Join(AppConfig.UserDataFolder, "bg");
        if (Path.GetDirectoryName(file.Path) != bg)
        {
            if (FileIsSupportedVideo(file.Name))
            {
                using var source = MediaSource.CreateFromStorageFile(file);
                await source.OpenAsync();
            }
            else
            {
                using var fs = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(fs);
            }
            {
                Directory.CreateDirectory(bg);
                string path = Path.Combine(bg, file.Name);
                using var dest = File.OpenWrite(path);
                using var stream = await file.OpenReadAsync();
                await stream.AsStream().CopyToAsync(dest);
            }
        }
        return file.Name;
    }


    /// <summary>
    /// 选择背景图文件
    /// </summary>
    /// <param name="xamlRoot"></param>
    /// <returns></returns>
    private async Task<string?> PickBackgroundFileAsync(XamlRoot xamlRoot)
    {
        var filter = new (string, string)[]
            {
                ("Image", ".bmp"),
                ("Image", ".jpg"),
                ("Image", ".png"),
                ("Image", ".webp"),
                ("Image", ".avif"),
                ("Image", ".jxl"),
                ("Video", ".mp4"),
                ("Video", ".mkv"),
                ("Video", ".webm"),
            };
        return await FileDialogHelper.PickSingleFileAsync(xamlRoot, filter);
    }



    /// <summary>
    /// 检查背景图文件是否可用
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private static async Task CheckBackgroundFileAvailableAsync(string file)
    {
        if (FileIsSupportedVideo(file))
        {
            // 0xC00D36C4
            using var source = MediaSource.CreateFromUri(new Uri(file));
            await source.OpenAsync();
        }
        else
        {
            // 0x88982F8B
            using var fs = File.OpenRead(file);
            var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
        }
    }


    #endregion


}
