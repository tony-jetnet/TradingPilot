using System;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace TradingPilot.HttpApi.Client.ConsoleTestApp;

public class ClientDemoService : ITransientDependency
{
    public async Task RunAsync()
    {
        Console.WriteLine("TradingPilot API Client Demo");
        await Task.CompletedTask;
    }
}
