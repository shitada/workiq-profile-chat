using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace GraphReportChat.Api;

public sealed class RunStore(Settings settings, ILogger<RunStore> logger)
{
    private BlobContainerClient Container()
    {
        var uri = new Uri(settings.Required("Report:StorageServiceUri"));
        if (uri.Scheme != "https" || uri.Query.Length > 0)
            throw new ApiException(503, "storage_configuration_invalid", "HTTPS Storage Service URI が必要です。");
        var options = new DefaultAzureCredentialOptions { ManagedIdentityClientId = settings.Get("Authentication:ManagedIdentityClientId") };
        return new BlobServiceClient(uri, new DefaultAzureCredential(options))
            .GetBlobContainerClient(settings.Get("Report:StateContainer", "report-runs"));
    }
    public static string NewHandle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static void ValidateOwner(RunState state, UserIdentity user, string provider, DateTimeOffset now)
    {
        if (state.TenantId != user.TenantId || state.SubjectId != user.ObjectId || state.Provider != provider)
            throw new ApiException(404, "run_not_found", "この実行状態は利用できません。");
        if (state.ExpiresAt <= now) throw new ApiException(410, "run_expired", "実行状態の有効期限が切れました。再生成してください。");
    }
    public async Task Create(RunState state, CancellationToken ct)
    {
        state.Id = NewHandle();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
        try
        {
            await Container().GetBlobClient($"{state.Id}.json").UploadAsync(BinaryData.FromObjectAsJson(state, Json.Options),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, HttpHeaders = new() { ContentType = "application/json" } }, ct);
        }
        catch (Exception ex) when (ex is not (ApiException or OperationCanceledException))
        {
            throw ServiceFailure.Wrap(ex, "state_create");
        }
    }
    public async Task<RunLease> Acquire(string handle, UserIdentity user, string provider, CancellationToken ct)
    {
        if (handle.Length != 64 || handle.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ApiException(404, "run_not_found", "実行状態が見つかりません。");
        var blob = Container().GetBlobClient($"{handle}.json");
        try
        {
            // Authorize before locking: a foreign handle cannot hold another user's lease.
            var initial = await blob.DownloadContentAsync(ct);
            var state = initial.Value.Content.ToObjectFromJson<RunState>(Json.Options)
                ?? throw new ApiException(500, "state_invalid", "実行状態を読み取れません。");
            ValidateOwner(state, user, provider, DateTimeOffset.UtcNow);
            var leaseClient = blob.GetBlobLeaseClient();
            var lease = await leaseClient.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct);
            try
            {
                var current = await blob.DownloadContentAsync(new BlobDownloadOptions { Conditions = new BlobRequestConditions { LeaseId = lease.Value.LeaseId } }, ct);
                state = current.Value.Content.ToObjectFromJson<RunState>(Json.Options)!;
                ValidateOwner(state, user, provider, DateTimeOffset.UtcNow);
                return new RunLease(blob, leaseClient, lease.Value.LeaseId, state, logger);
            }
            catch
            {
                try { await leaseClient.ReleaseAsync(cancellationToken: CancellationToken.None); }
                catch (Exception cleanup)
                {
                    logger.LogWarning("Report state lock cleanup failed Diagnostic={Diagnostic}",
                        ServiceFailure.Serialize(ServiceFailure.Describe(cleanup, "state_release")));
                }
                throw;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { throw new ApiException(404, "run_not_found", "実行状態が見つかりません。"); }
        catch (RequestFailedException ex) when (ex.Status == 409) { throw new ApiException(409, "run_busy", "別の要求が処理中です。少し待って再試行してください。"); }
        catch (Exception ex) when (ex is not (ApiException or OperationCanceledException))
        {
            throw ServiceFailure.Wrap(ex, "state_read");
        }
    }
}

public sealed class RunLease : IAsyncDisposable
{
    private readonly BlobClient blob;
    private readonly BlobLeaseClient lease;
    private readonly string leaseId;
    private readonly CancellationTokenSource renewalStop = new();
    private readonly Task renewal;
    private readonly ILogger logger;
    public RunState State { get; }
    public RunLease(BlobClient blob, BlobLeaseClient lease, string leaseId, RunState state, ILogger logger)
    {
        this.blob = blob; this.lease = lease; this.leaseId = leaseId; State = state;
        this.logger = logger;
        renewal = Renew();
    }
    private async Task Renew()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(renewalStop.Token))
                await lease.RenewAsync(cancellationToken: renewalStop.Token);
        }
        catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
    }
    public async Task Save(CancellationToken ct)
    {
        if (renewal.IsFaulted) throw new ApiException(409, "lease_lost", "実行状態のロックを失いました。再試行してください。");
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(State, Json.Options),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { LeaseId = leaseId } }, ct);
        }
        catch (Exception ex) when (ex is not (ApiException or OperationCanceledException))
        {
            throw ServiceFailure.Wrap(ex, "state_save");
        }
    }
    public async ValueTask DisposeAsync()
    {
        await renewalStop.CancelAsync();
        try { await renewal; }
        catch (Exception ex)
        {
            logger.LogWarning("Report state renewal failed Diagnostic={Diagnostic}",
                ServiceFailure.Serialize(ServiceFailure.Describe(ex, "state_renew")));
        }
        try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); }
        catch (Exception ex)
        {
            // Cleanup must not replace the original collection error or a saved result.
            logger.LogWarning("Report state lock cleanup failed Diagnostic={Diagnostic}",
                ServiceFailure.Serialize(ServiceFailure.Describe(ex, "state_release")));
        }
        renewalStop.Dispose();
    }
}
