using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KodiListenerGui
{
    // Result of a single Kodi JSON-RPC call. Distinguishes a transport/connection failure
    // from a JSON-RPC application error, and from a successful result, so callers never
    // have to guess why a value came back empty.
    public readonly struct KodiResponse
    {
        public bool Success { get; init; }
        public JsonElement Result { get; init; }
        public string? ErrorMessage { get; init; }
        public bool IsConnectionError { get; init; }

        public static KodiResponse Ok(JsonElement result) => new() { Success = true, Result = result };

        public static KodiResponse Fail(string message, bool isConnectionError) =>
            new() { Success = false, ErrorMessage = message, IsConnectionError = isConnectionError };
    }

    // Owns the HTTP transport and JSON-RPC envelope for talking to Kodi. Kept separate from
    // MainWindow so the UI layer does not also have to know about the wire protocol.
    public sealed class KodiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _hostUrl;
        private readonly Action<string> _log;
        private int _nextId;
        private bool? _reachable;

        public KodiClient(string hostUrl, string username, string password, Action<string> log)
        {
            _hostUrl = hostUrl;
            _log = log;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            if (!string.IsNullOrEmpty(username))
            {
                byte[] credentials = Encoding.ASCII.GetBytes($"{username}:{password}");
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
            }
        }

        public async Task<KodiResponse> SendRequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            int id = Interlocked.Increment(ref _nextId);
            var payload = new { jsonrpc = "2.0", method, @params = parameters ?? new { }, id };
            string jsonPayload = JsonSerializer.Serialize(payload);

            try
            {
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_hostUrl, content, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                _log($"{method} (id={id}) -> {(int)response.StatusCode} {response.StatusCode}");
                UpdateReachability(true);

                if (!response.IsSuccessStatusCode)
                {
                    return KodiResponse.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", isConnectionError: true);
                }

                return ParseEnvelope(body, method);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                UpdateReachability(false, ex.Message);
                return KodiResponse.Fail(ex.Message, isConnectionError: true);
            }
        }

        private void UpdateReachability(bool reachable, string? failureMessage = null)
        {
            if (_reachable == reachable)
            {
                return;
            }
            _reachable = reachable;
            _log(reachable ? "Kodi is reachable." : $"Kodi is unavailable: {failureMessage}");
        }

        // Sends several requests in a single HTTP round-trip using standard JSON-RPC batching.
        // Results are returned in the same order as the input requests. Reduces the request
        // count for a status refresh and keeps the calls that do run in flight to a minimum.
        public async Task<IReadOnlyList<KodiResponse>> SendBatchAsync(IReadOnlyList<(string Method, object? Parameters)> requests, CancellationToken cancellationToken = default)
        {
            var ids = new int[requests.Count];
            var payloadList = new List<object>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                int id = Interlocked.Increment(ref _nextId);
                ids[i] = id;
                payloadList.Add(new { jsonrpc = "2.0", method = requests[i].Method, @params = requests[i].Parameters ?? new { }, id });
            }

            var byId = new Dictionary<int, KodiResponse>(requests.Count);
            string jsonPayload = JsonSerializer.Serialize(payloadList);

            try
            {
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_hostUrl, content, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                _log($"Batch [{string.Join(", ", requests.Select(r => r.Method))}] -> {(int)response.StatusCode} {response.StatusCode}");
                UpdateReachability(true);

                if (!response.IsSuccessStatusCode)
                {
                    var failure = KodiResponse.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", isConnectionError: true);
                    foreach (int id in ids)
                    {
                        byId[id] = failure;
                    }
                    return ids.Select(id => byId[id]).ToList();
                }

                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int entryId))
                        {
                            continue;
                        }
                        byId[entryId] = ParseResultOrError(entry);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                UpdateReachability(false, ex.Message);
                var failure = KodiResponse.Fail(ex.Message, isConnectionError: true);
                foreach (int id in ids)
                {
                    byId[id] = failure;
                }
            }

            foreach (int id in ids)
            {
                byId.TryAdd(id, KodiResponse.Fail("No response received for this request.", isConnectionError: true));
            }

            return ids.Select(id => byId[id]).ToList();
        }

        private KodiResponse ParseEnvelope(string body, string method)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return ParseResultOrError(document.RootElement);
            }
            catch (JsonException ex)
            {
                _log($"Could not parse Kodi response for {method}: {ex.Message}");
                return KodiResponse.Fail($"Malformed response: {ex.Message}", isConnectionError: true);
            }
        }

        private KodiResponse ParseResultOrError(JsonElement envelope)
        {
            if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("error", out var error))
            {
                string message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString() ?? error.ToString()
                    : error.ToString();
                _log($"Kodi returned an error: {message}");
                return KodiResponse.Fail(message, isConnectionError: false);
            }

            if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("result", out var result))
            {
                return KodiResponse.Ok(result.Clone());
            }

            return KodiResponse.Fail("Response contained neither a result nor an error.", isConnectionError: false);
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
