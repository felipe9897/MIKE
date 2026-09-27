using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Mike.Common
{
    public static class ElevatedBroker
    {
        public const string PipeName = "MikeLocalElevatedIPC";

        public static async Task<string> ExecuteElevatedAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);

            await MikeIpcProtocol.WriteMessageAsync(client, command);
            return await MikeIpcProtocol.ReadMessageAsync(client, timeout.Token);
        }
    }
}
