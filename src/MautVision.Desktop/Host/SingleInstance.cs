using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MautVision.Desktop.Host;

/// <summary>
/// One application per work directory: a named mutex whose name holds a hash of the full work directory path (so runs with other work
/// directories, such as smoke runs, do not collide). A second launch signals the named event of the first, which shows its window, and
/// exits. Names are in the session namespace (Local\).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private readonly RegisteredWaitHandle registration;
    private int disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activation, Action activated)
    {
        this.mutex = mutex;
        this.activation = activation;
        registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => activated(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The instance of this work directory, or null when another process holds it (it was asked to show its window).</summary>
    /// <param name="activated">Called on a pool thread when a later launch asks this instance to show itself.</param>
    internal static SingleInstance? TryAcquire(string workDirectory, Action activated)
    {
        var name = Name(workDirectory);
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            ActivateExisting(name);
            return null;
        }
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, name + "-activate"), activated);
    }

    /// <summary>Session-local mutex name of a work directory: the case-insensitive full path, hashed.</summary>
    internal static string Name(string workDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory)).ToUpperInvariant();
        return @"Local\MautVision-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    /// <summary>Lets the first instance take the foreground and signals it; it may still be creating its event, so this retries briefly.</summary>
    private static void ActivateExisting(string name)
    {
        AllowSetForegroundWindow(-1);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(name + "-activate", out var existing))
            {
                using (existing) existing.Set();
                return;
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        registration.Unregister(null);
        activation.Dispose();
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released by another thread than the owner; the handle still goes away with the process.
        }
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
