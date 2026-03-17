using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;

namespace TradingPilot.Blazor.Client;

public class NoOpAuthStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> _state = Task.FromResult(
        new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;
}
