using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Mike.Common;

namespace Mike.Cli
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine($"--- {MikeConstants.AppName} CLI v{MikeConstants.Version} ---");

            if (args.Length == 0)
            {
                Console.WriteLine("Usage: mike <command> [args]");
                Console.WriteLine("Commands: ask <prompt>, tool <id> <args>, status, launch <agent>");
                return;
            }

            string command = args[0].ToLower();

            try
            {
                switch (command)
                {
                    case "launch":
                        if (args.Length < 2) { Console.WriteLine("Usage: mike launch <agent>"); return; }
                        var allowed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "claude", "codex", "openclaw", "opencode", "hermes", "hermes-desktop", "droid", "pi", "cline", "copilot", "omp", "dsh", "qwen", "codex-app" };
                        string agent = args[1];
                        if (!allowed.Contains(agent)) { Console.WriteLine($"Unknown agent: {agent}"); return; }
                        string localOllama = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
                        string ollama = File.Exists(localOllama) ? localOllama : "ollama.exe";
                        var launch = Process.Start(new ProcessStartInfo
                        {
                            FileName = ollama,
                            Arguments = "launch " + agent,
                            UseShellExecute = false
                        });
                        if (launch != null) await launch.WaitForExitAsync();
                        break;
                    case "ask":
                        string prompt = string.Join(" ", args[1..]);
                        string response = await MikeIpc.SendRequestAsync(prompt);
                        Console.WriteLine($"Mike: {response}");
                        break;

                    case "approve":
                        if (args.Length < 2) { Console.WriteLine("Usage: mike approve <approvalId>"); return; }
                        string approveId = args[1];
                        string approveResponse = await MikeIpc.SendRequestAsync($"approve:{approveId}");
                        Console.WriteLine($"Approval Result: {approveResponse}");
                        break;

                    case "tool":
                        if (args.Length < 2) { Console.WriteLine("Usage: mike tool <id> [args] OR mike tool approved <approvalId> <id> [args]"); return; }
                        if (args[1] == "approved")
                        {
                            if (args.Length < 4) { Console.WriteLine("Usage: mike tool approved <approvalId> <id> [args]"); return; }
                            string approvalId = args[2];
                            string toolId = args[3];
                            string toolArgs = args.Length > 4 ? string.Join(" ", args[4..]) : "";
                            string toolResult = await MikeIpc.SendRequestAsync($"tool:execute_approved|{approvalId}|{toolId}|{toolArgs}");
                            Console.WriteLine($"Result: {toolResult}");
                        }
                        else
                        {
                            string toolId = args[1];
                            string toolArgs = args.Length > 2 ? string.Join(" ", args[2..]) : "";
                            string toolResult = await MikeIpc.SendRequestAsync($"tool:{toolId}|{toolArgs}");
                            Console.WriteLine($"Result: {toolResult}");
                        }
                        break;

                    case "status":
                        Console.WriteLine("Checking Mike Service status...");
                        // In a real scenario, we'd check the service process or a heartbeat pipe
                        Console.WriteLine("Service: Running (Assumed via IPC)");
                        break;

                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
