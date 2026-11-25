using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Starward.Core.Passport;
using Starward.Features.Passport;
using System;

namespace Starward.Features.Account;

/// <summary>
/// main 的 Cookie / 令牌导入登录。
/// <para>
/// 账号中心已经换成上游 0.19 的 HYP 浮窗（网页登录），main 原来的密码 / 短信 / 扫码登录不再使用；
/// 只有这条手工导入还在：右键账号中心的「+」打开它，粘贴 Cookie 或令牌即可把账号加进账号池。
/// </para>
/// </summary>
public sealed partial class PassportCookieImportDialog : ContentDialogBase
{

    private readonly ILogger<PassportCookieImportDialog> _logger = AppConfig.GetLogger<PassportCookieImportDialog>();

    private readonly PassportAccountService _accountService = AppConfig.GetService<PassportAccountService>();


    private bool _busy;


    public PassportCookieImportDialog()
    {
        this.InitializeComponent();
    }


    /// <summary>
    /// 导入成功后得到的账号（已经写进账号池和上游 <c>PassportUserInfo</c> 表）；
    /// 取消或失败时为 null。
    /// </summary>
    public PassportUserInfoModel? LoginModel { get; private set; }


    private void Button_Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }


    private async void Button_Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        try
        {
            // 四个输入框都允许粘贴整段 Cookie / 请求头：解析前先留一份原始文本，
            // 下面会把解析结果写回输入框，但设备信息还要从原始文本里找。
            string rawStokenText = TextBox_Stoken.Text;
            string rawMidText = TextBox_Mid.Text;
            string rawDeviceIdText = TextBox_DeviceId.Text;
            string rawDeviceFpText = TextBox_DeviceFp.Text;
            string stoken = "";
            string mid = "";
            long aid = 0;
            // 逐栏解析再合并：不管用户把 Cookie 粘进哪一栏，缺的 stoken / mid / aid 都能补上。
            foreach (string? text in new[] { rawStokenText, rawMidText, rawDeviceIdText, rawDeviceFpText })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                var parsed = PassportCookieParser.Parse(text);
                if (string.IsNullOrWhiteSpace(stoken) && !string.IsNullOrWhiteSpace(parsed.Stoken))
                {
                    stoken = parsed.Stoken;
                }
                if (string.IsNullOrWhiteSpace(mid) && !string.IsNullOrWhiteSpace(parsed.Mid))
                {
                    mid = parsed.Mid;
                }
                if (aid == 0 && parsed.Aid > 0)
                {
                    aid = parsed.Aid;
                }
            }
            // 解析不出来时退回用户单独填的裸值（例如不带 v2_ 前缀的令牌 / 单独填的 mid）。
            if (string.IsNullOrWhiteSpace(stoken))
            {
                stoken = rawStokenText.Trim();
            }
            if (string.IsNullOrWhiteSpace(mid))
            {
                mid = rawMidText.Trim();
            }
            // 解析结果填回输入框，方便用户核对。
            TextBox_Stoken.Text = stoken;
            TextBox_Mid.Text = mid;
            // 设备号 / 设备指纹和 stoken、mid 一样，可以从任何一栏粘贴的 Cookie 或请求头里解析出来
            // （x-rpc-device_id / x-rpc-device_fp）；已经单独填过的项不会被解析结果覆盖。
            var device = new PassportDeviceInfo().Merge(rawStokenText, rawMidText, rawDeviceIdText, rawDeviceFpText);
            // 解析出来的值只在格式可信时才填回输入框：粘进来的文本里可能有别的 device_id / 指纹，
            // 格式不对就不要拿它去覆盖、更不要因此中断导入。
            if (string.IsNullOrWhiteSpace(TextBox_DeviceId.Text) && Guid.TryParse(device.DeviceId, out _))
            {
                TextBox_DeviceId.Text = device.DeviceId;
            }
            if (string.IsNullOrWhiteSpace(TextBox_DeviceFp.Text) && PassportAccountService.IsValidDeviceFp(device.DeviceFp))
            {
                TextBox_DeviceFp.Text = device.DeviceFp!;
            }
            if (string.IsNullOrWhiteSpace(stoken))
            {
                ShowStatus(Lang.PassportLogin_ImportCookieNotFound);
                return;
            }
            // 设备标识（x-rpc-device_id）是可选项：留空就完全不动现有值。
            string deviceIdInput = TextBox_DeviceId.Text.Trim();
            if (!string.IsNullOrEmpty(deviceIdInput))
            {
                if (!Guid.TryParse(deviceIdInput, out _) || !_accountService.SaveDeviceId(deviceIdInput))
                {
                    ShowStatus(Lang.PassportLogin_DeviceIdInvalid);
                    return;
                }
            }
            // 设备指纹（x-rpc-device_fp）同样可选：留空就完全不动现有值。
            string deviceFpInput = TextBox_DeviceFp.Text.Trim();
            if (!string.IsNullOrEmpty(deviceFpInput) && !_accountService.SaveDeviceFp(deviceFpInput))
            {
                ShowStatus(Lang.PassportLogin_DeviceFpInvalid);
                return;
            }
            SetBusy(true);
            ShowStatus(null);
            PassportLoginOutcome outcome = await _accountService.ImportStokenAsync(stoken, mid, aid);
            if (outcome.IsSuccess)
            {
                LoginModel = PassportService.GetPassportUserInfo(outcome.Aid);
                this.Hide();
                return;
            }
            ShowStatus(outcome.Message ?? Lang.PassportLogin_ServiceError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import passport cookie");
            ShowStatus(string.Format(Lang.PassportLogin_NetworkError, ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }


    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProgressRing_Import.IsActive = busy;
        ProgressRing_Import.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }


    private void ShowStatus(string? message)
    {
        TextBlock_Status.Text = message;
        TextBlock_Status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

}
