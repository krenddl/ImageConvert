using System.Text.Json;
using ImageForge.Shared.Messaging;

namespace ImageForge.Api.Services;

// Talks to the RabbitMQ Management HTTP API to read live queue metrics.
// Used by /api/stats so the frontend can show the worker fleet without
// running its own AMQP client in the browser.
public sealed class QueueStatsClient
{
    private readonly HttpClient _http;
    private readonly RabbitMqOptions _options;

    public QueueStatsClient(HttpClient http, RabbitMqOptions options)
    {
        _http = http;
        _options = options;

        // Management API lives on a separate port (15672 by default) and
        // uses Basic auth. We assume the same credentials as AMQP — true
        // for both the local dev box and the docker-compose stack.
        var creds = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{options.User}:{options.Password}"));
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
    }

    public async Task<QueueStats> GetAsync(CancellationToken ct = default)
    {
        // Management API queue lookup. "/" needs to be URL-encoded as %2F
        // because the default vhost is literally named "/".
        var url = $"http://{_options.Host}:15672/api/queues/%2F/{Uri.EscapeDataString(_options.Queue)}";
        using var response = await _http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Surface a "unknown" state instead of crashing the whole stats
            // call - the frontend just hides the fleet when fields are null.
            return new QueueStats(0, 0, 0, false);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int Read(string property) =>
            root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;

        return new QueueStats(
            Consumers: Read("consumers"),
            MessagesReady: Read("messages_ready"),
            MessagesUnacknowledged: Read("messages_unacknowledged"),
            Available: true);
    }
}

public sealed record QueueStats(
    int Consumers,
    int MessagesReady,
    int MessagesUnacknowledged,
    bool Available);
