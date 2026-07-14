using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AshenVoice;

internal sealed class DiscordRpcClient : IAsyncDisposable
{
    private readonly string _clientId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, DiscordVoiceMember> _voiceMemberCache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private string? _subscribedChannelId;

    public DiscordRpcClient(string clientId)
    {
        _clientId = clientId;
    }

    public event Action<string>? Log;
    public event Action<DiscordRpcUser>? Authenticated;
    public event Action<DiscordVoiceChannel?>? VoiceChannelChanged;
    public event Action<DiscordVoiceMember>? VoiceMemberUpserted;
    public event Action<string>? VoiceMemberRemoved;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<string>? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAndAuthenticateAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (!DiscordBuildConfig.IsConfigured)
        {
            throw new InvalidOperationException("Discord Application ID is not configured in this build.");
        }

        _pipe = await ConnectPipeAsync(cancellationToken);
        Log?.Invoke("Connected to the local Discord desktop client.");

        await WriteFrameAsync(0, new { v = 1, client_id = _clientId }, cancellationToken);
        DiscordFrame readyFrame = await ReadFrameAsync(_pipe, cancellationToken);
        if (readyFrame.Opcode != 1 || !IsDispatch(readyFrame.Payload, "READY"))
        {
            throw new InvalidOperationException("Discord did not accept the Ashen Voice RPC handshake.");
        }

        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);

        JsonElement authData = await SendRequestAsync(
            "AUTHENTICATE",
            new { access_token = accessToken },
            cancellationToken);

        DiscordRpcUser user = ParseUser(authData.TryGetProperty("user", out JsonElement userElement)
            ? userElement
            : authData);
        Authenticated?.Invoke(user);

        await SubscribeAsync("VOICE_CHANNEL_SELECT", new { }, cancellationToken);
        await RefreshSelectedVoiceChannelAsync(cancellationToken);
    }

    public async Task RefreshSelectedVoiceChannelAsync(CancellationToken cancellationToken)
    {
        JsonElement data = await SendRequestAsync("GET_SELECTED_VOICE_CHANNEL", new { }, cancellationToken);
        DiscordVoiceChannel? channel = ParseChannel(data);
        await SwitchChannelAsync(channel, cancellationToken);
    }

    private async Task SwitchChannelAsync(DiscordVoiceChannel? channel, CancellationToken cancellationToken)
    {
        string? oldChannel = _subscribedChannelId;
        if (!string.IsNullOrWhiteSpace(oldChannel) && !string.Equals(oldChannel, channel?.Id, StringComparison.Ordinal))
        {
            foreach (string evt in ChannelEvents)
            {
                await TryUnsubscribeAsync(evt, oldChannel, cancellationToken);
            }
        }

        _subscribedChannelId = channel?.Id;
        _voiceMemberCache.Clear();

        if (channel is null)
        {
            VoiceChannelChanged?.Invoke(null);
            return;
        }

        foreach (string evt in ChannelEvents)
        {
            await SubscribeAsync(evt, new { channel_id = channel.Id }, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(channel.GuildId))
        {
            try
            {
                JsonElement guildData = await SendRequestAsync(
                    "GET_GUILD",
                    new { guild_id = channel.GuildId },
                    cancellationToken);
                channel = channel with
                {
                    GuildName = GetString(guildData, "name") ?? channel.GuildName
                };
            }
            catch (Exception exception)
            {
                Log?.Invoke($"Discord server name was unavailable: {exception.Message}");
            }
        }

        foreach (DiscordVoiceMember member in channel.Members)
        {
            _voiceMemberCache[member.UserId] = member;
        }

        VoiceChannelChanged?.Invoke(channel);
        foreach (DiscordVoiceMember member in channel.Members)
        {
            VoiceMemberUpserted?.Invoke(member);
        }
    }

    private async Task SubscribeAsync(string evt, object args, CancellationToken cancellationToken)
    {
        await SendRequestAsync("SUBSCRIBE", args, cancellationToken, evt);
    }

    private async Task TryUnsubscribeAsync(string evt, string channelId, CancellationToken cancellationToken)
    {
        try
        {
            await SendRequestAsync("UNSUBSCRIBE", new { channel_id = channelId }, cancellationToken, evt);
        }
        catch
        {
            // Discord may have already removed subscriptions after a channel change.
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _pipe?.IsConnected == true)
            {
                DiscordFrame frame = await ReadFrameAsync(_pipe, cancellationToken);
                switch (frame.Opcode)
                {
                    case 1:
                        HandlePayload(frame.Payload);
                        break;
                    case 2:
                        throw new IOException("Discord closed the local RPC connection.");
                    case 3:
                        await WriteRawFrameAsync(4, frame.PayloadBytes, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            Disconnected?.Invoke(exception.Message);
        }
    }

    private void HandlePayload(JsonElement root)
    {
        string? nonce = GetString(root, "nonce");
        string? evt = GetString(root, "evt");

        if (evt == "ERROR")
        {
            string message = root.TryGetProperty("data", out JsonElement errorData)
                ? GetString(errorData, "message") ?? "Discord RPC returned an error."
                : "Discord RPC returned an error.";
            int? code = root.TryGetProperty("data", out errorData) &&
                        errorData.TryGetProperty("code", out JsonElement codeElement) &&
                        codeElement.TryGetInt32(out int parsedCode)
                ? parsedCode
                : null;
            var exception = new DiscordRpcException(code, message);

            if (!string.IsNullOrWhiteSpace(nonce) && _pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? pending))
            {
                pending.TrySetException(exception);
            }
            else
            {
                Log?.Invoke($"Discord RPC error{(code is null ? string.Empty : $" {code}")}: {message}");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(nonce) && _pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? completion))
        {
            JsonElement data = root.TryGetProperty("data", out JsonElement responseData)
                ? responseData.Clone()
                : JsonSerializer.SerializeToElement<object?>(null);
            completion.TrySetResult(data);
            return;
        }

        if (!string.Equals(GetString(root, "cmd"), "DISPATCH", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(evt))
        {
            return;
        }

        JsonElement dataElement = root.TryGetProperty("data", out JsonElement dispatchData)
            ? dispatchData
            : default;

        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshSelectedVoiceChannelAsync(_shutdown.Token);
                    }
                    catch (Exception exception)
                    {
                        Log?.Invoke($"Could not refresh the selected voice channel: {exception.Message}");
                    }
                });
                break;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                string? updatedId = GetVoiceStateUserId(dataElement);
                DiscordVoiceMember? existing = !string.IsNullOrWhiteSpace(updatedId) &&
                                               _voiceMemberCache.TryGetValue(updatedId, out DiscordVoiceMember? cached)
                    ? cached
                    : null;
                DiscordVoiceMember? member = ParseVoiceMember(dataElement, existing);
                if (member is not null)
                {
                    _voiceMemberCache[member.UserId] = member;
                    VoiceMemberUpserted?.Invoke(member);
                }
                break;

            case "VOICE_STATE_DELETE":
                string? removedId = GetVoiceStateUserId(dataElement);
                if (!string.IsNullOrWhiteSpace(removedId))
                {
                    _voiceMemberCache.TryRemove(removedId, out _);
                    VoiceMemberRemoved?.Invoke(removedId);
                }
                break;

            case "SPEAKING_START":
            case "SPEAKING_STOP":
                string? userId = GetString(dataElement, "user_id");
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    SpeakingChanged?.Invoke(userId, evt == "SPEAKING_START");
                }
                break;
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string command,
        object args,
        CancellationToken cancellationToken,
        string? evt = null)
    {
        string nonce = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(nonce, completion))
        {
            throw new InvalidOperationException("Could not register a Discord RPC request.");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        object payload = evt is null
            ? new { cmd = command, args, nonce }
            : new { cmd = command, args, evt, nonce };

        try
        {
            await WriteFrameAsync(1, payload, cancellationToken);
            return await completion.Task;
        }
        catch
        {
            _pending.TryRemove(nonce, out _);
            throw;
        }
    }

    private async Task WriteFrameAsync(int opcode, object payload, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteRawFrameAsync(opcode, json, cancellationToken);
    }

    private async Task WriteRawFrameAsync(int opcode, byte[] json, CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = _pipe ?? throw new InvalidOperationException("Discord RPC is not connected.");
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), json.Length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(header, cancellationToken);
            await pipe.WriteAsync(json, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<DiscordFrame> ReadFrameAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await ReadExactlyAsync(pipe, header, cancellationToken);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length < 0 || length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Discord sent an invalid RPC frame length.");
        }

        byte[] payloadBytes = new byte[length];
        await ReadExactlyAsync(pipe, payloadBytes, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(payloadBytes);
        return new DiscordFrame(opcode, document.RootElement.Clone(), payloadBytes);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Discord closed the local RPC pipe.");
            }
            offset += read;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                $"discord-ipc-{index}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(350, cancellationToken);
                return pipe;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                lastError = exception;
                pipe.Dispose();
            }
        }

        throw new InvalidOperationException(
            "Could not connect to the Discord desktop app. Make sure Discord is open, then try again.",
            lastError);
    }

    private static bool IsDispatch(JsonElement payload, string eventName) =>
        string.Equals(GetString(payload, "cmd"), "DISPATCH", StringComparison.Ordinal) &&
        string.Equals(GetString(payload, "evt"), eventName, StringComparison.Ordinal);

    private static DiscordRpcUser ParseUser(JsonElement user) => new(
        GetString(user, "id") ?? string.Empty,
        GetString(user, "global_name") ?? GetString(user, "username") ?? "Discord user",
        GetString(user, "avatar"));

    private static DiscordVoiceChannel? ParseChannel(JsonElement data)
    {
        if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string? id = GetString(data, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var members = new List<DiscordVoiceMember>();
        if (data.TryGetProperty("voice_states", out JsonElement states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement state in states.EnumerateArray())
            {
                DiscordVoiceMember? member = ParseVoiceMember(state);
                if (member is not null)
                {
                    members.Add(member);
                }
            }
        }

        return new DiscordVoiceChannel(
            id,
            GetString(data, "guild_id") ?? string.Empty,
            GetString(data, "name") ?? "Voice channel",
            string.Empty,
            members);
    }

    private static DiscordVoiceMember? ParseVoiceMember(
        JsonElement state,
        DiscordVoiceMember? existing = null)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool hasUser = state.TryGetProperty("user", out JsonElement user) &&
                       user.ValueKind == JsonValueKind.Object;
        string? userId = hasUser ? GetString(user, "id") : GetString(state, "user_id");
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        string? parsedName = GetString(state, "nick");
        if (string.IsNullOrWhiteSpace(parsedName) && hasUser)
        {
            parsedName = GetString(user, "global_name") ?? GetString(user, "username");
        }

        string displayName = !string.IsNullOrWhiteSpace(parsedName)
            ? parsedName
            : existing?.DisplayName ?? "Discord user";
        string? avatarHash = hasUser ? GetString(user, "avatar") : null;
        avatarHash ??= existing?.AvatarHash;

        bool botFlagPresent = hasUser &&
                              user.TryGetProperty("bot", out JsonElement botElement) &&
                              botElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool isBot = existing?.IsBot == true ||
                     (botFlagPresent && botElement.ValueKind == JsonValueKind.True);

        return new DiscordVoiceMember(userId, displayName, avatarHash, isBot);
    }

    private static string? GetVoiceStateUserId(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (state.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object)
        {
            string? userId = GetString(user, "id");
            if (!string.IsNullOrWhiteSpace(userId))
            {
                return userId;
            }
        }

        return GetString(state, "user_id");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private void FailPending(Exception exception)
    {
        foreach ((string nonce, TaskCompletionSource<JsonElement> completion) in _pending)
        {
            if (_pending.TryRemove(nonce, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            if (_readLoop is not null)
            {
                await _readLoop;
            }
        }
        catch
        {
        }

        FailPending(new ObjectDisposedException(nameof(DiscordRpcClient)));
        _voiceMemberCache.Clear();
        _pipe?.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    private static readonly string[] ChannelEvents =
    {
        "SPEAKING_START",
        "SPEAKING_STOP",
        "VOICE_STATE_CREATE",
        "VOICE_STATE_UPDATE",
        "VOICE_STATE_DELETE"
    };

    private sealed record DiscordFrame(int Opcode, JsonElement Payload, byte[] PayloadBytes);
}

internal sealed class DiscordRpcException : Exception
{
    public DiscordRpcException(int? code, string message) : base(message)
    {
        Code = code;
    }

    public int? Code { get; }
}

internal sealed record DiscordRpcUser(string Id, string DisplayName, string? AvatarHash);

internal sealed record DiscordVoiceMember(string UserId, string DisplayName, string? AvatarHash, bool IsBot);

internal sealed record DiscordVoiceChannel(
    string Id,
    string GuildId,
    string Name,
    string GuildName,
    IReadOnlyList<DiscordVoiceMember> Members);
