using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mike.Common
{
    /// <summary>
    /// Provides secure storage for secrets using Windows DPAPI via P/Invoke.
    /// This avoids external NuGet dependencies on restricted environments.
    /// </summary>
    public static class SecureStorage
    {
        private const uint CryptProtectLocalMachine = 0x00000004;
        private const int MaxSecretBytes = 1024 * 1024;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string? szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            out IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            ref DATA_BLOB pDataOut);

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static void SaveSecret(string key, string secret)
        {
            string path = GetPath(key);
            if (secret is null) throw new ArgumentNullException(nameof(secret));
            byte[] data = Encoding.UTF8.GetBytes(secret);
            if (data.Length == 0 || data.Length > MaxSecretBytes)
                throw new ArgumentException("Secret must be between 1 byte and 1 MiB.", nameof(secret));

            IntPtr inputPtr = Marshal.AllocHGlobal(data.Length);
            DATA_BLOB input = new DATA_BLOB { cbData = data.Length, pbData = inputPtr };
            Marshal.Copy(data, 0, inputPtr, data.Length);

            DATA_BLOB output = new DATA_BLOB();
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DPAPI is available only on Windows.");

                // Machine scope is intentional for the Windows service, but the file is
                // still ACL-protected by the installer's data-directory policy.
                if (!CryptProtectData(ref input, key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine, ref output))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                byte[] encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                WriteAtomically(path, encrypted);
                CryptographicOperations.ZeroMemory(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
                Marshal.FreeHGlobal(inputPtr);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }

        public static string? GetSecret(string key)
        {
            string path = GetPath(key);
            if (!File.Exists(path)) return null;

            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0 || encrypted.Length > MaxSecretBytes * 2)
                throw new InvalidDataException("Stored secret has an invalid size.");
            IntPtr inputPtr = Marshal.AllocHGlobal(encrypted.Length);
            DATA_BLOB input = new DATA_BLOB { cbData = encrypted.Length, pbData = inputPtr };
            Marshal.Copy(encrypted, 0, inputPtr, encrypted.Length);

            DATA_BLOB output = new DATA_BLOB();
            IntPtr description = IntPtr.Zero;
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DPAPI is available only on Windows.");

                if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, ref output))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (output.cbData <= 0 || output.cbData > MaxSecretBytes)
                    throw new InvalidDataException("Decrypted secret has an invalid size.");

                byte[] decrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, decrypted, 0, output.cbData);
                try
                {
                    return new UTF8Encoding(false, true).GetString(decrypted);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decrypted);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                Marshal.FreeHGlobal(inputPtr);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }

        private static string GetPath(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException("Invalid secret key.", nameof(key));
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "MikeLocal", "secrets");
            Directory.CreateDirectory(dir);
            string candidate = Path.GetFullPath(Path.Combine(dir, $"{key}.bin"));
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid secret key.", nameof(key));
            return candidate;
        }

        private static void WriteAtomically(string path, byte[] data)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    public static class MikeConstants
    {
        public const string AppName = "Mike Local";
        public const string Version = "1.0.57";
        public const string PipeName = "MikeLocalIPC";
        public const string HermesPort = "8080";
    }
}

