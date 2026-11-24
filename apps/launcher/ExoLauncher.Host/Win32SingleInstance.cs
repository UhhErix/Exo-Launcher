using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services;

internal sealed class Win32SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listener;

    internal bool IsPrimary { get; }
    internal event Action<string[]>? Activated;

    private Win32SingleInstance(Mutex mutex, bool isPrimary, string pipeName)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _pipeName = pipeName;
    }

    internal static Win32SingleInstance Acquire(string key)
    {
        var safe = Sanitize(key);
        var mutex = new Mutex(initiallyOwned: true, $"Local\\ExoLauncher.SingleInstance.{safe}", out var createdNew);
        var instance = new Win32SingleInstance(mutex, createdNew, $"exo-launcher-activate-{safe}");
        if (createdNew) instance.StartListener();
        return instance;
    }

    internal bool TryForwardActivation(string[] args, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(timeoutMs);
            var payload = EncodeArgs(args);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static byte[] EncodeArgs(string[] args)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var arg in args) writer.WriteStringValue(arg);
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    internal static string[] DecodeArgs(byte[] payload)
    {
        if (payload.Length == 0) return [];
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var result = new string[doc.RootElement.GetArrayLength()];
        var i = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
            result[i++] = item.GetString() ?? string.Empty;
        return result;
    }

    private void StartListener()
    {
        _listener = Task.Run(async () =>
        {
            var sid = WindowsIdentity.GetCurrent().User;
            PipeSecurity? security = null;
            if (sid is not null)
            {
                security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = security is not null
                        ? NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 0, security)
                        : new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, _cts.Token).ConfigureAwait(false);
                    var forwarded = DecodeArgs(ms.ToArray());
                    try { Activated?.Invoke(forwarded); } catch { }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); } catch { break; }
                }
            }
        });
    }

    private static string Sanitize(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Wait(250); } catch { }
        _cts.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
