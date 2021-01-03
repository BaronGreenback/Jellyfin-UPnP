using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Networking.Configuration;
using Jellyfin.Plugin.UPnP.Configuration;
using Jellyfin.Plugin.UPnP.Gateway;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Mono.Nat;
using NATLogger = Mono.Nat.Logging.ILogger;

namespace Jellyfin.Plugin.UPnP
{
    /// <summary>
    /// Defines the <see cref="UPnPPlugin" />.
    /// </summary>
    public class UPnPPlugin : BasePlugin<UPnPConfiguration>, IHasWebPages, IDisposable
    {
        private static UPnPPlugin? _instance;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger<UPnPPlugin> _logger;
        private readonly IServerConfigurationManager _config;
        private readonly INetworkManager _networkManager;
        private readonly IList<INatDevice> _devices;
        private readonly object _lock;
        private NetworkConfiguration _networkConfig;
        private IGatewayMonitor? _gatewayMonitor;
        private bool _stopped = true;
        private string _configIdentifier;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="UPnPPlugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">The <see cref="IApplicationPaths"/>.</param>
        /// <param name="xmlSerializer">The <see cref="IXmlSerializer"/>.</param>
        /// <param name="appHost">The <see cref="IServerApplicationHost"/>.</param>
        /// <param name="config">The <see cref="IServerConfigurationManager"/>.</param>
        /// <param name="networkManager">The <see cref="INetworkManager"/>.</param>
        /// <param name="logger">The <see cref="ILogger{UPnPPlugin}"/>.</param>
        public UPnPPlugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            IServerApplicationHost appHost,
            IServerConfigurationManager config,
            INetworkManager networkManager,
            ILogger<UPnPPlugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _lock = new object();
            _logger = logger;
            _appHost = appHost;
            _config = config;
            _networkConfig = config.GetNetworkConfiguration();
            _networkManager = networkManager;
            _devices = new List<INatDevice>();
            _configIdentifier = GetConfigIdentifier();
            Mono.Nat.Logging.Logger.Factory = GetLogger;
            UpdateConfiguration(Configuration);
            _config.NamedConfigurationUpdated += OnSystemConfigurationUpdated;
            Start();
        }

        /// <summary>
        /// Gets the Instance, nullable style.
        /// </summary>
        public static UPnPPlugin Instance
        {
            get => GetInstance();
            private set => _instance = value;
        }

        /// <summary>
        /// Gets the name of the plugin.
        /// </summary>
        public override string Name => "UPnP Plugin";

        /// <summary>
        /// Gets the plugin id.
        /// </summary>
        public override Guid Id => Guid.Parse("22e800c8-ea1f-4cb3-9ba3-b5623a02d2b5");

        /// <summary>
        /// Gets a value indicating whether uPnP is active.
        /// </summary>
        private bool IsUPnPActive
        {
            get
            {
                var config = Instance!.Configuration;
                return _networkConfig.EnableRemoteAccess &&
                    (_appHost.ListenWithHttps || (!_appHost.ListenWithHttps && (config.CreateHttpPortMap || config.CreateHttpsPortMap)));
            }
        }

