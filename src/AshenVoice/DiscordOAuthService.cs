using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AshenVoice;

internal sealed class DiscordOAuthService
{
    private const int CallbackPort = 53682;
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(3);

    public async Task<DiscordOAuthToken> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (!DiscordBuildConfig.IsConfigured)
        {
            throw new InvalidOperationException(
                "Ashen Voice was built without a Discord Application ID. Set the DISCORD_CLIENT_ID repository variable and rebuild the installer.");
        }

        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);

        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"Ashen Voice could not open its local Discord sign-in callback on port {CallbackPort}. Close any other Ashen Voice window and try again.",
                exception);
        }

        string authorizeUrl = BuildAuthorizeUrl(state);
        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthorizationTimeout);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Discord authorization timed out. Click Connect Discord and approve the request again.");
            }

            await using NetworkStream stream = client.GetStream();
            HttpRequest request = await ReadRequestAsync(stream, timeout.Token);

            if (request.Path.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHtmlResponseAsync(stream, BuildCallbackPage(), timeout.Token);
                client.Dispose();
                continue;
            }

            if (request.Path.StartsWith("/token", StringComparison.OrdinalIgnoreCase) &&
                request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    OAuthBrowserPayload? payload = JsonSerializer.Deserialize<OAuthBrowserPayload>(request.Body);
                    if (!string.IsNullOrWhiteSpace(payload?.Error))
                    {
                        throw new InvalidOperationException($"Discord authorization failed: {payload.Error}");
                    }

                    if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                    {
                        throw new InvalidOperationException("Discord did not return an access token.");
                    }

                    if (!string.Equals(payload.State, state, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Discord authorization state validation failed.");
                    }

                    int expiresIn = payload.ExpiresIn > 0 ? payload.ExpiresIn : 604800;
                    var token = new DiscordOAuthToken
                    {
                        AccessToken = payload.AccessToken,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60)
                    };

                    await WriteTextResponseAsync(stream, 200, "Authorization complete. You may close this tab.", timeout.Token);
                    client.Dispose();
                    return token;
                }
                catch (Exception exception)
                {
                    await WriteTextResponseAsync(stream, 400, exception.Message, timeout.Token);
                    client.Dispose();
                    throw;
                }
            }

            await WriteTextResponseAsync(stream, 404, "Not found.", timeout.Token);
            client.Dispose();
        }
    }

    private static string BuildAuthorizeUrl(string state)
    {
        string scope = Uri.EscapeDataString("identify rpc rpc.voice.read");
        string redirect = Uri.EscapeDataString(DiscordBuildConfig.RedirectUri);
        return "https://discord.com/oauth2/authorize" +
               $"?client_id={Uri.EscapeDataString(DiscordBuildConfig.ClientId)}" +
               "&response_type=token" +
               $"&redirect_uri={redirect}" +
               $"&scope={scope}" +
               $"&state={Uri.EscapeDataString(state)}" +
               "&prompt=consent";
    }

    private static string BuildCallbackPage()
    {
        return """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Ashen Voice</title>
<style>
body{margin:0;background:#15171b;color:#f5f5f5;font-family:Segoe UI,Arial,sans-serif;display:grid;place-items:center;min-height:100vh}
.card{width:min(520px,88vw);background:#25282e;border-radius:14px;padding:32px;box-shadow:0 18px 50px #0008}
h1{color:#ff6d2e;margin:0 0 12px}.muted{color:#b9bbc2;line-height:1.5}.ok{color:#6df58a}.bad{color:#ff8080}
</style>
</head>
<body><div class="card"><h1>ASHEN VOICE</h1><p id="status" class="muted">Finishing Discord authorization...</p></div>
<script>
(async () => {
  const status = document.getElementById('status');
  const p = new URLSearchParams(location.hash.slice(1));
  const error = p.get('error_description') || p.get('error');
  if (error) {
    try { await fetch('/token', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({error:error, state:p.get('state') || ''})}); } catch (_) {}
    status.className='bad'; status.textContent='Discord authorization failed: ' + error; return;
  }
  const token = p.get('access_token');
  const state = p.get('state');
  const expires = parseInt(p.get('expires_in') || '604800', 10);
  if (!token || !state) { status.className='bad'; status.textContent='Discord did not return the required authorization data.'; return; }
  try {
    const response = await fetch('/token', {
      method: 'POST',
      headers: {'Content-Type':'application/json'},
      body: JSON.stringify({access_token: token, state: state, expires_in: expires})
    });
    const text = await response.text();
    if (!response.ok) throw new Error(text);
    history.replaceState(null, '', '/callback/');
    status.className='ok'; status.textContent='Discord connected. You may close this tab and return to Ashen Voice.';
  } catch (e) {
    status.className='bad'; status.textContent='Ashen Voice could not finish the connection: ' + e.message;
  }
})();
</script></body></html>
""";
    }

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                throw new IOException("The browser closed the authorization callback connection.");
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > 1024 * 1024)
            {
                throw new InvalidOperationException("The authorization callback was unexpectedly large.");
            }

            headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        byte[] bytes = buffer.ToArray();
        string headers = Encoding.UTF8.GetString(bytes, 0, headerEnd);
        string[] lines = headers.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            throw new InvalidOperationException("Invalid local authorization request.");
        }

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[(separator + 1)..].Trim(), out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        while (bytes.Length - bodyStart < contentLength)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            bytes = buffer.ToArray();
        }

        string body = contentLength > 0 && bytes.Length >= bodyStart + contentLength
            ? Encoding.UTF8.GetString(bytes, bodyStart, contentLength)
            : string.Empty;

        return new HttpRequest(requestLine[0], requestLine[1], body);
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private static Task WriteHtmlResponseAsync(NetworkStream stream, string html, CancellationToken cancellationToken) =>
        WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, cancellationToken);

    private static Task WriteTextResponseAsync(NetworkStream stream, int statusCode, string text, CancellationToken cancellationToken) =>
        WriteResponseAsync(stream, statusCode, statusCode == 200 ? "OK" : "Error", "text/plain; charset=utf-8", text, cancellationToken);

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed record HttpRequest(string Method, string Path, string Body);

    private sealed class OAuthBrowserPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

internal sealed class DiscordOAuthToken
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1);
}
