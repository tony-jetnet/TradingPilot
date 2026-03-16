using Volo.Abp.Application.Services;

namespace TradingPilot.Webull.Hook;

public interface IWebullHookAppService : IApplicationService
{
    Task<HookStatusDto> GetStatusAsync();
    Task<HookStatusDto> StartAsync();
    Task<HookStatusDto> StopAsync();
    Task<List<MqttMessageDto>> GetRecentMessagesAsync(int count = 50);
    Task<string> PingHookAsync();
    Task<string> SubscribeTickerAsync(long tickerId, int[] types);
}