        /// <summary>
        /// Gets the configuration page.
        /// </summary>
        /// <returns>The <see cref="IEnumerable{PluginPageInfo}"/>.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                }
            };
        }

        /// <summary>
        /// Dispose method.
        /// </summary>
        public void Dispose()
        {
            _config.NamedConfigurationUpdated += OnSystemConfigurationUpdated;
            Stop(true);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc/>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var conf = (UPnPConfiguration)configuration;
            conf.HttpDestPort = conf.HttpDestPort == 0 ? _networkConfig.HttpServerPortNumber : Math.Clamp(conf.HttpDestPort, 1, 65535);
            conf.HttpsDestPort = conf.HttpsDestPort == 0 ? _networkConfig.HttpsPortNumber : Math.Clamp(conf.HttpsDestPort, 1, 65535);

            var oldConfigIdentifier = _configIdentifier;
            _configIdentifier = GetConfigIdentifier();
            if (!string.Equals(_configIdentifier, oldConfigIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                var restart = IsUPnPActive;
                Stop(!restart);
                if (restart)
                {
                    Start();
                }
            }

            if (_gatewayMonitor != null)
            {
                _gatewayMonitor.GatewayMonitorPeriod = Math.Clamp(Configuration.GatewayMonitorPeriod, 0, 6000000);
            }

            base.UpdateConfiguration(configuration);
        }

        /// <summary>
        /// Returns the static instance.
        /// </summary>
        /// <returns>The <see cref="UPnPPlugin"/>.</returns>
        private static UPnPPlugin GetInstance()
        {
            if (_instance == null)
            {
                throw new NullReferenceException(nameof(Instance));
            }

            return _instance;
        }

        /// <summary>
        /// Creates a logging instance for Mono.NAT.
        /// </summary>
        /// <param name="name">Name of instance.</param>
        /// <returns>ILogger implementation.</returns>
        private NATLogger GetLogger(string name)
        {
            return new LoggingInterface(_logger);
        }

        /// <summary>
        /// Converts the uPNP settings to a string.
        /// </summary>
        /// <returns>String representation of the settings.</returns>
        private string GetConfigIdentifier()
        {
            const char Separator = '|';
            return new StringBuilder(32)
                .Append(_networkConfig.HttpsPortNumber).Append(Separator)
                .Append(_networkConfig.HttpServerPortNumber).Append(Separator)
                .Append(Configuration.HttpDestPort).Append(Separator)
                .Append(Configuration.HttpsDestPort).Append(Separator)
                .Append(_appHost.ListenWithHttps).Append(Separator)
                .Append(_networkConfig.EnableRemoteAccess).Append(Separator)
                .ToString();
        }

        /// <summary>
        /// Triggered when the network configuration is updated.
        /// </summary>
        /// <param name="sender">Owner of the event.</param>
        /// <param name="e">Event parameters.</param>
        private void OnSystemConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
        {
            if (!e.Key.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _networkConfig = (NetworkConfiguration)e.NewConfiguration;
            UpdateConfiguration(Configuration);
        }

        /// <summary>
        /// Starts the discovery.
        /// </summary>
        private void Start()
        {
            if (!IsUPnPActive)
            {
                Configuration.Status = "Inactive.";
                return;
            }

            lock (_lock)
            {
                if (!_stopped)
                {
                    return;
                }

                _logger.LogInformation("Starting NAT discovery.");
                
                NatUtility.DeviceFound += DeviceFound;
                NatUtility.StartDiscovery();

                Configuration.Status = "Searching...";

                _networkManager.NetworkChanged += OnNetworkChange;
                var gmp = Math.Clamp(Configuration.GatewayMonitorPeriod, 0, 6000000);
                if (gmp > 0)
                {
                    if (_gatewayMonitor != null)
                    {
                        _gatewayMonitor.GatewayMonitorPeriod = gmp;
                    }
                    else
                    {
                        _gatewayMonitor = new GatewayMonitor(_logger, gmp);
                    }

                    _gatewayMonitor.OnGatewayFailure += OnGatewayFailed;
                }

                _stopped = false;
            }
        }

        private void OnGatewayFailed(object? sender, EventArgs e)
        {
            var args = (GatewayEventArgs)e;
            Configuration.Status = args.Gateway + " is not responding.";
            OnNetworkChange(sender, e);
        }

        /// <summary>
        /// Triggered when there is a change in the network.
        /// </summary>
        /// <param name="sender">INetworkManager object.</param>
        /// <param name="e">Event arguments.</param>
        private void OnNetworkChange(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (_stopped)
            {
                return;
            }

            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                NatUtility.StopDiscovery();
                _gatewayMonitor?.ResetGateways();
                NatUtility.StartDiscovery();
            }
        }

        /// <summary>
        /// Stops discovery, and releases resources.
        /// </summary>
        /// <param name="stopMonitoring">Dispose of the Gateway Monitor?.</param>
        private void Stop(bool stopMonitoring)
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                _logger.LogInformation("Stopping NAT discovery.");
                Configuration.Status = "Stopped";
                NatUtility.StopDiscovery();
                NatUtility.DeviceFound -= DeviceFound;

                if (_gatewayMonitor != null && stopMonitoring)
                {
                    _gatewayMonitor.ResetGateways();
                    _gatewayMonitor.OnGatewayFailure -= OnGatewayFailed;
                }

                _networkManager.NetworkChanged -= OnNetworkChange;

                foreach (INatDevice device in _devices)
                {
                    _ = RemoveRules(device);
                }

                _devices.Clear();
                _stopped = true;
            }
        }

        /// <summary>
        /// Triggered when a device is found.
        /// </summary>
        /// <param name="sender">Objects triggering the event.</param>
        /// <param name="e">Event arguments.</param>
        private async void DeviceFound(object? sender, DeviceEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _logger.LogDebug("Found Internet device");
                Configuration.Status = "Discovered " + e.Device;
                await CreateRules(e.Device).ConfigureAwait(false);

                _devices.Add(e.Device);
                Configuration.Status = "Active on " + e.Device;
                if (_gatewayMonitor != null)
                {
                    await _gatewayMonitor.AddGateway(e.Device.DeviceEndpoint.Address).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating port forwarding rules");
            }
        }
        
        /// <summary>
        /// Creates rules on a device.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <returns>Task.</returns>
        private Task CreateRules(INatDevice device)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            return Task.WhenAll(CreatePortMaps(device));
        }

        /// <summary>
        /// Attempts to create port mappings on a device.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <returns>IEnumerable.</returns>
        private IEnumerable<Task> CreatePortMaps(INatDevice device)
        {
            var config = Instance!.Configuration;
            if (config.CreateHttpPortMap)
            {
                yield return CreatePortMap(device, _networkConfig.HttpServerPortNumber, config.HttpDestPort);
            }

            if (_appHost.ListenWithHttps && config.CreateHttpsPortMap)
            {
                yield return CreatePortMap(device, _networkConfig.HttpsPortNumber, config.HttpsDestPort);
            }
        }

        /// <summary>
        /// Attempts to add a port mapping.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <param name="privatePort">Private port number.</param>
        /// <param name="publicPort">Public port number.</param>
        /// <returns>Task.</returns>
        private async Task CreatePortMap(INatDevice device, int privatePort, int publicPort)
        {
            _logger.LogDebug(
                "Creating port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}",
                privatePort,
                publicPort,
                device.DeviceEndpoint);

            try
            {
                var mapping = new Mapping(Protocol.Tcp, privatePort, publicPort, 0, _appHost.Name);
                await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error creating port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}.",
                    privatePort,
                    publicPort,
                    device.DeviceEndpoint);
            }
        }

        /// <summary>
        /// Attempts to removes rules from a device.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <returns>Task.</returns>
        private Task RemoveRules(INatDevice device)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            return Task.WhenAll(RemovePortMaps(device));
        }

        /// <summary>
        /// Attempts to remove port mappings from a device.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <returns>IEnumerable.</returns>
        private IEnumerable<Task> RemovePortMaps(INatDevice device)
        {
            if (Configuration.CreateHttpPortMap)
            {
                yield return RemovePortMap(device, Configuration.HttpDestPort, Configuration.HttpDestPort);
            }

            if (Configuration.CreateHttpsPortMap)
            {
                yield return RemovePortMap(device, Configuration.HttpsDestPort, Configuration.HttpsDestPort);
            }
        }

        /// <summary>
        /// Attempts to remove a port mapping.
        /// </summary>
        /// <param name="device">Destination device.</param>
        /// <param name="privatePort">Private port number.</param>
        /// <param name="publicPort">Public port number.</param>
        /// <returns>Task.</returns>
        private async Task RemovePortMap(INatDevice device, int privatePort, int publicPort)
        {
            _logger.LogInformation(
                "Removing port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}",
                privatePort,
                publicPort,
                device.DeviceEndpoint);

            try
            {
                var mapping = new Mapping(Protocol.Tcp, privatePort, publicPort, 0, _appHost.Name);
                await device.DeletePortMapAsync(mapping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error removing port map on local port {LocalPort} to public port {PublicPort} with device {DeviceEndpoint}.",
                    privatePort,
                    publicPort,
                    device.DeviceEndpoint);
            }
        }

        /// <summary>
        /// Interface class that transpose Mono.NAT logs into our logging system.
        /// </summary>
        private class LoggingInterface : NATLogger
        {
            private readonly ILogger _logger;

            /// <summary>
            /// Initializes a new instance of the <see cref="LoggingInterface"/> class.
            /// </summary>
            /// <param name="logger">ILogger instance to use.</param>
            public LoggingInterface(ILogger logger) => _logger = logger;

            /// <summary>
            /// The Info.
            /// </summary>
            /// <param name="message">The message<see cref="string"/>.</param>
            public void Info(string message)
            {
                if (!Instance!.Configuration.DebugLogging)
                {
                    return;
                }

                _logger.LogInformation(message.Replace("\r", " ", StringComparison.OrdinalIgnoreCase).Replace("\n", string.Empty, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>
            /// The Debug.
            /// </summary>
            /// <param name="message">The message<see cref="string"/>.</param>
            public void Debug(string message)
            {
                if (!Instance!.Configuration.DebugLogging)
                {
                    return;
                }

                _logger.LogDebug(message.Replace("\r", " ", StringComparison.OrdinalIgnoreCase).Replace("\n", string.Empty, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>
            /// The Error.
            /// </summary>
            /// <param name="message">The message<see cref="string"/>.</param>
            public void Error(string message)
            {
                if (!Instance!.Configuration.DebugLogging)
                {
                    return;
                }

                _logger.LogError(message.Replace("\r", " ", StringComparison.OrdinalIgnoreCase).Replace("\n", string.Empty, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
