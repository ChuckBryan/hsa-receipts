using HsaReceipts.Contracts;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace HsaReceipts.Client.Services;

public sealed class OfflineStore(IJSRuntime js)
{
    private IJSObjectReference? module;

    public async Task<bool> IsOnlineAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>("isOnline");

    public async Task<string?> GetLastUserKeyAsync() =>
        await (await GetModuleAsync()).InvokeAsync<string?>("getLastUserKey");

    public async Task SetLastUserKeyAsync(string userKey) =>
        await (await GetModuleAsync()).InvokeVoidAsync("setLastUserKey", userKey);

    public async Task ClearLastUserKeyAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(
            "setLastUserKey",
            (string?)null);

    public async Task<bool> IsOfflineEnabledAsync(string userKey) =>
        await (await GetModuleAsync()).InvokeAsync<bool>("isOfflineEnabled", userKey);

    public async Task SetOfflineEnabledAsync(string userKey, bool enabled) =>
        await (await GetModuleAsync()).InvokeVoidAsync("setOfflineEnabled", userKey, enabled);

    public async Task SaveReceiptSnapshotAsync(
        string userKey,
        ReceiptListResponse snapshot) =>
        await (await GetModuleAsync()).InvokeVoidAsync("saveReceiptSnapshot", userKey, snapshot);

    public async Task<ReceiptListResponse?> GetReceiptSnapshotAsync(string userKey) =>
        await (await GetModuleAsync()).InvokeAsync<ReceiptListResponse?>(
            "getReceiptSnapshot",
            userKey);

    public async Task SaveDraftAsync(
        string userKey,
        LocalReceiptDraft draft,
        IBrowserFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            await (await GetModuleAsync()).InvokeVoidAsync("saveDraftMetadata", userKey, draft);
            return;
        }

        await using var stream = file.OpenReadStream(
            ReceiptRules.MaxUploadBytes,
            cancellationToken);
        using var streamReference = new DotNetStreamReference(stream);
        await (await GetModuleAsync()).InvokeVoidAsync(
            "saveDraft",
            userKey,
            draft,
            streamReference);
    }

    public async Task<IReadOnlyList<LocalReceiptDraft>> GetDraftsAsync(string userKey) =>
        await (await GetModuleAsync()).InvokeAsync<LocalReceiptDraft[]>("getDrafts", userKey);

    public async Task DeleteDraftAsync(string userKey, string draftId) =>
        await (await GetModuleAsync()).InvokeVoidAsync("deleteDraft", userKey, draftId);

    public async Task<ReceiptDto> SubmitDraftAsync(
        string userKey,
        string draftId,
        string antiforgeryToken) =>
        await (await GetModuleAsync()).InvokeAsync<ReceiptDto>(
            "submitDraft",
            userKey,
            draftId,
            antiforgeryToken);

    public async Task<string> GetAntiforgeryTokenAsync()
    {
        var response = await (await GetModuleAsync()).InvokeAsync<AntiforgeryResponse>(
            "getAntiforgeryToken");
        return response.Token;
    }

    public async Task<bool> ConfirmAsync(string message) =>
        await (await GetModuleAsync()).InvokeAsync<bool>("confirmAction", message);

    public async Task ClearPrivateDataAsync(string userKey) =>
        await (await GetModuleAsync()).InvokeVoidAsync("clearPrivateData", userKey);

    public async Task SubmitLogoutAsync(string antiforgeryToken) =>
        await (await GetModuleAsync()).InvokeVoidAsync("submitLogout", antiforgeryToken);

    public async Task RequestPersistentStorageAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync("requestPersistentStorage");

    private async Task<IJSObjectReference> GetModuleAsync() =>
        module ??= await js.InvokeAsync<IJSObjectReference>(
            "import",
            "./js/offline-store.js");
}
