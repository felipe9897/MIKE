using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mike.Common
{
    public static class MikeIpc
    {
        public static async Task<string> SendRequestAsync(string requestJson, int timeoutMs = 360000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new NamedPipeClientStream(".", "MikeLocalIPC", PipeDirection.InOut);

            try
            {
                await client.ConnectAsync(cts.Token);

                await MikeIpcProtocol.WriteMessageAsync(client, requestJson);
                string response = await MikeIpcProtocol.ReadMessageAsync(client, cts.Token);

                return response;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"IPC request timed out after {timeoutMs}ms");
            }
        }
    }
}
