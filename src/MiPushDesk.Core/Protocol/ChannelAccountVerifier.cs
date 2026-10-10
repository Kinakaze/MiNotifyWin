using System.Net.Sockets;
using System.Security.Cryptography;

namespace MiPushDesk.Core.Protocol;

public sealed class ChannelAccountVerifier(string host = "cn.app.chat.xiaomi.net", int port = 5222)
{
    public async Task VerifyAsync(ChannelAccount account, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new TcpClient { NoDelay = true };
        byte[]? sessionKey = null;
        try
        {
            await client.ConnectAsync(host, port, deadline.Token);
            await using var stream = client.GetStream();
            var hello = SlimProtocol.Protobuf((1, 106), (2, "MiPushDesk"), (3, "Windows"), (4, account.DeviceUuid),
                (5, 48), (6, "wifi"), (7, host), (8, "zh_CN"), (10, 0));
            await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CONN", hello)), deadline.Token);
            var response = await SlimProtocol.ReadFrameAsync(stream, null, TimeSpan.FromSeconds(8), deadline.Token);
            if (response.Command != "CONN") throw new InvalidDataException("服务器未返回登录握手。");
            sessionKey = MiPushMessage.SessionKey(SlimProtocol.ReadProtobuf(response.Payload).Text(1), account.DeviceUuid);
            await stream.WriteAsync(SlimProtocol.Frame(MiPushMessage.Bind(account,
                SlimProtocol.ReadProtobuf(response.Payload).Text(1)), sessionKey), deadline.Token);
            while (true)
            {
                response = await SlimProtocol.ReadFrameAsync(stream, sessionKey, TimeSpan.FromSeconds(8), deadline.Token);
                if (response.Command == "BIND" && response.Channel == 5)
                {
                    if (SlimProtocol.ReadProtobuf(response.Payload).Number(1) != 1)
                        throw new InvalidDataException("登录验证未通过，请检查 security 和账号是否有效。");
                    await stream.WriteAsync(SlimProtocol.Frame(SlimProtocol.Command("CLOSE"), sessionKey), deadline.Token);
                    return;
                }
                if (response.Command is "KICK" or "CLOSE")
                    throw new InvalidDataException("服务器中断验证，请停止其他桌面连接后重试。");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("登录验证超时，请检查网络后重试。"); }
        finally { if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey); }
    }
}
