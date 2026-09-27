using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mike.Common
{
    /// <summary>
    /// Versioned IPC Protocol for Mike Local.
    /// Framing: [Length (4 bytes)] [Version (2 bytes)] [Payload]
    /// </summary>
    public static class MikeIpcProtocol
    {
        public const ushort CurrentVersion = 1;
        public const int MaxPayloadSize = 10 * 1024 * 1024; // 10 MB

        public static async Task WriteMessageAsync(Stream stream, string payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(payload);
            if (data.Length > MaxPayloadSize)
                throw new InvalidOperationException("Payload exceeds maximum size limit.");

            byte[] lengthHeader = BitConverter.GetBytes(data.Length);
            byte[] versionHeader = BitConverter.GetBytes(CurrentVersion);

            await stream.WriteAsync(lengthHeader, 0, 4);
            await stream.WriteAsync(versionHeader, 0, 2);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();
        }

        public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken token)
        {
            byte[] lengthBuffer = new byte[4];
            await ReadExactlyAsync(stream, lengthBuffer, token, "Length header missing.");

            int length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length < 0 || length > MaxPayloadSize)
                throw new InvalidOperationException($"Invalid IPC frame: Length {length} is out of bounds.");

            byte[] versionBuffer = new byte[2];
            await ReadExactlyAsync(stream, versionBuffer, token, "Version header missing.");

            ushort version = BitConverter.ToUInt16(versionBuffer, 0);
            if (version != CurrentVersion)
                throw new NotSupportedException($"IPC version {version} is not supported. Expected {CurrentVersion}.");

            byte[] payloadBuffer = new byte[length];
            await ReadExactlyAsync(stream, payloadBuffer, token, "IPC stream closed unexpectedly.");

            return Encoding.UTF8.GetString(payloadBuffer);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token, string missingMessage)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, token);
                if (read == 0)
                    throw new EndOfStreamException($"Invalid IPC frame: {missingMessage}");

                totalRead += read;
            }
        }
    }

    public static class MikeLogger
    {
        public static string Redact(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            // Redact common sensitive patterns (Passwords, Keys, etc.)
            return System.Text.RegularExpressions.Regex.Replace(message,
                @"(password|key|token|secret|auth)\s*[:=]\s*[^,\s\n]+",
                "$1=***REDACTED***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
