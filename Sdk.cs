using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LinuxDoSpace;

public static class Suffix
{
    public const string LinuxDoSpace = "linuxdo.space";
}

public class LinuxDoSpaceException : Exception
{
    public LinuxDoSpaceException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class AuthenticationException : LinuxDoSpaceException
{
    public AuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class StreamException : LinuxDoSpaceException
{
    public StreamException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class MailMessage
{
    public required string Address { get; init; }
    public required string Sender { get; init; }
    public required IReadOnlyList<string> Recipients { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required string Subject { get; init; }
    public string? MessageId { get; init; }
    public DateTimeOffset? Date { get; init; }
    public required string FromHeader { get; init; }
    public required string ToHeader { get; init; }
    public required string CcHeader { get; init; }
    public required string ReplyToHeader { get; init; }
    public required IReadOnlyList<string> FromAddresses { get; init; }
    public required IReadOnlyList<string> ToAddresses { get; init; }
    public required IReadOnlyList<string> CcAddresses { get; init; }
    public required IReadOnlyList<string> ReplyToAddresses { get; init; }
    public required string Text { get; init; }
    public required string Html { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Raw { get; init; }
    public required byte[] RawBytes { get; init; }
}

public sealed class MailBox : IAsyncDisposable
{
    private readonly Action _unbind;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>();
    private int _listenState;
    private bool _closed;
    private bool _listenActivated;

    internal MailBox(string mode, string suffix, bool allowOverlap, string? prefix, string? pattern, Action unbind)
    {
        Mode = mode;
        Suffix = suffix;
        AllowOverlap = allowOverlap;
        Prefix = prefix;
        Pattern = pattern;
        Address = prefix is null ? null : $"{prefix}@{suffix}";
        _unbind = unbind;
    }

    public string Mode { get; }
    public string Suffix { get; }
    public bool AllowOverlap { get; }
    public string? Prefix { get; }
    public string? Pattern { get; }
    public string? Address { get; }
    public bool Closed => _closed;

    public async IAsyncEnumerable<MailMessage> ListenAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_closed) throw new LinuxDoSpaceException("mailbox is already closed");
        if (Interlocked.CompareExchange(ref _listenState, 1, 0) != 0) throw new LinuxDoSpaceException("mailbox already has an active listener");
        _listenActivated = true;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    if (ReferenceEquals(item, Client.CloseSignal)) yield break;
                    if (item is LinuxDoSpaceException ex) throw ex;
                    if (item is MailMessage message) yield return message;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _listenState, 0);
        }
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public ValueTask CloseAsync()
    {
        if (_closed) return ValueTask.CompletedTask;
        _closed = true;
        _unbind();
        _queue.Writer.TryWrite(Client.CloseSignal);
        return ValueTask.CompletedTask;
    }

    internal void Enqueue(MailMessage message)
    {
        if (_closed || !_listenActivated) return;
        _queue.Writer.TryWrite(message);
    }

    internal void EnqueueControl(object item) => _queue.Writer.TryWrite(item);
}

public sealed class Client : IAsyncDisposable
{
    private sealed class Binding
    {
        public required string Mode { get; init; }
        public required string Suffix { get; init; }
        public required bool AllowOverlap { get; init; }
        public string? Prefix { get; init; }
        public Regex? Regex { get; init; }
        public required MailBox MailBox { get; init; }
        public bool Matches(string localPart) => Mode == "exact" ? Prefix == localPart : Regex?.IsMatch(localPart) == true;
    }

    internal static readonly object CloseSignal = new();

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _token;
    private readonly Uri _baseUri;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _streamTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<object> _fullQueue = Channel.CreateUnbounded<object>();
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Binding>> _bindings = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readerTask;
    private bool _closed;

    public Client(string token, string baseUrl = "https://api.linuxdo.space", TimeSpan? connectTimeout = null, TimeSpan? streamTimeout = null)
    {
        _token = token.Trim();
        if (_token.Length == 0) throw new ArgumentException("token must not be empty");
        _baseUri = new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        _streamTimeout = streamTimeout ?? TimeSpan.FromSeconds(30);
        _readerTask = Task.Run(ReadLoopAsync);
        if (!_firstConnect.Task.Wait(_connectTimeout + TimeSpan.FromSeconds(1)))
        {
            _ = CloseAsync();
            throw new StreamException("timed out while opening LinuxDoSpace stream");
        }
        if (_firstConnect.Task.IsFaulted) throw _firstConnect.Task.Exception?.InnerException ?? new StreamException("initial stream connection failed");
    }

    public async IAsyncEnumerable<MailMessage> ListenAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _fullQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_fullQueue.Reader.TryRead(out var item))
            {
                if (ReferenceEquals(item, CloseSignal)) yield break;
                if (item is LinuxDoSpaceException ex) throw ex;
                if (item is MailMessage message) yield return message;
            }
        }
    }

    public MailBox Bind(string? prefix = null, string? pattern = null, string suffix = Suffix.LinuxDoSpace, bool allowOverlap = false)
    {
        var hasPrefix = !string.IsNullOrWhiteSpace(prefix);
        var hasPattern = !string.IsNullOrWhiteSpace(pattern);
        if (hasPrefix == hasPattern) throw new ArgumentException("exactly one of prefix or pattern must be provided");
        var normalizedSuffix = suffix.Trim().ToLowerInvariant();
        var normalizedPrefix = hasPrefix ? prefix!.Trim().ToLowerInvariant() : null;
        var mode = hasPrefix ? "exact" : "pattern";
        var regex = hasPattern ? new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant | RegexOptions.Compiled) : null;

        Binding? bindingRef = null;
        var mailbox = new MailBox(mode, normalizedSuffix, allowOverlap, normalizedPrefix, hasPattern ? pattern : null, () =>
        {
            lock (_lock)
            {
                if (bindingRef is null) return;
                if (!_bindings.TryGetValue(normalizedSuffix, out var list)) return;
                list.RemoveAll(v => ReferenceEquals(v, bindingRef));
                if (list.Count == 0) _bindings.Remove(normalizedSuffix);
            }
        });
        bindingRef = new Binding { Mode = mode, Suffix = normalizedSuffix, AllowOverlap = allowOverlap, Prefix = normalizedPrefix, Regex = regex, MailBox = mailbox };

        lock (_lock)
        {
            if (!_bindings.TryGetValue(normalizedSuffix, out var list))
            {
                list = [];
                _bindings[normalizedSuffix] = list;
            }
            list.Add(bindingRef);
        }
        return mailbox;
    }

    public IReadOnlyList<MailBox> Route(MailMessage message) => MatchForAddress(message.Address).Select(v => v.MailBox).ToArray();

    public ValueTask DisposeAsync() => CloseAsync();

    public async ValueTask CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _fullQueue.Writer.TryWrite(CloseSignal);
        List<MailBox> mailboxes;
        lock (_lock)
        {
            mailboxes = _bindings.Values.SelectMany(v => v.Select(b => b.MailBox)).Distinct().ToList();
            _bindings.Clear();
        }
        foreach (var mailbox in mailboxes) await mailbox.CloseAsync().ConfigureAwait(false);
        try { await _readerTask.ConfigureAwait(false); } catch { }
    }

    private async Task ReadLoopAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ConsumeOnceAsync(_cts.Token).ConfigureAwait(false);
                if (first) { first = false; _firstConnect.TrySetResult(true); }
            }
            catch (AuthenticationException ex)
            {
                if (first) _firstConnect.TrySetException(ex);
                BroadcastControl(ex);
                return;
            }
            catch (Exception ex)
            {
                var wrapped = ex as LinuxDoSpaceException ?? new StreamException("stream failure", ex);
                if (first) { _firstConnect.TrySetException(wrapped); return; }
                BroadcastControl(wrapped);
            }
            try { await Task.Delay(300, _cts.Token).ConfigureAwait(false); } catch { return; }
        }
    }

    private async Task ConsumeOnceAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/v1/token/email/stream"));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.ParseAdd("application/x-ndjson");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403) throw new AuthenticationException("api token was rejected by backend");
        if (!resp.IsSuccessStatusCode) throw new StreamException($"unexpected stream status code: {(int)resp.StatusCode}");
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, true);
        while (!cancellationToken.IsCancellationRequested)
        {
            var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
            var done = await Task.WhenAny(readTask, Task.Delay(_streamTimeout, cancellationToken)).ConfigureAwait(false);
            if (done != readTask) throw new StreamException("mail stream stalled and will reconnect");
            var line = await readTask.ConfigureAwait(false);
            if (line is null) return;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            HandleLine(trimmed);
        }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("type", out var typeNode)) throw new StreamException("event missing type");
        var type = typeNode.GetString() ?? string.Empty;
        if (type is "ready" or "heartbeat") return;
        if (type != "mail") return;
        DispatchMail(doc.RootElement);
    }

    private void DispatchMail(JsonElement payload)
    {
        var recipients = payload.TryGetProperty("original_recipients", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(v => (v.GetString() ?? string.Empty).Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToArray()
            : [];
        var rawBase64 = payload.TryGetProperty("raw_message_base64", out var rawNode) ? (rawNode.GetString() ?? string.Empty).Trim() : string.Empty;
        if (rawBase64.Length == 0) throw new StreamException("mail event missing raw_message_base64");
        byte[] rawBytes;
        try { rawBytes = Convert.FromBase64String(rawBase64); } catch (Exception ex) { throw new StreamException("invalid mail base64", ex); }
        var raw = Encoding.UTF8.GetString(rawBytes);
        var headers = ParseHeaders(raw, out var body);
        var receivedAtText = payload.TryGetProperty("received_at", out var t) ? (t.GetString() ?? string.Empty).Trim() : string.Empty;
        if (!DateTimeOffset.TryParse(receivedAtText, out var receivedAt)) throw new StreamException($"invalid mail timestamp: {receivedAtText}");
        var sender = payload.TryGetProperty("original_envelope_from", out var s) ? (s.GetString() ?? string.Empty).Trim() : string.Empty;

        var primary = recipients.FirstOrDefault() ?? string.Empty;
        _fullQueue.Writer.TryWrite(BuildMessage(primary, sender, recipients, receivedAt, raw, rawBytes, headers, body));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipient in recipients.Where(seen.Add))
        {
            var msg = BuildMessage(recipient, sender, recipients, receivedAt, raw, rawBytes, headers, body);
            foreach (var binding in MatchForAddress(recipient)) binding.MailBox.Enqueue(msg);
        }
    }

    private List<Binding> MatchForAddress(string address)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var chunks = normalized.Split('@', 2);
        if (chunks.Length != 2 || chunks[0].Length == 0 || chunks[1].Length == 0) return [];
        var localPart = chunks[0];
        var suffix = chunks[1];
        List<Binding> chain;
        lock (_lock) chain = _bindings.TryGetValue(suffix, out var list) ? [.. list] : [];
        var matched = new List<Binding>();
        foreach (var binding in chain)
        {
            if (!binding.Matches(localPart)) continue;
            matched.Add(binding);
            if (!binding.AllowOverlap) break;
        }
        return matched;
    }

    private void BroadcastControl(LinuxDoSpaceException error)
    {
        _fullQueue.Writer.TryWrite(error);
        List<MailBox> boxes;
        lock (_lock) boxes = _bindings.Values.SelectMany(v => v.Select(b => b.MailBox)).Distinct().ToList();
        foreach (var box in boxes) box.EnqueueControl(error);
    }

    private static MailMessage BuildMessage(string address, string sender, IReadOnlyList<string> recipients, DateTimeOffset receivedAt, string raw, byte[] rawBytes, Dictionary<string, string> headers, string body)
    {
        var contentType = GetHeader(headers, "Content-Type").ToLowerInvariant();
        var text = contentType.Contains("text/html", StringComparison.Ordinal) ? string.Empty : body.Trim();
        var html = contentType.Contains("text/html", StringComparison.Ordinal) ? body.Trim() : string.Empty;
        DateTimeOffset? parsedDate = DateTimeOffset.TryParse(GetHeader(headers, "Date"), out var dateValue) ? dateValue : null;
        return new MailMessage
        {
            Address = address,
            Sender = sender,
            Recipients = recipients,
            ReceivedAt = receivedAt,
            Subject = GetHeader(headers, "Subject"),
            MessageId = GetOptionalHeader(headers, "Message-ID"),
            Date = parsedDate,
            FromHeader = GetHeader(headers, "From"),
            ToHeader = GetHeader(headers, "To"),
            CcHeader = GetHeader(headers, "Cc"),
            ReplyToHeader = GetHeader(headers, "Reply-To"),
            FromAddresses = ParseAddressList(GetHeader(headers, "From")),
            ToAddresses = ParseAddressList(GetHeader(headers, "To")),
            CcAddresses = ParseAddressList(GetHeader(headers, "Cc")),
            ReplyToAddresses = ParseAddressList(GetHeader(headers, "Reply-To")),
            Text = text,
            Html = html,
            Headers = headers,
            Raw = raw,
            RawBytes = rawBytes
        };
    }

    private static Dictionary<string, string> ParseHeaders(string raw, out string body)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split >= 0 ? normalized[..split] : normalized;
        body = split >= 0 ? normalized[(split + 2)..] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? active = null;
        foreach (var line in head.Split('\n'))
        {
            if (line.Length == 0) continue;
            if ((line.StartsWith(" ") || line.StartsWith("\t")) && active is not null) { headers[active] = $"{headers[active]} {line.Trim()}"; continue; }
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            headers[key] = value;
            active = key;
        }
        return headers;
    }

    private static string[] ParseAddressList(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return [];
        var result = new List<string>();
        foreach (var part in headerValue.Split(','))
        {
            var chunk = part.Trim();
            var lt = chunk.IndexOf('<');
            var gt = chunk.IndexOf('>');
            var candidate = lt >= 0 && gt > lt ? chunk[(lt + 1)..gt] : chunk;
            candidate = candidate.Trim().ToLowerInvariant();
            if (candidate.Contains('@')) result.Add(candidate);
        }
        return [.. result];
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var value = baseUrl.Trim().TrimEnd('/');
        if (value.Length == 0) throw new ArgumentException("base_url must not be empty");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new ArgumentException("base_url must be absolute");
        if (uri.Scheme is not ("https" or "http")) throw new ArgumentException("base_url must use http or https");
        if (uri.Scheme == "http")
        {
            var host = uri.Host.Trim().ToLowerInvariant();
            var isLocal = host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);
            if (!isLocal) throw new ArgumentException("non-local base_url must use https");
        }
        return value;
    }

    private static string GetHeader(IReadOnlyDictionary<string, string> headers, string key) => headers.TryGetValue(key, out var value) ? value : string.Empty;
    private static string? GetOptionalHeader(IReadOnlyDictionary<string, string> headers, string key) => headers.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value : null;
}
