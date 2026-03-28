using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LinuxDoSpace;

public static class Suffix
{
    // LinuxDoSpace is semantic rather than literal: SDK bindings resolve it to
    // the current token owner's canonical `@<owner>-mail.linuxdo.space`
    // namespace after the stream ready event exposes owner_username.
    public const string LinuxDoSpace = "linuxdo.space";

    public static SemanticSuffix WithSuffix(string fragment) => new(LinuxDoSpace, fragment);
}

public sealed class SemanticSuffix
{
    public SemanticSuffix(string baseSuffix, string fragment)
    {
        Base = (baseSuffix ?? string.Empty).Trim().ToLowerInvariant();
        if (Base.Length == 0)
        {
            throw new ArgumentException("base suffix must not be empty");
        }
        MailSuffixFragment = NormalizeMailSuffixFragment(fragment);
    }

    public string Base { get; }

    public string MailSuffixFragment { get; }

    public SemanticSuffix WithSuffix(string fragment) => new(Base, fragment);

    private static string NormalizeMailSuffixFragment(string raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var character in value)
        {
            if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
            {
                builder.Append(character);
                lastWasDash = false;
                continue;
            }

            if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("mail suffix fragment does not contain any valid dns characters");
        }
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("mail suffix fragment must stay inside one dns label");
        }
        if (normalized.Length > 48)
        {
            throw new ArgumentException("mail suffix fragment must be 48 characters or fewer");
        }
        return normalized;
    }
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
            _listenActivated = false;
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
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Binding>> _bindings = new(StringComparer.Ordinal);
    private readonly List<Channel<object>> _fullListeners = [];
    private readonly TaskCompletionSource<bool> _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stream? _activeStream;
    private Task _readerTask;
    private bool _closed;
    private LinuxDoSpaceException? _fatalError;
    private string? _ownerUsername;
    private string[]? _syncedMailboxSuffixFragments;

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
        AssertUsable();

        var queue = Channel.CreateUnbounded<object>();
        lock (_lock)
        {
            AssertUsable();
            _fullListeners.Add(queue);
        }

        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var item))
                {
                    if (ReferenceEquals(item, CloseSignal)) yield break;
                    if (item is LinuxDoSpaceException ex) throw ex;
                    if (item is MailMessage message) yield return message;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _fullListeners.Remove(queue);
            }
            queue.Writer.TryWrite(CloseSignal);
        }
    }

    public MailBox Bind(string? prefix = null, string? pattern = null, string suffix = Suffix.LinuxDoSpace, bool allowOverlap = false)
    {
        return Bind(prefix, pattern, ResolveBindingSuffixInput(suffix), allowOverlap);
    }

    public MailBox Bind(string? prefix = null, string? pattern = null, SemanticSuffix? suffix = null, bool allowOverlap = false)
    {
        return Bind(prefix, pattern, ResolveBindingSuffixInput(suffix ?? new SemanticSuffix(Suffix.LinuxDoSpace, string.Empty)), allowOverlap);
    }

    private MailBox Bind(string? prefix, string? pattern, (string ResolvedSuffix, string? MailSuffixFragment) resolvedSuffix, bool allowOverlap)
    {
        AssertUsable();
        var hasPrefix = !string.IsNullOrWhiteSpace(prefix);
        var hasPattern = !string.IsNullOrWhiteSpace(pattern);
        if (hasPrefix == hasPattern) throw new ArgumentException("exactly one of prefix or pattern must be provided");
        var normalizedSuffix = resolvedSuffix.ResolvedSuffix;
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
            _ = TrySyncRemoteMailboxFilters(strict: false);
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
        try
        {
            TrySyncRemoteMailboxFilters(strict: true);
        }
        catch
        {
            mailbox.CloseAsync().GetAwaiter().GetResult();
            throw;
        }
        return mailbox;
    }

    public IReadOnlyList<MailBox> Route(MailMessage message)
    {
        AssertUsable();
        return MatchForAddress(message.Address).Select(v => v.MailBox).ToArray();
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public async ValueTask CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        CloseActiveStream();
        BroadcastControl(CloseSignal);
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
                first = false;
            }
            catch (AuthenticationException ex)
            {
                if (first) _firstConnect.TrySetException(ex);
                FailAll(ex);
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
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.ParseAdd("application/x-ndjson");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403) throw new AuthenticationException("api token was rejected by backend");
        if (!resp.IsSuccessStatusCode) throw new StreamException($"unexpected stream status code: {(int)resp.StatusCode}");
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _activeStream = stream;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, true);
        while (!cancellationToken.IsCancellationRequested)
        {
            var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
            var done = await Task.WhenAny(readTask, Task.Delay(_streamTimeout, cancellationToken)).ConfigureAwait(false);
            if (done != readTask) throw new StreamException("mail stream stalled and will reconnect");
            var line = await readTask.ConfigureAwait(false);
            if (line is null)
            {
                if (!_closed && !_firstConnect.Task.IsCompleted)
                {
                    throw new StreamException("mail stream ended before ready event");
                }
                return;
            }
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            HandleLine(trimmed);
        }
        lock (_lock)
        {
            if (ReferenceEquals(_activeStream, stream))
            {
                _activeStream = null;
            }
        }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("type", out var typeNode)) throw new StreamException("event missing type");
        var type = typeNode.GetString() ?? string.Empty;
        if (type == "ready")
        {
            HandleReady(doc.RootElement);
            return;
        }
        if (type == "heartbeat") return;
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
        BroadcastFull(BuildMessage(primary, sender, recipients, receivedAt, raw, rawBytes, headers, body));
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
        lock (_lock)
        {
            chain = _bindings.TryGetValue(suffix, out var list) ? [.. list] : [];
            if (chain.Count == 0)
            {
                var ownerUsername = (_ownerUsername ?? string.Empty).Trim().ToLowerInvariant();
                if (ownerUsername.Length > 0)
                {
                var semanticDefaultSuffix = $"{ownerUsername}.{Suffix.LinuxDoSpace}";
                var semanticMailSuffix = $"{ownerUsername}-mail.{Suffix.LinuxDoSpace}";
                if (suffix == semanticDefaultSuffix)
                {
                    chain = _bindings.TryGetValue(semanticMailSuffix, out var semanticList) ? [.. semanticList] : [];
                }
            }
        }
        }
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
        BroadcastControl((object)error);
        List<MailBox> boxes;
        lock (_lock) boxes = _bindings.Values.SelectMany(v => v.Select(b => b.MailBox)).Distinct().ToList();
        foreach (var box in boxes) box.EnqueueControl(error);
    }

    private void BroadcastFull(MailMessage message)
    {
        List<Channel<object>> listeners;
        lock (_lock)
        {
            listeners = [.. _fullListeners];
        }
        foreach (var listener in listeners)
        {
            listener.Writer.TryWrite(message);
        }
    }

    private void BroadcastControl(object item)
    {
        List<Channel<object>> listeners;
        lock (_lock)
        {
            listeners = [.. _fullListeners];
            if (ReferenceEquals(item, CloseSignal))
            {
                _fullListeners.Clear();
            }
        }
        foreach (var listener in listeners)
        {
            listener.Writer.TryWrite(item);
        }
    }

    private void MarkInitialReady()
    {
        if (_firstConnect.Task.IsCompleted)
        {
            return;
        }
        _firstConnect.TrySetResult(true);
    }

    private void HandleReady(JsonElement payload)
    {
        if (!payload.TryGetProperty("owner_username", out var ownerNode))
        {
            throw new StreamException("ready event did not include owner_username");
        }

        var normalizedOwnerUsername = (ownerNode.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedOwnerUsername.Length == 0)
        {
            throw new StreamException("ready event did not include owner_username");
        }

        _ownerUsername = normalizedOwnerUsername;
        TrySyncRemoteMailboxFilters(strict: true);
        MarkInitialReady();
    }

    private (string ResolvedSuffix, string? MailSuffixFragment) ResolveBindingSuffixInput(string suffix)
    {
        var normalizedSuffix = suffix.Trim().ToLowerInvariant();
        if (normalizedSuffix.Length == 0)
        {
            throw new ArgumentException("suffix must not be empty");
        }
        if (!string.Equals(normalizedSuffix, Suffix.LinuxDoSpace, StringComparison.Ordinal))
        {
            return (normalizedSuffix, null);
        }

        var ownerUsername = (_ownerUsername ?? string.Empty).Trim().ToLowerInvariant();
        if (ownerUsername.Length == 0)
        {
            throw new StreamException("stream bootstrap did not provide owner_username required to resolve Suffix.LinuxDoSpace");
        }
        return ($"{ownerUsername}-mail.{normalizedSuffix}", string.Empty);
    }

    private (string ResolvedSuffix, string? MailSuffixFragment) ResolveBindingSuffixInput(SemanticSuffix suffix)
    {
        if (!string.Equals(suffix.Base, Suffix.LinuxDoSpace, StringComparison.Ordinal))
        {
            return (suffix.Base, null);
        }

        var ownerUsername = (_ownerUsername ?? string.Empty).Trim().ToLowerInvariant();
        if (ownerUsername.Length == 0)
        {
            var message = suffix.MailSuffixFragment.Length == 0
                ? "stream bootstrap did not provide owner_username required to resolve Suffix.LinuxDoSpace"
                : "stream bootstrap did not provide owner_username required to resolve Suffix.WithSuffix(...)";
            throw new StreamException(message);
        }
        return ($"{ownerUsername}-mail{suffix.MailSuffixFragment}.{suffix.Base}", suffix.MailSuffixFragment);
    }

    private void TrySyncRemoteMailboxFilters(bool strict)
    {
        var ownerUsername = (_ownerUsername ?? string.Empty).Trim().ToLowerInvariant();
        if (ownerUsername.Length == 0)
        {
            return;
        }

        var fragments = CollectRemoteMailboxSuffixFragments(ownerUsername);
        if (fragments.Length == 0 && _syncedMailboxSuffixFragments is null)
        {
            return;
        }
        if (_syncedMailboxSuffixFragments is not null && _syncedMailboxSuffixFragments.SequenceEqual(fragments))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_baseUri, "/v1/token/email/filters"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(new { suffixes = fragments });
        using var response = _http.Send(request, _cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            if (!strict)
            {
                return;
            }
            throw new StreamException($"unexpected mailbox filter sync status code: {(int)response.StatusCode}");
        }

        _ = response.Content.ReadAsStringAsync(_cts.Token).GetAwaiter().GetResult();
        _syncedMailboxSuffixFragments = fragments;
    }

    private string[] CollectRemoteMailboxSuffixFragments(string ownerUsername)
    {
        var fragments = new HashSet<string>(StringComparer.Ordinal);
        var canonicalPrefix = $"{ownerUsername}-mail";
        lock (_lock)
        {
            foreach (var suffix in _bindings.Keys)
            {
                var normalizedSuffix = suffix.Trim().ToLowerInvariant();
                var rootSuffix = $".{Suffix.LinuxDoSpace}";
                if (!normalizedSuffix.EndsWith(rootSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var label = normalizedSuffix[..^rootSuffix.Length];
                if (label.Contains('.', StringComparison.Ordinal) || !label.StartsWith(canonicalPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                fragments.Add(label[canonicalPrefix.Length..]);
            }
        }
        return fragments.OrderBy(static item => item, StringComparer.Ordinal).ToArray();
    }

    private void CloseActiveStream()
    {
        Stream? stream;
        lock (_lock)
        {
            stream = _activeStream;
            _activeStream = null;
        }
        stream?.Dispose();
    }

    private void FailAll(LinuxDoSpaceException error)
    {
        _fatalError = error;
        _closed = true;
        _cts.Cancel();
        CloseActiveStream();
        BroadcastControl(error);
    }

    private void AssertUsable()
    {
        if (_fatalError is not null)
        {
            throw _fatalError;
        }
        if (_closed)
        {
            throw new LinuxDoSpaceException("client is already closed");
        }
        if (_firstConnect.Task.IsFaulted)
        {
            throw _firstConnect.Task.Exception?.InnerException ?? new StreamException("initial stream connection failed");
        }
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
