using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HsaReceipts.Contracts;
using Microsoft.AspNetCore.Components.Forms;

namespace HsaReceipts.Client.Services;

public sealed class ReceiptApiClient(HttpClient http)
{
    public async Task<SessionResponse> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("bff/session", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken)
            ?? new SessionResponse(false, null, null);
    }

    public async Task<ReceiptListResponse> GetReceiptsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/receipts", cancellationToken);
        await EnsureApiSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReceiptListResponse>(cancellationToken)
            ?? new ReceiptListResponse([], 0, DateTimeOffset.UtcNow.ToString("O"));
    }

    public async Task<ReceiptDto> CreateAsync(
        ReceiptFields fields,
        IBrowserFile upload,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(fields.Provider), "provider");
        content.Add(new StringContent(fields.Patient), "patient");
        content.Add(new StringContent(fields.ServiceDate), "serviceDate");
        content.Add(new StringContent(
            fields.Amount.ToString(CultureInfo.InvariantCulture)), "amount");
        content.Add(new StringContent(fields.Notes), "notes");
        content.Add(new StringContent(idempotencyKey), "idempotencyKey");
        var fileContent = new StreamContent(
            upload.OpenReadStream(ReceiptRules.MaxUploadBytes, cancellationToken));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(upload.ContentType)
                ? "application/octet-stream"
                : upload.ContentType);
        content.Add(fileContent, "upload", upload.Name);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/receipts")
        {
            Content = content
        };
        request.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureApiSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ReceiptDto>(cancellationToken))!;
    }

    public async Task EditAsync(
        string id,
        EditReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/receipts/{Uri.EscapeDataString(id)}")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(cancellationToken));
        using var response = await http.SendAsync(message, cancellationToken);
        await EnsureApiSuccessAsync(response, cancellationToken);
    }

    public async Task ReimburseAsync(
        string id,
        ReimbursementRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/receipts/{Uri.EscapeDataString(id)}/reimbursement")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(cancellationToken));
        using var response = await http.SendAsync(message, cancellationToken);
        await EnsureApiSuccessAsync(response, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "bff/logout")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Found and not HttpStatusCode.Redirect)
        {
            await EnsureApiSuccessAsync(response, cancellationToken);
        }
    }

    private async Task<string> GetAntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("bff/antiforgery", cancellationToken);
        await EnsureApiSuccessAsync(response, cancellationToken);
        var token = await response.Content.ReadFromJsonAsync<AntiforgeryResponse>(
            cancellationToken);
        return token?.Token ?? throw new InvalidOperationException(
            "The server did not issue an antiforgery token.");
    }

    private static async Task EnsureApiSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SessionExpiredException();
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ReceiptApiException(
            string.IsNullOrWhiteSpace(details)
                ? $"The server returned {(int)response.StatusCode}."
                : details,
            response.StatusCode);
    }
}

public sealed class SessionExpiredException : Exception;

public sealed class ReceiptApiException(string message, HttpStatusCode statusCode)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
