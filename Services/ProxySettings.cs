using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;

namespace CineLibraryCS.Services;

/// <summary>Shared proxy configuration for application-managed outbound HTTP(S) traffic.</summary>
public sealed record ProxyConfiguration(
    bool Enabled,
    string Type,
    string Host,
    int Port,
    bool UseAuthentication,
    string Username,
    string Password);

public static class ProxySettings
{
    private const string Prefix = "network_proxy_";
    public static bool Enabled { get; private set; }
    public static string Type { get; private set; } = "SOCKS5";
    public static string Host { get; private set; } = "127.0.0.1";
    public static int Port { get; private set; } = 1080;
    public static bool UseAuthentication { get; private set; }
    public static string Username { get; private set; } = "";
    public static string Password { get; private set; } = "";

    public static void Load()
    {
        var db = AppState.Instance;
        Enabled = db.GetPref(Prefix + "enabled", "false") == "true";
        Type = NormalizeType(db.GetPref(Prefix + "type", "SOCKS5"));
        Host = db.GetPref(Prefix + "host", "127.0.0.1");
        if (!int.TryParse(db.GetPref(Prefix + "port", Type == "HTTP" ? "8080" : "1080"), out var port) || port is < 1 or > 65535)
            port = Type == "HTTP" ? 8080 : 1080;
        Port = port;
        UseAuthentication = db.GetPref(Prefix + "auth", "false") == "true";
        Username = db.GetPref(Prefix + "username", "");
        Password = UnprotectPassword(db.GetPref(Prefix + "password", ""));
    }

    public static ProxyConfiguration Current => new(Enabled, Type, Host, Port, UseAuthentication, Username, Password);

    public static void Save(ProxyConfiguration value)
    {
        var type = NormalizeType(value.Type);
        var port = value.Port is >= 1 and <= 65535 ? value.Port : (type == "HTTP" ? 8080 : 1080);
        Enabled = value.Enabled;
        Type = type;
        Host = (value.Host ?? "").Trim();
        Port = port;
        UseAuthentication = value.UseAuthentication;
        Username = value.Username ?? "";
        Password = value.Password ?? "";
        var db = AppState.Instance;
        db.SetPref(Prefix + "enabled", Enabled ? "true" : "false");
        db.SetPref(Prefix + "type", Type);
        db.SetPref(Prefix + "host", Host);
        db.SetPref(Prefix + "port", Port.ToString(CultureInfo.InvariantCulture));
        db.SetPref(Prefix + "auth", UseAuthentication ? "true" : "false");
        db.SetPref(Prefix + "username", Username);
        db.SetPref(Prefix + "password", ProtectPassword(Password));
    }

