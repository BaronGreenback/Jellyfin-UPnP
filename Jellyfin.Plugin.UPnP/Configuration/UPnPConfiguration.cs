using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UPnP.Configuration
{
    /// <summary>
    /// Defines the <see cref="UPnPConfiguration" />.
    /// </summary>
    public class UPnPConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the time (in seconds) between the pings of SSDP gateway monitor.
        /// </summary>
        public int GatewayMonitorPeriod { get; set; } = 60;

        /// <summary>
        /// Gets or sets the public mapped port.
        /// </summary>
        public int HttpDestPort { get; set; }

        /// <summary>
        /// Gets or sets the public mapped port.
        /// </summary>
        public int HttpsDestPort { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the http port should be mapped as part of UPnP automatic port forwarding.
        /// </summary>
        public bool CreateHttpPortMap { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the http port should be mapped as part of UPnP automatic port forwarding.
        /// </summary>
        public bool CreateHttpsPortMap { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether mono.log logging is enabled.
        /// </summary>
        public bool DebugLogging { get; set; }

        /// <summary>
        /// Gets or sets the status of UPnP.
        /// </summary>
        [XmlIgnore]
        public string? Status { get; set; }
    }
}
