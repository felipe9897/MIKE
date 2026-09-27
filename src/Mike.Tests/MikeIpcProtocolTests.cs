using System.IO;
using System.Text;
using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class MikeIpcProtocolTests
{
    [Fact]
    public async Task ReadMessageHandlesPartialHeadersAndPayload()
    {
        await using var source = new MemoryStream();
        await MikeIpcProtocol.WriteMessageAsync(source, "olá Mike");
        source.Position = 0;

        await using var chunked = new ChunkedReadStream(source.ToArray(), maxChunk: 1);
        var result = await MikeIpcProtocol.ReadMessageAsync(chunked, CancellationToken.None);

        Assert.Equal("olá Mike", result);
    }

    [Fact]
    public async Task ReadMessageRejectsUnsupportedVersion()
    {
        var frame = BuildFrame(version: MikeIpcProtocol.CurrentVersion + 1, payload: "test");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            MikeIpcProtocol.ReadMessageAsync(new MemoryStream(frame), CancellationToken.None));
    }

    [Fact]
    public async Task ReadMessageRejectsOversizedPayloadBeforeAllocation()
    {
        var length = BitConverter.GetBytes(MikeIpcProtocol.MaxPayloadSize + 1);
        var version = BitConverter.GetBytes(MikeIpcProtocol.CurrentVersion);
        using var frame = new MemoryStream();
        await frame.WriteAsync(length);
        await frame.WriteAsync(version);
        frame.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MikeIpcProtocol.ReadMessageAsync(frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteMessageRejectsOversizedPayload()
    {
        var payload = new string('x', MikeIpcProtocol.MaxPayloadSize + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MikeIpcProtocol.WriteMessageAsync(Stream.Null, payload));
    }

    private static byte[] BuildFrame(ushort version, string payload)
    {
        var data = Encoding.UTF8.GetBytes(payload);
        using var frame = new MemoryStream();
        frame.Write(BitConverter.GetBytes(data.Length));
        frame.Write(BitConverter.GetBytes(version));
        frame.Write(data);
        return frame.ToArray();
    }

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _maxChunk;

        public ChunkedReadStream(byte[] buffer, int maxChunk) : base(buffer, writable: false)
        {
            _maxChunk = maxChunk;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return base.ReadAsync(buffer, offset, Math.Min(count, _maxChunk), cancellationToken);
        }
    }
}