    // Shared DPAPI helpers for secrets stored in application preferences.
    public static string ProtectSecret(string value) => ProtectPassword(value);
    public static string UnprotectSecret(string value) => UnprotectPassword(value);

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null) => CreateHttpClient(Current, timeout);

    public static HttpClient CreateHttpClient(ProxyConfiguration config, TimeSpan? timeout = null)
    {
        HttpMessageHandler handler;
        if (!config.Enabled)
        {
            handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
        }
        else
        {
            Validate(config);
            if (NormalizeType(config.Type) == "HTTP")
            {
                var proxy = new WebProxy($"http://{FormatHost(config.Host)}:{config.Port}");
                if (config.UseAuthentication)
                    proxy.Credentials = new NetworkCredential(config.Username, config.Password);
                handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                };
            }
            else
            {
                var socksHandler = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                };
                socksHandler.ConnectCallback = (context, cancellationToken) => new ValueTask<Stream>(ConnectSocks5Async(config, context.DnsEndPoint, cancellationToken));
                handler = socksHandler;
            }
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    public static async Task<string> TestAsync(ProxyConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use a lightweight public endpoint that does not require API credentials.
            // GitHub's API can return 403 because of rate limits or access policy even
            // when the proxy tunnel itself is perfectly healthy.
            using var client = CreateHttpClient(config, TimeSpan.FromSeconds(12));
            using var response = await client.GetAsync("https://example.com/", cancellationToken);
            if (response.IsSuccessStatusCode)
                return $"Прокси работает: соединение установлено (HTTP {(int)response.StatusCode}).";

            // An HTTP response proves that the proxy connection reached the destination;
            // report a destination-side response separately from a proxy connection error.
            return $"Прокси подключён, но проверочный сервер вернул HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Это не обязательно означает ошибку прокси.";
        }
        catch (Exception ex)
        {
            return "Не удалось подключиться через прокси: " + ex.GetBaseException().Message;
        }
    }

    private static async Task<Stream> ConnectSocks5Async(ProxyConfiguration config, DnsEndPoint destination, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(config.Host, config.Port, cancellationToken).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var useAuth = config.UseAuthentication;
            await stream.WriteAsync(useAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] == 255) throw new IOException("SOCKS5-сервер не принял способ авторизации.");
            if (greeting[1] == 2)
            {
                if (!useAuth) throw new IOException("SOCKS5 требует логин и пароль.");
                var user = Encoding.UTF8.GetBytes(config.Username ?? "");
                var pass = Encoding.UTF8.GetBytes(config.Password ?? "");
                if (user.Length > 255 || pass.Length > 255) throw new IOException("Логин или пароль SOCKS5 слишком длинный.");
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1; auth[1] = (byte)user.Length; Buffer.BlockCopy(user, 0, auth, 2, user.Length);
                auth[2 + user.Length] = (byte)pass.Length; Buffer.BlockCopy(pass, 0, auth, 3 + user.Length, pass.Length);
                await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);
                var authReply = new byte[2]; await ReadExactlyAsync(stream, authReply, cancellationToken).ConfigureAwait(false);
                if (authReply[0] != 1 || authReply[1] != 0) throw new IOException("SOCKS5 отклонил логин или пароль.");
            }
            else if (greeting[1] != 0) throw new IOException("SOCKS5 выбрал неподдерживаемый способ авторизации.");

            var host = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(destination.Host));
            if (host.Length is < 1 or > 255) throw new IOException("Имя узла слишком длинное для SOCKS5.");
            var request = new byte[7 + host.Length];
            request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)host.Length;
            Buffer.BlockCopy(host, 0, request, 5, host.Length);
            request[5 + host.Length] = (byte)(destination.Port >> 8); request[6 + host.Length] = (byte)(destination.Port & 0xff);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var reply = new byte[4]; await ReadExactlyAsync(stream, reply, cancellationToken).ConfigureAwait(false);
            if (reply[0] != 5 || reply[1] != 0) throw new IOException($"SOCKS5 не смог подключиться к серверу (код {reply[1]}).");
            var addressLength = reply[3] switch { 1 => 4, 4 => 16, 3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false), _ => throw new IOException("Некорректный ответ SOCKS5.") };
            var tail = new byte[addressLength + 2]; await ReadExactlyAsync(stream, tail, cancellationToken).ConfigureAwait(false);
            return new OwnedNetworkStream(stream, tcp);
        }
        catch { tcp.Dispose(); throw; }
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1]; await ReadExactlyAsync(stream, one, token).ConfigureAwait(false); return one[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Прокси закрыл соединение во время согласования SOCKS5.");
            offset += read;
        }
    }

    private static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        try
        {
            var inputBytes = Encoding.UTF8.GetBytes(password);
            var inputPtr = Marshal.AllocHGlobal(inputBytes.Length);
            try
            {
                Marshal.Copy(inputBytes, 0, inputPtr, inputBytes.Length);
                var input = new DataBlob { Size = inputBytes.Length, Data = inputPtr };
                if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var encrypted = new byte[output.Size];
                    Marshal.Copy(output.Data, encrypted, 0, output.Size);
                    return "dpapi:" + Convert.ToBase64String(encrypted);
                }
                finally { LocalFree(output.Data); }
            }
            finally { Marshal.FreeHGlobal(inputPtr); }
        }
        catch { return ""; }
    }

    private static string UnprotectPassword(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith("legacy:", StringComparison.Ordinal))
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(stored[7..])); } catch { return ""; }
        }
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // migrate old plaintext setting on next save
        try
        {
            var bytes = Convert.FromBase64String(stored[6..]);
            var inputPtr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, inputPtr, bytes.Length);
                var input = new DataBlob { Size = bytes.Length, Data = inputPtr };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)) return "";
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return Encoding.UTF8.GetString(result);
                }
                finally { LocalFree(output.Data); }
            }
            finally { Marshal.FreeHGlobal(inputPtr); }
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string NormalizeType(string? type) => string.Equals(type, "HTTP", StringComparison.OrdinalIgnoreCase) ? "HTTP" : "SOCKS5";
    private static string FormatHost(string host) => host.Contains(':') && !host.StartsWith("[") ? $"[{host}]" : host;
    private static void Validate(ProxyConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Host)) throw new ArgumentException("Укажи адрес прокси.");
        if (config.Port is < 1 or > 65535) throw new ArgumentException("Порт должен быть в диапазоне 1–65535.");
        if (config.UseAuthentication && string.IsNullOrWhiteSpace(config.Username)) throw new ArgumentException("Укажи логин прокси.");
    }

    private sealed class OwnedNetworkStream(Stream inner, TcpClient owner) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); owner.Dispose(); GC.SuppressFinalize(this); }
    }
}
