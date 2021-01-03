using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UPnP.Gateway
{
    /// <summary>
    /// Singleton that periodically checks the state of the internet.
    /// </summary>
    public class GatewayMonitor : IGatewayMonitor, IDisposable
    {
        private readonly object _gwLock = new ();

        /// <summary>
        /// Logger.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// List of IPAddresses to monitor.
        /// </summary>
        private readonly List<IPAddress> _gwAddress;

        /// <summary>
        /// Timer object.
        /// </summary>
        private Timer? _pinger;

        /// <summary>
        /// Set if disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Set if the internet resolver is running.
        /// </summary>
        private bool _active;

        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayMonitor"/> class.
        /// Constructor for InternetChecker.
        /// </summary>
        /// <param name="logger">An <see cref="ILogger"/> instance.</param>
        /// <param name="period">Gateway Monitor Period in seconds.</param>
        public GatewayMonitor(ILogger logger, int period)
        {
            _logger = logger;
            _gwAddress = new List<IPAddress>();
            GatewayMonitorPeriod = period;
        }

        /// <summary>
        /// Event that gets called every time a ping to the gateway fails.
        /// </summary>
        public event EventHandler? OnGatewayFailure;

        /// <summary>
        /// Gets or sets the gateway monitoring period (in seconds).
        /// </summary>
        public int GatewayMonitorPeriod { get; set; }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Adds a gateway for monitoring.
        /// </summary>
        /// <param name="gwAddress">IP Address to monitor.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task AddGateway(IPAddress gwAddress)
        {
            // ping an see if we can add
            if (await IsPingable(gwAddress).ConfigureAwait(false))
            {
                _logger.LogDebug("Device responds to ICMP. Monitoring.");
                lock (_gwLock)
                {
                    _gwAddress.Add(gwAddress);
                }

                _pinger ??= new Timer(CheckRouterStatus, null, 0, Timeout.Infinite);

                _active = true;
            }
            else
            {
                _logger.LogDebug("Unable to monitor, device doesn't responds to ICMP.");
            }
        }

        /// <summary>
        /// Clears all the gateways.
        /// </summary>
        public void ResetGateways()
        {
            _logger.LogInformation("Resetting gateways.");
            lock (_gwLock)
            {
                _gwAddress.Clear();
                _pinger?.Dispose();
                _pinger = null;
                _active = false;
            }
        }

        /// <summary>
        /// Returns true if the ip address provided responds to a ping.
        /// </summary>
        /// <param name="gwAddress">IP address to check.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private static async Task<bool> IsPingable(IPAddress gwAddress)
        {
            using var ping = new Ping();
            var result = await ping.SendPingAsync(gwAddress, 2000).ConfigureAwait(false);
            return result.Status == IPStatus.Success;
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool dispose)
        {
            if (_disposed)
            {
                return;
            }

            if (!dispose)
            {
                return;
            }

            _pinger?.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Timer handler to check the status of inbound traffic.
        /// </summary>
        /// <param name="state">Timer state.</param>
        private void CheckRouterStatus(object? state)
        {
            if (_disposed)
            {
                return;
            }

            _pinger?.Change(GatewayMonitorPeriod * 1000, Timeout.Infinite);
            _ = CheckRouter();
        }

        /// <summary>
        /// Checks the status of the firewalls in the list.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task CheckRouter()
        {
            List<Task<PingReply>> pingTasks;

            _logger.LogInformation("Pinging gateways.");
            lock (_gwLock)
            {
                pingTasks = _gwAddress.Select(
                     host => new Ping().SendPingAsync(host, 2000)).ToList();
            }

            await Task.WhenAll(pingTasks).ConfigureAwait(true);

            if (_active && !_disposed)
            {
                for (int i = 0; i <= pingTasks.Count - 1; i++)
                {
                    PingReply result = await pingTasks[i].ConfigureAwait(true);
                    IPAddress gw = _gwAddress[i];
                    if (result.Status == IPStatus.Success)
                    {
                        _logger.LogWarning("Gateway down: {Ip}", gw);
                        OnGatewayFailure?.Invoke(this, new GatewayEventArgs(gw, result.Status));
                    }
                    else
                    {
                        _logger.LogDebug("Reply from {Ip}", gw);
                    }

                    if (!_active || _disposed)
                    {
                        break;
                    }
                }
            }
        }
    }
}
