using Microsoft.Extensions.Options;
using WorkIqProfileChat.Api.Models;
using WorkIqProfileChat.Api.Options;

namespace WorkIqProfileChat.Api.Services;

public sealed class SecretExpiryService(IOptions<AuthenticationOptions> options)
{
    private readonly DateTimeOffset _expiresOn =
        options.Value.WorkIqClientSecretExpiresOn;

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_expiresOn == default)
        {
            return Task.FromResult(new ServiceStatus(true, null));
        }

        int daysRemaining = Math.Max(
            0,
            (int)Math.Floor((_expiresOn - DateTimeOffset.UtcNow).TotalDays));
        return Task.FromResult(
            new ServiceStatus(daysRemaining <= 30, daysRemaining));
    }
}
