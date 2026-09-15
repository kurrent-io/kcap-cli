using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Uploads staged chips to the server's temp attachment store and hands back their ids. Every
/// failure is a value; nothing throws past cancellation.
public sealed class ServerAttachmentUploader(ICapacitorHttpClient? http, ProfileContext? profiles) : IAttachmentUploader {
    public const string UnexpectedResponse = "the server returned an unexpected upload response";

    public async Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return UploadOutcome.Unauthorized("not_signed_in");
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return UploadOutcome.Unauthorized("not_signed_in");
                using var content = new MultipartFormDataContent();
                foreach (var file in files) {
                    var part = new ByteArrayContent(file.Bytes.ToArray());
                    part.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                    content.Add(part, "files", file.FileName);
                }
                using var response = await client.PostAsync($"{serverUrl.TrimEnd('/')}/api/attachments/upload", content, ct).ConfigureAwait(false);
                switch (response.StatusCode) {
                    case HttpStatusCode.OK:
                        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        return ParseIds(body, files.Count);
                    case HttpStatusCode.Unauthorized: return UploadOutcome.Unauthorized("not_signed_in");
                    case HttpStatusCode.BadRequest: return UploadOutcome.Rejected(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    default: return UploadOutcome.Unreachable($"server_status_{(int)response.StatusCode}");
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return UploadOutcome.Unreachable(ex.Message);
        }
    }

    internal static UploadOutcome ParseIds(string body, int expected) {
        try {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.IsArray || doc.RootElement.GetArrayLength() != expected) return UploadOutcome.Rejected(UnexpectedResponse);
            var ids = new List<string>(expected);
            foreach (var element in doc.RootElement.EnumerateArray()) {
                if (element.Str("id") is not { } id) return UploadOutcome.Rejected(UnexpectedResponse);
                ids.Add(id);
            }
            if (AttachmentIds.Validate(ids) is not null || (ids.Count == 0 && expected > 0)) return UploadOutcome.Rejected(UnexpectedResponse);
            return new UploadOutcome(UploadKind.Uploaded, ids, null);
        } catch (JsonException) {
            return UploadOutcome.Rejected(UnexpectedResponse);
        }
    }
}
