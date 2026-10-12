using System.Net;
using System.Text;

namespace NightlyDawn.Storage.Tests;

/// <summary>
/// A loopback HTTP server for the image-loader tests. Real sockets, real HTTP: the loader does its own DNS
/// resolution and its own connect, so a mocked <c>HttpMessageHandler</c> would skip exactly the code that
/// decides which address may be dialled.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerContext, Task> _handle;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private int _requests;

    public TestHttpServer(Func<HttpListenerContext, Task> handle)
    {
        _handle = handle;
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>Cancelled by <see cref="DisposeAsync"/>. A handler that deliberately stalls must wait on this,
    /// or disposing the server blocks for as long as the stall.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>How many requests actually arrived. A cache hit, a rejected scheme or a refused address must
    /// leave this at the number of fetches that were supposed to happen -- otherwise a test can pass while the
    /// loader is doing something entirely different from what the test claims.</summary>
    public int Requests => Volatile.Read(ref _requests);

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The accept loop always ends by the listener being stopped underneath it.
        }

        _listener.Close();
        _stopping.Dispose();
    }

    public static byte[] Png(int totalBytes = 64)
    {
        var bytes = new byte[Math.Max(totalBytes, 8)];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        for (var i = signature.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    public static async Task WriteAsync(HttpListenerContext context, byte[] body, string contentType = "image/png")
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    public static async Task WriteChunkedAsync(HttpListenerContext context, byte[] chunk, int times)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.SendChunked = true; // No Content-Length, so only the streaming cap can stop this.
        for (var i = 0; i < times; i++)
        {
            await context.Response.OutputStream.WriteAsync(chunk).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
        }

        context.Response.Close();
    }

    public static void Redirect(HttpListenerContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers["Location"] = location;
        context.Response.Close();
    }

    public static void Text(HttpListenerContext context, string body, string contentType = "text/html")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            Interlocked.Increment(ref _requests);

            try
            {
                await _handle(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loader aborts bodies mid-stream on purpose; a broken pipe here is the test working.
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                    // Already torn down.
                }
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>Allows every address. Only the tests use this: it is what lets them reach a loopback server, and
/// <c>TheDefaultPolicyRefusesToDialLoopback</c> is the control proving the production default does not.</summary>
public sealed class AllowAnyAddressPolicy : IRemoteAddressPolicy
{
    public bool IsAllowed(IPAddress address) => true;
}
