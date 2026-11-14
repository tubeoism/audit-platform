using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditPlatform.Contracts.Evidence;

namespace AuditPlatform.Client.Core.Evidence;

public enum EvidenceSend
{
    Stored = 0,

    /// <summary>The master answered and will not take this file; sending it again cannot help.</summary>
    Refused = 1,

    /// <summary>No answer, or an answer that says nothing about the file (outage, proxy, expired session). Try again later.</summary>
    Unreachable = 2,
}

public sealed record EvidenceSendResult(EvidenceSend Outcome, string? Reason = null);

/// <summary>Moves evidence files between this laptop and the master. Abstracted so the rest can be tested without one.</summary>
public interface IEvidenceTransport
{
    Task<EvidenceSendResult> SendAsync(Guid engagementId, string hash, string fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>The engagement's evidence as the master lists it, or null when it could not be read.</summary>
    Task<IReadOnlyList<EvidenceInfo>?> ListAsync(Guid engagementId, CancellationToken cancellationToken);

    /// <summary>Copies one file from the master into <paramref name="destination"/>. False when it could not be read.</summary>
    Task<bool> FetchAsync(Guid engagementId, string hash, Stream destination, CancellationToken cancellationToken);
}

/// <summary>
/// Evidence over the master's HTTP API. It takes its own <see cref="HttpClient"/>: a file of tens of megabytes on a
/// hotel line needs minutes, not the short timeout that suits sign-in.
/// </summary>
public sealed class MasterEvidenceTransport(HttpClient http, ClientOptions options, Func<Task<string?>> accessToken) : IEvidenceTransport
{
    public async Task<EvidenceSendResult> SendAsync(Guid engagementId, string hash, string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var route = $"{EvidenceRoutes.FileFor(engagementId, hash)}?{EvidenceRoutes.FileNameParameter}={Uri.EscapeDataString(fileName)}";
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = content.Length;
        using var response = await SendAsync(HttpMethod.Put, route, body, cancellationToken);
        if (response is null)
        {
            return new EvidenceSendResult(EvidenceSend.Unreachable);
        }

        if (response.IsSuccessStatusCode)
        {
            return new EvidenceSendResult(EvidenceSend.Stored);
        }

        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict
            or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity))
        {
            return new EvidenceSendResult(EvidenceSend.Unreachable, $"HTTP {(int)response.StatusCode}");
        }

        EvidenceError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<EvidenceError>(cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not the master's own answer (a proxy page, for example).
        }

        // A refusal that is not the master's own wording is not trusted to be final.
        return error is null
            ? new EvidenceSendResult(EvidenceSend.Unreachable, $"HTTP {(int)response.StatusCode}")
            : new EvidenceSendResult(EvidenceSend.Refused, error.Message);
    }

    public async Task<IReadOnlyList<EvidenceInfo>?> ListAsync(Guid engagementId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, EvidenceRoutes.ListFor(engagementId), null, cancellationToken);
        if (response is not { IsSuccessStatusCode: true })
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<List<EvidenceInfo>>(cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<bool> FetchAsync(Guid engagementId, string hash, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var response = await SendAsync(HttpMethod.Get, EvidenceRoutes.FileFor(engagementId, hash), null, cancellationToken);
        if (response is not { IsSuccessStatusCode: true })
        {
            return false;
        }

        try
        {
            await response.Content.CopyToAsync(destination, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string route, HttpContent? body, CancellationToken cancellationToken)
    {
        if (await accessToken() is not { } token)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(options.MasterUrl, route)) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
