using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Jellyfin.Networking.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Networking.Manager
{
    /// <summary>
    /// Class to take care of network interface management.
    /// </summary>
    public class NetworkManager : INetworkManager, IDisposable
    {
        /// <summary>
        /// Threading lock for network properties.
        /// </summary>
        private readonly object _initLock;

        private readonly ILogger<NetworkManager> _logger;

        private readonly IConfigurationManager _configurationManager;

        private readonly object _networkEventLock;

        /// <summary>
        /// Holds the published server URLs and the IPs to use them on.
        /// </summary>
        private IReadOnlyDictionary<IPData, string> _publishedServerUrls;

        private IReadOnlyList<IPNetwork> _remoteAddressFilter;

        /// <summary>
        /// Used to stop "event-racing conditions".
        /// </summary>
        private bool _eventfire;

        /// <summary>
        /// List of all interface MAC addresses.
        /// </summary>
        private IReadOnlyList<PhysicalAddress> _macAddresses;

        /// <summary>
        /// Dictionary containing interface addresses and their subnets.
        /// </summary>
        private IReadOnlyList<IPData> _interfaces;

        /// <summary>
        /// Unfiltered user defined LAN subnets (<see cref="NetworkConfiguration.LocalNetworkSubnets"/>)
        /// or internal interface network subnets if undefined by user.
        /// </summary>
        private IReadOnlyList<IPNetwork> _lanSubnets;

        /// <summary>
        /// User defined list of subnets to excluded from the LAN.
        /// </summary>
        private IReadOnlyList<IPNetwork> _excludedSubnets;

        /// <summary>
        /// True if this object is disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkManager"/> class.
        /// </summary>
        /// <param name="configurationManager">IServerConfigurationManager instance.</param>
        /// <param name="logger">Logger to use for messages.</param>
#pragma warning disable CS8618 // Non-nullable field is uninitialized. : Values are set in UpdateSettings function. Compiler doesn't yet recognise this.
        public NetworkManager(IConfigurationManager configurationManager, ILogger<NetworkManager> logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(configurationManager);

            _logger = logger;
            _configurationManager = configurationManager;
            _initLock = new();
            _interfaces = new List<IPData>();
            _macAddresses = new List<PhysicalAddress>();
            _publishedServerUrls = new Dictionary<IPData, string>();
            _networkEventLock = new object();
            _remoteAddressFilter = new List<IPNetwork>();

            UpdateSettings(_configurationManager.GetNetworkConfiguration());

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            _configurationManager.NamedConfigurationUpdated += ConfigurationUpdated;
        }
#pragma warning restore CS8618 // Non-nullable field is uninitialized.

        /// <summary>
        /// Event triggered on network changes.
        /// </summary>
        public event EventHandler? NetworkChanged;

        /// <summary>
        /// Gets or sets a value indicating whether testing is taking place.
        /// </summary>
        public static string MockNetworkSettings { get; set; } = string.Empty;

        /// <summary>
        /// Gets a value indicating whether IP4 is enabled.
        /// </summary>
        public bool IsIPv4Enabled => _configurationManager.GetNetworkConfiguration().EnableIPv4;

        /// <summary>
        /// Gets a value indicating whether IP6 is enabled.
        /// </summary>
        public bool IsIPv6Enabled => _configurationManager.GetNetworkConfiguration().EnableIPv6;

        /// <summary>
        /// Gets a value indicating whether is all IPv6 interfaces are trusted as internal.
        /// </summary>
        public bool TrustAllIPv6Interfaces { get; private set; }

        /// <summary>
        /// Gets the Published server override list.
        /// </summary>
        public IReadOnlyDictionary<IPData, string> PublishedServerUrls => _publishedServerUrls;

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Handler for network change events.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">A <see cref="NetworkAvailabilityEventArgs"/> containing network availability information.</param>
        private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            _logger.LogDebug("Network availability changed.");
            HandleNetworkChange();
        }

        /// <summary>
        /// Handler for network change events.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">An <see cref="EventArgs"/>.</param>
        private void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            _logger.LogDebug("Network address change detected.");
            HandleNetworkChange();
        }

        /// <summary>
        /// Triggers our event, and re-loads interface information.
        /// </summary>
        private void HandleNetworkChange()
        {
            lock (_networkEventLock)
            {
                if (!_eventfire)
                {
                    _logger.LogDebug("Network Address Change Event.");
                    // As network events tend to fire one after the other only fire once every second.
                    _eventfire = true;
                    OnNetworkChange();
                }
            }
        }

        /// <summary>
        /// Waits for 2 seconds before re-initialising the settings, as typically these events fire multiple times in succession.
        /// </summary>
        private void OnNetworkChange()
        {
            try
            {
                Thread.Sleep(2000);
                var networkConfig = _configurationManager.GetNetworkConfiguration();
                if (IsIPv6Enabled && !Socket.OSSupportsIPv6)
                {
                    UpdateSettings(networkConfig);
                }
                else
                {
                    InitialiseInterfaces();
                    InitialiseLan(networkConfig);
                    EnforceBindSettings(networkConfig);
                }

                NetworkChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _eventfire = false;
            }
        }

        /// <summary>
        /// Generate a list of all the interface ip addresses and submasks where that are in the active/unknown state.
        /// Generate a list of all active mac addresses that aren't loopback addresses.
        /// </summary>
        private void InitialiseInterfaces()
        {
            lock (_initLock)
            {
                _logger.LogDebug("Refreshing interfaces.");

                var interfaces = new List<IPData>();
                var macAddresses = new List<PhysicalAddress>();

                try
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(i => i.SupportsMulticast && i.OperationalStatus == OperationalStatus.Up);

                    foreach (NetworkInterface adapter in nics)
                    {
                        try
                        {
                            var ipProperties = adapter.GetIPProperties();
                            var mac = adapter.GetPhysicalAddress();

                            // Populate MAC list
                            if (adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback && PhysicalAddress.None.Equals(mac))
                            {
                                macAddresses.Add(mac);
                            }

                            // Populate interface list
                            foreach (var info in ipProperties.UnicastAddresses)
                            {
                                if (IsIPv4Enabled && info.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    var interfaceObject = new IPData(info.Address, new IPNetwork(info.Address, info.PrefixLength), adapter.Name);
                                    interfaceObject.Index = ipProperties.GetIPv4Properties().Index;
                                    interfaceObject.Name = adapter.Name;

                                    interfaces.Add(interfaceObject);
                                }
                                else if (IsIPv6Enabled && info.Address.AddressFamily == AddressFamily.InterNetworkV6)
                                {
                                    var interfaceObject = new IPData(info.Address, new IPNetwork(info.Address, info.PrefixLength), adapter.Name);
                                    interfaceObject.Index = ipProperties.GetIPv6Properties().Index;
                                    interfaceObject.Name = adapter.Name;

                                    interfaces.Add(interfaceObject);
                                }
                            }
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                        {
                            // Ignore error, and attempt to continue.
                            _logger.LogError(ex, "Error encountered parsing interfaces.");
                        }
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    _logger.LogError(ex, "Error obtaining interfaces.");
                }

                // If no interfaces are found, fallback to loopback interfaces.
                if (interfaces.Count == 0)
                {
                    _logger.LogWarning("No interface information available. Using loopback interface(s).");

                    if (IsIPv4Enabled && !IsIPv6Enabled)
                    {
                        interfaces.Add(new IPData(IPAddress.Loopback, new IPNetwork(IPAddress.Loopback, 8), "lo"));
                    }

                    if (!IsIPv4Enabled && IsIPv6Enabled)
                    {
                        interfaces.Add(new IPData(IPAddress.IPv6Loopback, new IPNetwork(IPAddress.IPv6Loopback, 128), "lo"));
                    }
                }

                _logger.LogDebug("Discovered {NumberOfInterfaces} interfaces.", interfaces.Count);
                _logger.LogDebug("Interfaces addresses: {Addresses}", interfaces.OrderByDescending(s => s.AddressFamily == AddressFamily.InterNetwork).Select(s => s.Address.ToString()));

                _macAddresses = macAddresses;
                _interfaces = interfaces;
            }
        }

        /// <summary>
        /// Initialises internal LAN cache.
        /// </summary>
        private void InitialiseLan(NetworkConfiguration config)
        {
            lock (_initLock)
            {
                _logger.LogDebug("Refreshing LAN information.");

                // Get configuration options
                var subnets = config.LocalNetworkSubnets;

                // If no LAN addresses are specified, all private subnets and Loopback are deemed to be the LAN
                if (!NetworkExtensions.TryParseToSubnets(subnets, out var lanSubnets, false) || lanSubnets.Count == 0)
                {
                    _logger.LogDebug("Using LAN interface addresses as user provided no LAN details.");

                    var fallbackLanSubnets = new List<IPNetwork>();
                    if (IsIPv6Enabled)
                    {
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.IPv6Loopback, 128)); // RFC 4291 (Loopback)
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Parse("fe80::"), 10)); // RFC 4291 (Site local)
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Parse("fc00::"), 7)); // RFC 4193 (Unique local)
                    }

                    if (IsIPv4Enabled)
                    {
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Loopback, 8)); // RFC 5735 (Loopback)
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8)); // RFC 1918 (private)
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Parse("172.16.0.0"), 12)); // RFC 1918 (private)
                        fallbackLanSubnets.Add(new IPNetwork(IPAddress.Parse("192.168.0.0"), 16)); // RFC 1918 (private)
                    }

                    _lanSubnets = fallbackLanSubnets;
                }
                else
                {
                    _lanSubnets = lanSubnets;
                }

                _excludedSubnets = NetworkExtensions.TryParseToSubnets(subnets, out var excludedSubnets, true)
                    ? excludedSubnets
                    : new List<IPNetwork>();

                _logger.LogInformation("Defined LAN addresses: {0}", _lanSubnets.Select(s => s.Prefix + "/" + s.PrefixLength));
                _logger.LogInformation("Defined LAN exclusions: {0}", _excludedSubnets.Select(s => s.Prefix + "/" + s.PrefixLength));
                _logger.LogInformation("Using LAN addresses: {0}", _lanSubnets.Where(s => !_excludedSubnets.Contains(s)).Select(s => s.Prefix + "/" + s.PrefixLength));
            }
        }

        /// <summary>
        /// Enforce bind addresses and exclusions on available interfaces.
        /// </summary>
        private void EnforceBindSettings(NetworkConfiguration config)
        {
            lock (_initLock)
            {
                // Respect explicit bind addresses
                var interfaces = _interfaces.ToList();
                var localNetworkAddresses = config.LocalNetworkAddresses;
                if (localNetworkAddresses.Length > 0 && !string.IsNullOrWhiteSpace(localNetworkAddresses[0]))
                {
                    var bindAddresses = localNetworkAddresses.Select(p => NetworkExtensions.TryParseToSubnet(p, out var network)
                        ? network.Prefix
                        : (interfaces.Where(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.Address)
                            .FirstOrDefault() ?? IPAddress.None))
                        .Where(x => x != IPAddress.None)
                        .ToHashSet();
                    interfaces = interfaces.Where(x => bindAddresses.Contains(x.Address)).ToList();

                    if (bindAddresses.Contains(IPAddress.Loopback))
                    {
                        interfaces.Add(new IPData(IPAddress.Loopback, new IPNetwork(IPAddress.Loopback, 8), "lo"));
                    }

                    if (bindAddresses.Contains(IPAddress.IPv6Loopback))
                    {
                        interfaces.Add(new IPData(IPAddress.IPv6Loopback, new IPNetwork(IPAddress.IPv6Loopback, 128), "lo"));
                    }
                }

                // Remove all interfaces matching any virtual machine interface prefix
                if (config.IgnoreVirtualInterfaces)
                {
                    // Remove potentially existing * and split config string into prefixes
                    var virtualInterfacePrefixes = config.VirtualInterfaceNames
                        .Select(i => i.Replace("*", string.Empty, StringComparison.OrdinalIgnoreCase));

                    // Check all interfaces for matches against the prefixes and remove them
                    if (_interfaces.Count > 0)
                    {
                        foreach (var virtualInterfacePrefix in virtualInterfacePrefixes)
                        {
                            interfaces.RemoveAll(x => x.Name.StartsWith(virtualInterfacePrefix, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }

                // Remove all IPv4 interfaces if IPv4 is disabled
                if (!IsIPv4Enabled)
                {
                    interfaces.RemoveAll(x => x.AddressFamily == AddressFamily.InterNetwork);
                }

                // Remove all IPv6 interfaces if IPv6 is disabled
                if (!IsIPv6Enabled)
                {
                    interfaces.RemoveAll(x => x.AddressFamily == AddressFamily.InterNetworkV6);
                }

                _logger.LogInformation("Using bind addresses: {0}", interfaces.OrderByDescending(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.Address));
                _interfaces = interfaces;
            }
        }

        /// <summary>
        /// Initialises the remote address values.
        /// </summary>
        private void InitialiseRemote(NetworkConfiguration config)
        {
            lock (_initLock)
            {
                // Parse config values into filter collection
                var remoteIPFilter = config.RemoteIPFilter;
                if (remoteIPFilter.Any() && !string.IsNullOrWhiteSpace(remoteIPFilter.First()))
                {
                    // Parse all IPs with netmask to a subnet
                    var remoteAddressFilter = new List<IPNetwork>();
                    var remoteFilteredSubnets = remoteIPFilter.Where(x => x.Contains('/', StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (NetworkExtensions.TryParseToSubnets(remoteFilteredSubnets, out var remoteAddressFilterResult, false))
                    {
                        remoteAddressFilter = remoteAddressFilterResult.ToList();
                    }

                    // Parse everything else as an IP and construct subnet with a single IP
                    var remoteFilteredIPs = remoteIPFilter.Where(x => !x.Contains('/', StringComparison.OrdinalIgnoreCase));
                    foreach (var ip in remoteFilteredIPs)
                    {
                        if (IPAddress.TryParse(ip, out var ipp))
                        {
                            remoteAddressFilter.Add(new IPNetwork(ipp, ipp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
                        }
                    }

                    _remoteAddressFilter = remoteAddressFilter;
                }
            }
        }

        /// <summary>
        /// Parses the user defined overrides into the dictionary object.
        /// Overrides are the equivalent of localised publishedServerUrl, enabling
        /// different addresses to be advertised over different subnets.
        /// format is subnet=ipaddress|host|uri
        /// when subnet = 0.0.0.0, any external address matches.
        /// </summary>
        private void InitialiseOverrides(NetworkConfiguration config)
        {
            lock (_initLock)
            {
                var publishedServerUrls = new Dictionary<IPData, string>();
                var overrides = config.PublishedServerUriBySubnet;

                foreach (var entry in overrides)
                {
                    var parts = entry.Split('=');
                    if (parts.Length != 2)
                    {
                        _logger.LogError("Unable to parse bind override: {Entry}", entry);
                        return;
                    }

                    var replacement = parts[1].Trim();
                    var identifier = parts[0];
                    if (string.Equals(identifier, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        publishedServerUrls[new IPData(IPAddress.Broadcast, null)] = replacement;
                    }
                    else if (string.Equals(identifier, "external", StringComparison.OrdinalIgnoreCase))
                    {
                        publishedServerUrls[new IPData(IPAddress.Any, new IPNetwork(IPAddress.Any, 0))] = replacement;
                        publishedServerUrls[new IPData(IPAddress.IPv6Any, new IPNetwork(IPAddress.IPv6Any, 0))] = replacement;
                    }
                    else if (string.Equals(identifier, "internal", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var lan in _lanSubnets)
                        {
                            var lanPrefix = lan.Prefix;
                            publishedServerUrls[new IPData(lanPrefix, new IPNetwork(lanPrefix, lan.PrefixLength))] = replacement;
                        }
                    }
                    else if (NetworkExtensions.TryParseToSubnet(identifier, out var result) && result is not null)
                    {
                        var data = new IPData(result.Prefix, result);
                        publishedServerUrls[data] = replacement;
                    }
                    else if (TryParseInterface(identifier, out var ifaces))
                    {
                        foreach (var iface in ifaces)
                        {
                            publishedServerUrls[iface] = replacement;
                        }
                    }
                    else
                    {
                        _logger.LogError("Unable to parse bind override: {Entry}", entry);
                    }
                }

                _publishedServerUrls = publishedServerUrls;
            }
        }

        private void ConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs evt)
        {
            if (evt.Key.Equals(NetworkConfigurationStore.StoreKey, StringComparison.Ordinal))
            {
                UpdateSettings((NetworkConfiguration)evt.NewConfiguration);
            }
        }

        /// <summary>
        /// Reloads all settings and re-initialises the instance.
        /// </summary>
        /// <param name="configuration">The <see cref="NetworkConfiguration"/> to use.</param>
        public void UpdateSettings(object configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var config = (NetworkConfiguration)configuration;
            HappyEyeballs.HttpClientExtension.UseIPv6 = config.EnableIPv6;

            InitialiseLan(config);
            InitialiseRemote(config);

            if (string.IsNullOrEmpty(MockNetworkSettings))
            {
                InitialiseInterfaces();
            }
            else // Used in testing only.
            {
                // Format is <IPAddress>,<Index>,<Name>: <next interface>. Set index to -ve to simulate a gateway.
                var interfaceList = MockNetworkSettings.Split('|');
                var interfaces = new List<IPData>();
                foreach (var details in interfaceList)
                {
                    var parts = details.Split(',');
                    if (NetworkExtensions.TryParseToSubnet(parts[0], out var subnet))
                    {
                        var address = subnet.Prefix;
                        var index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        if (address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            var data = new IPData(address, subnet, parts[2]);
                            data.Index = index;
                            interfaces.Add(data);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not parse mock interface settings: {Part}", details);
                    }
                }

                _interfaces = interfaces;
            }

            EnforceBindSettings(config);
            InitialiseOverrides(config);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern.
        /// </summary>
        /// <param name="disposing"><c>True</c> to dispose the managed state.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _configurationManager.NamedConfigurationUpdated -= ConfigurationUpdated;
                    NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                    NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                }

                _disposed = true;
            }
        }

        /// <inheritdoc/>
        public bool TryParseInterface(string intf, [NotNullWhen(true)] out IReadOnlyList<IPData>? result)
        {
            if (string.IsNullOrEmpty(intf)
                || _interfaces is null
                || _interfaces.Count == 0)
            {
                result = null;
                return false;
            }

            // Match all interfaces starting with names starting with token
            result = _interfaces
                .Where(i => i.Name.Equals(intf, StringComparison.OrdinalIgnoreCase)
                    && ((IsIPv4Enabled && i.Address.AddressFamily == AddressFamily.InterNetwork)
                        || (IsIPv6Enabled && i.Address.AddressFamily == AddressFamily.InterNetworkV6)))
                .OrderBy(x => x.Index)
                .ToArray();
            return result.Count > 0;
        }

        /// <inheritdoc/>
        public bool HasRemoteAccess(IPAddress remoteIP)
        {
            var config = _configurationManager.GetNetworkConfiguration();
            if (config.EnableRemoteAccess)
            {
                // Comma separated list of IP addresses or IP/netmask entries for networks that will be allowed to connect remotely.
                // If left blank, all remote addresses will be allowed.
                if (_remoteAddressFilter.Any() && !_lanSubnets.Any(x => x.Contains(remoteIP)))
                {
                    // remoteAddressFilter is a whitelist or blacklist.
                    var matches = _remoteAddressFilter.Count(remoteNetwork => remoteNetwork.Contains(remoteIP));
                    if ((!config.IsRemoteIPFilterBlacklist && matches > 0)
                        || (config.IsRemoteIPFilterBlacklist && matches == 0))
                    {
                        return true;
                    }

                    return false;
                }
            }
            else if (!_lanSubnets.Any(x => x.Contains(remoteIP)))
            {
                // Remote not enabled. So everyone should be LAN.
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public IReadOnlyList<PhysicalAddress> GetMacAddresses()
        {
            // Populated in construction - so always has values.
            return _macAddresses;
        }

        /// <inheritdoc/>
        public IReadOnlyList<IPData> GetLoopbacks()
        {
            if (!IsIPv4Enabled && !IsIPv6Enabled)
            {
                return Array.Empty<IPData>();
            }

            var loopbackNetworks = new List<IPData>();
            if (IsIPv4Enabled)
            {
                loopbackNetworks.Add(new IPData(IPAddress.Loopback, new IPNetwork(IPAddress.Loopback, 8), "lo"));
            }

            if (IsIPv6Enabled)
            {
                loopbackNetworks.Add(new IPData(IPAddress.IPv6Loopback, new IPNetwork(IPAddress.IPv6Loopback, 128), "lo"));
            }

            return loopbackNetworks;
        }

        /// <inheritdoc/>
        public IReadOnlyList<IPData> GetAllBindInterfaces(bool individualInterfaces = false)
        {
            if (_interfaces.Count != 0)
            {
                return _interfaces;
            }

            // No bind address and no exclusions, so listen on all interfaces.
            var result = new List<IPData>();

            if (individualInterfaces)
            {
                result.AddRange(_interfaces);
                return result;
            }

            if (IsIPv4Enabled && IsIPv6Enabled)
            {
                // Kestrel source code shows it uses Sockets.DualMode - so this also covers IPAddress.Any by default
                result.Add(new IPData(IPAddress.IPv6Any, new IPNetwork(IPAddress.IPv6Any, 0)));
            }
            else if (IsIPv4Enabled)
            {
                result.Add(new IPData(IPAddress.Any, new IPNetwork(IPAddress.Any, 0)));
            }
            else if (IsIPv6Enabled)
            {
                // Cannot use IPv6Any as Kestrel will bind to IPv4 addresses too.
                foreach (var iface in _interfaces)
                {
                    if (iface.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        result.Add(iface);
                    }
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public string GetBindAddress(string source, out int? port)
        {
            if (!NetworkExtensions.TryParseHost(source, out var addresses, IsIPv4Enabled, IsIPv6Enabled))
            {
                addresses = Array.Empty<IPAddress>();
            }

            var result = GetBindAddress(addresses.FirstOrDefault(), out port);
            return result;
        }

        /// <inheritdoc/>
        public string GetBindAddress(HttpRequest source, out int? port)
        {
            var result = GetBindAddress(source.Host.Host, out port);
            port ??= source.Host.Port;

            return result;
        }

        /// <inheritdoc/>
        public string GetBindAddress(IPAddress? source, out int? port, bool skipOverrides = false)
        {
            port = null;

            string result;

            if (source is not null)
            {
                if (IsIPv4Enabled && !IsIPv6Enabled && source.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    _logger.LogWarning("IPv6 is disabled in Jellyfin, but enabled in the OS. This may affect how the interface is selected.");
                }

                if (!IsIPv4Enabled && IsIPv6Enabled && source.AddressFamily == AddressFamily.InterNetwork)
                {
                    _logger.LogWarning("IPv4 is disabled in Jellyfin, but enabled in the OS. This may affect how the interface is selected.");
                }

                bool isExternal = !_lanSubnets.Any(network => network.Contains(source));
                _logger.LogDebug("Trying to get bind address for source {Source} - External: {IsExternal}", source, isExternal);

                if (!skipOverrides && MatchesPublishedServerUrl(source, isExternal, out result))
                {
                    return result;
                }

                // No preference given, so move on to bind addresses.
                if (MatchesBindInterface(source, isExternal, out result))
                {
                    return result;
                }

                if (isExternal && MatchesExternalInterface(source, out result))
                {
                    return result;
                }
            }

            // Get the first LAN interface address that's not excluded and not a loopback address.
            // Get all available interfaces, prefer local interfaces
            var availableInterfaces = _interfaces.Where(x => !IPAddress.IsLoopback(x.Address))
                .OrderByDescending(x => IsInLocalNetwork(x.Address))
                .ThenBy(x => x.Index)
                .ToList();

            if (availableInterfaces.Count == 0)
            {
                // There isn't any others, so we'll use the loopback.
                result = IsIPv4Enabled && !IsIPv6Enabled ? "127.0.0.1" : "::1";
                _logger.LogWarning("{Source}: Only loopback {Result} returned, using that as bind address.", source, result);
                return result;
            }

            // If no source address is given, use the preferred (first) interface
            if (source is null)
            {
                result = NetworkExtensions.FormatIPString(availableInterfaces.First().Address);
                _logger.LogDebug("{Source}: Using first internal interface as bind address: {Result}", source, result);
                return result;
            }

            // Does the request originate in one of the interface subnets?
            // (For systems with multiple internal network cards, and multiple subnets)
            foreach (var intf in availableInterfaces)
            {
                if (intf.Subnet.Contains(source))
                {
                    result = NetworkExtensions.FormatIPString(intf.Address);
                    _logger.LogDebug("{Source}: Found interface with matching subnet, using it as bind address: {Result}", source, result);
                    return result;
                }
            }

            // Fallback to first available interface
            result = NetworkExtensions.FormatIPString(availableInterfaces[0].Address);
            _logger.LogDebug("{Source}: No matching interfaces found, using preferred interface as bind address: {Result}", source, result);
            return result;
        }

        /// <inheritdoc/>
        public IReadOnlyList<IPData> GetInternalBindAddresses()
        {
            // Select all local bind addresses
            return _interfaces.Where(x => IsInLocalNetwork(x.Address))
                .OrderBy(x => x.Index)
                .ToList();
        }

        /// <inheritdoc/>
        public bool IsInLocalNetwork(string address)
        {
            if (NetworkExtensions.TryParseToSubnet(address, out var subnet))
            {
                return IPAddress.IsLoopback(subnet.Prefix) || (_lanSubnets.Any(x => x.Contains(subnet.Prefix)) && !_excludedSubnets.Any(x => x.Contains(subnet.Prefix)));
            }

            if (NetworkExtensions.TryParseHost(address, out var addresses, IsIPv4Enabled, IsIPv6Enabled))
            {
                foreach (var ept in addresses)
                {
                    if (IPAddress.IsLoopback(ept) || (_lanSubnets.Any(x => x.Contains(ept)) && !_excludedSubnets.Any(x => x.Contains(ept))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public bool IsInLocalNetwork(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);

            // See conversation at https://github.com/jellyfin/jellyfin/pull/3515.
            if ((TrustAllIPv6Interfaces && address.AddressFamily == AddressFamily.InterNetworkV6)
                || address.Equals(IPAddress.Loopback)
                || address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            // As private addresses can be redefined by Configuration.LocalNetworkAddresses
            return CheckIfLanAndNotExcluded(address);
        }

        private bool CheckIfLanAndNotExcluded(IPAddress address)
        {
            foreach (var lanSubnet in _lanSubnets)
            {
                if (lanSubnet.Contains(address))
                {
                    foreach (var excludedSubnet in _excludedSubnets)
                    {
                        if (excludedSubnet.Contains(address))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to match the source against the published server URL overrides.
        /// </summary>
        /// <param name="source">IP source address to use.</param>
        /// <param name="isInExternalSubnet">True if the source is in an external subnet.</param>
        /// <param name="bindPreference">The published server URL that matches the source address.</param>
        /// <returns><c>true</c> if a match is found, <c>false</c> otherwise.</returns>
        private bool MatchesPublishedServerUrl(IPAddress source, bool isInExternalSubnet, out string bindPreference)
        {
            bindPreference = string.Empty;
            int? port = null;

            var validPublishedServerUrls = _publishedServerUrls.Where(x => x.Key.Address.Equals(IPAddress.Any)
                                                || x.Key.Address.Equals(IPAddress.IPv6Any)
                                                || x.Key.Subnet.Contains(source))
                                            .DistinctBy(x => x.Key)
                                            .OrderBy(x => x.Key.Address.Equals(IPAddress.Any)
                                                || x.Key.Address.Equals(IPAddress.IPv6Any))
                                            .ToList();

            // Check for user override.
            foreach (var data in validPublishedServerUrls)
            {
                if (isInExternalSubnet && (data.Key.Address.Equals(IPAddress.Any) || data.Key.Address.Equals(IPAddress.IPv6Any)))
                {
                    // External.
                    bindPreference = data.Value;
                    break;
                }

                // Get address interface.
                var intf = _interfaces.OrderBy(x => x.Index).FirstOrDefault(x => data.Key.Subnet.Contains(x.Address));

                if (intf?.Address is not null)
                {
                    // Match IP address.
                    bindPreference = data.Value;
                    break;
                }
            }

            if (string.IsNullOrEmpty(bindPreference))
            {
                _logger.LogDebug("{Source}: No matching bind address override found", source);
                return false;
            }

            // Has it got a port defined?
            var parts = bindPreference.Split(':');
            if (parts.Length > 1)
            {
                if (int.TryParse(parts[1], out int p))
                {
                    bindPreference = parts[0];
                    port = p;
                }
            }

            if (port is not null)
            {
                _logger.LogDebug("{Source}: Matching bind address override found: {Address}:{Port}", source, bindPreference, port);
            }
            else
            {
                _logger.LogDebug("{Source}: Matching bind address override found: {Address}", source, bindPreference);
            }

            return true;
        }

        /// <summary>
        /// Attempts to match the source against the user defined bind interfaces.
        /// </summary>
        /// <param name="source">IP source address to use.</param>
        /// <param name="isInExternalSubnet">True if the source is in the external subnet.</param>
        /// <param name="result">The result, if a match is found.</param>
        /// <returns><c>true</c> if a match is found, <c>false</c> otherwise.</returns>
        private bool MatchesBindInterface(IPAddress source, bool isInExternalSubnet, out string result)
        {
            result = string.Empty;

            int count = _interfaces.Count;
            if (count == 1 && (_interfaces[0].Equals(IPAddress.Any) || _interfaces[0].Equals(IPAddress.IPv6Any)))
            {
                // Ignore IPAny addresses.
                count = 0;
            }

            if (count == 0)
            {
                return false;
            }

            IPAddress? bindAddress = null;
            if (isInExternalSubnet)
            {
                var externalInterfaces = _interfaces.Where(x => !IsInLocalNetwork(x.Address))
                    .OrderBy(x => x.Index)
                    .ToList();
                if (externalInterfaces.Count > 0)
                {
                    // Check to see if any of the external bind interfaces are in the same subnet as the source.
                    // If none exists, this will select the first external interface if there is one.
                    bindAddress = externalInterfaces
                        .OrderByDescending(x => x.Subnet.Contains(source))
                        .ThenBy(x => x.Index)
                        .Select(x => x.Address)
                        .First();

                    result = NetworkExtensions.FormatIPString(bindAddress);
                    _logger.LogDebug("{Source}: External request received, matching external bind address found: {Result}", source, result);
                    return true;
                }

                _logger.LogWarning("{Source}: External request received, no matching external bind address found, trying internal addresses.", source);
            }
            else
            {
                // Check to see if any of the internal bind interfaces are in the same subnet as the source.
                // If none exists, this will select the first internal interface if there is one.
                bindAddress = _interfaces.Where(x => IsInLocalNetwork(x.Address))
                    .OrderByDescending(x => x.Subnet.Contains(source))
                    .ThenBy(x => x.Index)
                    .Select(x => x.Address)
                    .FirstOrDefault();

                if (bindAddress is not null)
                {
                    result = NetworkExtensions.FormatIPString(bindAddress);
                    _logger.LogDebug("{Source}: Internal request received, matching internal bind address found: {Result}", source, result);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to match the source against external interfaces.
        /// </summary>
        /// <param name="source">IP source address to use.</param>
        /// <param name="result">The result, if a match is found.</param>
        /// <returns><c>true</c> if a match is found, <c>false</c> otherwise.</returns>
        private bool MatchesExternalInterface(IPAddress source, out string result)
        {
            // Get the first external interface address that isn't a loopback.
            var extResult = _interfaces.Where(p => !IsInLocalNetwork(p.Address)).OrderBy(x => x.Index).ToArray();

            // No external interface found
            if (extResult.Length == 0)
            {
                result = string.Empty;
                _logger.LogWarning("{Source}: External request received, but no external interface found. Need to route through internal network.", source);
                return false;
            }

            // Does the request originate in one of the interface subnets?
            // (For systems with multiple network cards and/or multiple subnets)
            foreach (var intf in extResult)
            {
                if (intf.Subnet.Contains(source))
                {
                    result = NetworkExtensions.FormatIPString(intf.Address);
                    _logger.LogDebug("{Source}: Found external interface with matching subnet, using it as bind address: {Result}", source, result);
                    return true;
                }
            }

            // Fallback to first external interface.
            result = NetworkExtensions.FormatIPString(extResult.First().Address);
            _logger.LogDebug("{Source}: Using first external interface as bind address: {Result}", source, result);
            return true;
        }
    }
}
