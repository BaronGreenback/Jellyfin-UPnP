using System;
using System.Net;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.UPnP.Gateway
{
    /// <summary>
    /// Interface for <see cref="IGatewayMonitor"/>.
    /// </summary>
    public interface IGatewayMonitor
    {
        /// <summary>
        /// Event that gets called every time a ping to the gateway fails.
        /// </summary>
        event EventHandler? OnGatewayFailure;

        /// <summary>
        /// Gets or sets the gateway monitoring period.
        /// </summary>
        int GatewayMonitorPeriod { get; set; }

        /// <summary>
        /// Adds a gateway for monitoring.
        /// </summary>
        /// <param name="gwAddress">IP Address to monitor.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        Task AddGateway(IPAddress gwAddress);

        /// <summary>
        /// Clears all the gateways.
        /// </summary>
        void ResetGateways();
    }
}
