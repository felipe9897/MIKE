using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mike.Common
{
    public class ResourceManager
    {
        private readonly ILogger _logger;
        private DateTime _lastUserActivity = DateTime.UtcNow;
        private readonly TimeSpan _idleThreshold = TimeSpan.FromMinutes(30);

        public ResourceManager(ILogger logger)
        {
            _logger = logger;
        }

        public void UpdateUserActivity()
        {
            _lastUserActivity = DateTime.UtcNow;
        }

        public async Task MonitorResourcesAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool isIdle = (DateTime.UtcNow - _lastUserActivity) > _idleThreshold;
                double cpuLimit = isIdle ? 1.0 : 0.5;
                double ramLimit = isIdle ? 1.0 : 0.5;

                var currentCpu = GetCpuUsage();
                var currentRam = GetRamUsage();

                if (currentCpu > cpuLimit * 100)
                {
                    _logger.LogWarning("Resource limit exceeded: CPU {Usage}% (Limit: {Limit}%)", currentCpu, cpuLimit * 100);
                    ThrottleResources();
                }

                CheckBatteryAndTemp();

                await Task.Delay(5000, ct);
            }
        }

        private double GetCpuUsage()
        {
            // Simulated CPU usage for this environment
            return new Random().NextDouble() * 100;
        }

        private double GetRamUsage()
        {
            // Simulated RAM usage
            return new Random().NextDouble() * 100;
        }

        private void ThrottleResources()
        {
            // In a real implementation, we'd change process priority or use Job Objects (Windows)
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            _logger.LogInformation("Throttling resources to protect system stability.");
        }

        private void CheckBatteryAndTemp()
        {
            // Simulated battery and temperature check
            var battery = new Random().Next(0, 100);
            var temp = new Random().Next(30, 90);

            if (battery < 20)
            {
                _logger.LogWarning("Low battery detected: {Percent}%. Reducing resource consumption.", battery);
                ThrottleResources();
            }

            if (temp > 80)
            {
                _logger.LogCritical("Critical temperature detected: {Temp}C. Forcing low power mode.", temp);
                ThrottleResources();
            }
        }
    }
}
