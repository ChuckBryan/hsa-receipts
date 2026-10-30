using HsaReceipts.Contracts;

namespace HsaReceipts.Client.Services;

public sealed class UserContext(ReceiptApiClient api, OfflineStore offlineStore)
{
    private Task? initializationTask;

    public SessionResponse? Session { get; private set; }
    public string? UserKey { get; private set; }
    public bool IsOffline { get; private set; }
    public bool HasKnownOnlineSession => Session?.IsAuthenticated == true;

    public Task InitializeAsync() =>
        initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            Session = await api.GetSessionAsync();
            IsOffline = false;
            if (Session.IsAuthenticated && Session.UserKey is not null)
            {
                UserKey = Session.UserKey;
                await offlineStore.SetLastUserKeyAsync(UserKey);
                return;
            }

            UserKey = null;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            UserKey = await offlineStore.GetLastUserKeyAsync();
            Session = new SessionResponse(false, null, UserKey);
        }
    }
}
