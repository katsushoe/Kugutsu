using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using System.Buffers.Text;

namespace Kugutsu.Hosting;

/// <summary>Expected credential failures, without credential contents or system exception text.</summary>
internal sealed class CredentialException(LogError reason) : Exception("MCP credential loading failed.")
{
    public LogError Reason { get; } = reason;
}

/// <summary>Reads or initializes Kugutsu's generic credential in the current Windows logon session.</summary>
internal static class CredentialLoader
{
    public const string TargetName = "Kugutsu/McpBearerToken";
    private const uint GenericCredential = 1;
    private const uint MaxBlobBytes = 2560;
    private const int NotFound = 1168;
    private static readonly Encoding TokenEncoding = new UnicodeEncoding(false, false, true);

    /// <summary>Loads and validates a token; never writes or silently generates a credential.</summary>
    public static BearerCredential Load()
    {
        if (!OperatingSystem.IsWindows()) { throw new CredentialException(LogError.CredentialUnavailable); }
        try { return Read(); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new CredentialException(LogError.CredentialUnavailable); }
    }

    /// <summary>Creates a per-user token only when the existing credential is absent.</summary>
    public static void EnsureCreated()
    {
        if (!OperatingSystem.IsWindows()) { throw new CredentialException(LogError.CredentialUnavailable); }
        using var mutex = new Mutex(false, @"Local\Kugutsu.McpCredentialProvisioning");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { throw new CredentialException(LogError.CredentialUnavailable); }
            try
            {
                using var existing = Load();
                return;
            }
            catch (CredentialException exception) when (exception.Reason == LogError.CredentialMissing)
            { WriteNewCredential(); }
        }
        finally { if (acquired) { mutex.ReleaseMutex(); } }
    }

    private static void WriteNewCredential()
    {
        Span<byte> random = stackalloc byte[32];
        Span<byte> encoded = stackalloc byte[44];
        byte[]? blob = null;
        var blobPointer = IntPtr.Zero;
        var targetPointer = IntPtr.Zero;
        var userPointer = IntPtr.Zero;
        try
        {
            RandomNumberGenerator.Fill(random);
            var status = Base64.EncodeToUtf8(random, encoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != random.Length)
            { throw new CredentialException(LogError.CredentialUnavailable); }
            while (written > 0 && encoded[written - 1] == (byte)'=') { written--; }
            blob = new byte[written * 2];
            for (var index = 0; index < written; index++)
            {
                var value = encoded[index] switch { (byte)'+' => (byte)'-', (byte)'/' => (byte)'_', var item => item };
                blob[index * 2] = value;
            }
            targetPointer = Marshal.StringToHGlobalUni(TargetName);
            userPointer = Marshal.StringToHGlobalUni("Kugutsu");
            blobPointer = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                BlobSize = (uint)blob.Length,
                Blob = blobPointer,
                Persist = 2,
                UserName = userPointer
            };
            if (!CredWrite(ref credential, 0))
            { throw new CredentialException(LogError.CredentialUnavailable); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            CryptographicOperations.ZeroMemory(encoded);
            if (blob is not null) { CryptographicOperations.ZeroMemory(blob); }
            if (blobPointer != IntPtr.Zero)
            {
                if (blob is not null) { for (var index = 0; index < blob.Length; index++) { Marshal.WriteByte(blobPointer, index, 0); } }
                Marshal.FreeHGlobal(blobPointer);
            }
            if (targetPointer != IntPtr.Zero) { Marshal.FreeHGlobal(targetPointer); }
            if (userPointer != IntPtr.Zero) { Marshal.FreeHGlobal(userPointer); }
        }
    }
    private static BearerCredential Read()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out var pointer))
        {
            var code = Marshal.GetLastWin32Error();
            throw new CredentialException(code == NotFound ? LogError.CredentialMissing : LogError.CredentialUnavailable);
        }
        if (pointer == IntPtr.Zero) { throw new CredentialException(LogError.CredentialUnavailable); }

        byte[]? bytes = null;
        char[]? characters = null;
        NativeCredential native = default;
        try
        {
            native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.Type != GenericCredential || native.Blob == IntPtr.Zero
                || native.BlobSize == 0 || native.BlobSize > MaxBlobBytes || native.BlobSize % 2 != 0)
            { throw new CredentialException(LogError.CredentialInvalid); }
            bytes = new byte[(int)native.BlobSize];
            Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
            try { characters = TokenEncoding.GetChars(bytes); }
            catch (DecoderFallbackException) { throw new CredentialException(LogError.CredentialInvalid); }
            ValidateToken(characters);
            var token = new byte[characters.Length];
            for (var index = 0; index < characters.Length; index++) { token[index] = (byte)characters[index]; }
            return new BearerCredential(token);
        }
        finally
        {
            if (bytes is not null) { CryptographicOperations.ZeroMemory(bytes); }
            if (characters is not null) { Array.Clear(characters); }
            try
            {
                if (native.Blob != IntPtr.Zero && native.BlobSize <= MaxBlobBytes)
                {
                    for (var index = 0; index < (int)native.BlobSize; index++) { Marshal.WriteByte(native.Blob, index, 0); }
                }
            }
            finally { CredFree(pointer); }
        }
    }

    private static void ValidateToken(ReadOnlySpan<char> token)
    {
        var padding = false;
        var content = false;
        foreach (var character in token)
        {
            if (character == '=') { padding = true; continue; }
            if (padding || !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '-' or '.' or '_' or '~' or '+' or '/'))
            { throw new CredentialException(LogError.CredentialInvalid); }
            content = true;
        }
        if (!content) { throw new CredentialException(LogError.CredentialInvalid); }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
