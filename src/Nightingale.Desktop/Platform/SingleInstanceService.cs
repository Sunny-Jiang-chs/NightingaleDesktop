using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Nightingale.Desktop.Platform;

/// <summary>
/// One player per Windows user and data directory. FilesReceived runs on a worker
/// thread; WPF callers must dispatch to their window's Dispatcher. An empty array
/// requests activation without replacing the current media.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const int MaxPayloadBytes = 1024 * 1024;
    private readonly Mutex _marker;
    private readonly string _instanceName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listener;
    private bool _disposed;

    public event Action<string[]>? FilesReceived;

    private SingleInstanceService(Mutex marker, string instanceName)
    {
        _marker = marker;
        _instanceName = instanceName;
        _listener = ListenAsync(_shutdown.Token);
    }

    public static bool TryCreate(out SingleInstanceService? instance)
    {
        // Holding the named object's handle is sufficient; no mutex ownership is
        // taken, so cleanup is safe even if a caller disposes on another thread.
        var instanceName = InstanceName;
        var marker = new Mutex(false, "Local\\" + instanceName, out var created);
        if (!created)
        {
            marker.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstanceService(marker, instanceName);
        return true;
    }

    public static async Task<bool> ForwardAsync(string[] files, CancellationToken cancellationToken = default)
    {
        if (files.Length > 1024) return false;
        byte[] payload;
        try
        {
            // The receiving process may have a different current directory.
            payload = JsonSerializer.SerializeToUtf8Bytes(files.Select(Path.GetFullPath).ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (payload.Length > MaxPayloadBytes) return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            // Explorer may have given foreground rights to this short-lived new
            // process. Transfer those rights before the running player activates.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId))
                AllowSetForegroundWindow(serverProcessId);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_instanceName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var requestToken = requestDeadline.Token;
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, requestToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > MaxPayloadBytes) continue;
                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, requestToken).ConfigureAwait(false);
                var files = JsonSerializer.Deserialize<string[]>(payload);
                if (files is null || files.Length > 1024 || files.Any(p =>
                        string.IsNullOrWhiteSpace(p) || p.Length > 32767 || !Path.IsPathFullyQualified(p))) continue;
                var handler = FilesReceived;
                if (handler is null) continue;
                handler.Invoke(files);
                await pipe.WriteAsync(new byte[] { 1 }, requestToken).ConfigureAwait(false);
                await pipe.FlushAsync(requestToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
                                           or ArgumentException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Single-instance request rejected: {ex.Message}");
            }
        }
    }

    private static string InstanceName
    {
        get
        {
            var directory = Environment.GetEnvironmentVariable("NIGHTINGALE_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightingalePlayer");
            var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
            var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user + "|" + canonicalDirectory));
            return "Nightingale." + Convert.ToHexString(hash.AsSpan(0, 16));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _marker.Dispose();
        // The listener owns its active pipe and closes it when cancellation runs.
        _ = _listener.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
