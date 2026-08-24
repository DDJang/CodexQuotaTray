using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.Auth;

public interface IOAuthCredentialStore
{
    Task<OAuthCredentials?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(OAuthCredentials credentials, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Stores OAuth credentials as a DPAPI-protected blob for the current Windows user.
/// The clear-text JSON exists only in memory while encrypting/decrypting.
/// </summary>
public sealed class DpapiOAuthCredentialStore(string path) : IOAuthCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OAuthCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (protectedBytes.Length is 0 or > JsonFileStore.MaximumBytes)
            {
                return null;
            }

            var clearBytes = Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<OAuthCredentials>(clearBytes, JsonOptions);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(OAuthCredentials credentials, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A credential directory is required.");
        Directory.CreateDirectory(directory);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        var protectedBytes = Protect(clearBytes);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] value)
    {
        var input = new DataBlob(value);
        var output = default(DATA_BLOB);
        try
        {
            if (!CryptProtectData(ref input.Value, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return Copy(output);
        }
        finally
        {
            input.Dispose();
            Free(output);
        }
    }

    private static byte[] Unprotect(byte[] value)
    {
        var input = new DataBlob(value);
        var output = default(DATA_BLOB);
        try
        {
            if (!CryptUnprotectData(ref input.Value, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return Copy(output);
        }
        finally
        {
            input.Dispose();
            Free(output);
        }
    }

    private static byte[] Copy(DATA_BLOB value)
    {
        if (value.cbData <= 0 || value.pbData == IntPtr.Zero)
        {
            throw new CryptographicException("DPAPI returned an empty payload.");
        }

        var result = new byte[value.cbData];
        Marshal.Copy(value.pbData, result, 0, result.Length);
        return result;
    }

    private static void Free(DATA_BLOB value)
    {
        if (value.pbData != IntPtr.Zero)
        {
            _ = LocalFree(value.pbData);
        }
    }

    private sealed class DataBlob : IDisposable
    {
        internal DataBlob(byte[] value)
        {
            Value.cbData = value.Length;
            Value.pbData = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, Value.pbData, value.Length);
        }

        internal DATA_BLOB Value;

        public void Dispose()
        {
            if (Value.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Value.pbData);
                Value.pbData = IntPtr.Zero;
                Value.cbData = 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        internal int cbData;
        internal IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        IntPtr szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

public sealed class OAuthCredentialManager(
    IOAuthCredentialStore store,
    OAuthClient client) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private OAuthCredentials? credentials;
    private bool loaded;
    private bool disposed;

    public bool HasCachedCredentials => credentials is not null;

    internal OAuthClient Client => client;

    public async Task<OAuthCredentials?> GetValidAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!loaded)
            {
                credentials = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                loaded = true;
            }

            if (credentials is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (!credentials.NeedsRefresh(now))
            {
                return credentials;
            }

            try
            {
                var refreshed = await client.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);
                credentials = refreshed;
                await store.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return refreshed;
            }
            catch (OAuthException error) when (error.Kind is OAuthFailureKind.RefreshExpired
                or OAuthFailureKind.RefreshRevoked
                or OAuthFailureKind.RefreshReused
                or OAuthFailureKind.LoginRequired)
            {
                credentials = null;
                await store.ClearAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetAsync(OAuthCredentials value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            credentials = value;
            loaded = true;
            await store.SaveAsync(value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<OAuthCredentials?> RefreshAfterUnauthorizedAsync(
        OAuthCredentials current,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!loaded)
            {
                credentials = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                loaded = true;
            }

            if (credentials is null)
            {
                return null;
            }

            if (!string.Equals(credentials.RefreshToken, current.RefreshToken, StringComparison.Ordinal))
            {
                return credentials;
            }

            var refreshed = await client.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);
            credentials = refreshed;
            await store.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        catch (OAuthException error) when (error.Kind is OAuthFailureKind.RefreshExpired
            or OAuthFailureKind.RefreshRevoked
            or OAuthFailureKind.RefreshReused
            or OAuthFailureKind.LoginRequired)
        {
            credentials = null;
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            credentials = null;
            loaded = true;
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
        client.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
