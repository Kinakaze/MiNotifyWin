using System.Diagnostics;
using System.Net;

namespace MiPushDesk.Core;

public sealed record AdbDevice(string Serial, string State, string Model, bool Usb)
{
    public bool Ready => State == "device";
    public string Label => $"{Model} · {(Usb ? "USB" : "Wi-Fi")}"
        + (Ready ? "" : State == "unauthorized" ? " · 请在手机授权" : " · 离线");
}

public sealed class AdbClient(string executable)
{
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "tools", "adb", "adb.exe");

    public async Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken cancellationToken) =>
        ParseDevices(await RunAsync(["devices", "-l"], cancellationToken));

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1] is not ("device" or "offline" or "unauthorized")) continue;
            var model = fields.FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal))?[6..]
                .Replace('_', ' ') ?? fields[0];
            var usb = fields.Any(field => field.StartsWith("usb:", StringComparison.Ordinal))
                || !fields[0].Contains(':') && !fields[0].EndsWith("._tcp", StringComparison.Ordinal);
            devices.Add(new(fields[0], fields[1], model, usb));
        }
        return devices;
    }

    public async Task ConnectAsync(string address, CancellationToken cancellationToken)
    {
        ValidateAddress(address);
        var output = await RunAsync(["connect", address], cancellationToken);
        if (!output.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Wi-Fi 连接失败，请检查连接地址、网络及手机授权。");
    }

    public async Task PairAsync(string address, string code, CancellationToken cancellationToken)
    {
        ValidateAddress(address);
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) throw new InvalidDataException("请输入手机显示的六位配对码。");
        var output = await RunAsync(["pair", address], cancellationToken, code);
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new IOException("配对失败，请检查配对地址与配对码。");
    }

    public static void ValidateAddress(string address)
    {
        if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port is < 1 or > 65535
            || address.Trim() != address)
            throw new InvalidDataException("请输入手机无线调试页面的 IP:端口。");
    }

    public async Task<string> ExtractSecurityAsync(AdbDevice device, AccountCandidate account,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!device.Ready) throw new IOException(device.State == "unauthorized" ? "请在手机允许 USB 调试，再刷新设备。" : "手机已离线，请重新连接。");
        progress.Report("正在读取手机日志…");
        var recent = await RunAsync(["-s", device.Serial, "logcat", "-d", "-v", "raw", "PushService:V", "*:S"], cancellationToken);
        foreach (var line in recent.Split('\n'))
            if (account.SecurityFromLog(line) is { } security) return security;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        using var process = Start(["-s", device.Serial, "logcat", "-T", "1", "-v", "raw", "PushService:V", "*:S"]);
        var errors = process.StandardError.ReadToEndAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var diagnosticStarted = false;
        try
        {
            progress.Report("正在启动手机诊断，请在手机允许 VPN…");
            diagnosticStarted = true;
            var launch = await RunAsync(["-s", device.Serial, "shell", "am", "start", "-W", "-n",
                "io.github.mipush.analyzer/.DiagnosticActivity", "--es", "request_id", requestId], deadline.Token);
            if (launch.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || launch.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法启动手机诊断，请安装 MiNoitifyApp 0.5.0 或更新版本。");
            progress.Report("等待手机诊断日志；如有提示，请允许 VPN。保持手机解锁。");
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                if (account.SecurityFromLog(line) is { } security) return security;
            throw Failure(await errors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("未找到匹配的 security。请停止手机抓包后重试；账号变更后需要重新分享 JSON。"); }
        finally
        {
            await StopAsync(process);
            await errors;
            if (diagnosticStarted)
            {
                try
                {
                    await RunAsync(["-s", device.Serial, "shell", "am", "broadcast", "--receiver-foreground", "-n",
                        "io.github.mipush.analyzer/.DiagnosticReceiver", "-a", "io.github.mipush.analyzer.STOP_DIAGNOSTIC",
                        "--es", "request_id", requestId], CancellationToken.None);
                }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
        }
    }

    private Process Start(IEnumerable<string> arguments, bool input = false)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("安装目录缺少 ADB，请重新解压完整安装包。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动 ADB。");
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Start(arguments, input is not null);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw Failure(await errors);
            return await output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("ADB 操作超时，请检查手机连接与调试授权。"); }
        finally
        {
            await StopAsync(process);
            await Task.WhenAll(output, errors);
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync();
    }

    private static IOException Failure(string message) => new(message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        ? "请在手机允许调试，再刷新设备。"
        : message.Contains("offline", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? "手机已断开，请重新连接后刷新设备。" : "ADB 操作失败，请检查手机连接与调试授权。");
}
