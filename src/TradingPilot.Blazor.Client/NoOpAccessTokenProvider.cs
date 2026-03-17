using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace TradingPilot.Blazor.Client;

public class NoOpAccessTokenProvider : IAccessTokenProvider
{
    public System.Threading.Tasks.ValueTask<AccessTokenResult> RequestAccessToken()
    {
        return new System.Threading.Tasks.ValueTask<AccessTokenResult>(
            new AccessTokenResult(
                AccessTokenResultStatus.RequiresRedirect,
                new AccessToken(),
                "/",
                new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = "/" }));
    }

    public System.Threading.Tasks.ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options)
    {
        return RequestAccessToken();
    }
}
