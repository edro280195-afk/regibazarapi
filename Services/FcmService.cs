using FirebaseAdmin.Messaging;

namespace EntregasApi.Services;

public interface IFcmService
{
    Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null);
    Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null);
}

public class FcmService : IFcmService
{
    private readonly ILogger<FcmService> _logger;

    public FcmService(ILogger<FcmService> logger)
    {
        _logger = logger;
    }

    public async Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null)
    {
        var tokenList = fcmTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokenList.Count == 0) return;

        // FCM multicast: máx 500 tokens por llamada
        const int chunkSize = 500;
        for (int i = 0; i < tokenList.Count; i += chunkSize)
        {
            var chunk = tokenList.Skip(i).Take(chunkSize).ToList();
            var message = new MulticastMessage
            {
                Tokens = chunk,
                Notification = new Notification { Title = title, Body = body },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        Title = title,
                        Body = body,
                        Icon = "ic_notification",
                        Color = "#FF0072",
                        ChannelId = "regibazar_channel"
                    }
                },
                Data = data
            };

            try
            {
                var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message);
                _logger.LogInformation("FCM multicast: {Success}/{Total} enviados.", response.SuccessCount, chunk.Count);

                // Log tokens fallidos (expirados, inválidos)
                for (int j = 0; j < response.Responses.Count; j++)
                {
                    if (!response.Responses[j].IsSuccess)
                    {
                        _logger.LogWarning("FCM token fallido [{Token}]: {Error}", chunk[j], response.Responses[j].Exception?.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando FCM multicast.");
            }
        }
    }

    public async Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) return;

        var message = new Message
        {
            Token = fcmToken,
            Notification = new Notification { Title = title, Body = body },
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    Title = title,
                    Body = body,
                    Icon = "ic_notification",
                    Color = "#FF0072",
                    ChannelId = "regibazar_channel"
                }
            },
            Data = data
        };

        try
        {
            var msgId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
            _logger.LogInformation("FCM enviado: {MsgId}", msgId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando FCM a token {Token}.", fcmToken);
        }
    }
}
