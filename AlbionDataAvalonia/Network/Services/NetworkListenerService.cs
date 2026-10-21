using AFMDataClient.Core;
using Albion.Network;
using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Items.Services;
using AlbionDataAvalonia.Legendary;
using AlbionDataAvalonia.Logging;
using AlbionDataAvalonia.Network.Handlers;
using AlbionDataAvalonia.Network.Models;
using AlbionDataAvalonia.Party;
using AlbionDataAvalonia.Players;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.State;
using Microsoft.Win32;
using PacketDotNet;
using PhotonPackageParser;
using Serilog;
using SharpPcap;
using SharpPcap.LibPcap;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Network.Services
{
    public class NetworkListenerService : IDisposable
    {
        private const string MacOSCapturePermissionSetupScriptName = "setup-capture-permissions.sh";
        private const string MacOSCapturePermissionLaunchDaemonPath =
            "/Library/LaunchDaemons/com.albionfreemarket.afmdataclient.chmodbpf.plist";
        private const string MacOSLegacyCapturePermissionScheduleKey = "<key>StartInterval</key>";
        private const ushort AlbionGamePort = 5056;
        private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes(1);
        private readonly HashSet<string> _unknownServerIps = new HashSet<string>();
        private readonly object _unknownServerIpsLock = new object();
        // Adapter names and why one couldn't open, for Copy diagnostics. Keyed by the driver's device name.
        private volatile IReadOnlyDictionary<string, string> _knownAdapters = new Dictionary<string, string>();
        private readonly ConcurrentDictionary<string, bool> _adapterOpenFailures = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, LinkLayers> _adapterLinkTypes = new(StringComparer.Ordinal);
        // Adapters the system has up that the capture driver doesn't list, such as a VPN tunnel Npcap isn't bound to.
        private volatile IReadOnlyList<string> _adaptersMissingFromCapture = [];
        private readonly HashSet<string> _loggedMissingAdapters = new(StringComparer.Ordinal);
        private string? _lastPinnedAdapterLog;
        private int _gameTrafficTransport;
        private int _captureRestartCount;
        private CaptureRestart? _lastCaptureRestart;
        private readonly Dictionary<string, long> _failureLogTimestamps =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object _failureLogLock = new object();
        private readonly object _lifecycleStateLock = new object();
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _supervisorSignal = new SemaphoreSlim(0, 1);

        private readonly PlayerState _playerState;
        private readonly SettingsManager _settingsManager;
        private readonly MailService _mailService;
        private readonly TradeService _tradeService;
        private readonly ItemEstimatedMarketValueService _itemEstimatedMarketValues;
        private readonly ActivityTrackerService _activityTracker;
        private readonly PartyTrackerService _partyTracker;
        private readonly PlayerIdentityService _playerIdentityService;
        private readonly LegendaryItemTrackerService _legendaryTracker;
        private readonly DesktopClientCore _clientCore;
        private readonly PacketDiagnostics _packetDiagnostics;

        private CancellationTokenSource? _lifecycleCancellation;
        private CancellationTokenSource? _supervisorCancellation;
        private Task? _supervisorTask;
        private CaptureSession? _activeSession;
        private long _lifecycleGeneration;
        private bool _listeningRequested;
        private bool _isPowerSuspended;
        private bool _disposed;

        public event EventHandler? MacOSCapturePermissionSetupRequiredChanged;
        public bool IsMacOSCapturePermissionSetupRequired { get; private set; }
        public bool IsMacOSCapturePermissionSetupOutdated { get; private set; }

        public NetworkListenerService(PlayerState playerState, SettingsManager settingsManager, MailService mailService, TradeService tradeService, ItemEstimatedMarketValueService itemEstimatedMarketValues, ActivityTrackerService activityTracker, PartyTrackerService partyTracker, PlayerIdentityService playerIdentityService, LegendaryItemTrackerService legendaryTracker, DesktopClientCore clientCore, PacketDiagnostics packetDiagnostics)
        {
            _playerState = playerState;
            _settingsManager = settingsManager;
            _mailService = mailService;
            _itemEstimatedMarketValues = itemEstimatedMarketValues;
            _activityTracker = activityTracker;
            _partyTracker = partyTracker;
            _playerIdentityService = playerIdentityService;
            _legendaryTracker = legendaryTracker;
            _clientCore = clientCore;
            _packetDiagnostics = packetDiagnostics;
            _activityTracker.DiagnosticNote += _packetDiagnostics.Note;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            }

            _tradeService = tradeService;
            _settingsManager.UserSettings.PropertyChanged += OnUserSettingsPropertyChanged;
            IsMacOSCapturePermissionSetupOutdated =
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                && HasLegacyMacOSCapturePermissionSetup();
        }

        /// <summary>A cheap snapshot of the capture session for the setup checklist.</summary>
        public CaptureStatus GetCaptureStatus()
        {
            var session = Volatile.Read(ref _activeSession);
            if (session is null)
            {
                return CaptureStatus.NotStarted;
            }

            var selected = Volatile.Read(ref session.SelectedRegistration);
            return new CaptureStatus(true, session.GetActiveDeviceCount(), session.AvailableDeviceCount, selected?.DisplayName);
        }

        /// <summary>Adapters with their state, unknown server addresses and capture restarts, for Copy diagnostics.</summary>
        public CaptureDiagnostics GetCaptureDiagnostics()
        {
            var session = Volatile.Read(ref _activeSession);
            var selected = session is null ? null : Volatile.Read(ref session.SelectedRegistration);
            var registrations = session?.GetDevicesSnapshot() ?? [];
            var adapters = new List<CaptureAdapterState>();
            foreach (var (deviceName, displayName) in _knownAdapters)
            {
                var registration = registrations.FirstOrDefault(candidate =>
                    candidate.DeviceName == deviceName && Volatile.Read(ref candidate.IsTerminated) == 0);
                var state = selected?.DeviceName == deviceName ? "selected"
                    : registration is not null && Volatile.Read(ref registration.CaptureStoppedUnexpectedly) != 0 ? "stopped"
                    : registration is not null && Volatile.Read(ref registration.IsRetired) == 0 ? "open"
                    : _adapterOpenFailures.TryGetValue(deviceName, out var denied) ? denied ? "permission denied" : "couldn't open"
                    : selected is not null ? "not used" : "closed";
                var linkType = _adapterLinkTypes.TryGetValue(deviceName, out var link) && link != LinkLayers.Ethernet
                    ? link.ToString()
                    : null;
                adapters.Add(new CaptureAdapterState(displayName, state, linkType));
            }

            string[] unknownServerIps;
            lock (_unknownServerIpsLock)
            {
                unknownServerIps = _unknownServerIps.ToArray();
            }

            return new CaptureDiagnostics(
                adapters,
                unknownServerIps,
                (GameTrafficTransport)Volatile.Read(ref _gameTrafficTransport),
                Volatile.Read(ref _captureRestartCount),
                Volatile.Read(ref _lastCaptureRestart),
                _adaptersMissingFromCapture,
                _settingsManager.UserSettings.CaptureAdapterName is { Length: > 0 } pinned ? pinned : null);
        }

        /// <summary>The adapters the capture driver lists now, for the adapter picker in settings.</summary>
        public IReadOnlyList<CaptureAdapterInfo> ListCaptureAdapters()
        {
            var adapters = new List<CaptureAdapterInfo>();
            try
            {
                foreach (ILiveDevice device in CaptureDeviceList.New())
                {
                    try
                    {
                        var key = GetAdapterKey(device);
                        var label = GetAdapterLabel(device);
                        var isTunnel = _adapterLinkTypes.TryGetValue(device.Name, out var link)
                            ? CapturedPacketParser.IsTunnelLinkType(link)
                            : TrafficRouterDetector.MatchAdapter(label) is not null;
                        if (adapters.All(adapter => adapter.Key != key))
                        {
                            adapters.Add(new CaptureAdapterInfo(key, label, isTunnel));
                        }
                    }
                    finally
                    {
                        DisposeUnownedCaptureDevice(device);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unable to list network adapters for the adapter picker.");
            }

            return adapters
                .OrderByDescending(adapter => adapter.IsTunnel)
                .ThenBy(adapter => adapter.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void OnUserSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UserSettings.CaptureAdapterName))
            {
                return;
            }

            lock (_lifecycleStateLock)
            {
                if (_disposed || _isPowerSuspended || !_listeningRequested)
                {
                    return;
                }
            }

            Log.Information("Network adapter choice changed; restarting capture.");
            _ = RequestStartAsync(forceRestart: true, applyStartDelay: false);
        }

        public Task StartNetworkListeningAsync()
        {
            return RequestStartAsync(forceRestart: false, applyStartDelay: true);
        }

        private Task RequestStartAsync(bool forceRestart, bool applyStartDelay)
        {
            LifecycleRequest request;
            CancellationTokenSource? previousCancellation;

            lock (_lifecycleStateLock)
            {
                if (_disposed)
                {
                    Log.Debug("Ignoring network listener start because the service is disposed.");
                    return Task.CompletedTask;
                }

                if (_isPowerSuspended)
                {
                    Log.Debug("Ignoring network listener start while the system is suspended.");
                    return Task.CompletedTask;
                }

                if (_listeningRequested && !forceRestart)
                {
                    Log.Information("Network listening is already active or starting.");
                    return Task.CompletedTask;
                }

                _listeningRequested = true;
                EnsureSupervisorStartedLocked();
                request = CreateLifecycleRequestLocked();
                previousCancellation = ReplaceLifecycleCancellationLocked(request.Cancellation);
            }

            CancelLifecycleRequest(previousCancellation);
            return ApplyStartRequestAsync(request, forceRestart, applyStartDelay);
        }

        private async Task ApplyStartRequestAsync(
            LifecycleRequest request,
            bool forceRestart,
            bool applyStartDelay)
        {
            CaptureSession? pendingSession = null;
            Dictionary<string, ILiveDevice>? unownedCaptureDevices = null;
            var acquiredGate = false;
            var startSucceeded = false;

            try
            {
                await _lifecycleGate.WaitAsync(request.Token).ConfigureAwait(false);
                acquiredGate = true;

                if (!IsCurrentRequest(request, listeningRequested: true))
                {
                    return;
                }

                if (forceRestart)
                {
                    var previousSession = Interlocked.Exchange(ref _activeSession, null);
                    TerminateSession(previousSession);
                }
                else if (Volatile.Read(ref _activeSession) is not null)
                {
                    startSucceeded = true;
                    return;
                }

                if (applyStartDelay)
                {
                    // A short delay is useful on process startup and power resume, but an
                    // internal failover must not add an avoidable capture gap.
                    Log.Information(
                        "Waiting {DelaySeconds} seconds for network drivers to be ready",
                        _settingsManager.AppSettings.NetworkDevicesStartDelaySecs);
                    await Task.Delay(
                            TimeSpan.FromSeconds(_settingsManager.AppSettings.NetworkDevicesStartDelaySecs),
                            request.Token)
                        .ConfigureAwait(false);
                }

                request.Token.ThrowIfCancellationRequested();
                if (!IsCurrentRequest(request, listeningRequested: true))
                {
                    return;
                }

                if (NpCapInstallationChecker.IsNpCapInstalled() == false)
                {
                    Log.Error("NpCap is not installed, please install it to use this application");
                    return;
                }

                var filter = _settingsManager.AppSettings.PacketFilterPortText ?? string.Empty;

                ReceiverBuilder builder = ReceiverBuilder.Create();

                _clientCore.Core.ResetCapture();
                _activityTracker.OnCaptureReset();
                _clientCore.Core.RegisterHandlers(builder);

                // Desktop-only tracker subscribers run after shared enrichment.
                // EVENTS
                builder.AddEventHandler(new NewCharacterEventHandler(
                    _partyTracker,
                    _playerIdentityService,
                    _playerState));
                builder.AddEventHandler(new PartyJoinedEventHandler(_partyTracker));
                builder.AddEventHandler(new PartyPlayerJoinedEventHandler(_partyTracker));
                builder.AddEventHandler(new PartyPlayerLeftEventHandler(_partyTracker));
                builder.AddEventHandler(new PartyDisbandedEventHandler(_partyTracker));
                builder.AddEventHandler(new PartyOnClusterPartyJoinedEventHandler(_partyTracker));
                builder.AddEventHandler(new PartySetRoleFlagEventHandler(_partyTracker));
                builder.AddEventHandler(new EstimatedMarketValueUpdateEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new AttachItemContainerEventHandler(_legendaryTracker));
                builder.AddEventHandler(new DetachItemContainerEventHandler(_legendaryTracker));
                builder.AddEventHandler(new InventoryDeleteItemEventHandler(_legendaryTracker));
                builder.AddEventHandler(new InventoryPutItemEventHandler(_legendaryTracker));
                builder.AddEventHandler(new NewSimpleItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewJournalItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewLaborerItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewEquipmentItemEventHandler(_itemEstimatedMarketValues, _playerState, _legendaryTracker));
                builder.AddEventHandler(new NewFurnitureItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewKillTrophyItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewSiegeBannerItemEventHandler(_itemEstimatedMarketValues, _playerState));
                builder.AddEventHandler(new NewEquipmentItemLegendarySoulEventHandler(_legendaryTracker));
                builder.AddEventHandler(new BankVaultInfoEventHandler(_legendaryTracker));
                builder.AddEventHandler(new GuildVaultInfoEventHandler(_legendaryTracker));
                // RESPONSE
                builder.AddResponseHandler(new AuctionGetOffersResponseHandler(_playerState, _tradeService));
                builder.AddResponseHandler(new AuctionGetRequestsResponseHandler(_playerState, _tradeService));
                builder.AddResponseHandler(new JoinResponseHandler(_playerState, _partyTracker, _playerIdentityService, _legendaryTracker));
                builder.AddResponseHandler(new GetMailInfosResponseHandler(_playerState, _mailService));
                builder.AddResponseHandler(new ReadMailResponseHandler(_playerState, _mailService));
                builder.AddResponseHandler(new AuctionBuyOfferResponseHandler(_playerState, _tradeService));
                builder.AddResponseHandler(new AuctionSellSpecificItemRequestResponseHandler(_playerState, _tradeService));
                // REQUEST
                builder.AddRequestHandler(new AuctionBuyOfferRequestHandler(_playerState, _tradeService));
                builder.AddRequestHandler(new AuctionSellSpecificItemRequestRequestHandler(_playerState, _tradeService));

                ActivityPacketSubscriptions.Register(builder, _activityTracker);
                builder.ObserveHandledPackets(_packetDiagnostics.Write, () => _packetDiagnostics.IsEnabled);

                var localReceiver = builder.Build();
                localReceiver.ResetTransportContext();

                if (localReceiver == null)
                {
                    Log.Error("Failed to create network receiver");
                    return;
                }

                Log.Debug("Starting network device listening");

                Dictionary<string, ILiveDevice> discoveredDevices;
                try
                {
                    discoveredDevices = EnumerateCaptureDevices();
                    unownedCaptureDevices = discoveredDevices;
                    ClearFailureLog("enumeration");
                }
                catch (Exception ex)
                {
                    // Keep a discovery session alive so the supervisor can retry a
                    // transient enumeration failure without requiring an app restart.
                    LogRepeatedFailure(
                        "enumeration",
                        ex,
                        "Unable to enumerate network capture devices. Will retry in the background.");
                    discoveredDevices = new Dictionary<string, ILiveDevice>(StringComparer.Ordinal);
                    unownedCaptureDevices = discoveredDevices;
                }

                pendingSession = new CaptureSession(
                    localReceiver,
                    discoveredDevices.Keys);
                var openedDeviceCount = 0;
                var failedDeviceCount = 0;
                var sawPermissionDenied = false;
                foreach (var entry in discoveredDevices.ToArray())
                {
                    request.Token.ThrowIfCancellationRequested();

                    var device = entry.Value;
                    var registration = new CaptureDeviceRegistration(
                        device,
                        pendingSession,
                        this);
                    pendingSession.AddDevice(registration);
                    discoveredDevices.Remove(entry.Key);
                    var result = await StartDeviceCaptureAsync(registration, filter)
                        .ConfigureAwait(false);
                    if (result.Opened)
                    {
                        openedDeviceCount++;
                    }
                    else
                    {
                        pendingSession.RemoveDevice(registration);
                        failedDeviceCount++;
                        sawPermissionDenied |= result.PermissionDenied;
                    }
                }

                DisposeUnownedCaptureDevices(discoveredDevices.Values);
                discoveredDevices.Clear();

                if (openedDeviceCount == 0)
                {
                    if (pendingSession.AvailableDeviceCount == 0)
                    {
                        Log.Warning("No network capture devices were found. Waiting for an adapter to become available.");
                    }
                    else if (ShouldLogRepeatedFailure("no-open-devices"))
                    {
                        LogNoCaptureDevicesOpened(failedDeviceCount, sawPermissionDenied);
                    }
                }
                else if (failedDeviceCount > 0)
                {
                    if (ShouldLogRepeatedFailure("partial-open-devices"))
                    {
                        Log.Warning(
                            "Opened {OpenedDeviceCount} network capture device(s), but failed to open {FailedDeviceCount}.",
                            openedDeviceCount,
                            failedDeviceCount);
                    }
                }

                request.Token.ThrowIfCancellationRequested();
                if (openedDeviceCount > 0)
                {
                    SetMacOSCapturePermissionSetupRequired(false);
                    ClearFailureLog("no-open-devices");
                }

                if (!TryPublishSession(request, pendingSession, out var replacedSession))
                {
                    return;
                }

                pendingSession = null;
                TerminateSession(replacedSession);

                if (openedDeviceCount > 0)
                {
                    Log.Information(
                        "Discovering the Albion network adapter across {OpenedDeviceCount} capture device(s).",
                        openedDeviceCount);
                }
                else
                {
                    Log.Information("Network capture is waiting for an available device.");
                }

                startSucceeded = true;
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
                Log.Debug("Network listener start was superseded by a newer lifecycle request.");
            }
            catch (Exception ex)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && IsPacketCapturePermissionError(ex))
                {
                    SetMacOSCapturePermissionSetupRequired(true);
                    Log.Error(ex, "Error starting network listening because macOS denied packet capture access.");
                    LogMacOSCapturePermissionHelp();
                }
                else
                {
                    SetMacOSCapturePermissionSetupRequired(false);
                    Log.Error(ex, "Error starting network listening");
                }
            }
            finally
            {
                if (unownedCaptureDevices is not null)
                {
                    DisposeUnownedCaptureDevices(unownedCaptureDevices.Values);
                    unownedCaptureDevices.Clear();
                }

                TerminateSession(pendingSession);

                if (acquiredGate)
                {
                    _lifecycleGate.Release();
                }

                CompleteLifecycleRequest(request, failedStart: !startSucceeded);
            }
        }

        private CaptureDeviceOpenResult TryStartDeviceCapture(
            CaptureDeviceRegistration registration,
            string filter)
        {
            var device = registration.Device;

            try
            {
                Log.Debug("Opening network device: {Device}", registration.DisplayName);

                device.OnPacketArrival += registration.PacketHandler;
                device.OnCaptureStopped += registration.CaptureStoppedHandler;
                // Without immediate mode the driver holds packets until its buffer fills or the read
                // timeout passes, so quiet traffic such as movement arrives in bursts seconds late.
                var configuration = new DeviceConfiguration
                {
                    Mode = DeviceModes.MaxResponsiveness,
                    ReadTimeout = 5000
                };
                configuration.ConfigurationFailed += (_, failure) => Log.Warning(
                    "Network device {Device} couldn't apply {Property}: {Message}",
                    registration.DisplayName, failure.Property, failure.Message);
                device.Open(configuration);
                device.Filter = filter;
                device.StartCapture();
                var linkType = device.LinkType;
                _adapterLinkTypes[registration.DeviceName] = linkType;
                Log.Debug(
                    "Opened network device: {Device} ({LinkType}) with filter: {Filter}",
                    registration.DisplayName,
                    linkType,
                    filter);
                if (CapturedPacketParser.IsTunnelLinkType(linkType))
                {
                    Log.Information(
                        "Listening on tunnel adapter {Device} ({LinkType}) for game traffic routed through a VPN.",
                        registration.DisplayName,
                        linkType);
                }
                ClearFailureLog($"open:{registration.DeviceName}");
                _adapterOpenFailures.TryRemove(registration.DeviceName, out _);
                return new CaptureDeviceOpenResult(true, false);
            }
            catch (Exception ex)
            {
                registration.InitializationCompleted.TrySetResult(true);
                TerminateDeviceCapture(registration);
                if (ShouldLogRepeatedFailure($"open:{registration.DeviceName}"))
                {
                    Log.Warning(
                        ex,
                        "Error initializing network device {Device}.",
                        registration.DisplayName);
                }
                else
                {
                    Log.Debug(
                        ex,
                        "Network device {Device} still cannot be opened.",
                        registration.DisplayName);
                }

                var permissionDenied = IsPacketCapturePermissionError(ex);
                _adapterOpenFailures[registration.DeviceName] = permissionDenied;
                return new CaptureDeviceOpenResult(false, permissionDenied);
            }
            finally
            {
                registration.InitializationCompleted.TrySetResult(true);
            }
        }

        private async Task<CaptureDeviceOpenResult> StartDeviceCaptureAsync(
            CaptureDeviceRegistration registration,
            string filter)
        {
            try
            {
                return await Task.Run(() => TryStartDeviceCapture(registration, filter))
                    .ConfigureAwait(false);
            }
            catch
            {
                registration.InitializationCompleted.TrySetResult(true);
                TerminateDeviceCapture(registration);
                throw;
            }
        }

        public async Task<bool> InstallMacOSCapturePermissionsAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Log.Warning("macOS packet capture permission setup is only available on macOS.");
                return false;
            }

            var setupScriptPath = GetMacOSCapturePermissionSetupScriptPath();
            if (!File.Exists(setupScriptPath))
            {
                Log.Error("macOS packet capture permission setup script was not found at {SetupScriptPath}.", setupScriptPath);
                return false;
            }

            var shellCommand = "/bin/sh " + QuoteForPosixShell(setupScriptPath);
            var appleScript = string.Format(
                "do shell script \"{0}\" with administrator privileges",
                EscapeForAppleScript(shellCommand));

            var startInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(appleScript);

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    Log.Error("Unable to start macOS packet capture permission setup.");
                    return false;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode == 0)
                {
                    IsMacOSCapturePermissionSetupOutdated = false;
                    Log.Information("macOS packet capture permission setup completed. Restart AFM Data Client. If capture is still denied, log out and back in or reboot.");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Log.Debug("macOS packet capture permission setup output: {Output}", output.Trim());
                    }

                    return true;
                }

                Log.Warning(
                    "macOS packet capture permission setup did not complete. Exit code: {ExitCode}. Output: {Output}. Error: {Error}",
                    process.ExitCode,
                    output.Trim(),
                    error.Trim());
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error running macOS packet capture permission setup.");
                return false;
            }
        }

        private void LogNoCaptureDevicesOpened(int failedDeviceCount, bool permissionDenied)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && permissionDenied)
            {
                SetMacOSCapturePermissionSetupRequired(true);
                Log.Error(
                    "macOS denied packet capture access for all {FailedDeviceCount} network device(s).",
                    failedDeviceCount);
                LogMacOSCapturePermissionHelp();
                return;
            }

            SetMacOSCapturePermissionSetupRequired(false);
            Log.Error("No network capture devices could be opened. Failed devices: {FailedDeviceCount}.", failedDeviceCount);
        }

        private void SetMacOSCapturePermissionSetupRequired(bool isRequired)
        {
            var nextValue = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && isRequired;
            if (IsMacOSCapturePermissionSetupRequired == nextValue)
            {
                return;
            }

            IsMacOSCapturePermissionSetupRequired = nextValue;
            MacOSCapturePermissionSetupRequiredChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void LogMacOSCapturePermissionHelp()
        {
            Log.Error(
                "Run the macOS packet capture permission setup once, then restart AFM Data Client: sudo /bin/sh \"{SetupScriptPath}\". If capture is still denied after setup, log out and back in or reboot.",
                GetMacOSCapturePermissionSetupScriptPath());
        }

        private static string GetMacOSCapturePermissionSetupScriptPath()
        {
            var bundleScriptPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "Resources",
                MacOSCapturePermissionSetupScriptName));

            return File.Exists(bundleScriptPath)
                ? bundleScriptPath
                : Path.Combine(
                    "AFMDataClient_MacOS.app",
                    "Contents",
                    "Resources",
                    MacOSCapturePermissionSetupScriptName);
        }

        private static bool HasLegacyMacOSCapturePermissionSetup()
        {
            if (!File.Exists(MacOSCapturePermissionLaunchDaemonPath))
            {
                return false;
            }

            try
            {
                return File.ReadAllText(MacOSCapturePermissionLaunchDaemonPath)
                    .Contains(MacOSLegacyCapturePermissionScheduleKey, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Unable to inspect the installed macOS packet capture permission service at {PlistPath}.",
                    MacOSCapturePermissionLaunchDaemonPath);
                return false;
            }
        }

        private static string QuoteForPosixShell(string value)
        {
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static string EscapeForAppleScript(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool IsPacketCapturePermissionError(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                var message = current.Message;
                if (message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("/dev/bpf", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("BIOC", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetDeviceDisplayName(ILiveDevice device)
        {
            return string.IsNullOrWhiteSpace(device.Description)
                ? device.Name
                : device.Description;
        }

        private Dictionary<string, ILiveDevice> EnumerateCaptureDevices()
        {
            var devicesByName = new Dictionary<string, ILiveDevice>(StringComparer.Ordinal);

            try
            {
                foreach (ILiveDevice device in CaptureDeviceList.New())
                {
                    string deviceName;
                    try
                    {
                        deviceName = device.Name;
                    }
                    catch
                    {
                        DisposeUnownedCaptureDevice(device);
                        throw;
                    }

                    if (!devicesByName.TryAdd(deviceName, device))
                    {
                        DisposeUnownedCaptureDevice(device);
                    }
                }

                _knownAdapters = devicesByName.ToDictionary(
                    entry => entry.Key,
                    entry => GetDeviceDisplayName(entry.Value),
                    StringComparer.Ordinal);
                UpdateAdaptersMissingFromCapture(devicesByName.Keys);
                return ApplyPinnedAdapter(devicesByName);
            }
            catch
            {
                DisposeUnownedCaptureDevices(devicesByName.Values);
                throw;
            }
        }

        /// <summary>
        /// Keeps only the adapter chosen in settings when it is present. While it is missing, for example before the
        /// VPN connects, every adapter stays a candidate; the rescan notices when it appears and restarts capture on it.
        /// </summary>
        private Dictionary<string, ILiveDevice> ApplyPinnedAdapter(Dictionary<string, ILiveDevice> devicesByName)
        {
            var pinned = _settingsManager.UserSettings.CaptureAdapterName;
            if (string.IsNullOrEmpty(pinned))
            {
                LogPinnedAdapterOnce(null, () => { });
                return devicesByName;
            }

            var match = devicesByName.FirstOrDefault(entry => MatchesAdapter(entry.Value, pinned));
            if (match.Value is null)
            {
                LogPinnedAdapterOnce($"missing:{pinned}", () => Log.Warning(
                    "The network adapter chosen in settings, {Adapter}, isn't available. Listening on every adapter until it appears.",
                    pinned));
                return devicesByName;
            }

            foreach (var entry in devicesByName)
            {
                if (!ReferenceEquals(entry.Value, match.Value))
                {
                    DisposeUnownedCaptureDevice(entry.Value);
                }
            }

            var displayName = GetDeviceDisplayName(match.Value);
            LogPinnedAdapterOnce($"pinned:{match.Key}", () => Log.Information(
                "Capture is limited to the network adapter chosen in settings: {Adapter}.",
                displayName));
            return new Dictionary<string, ILiveDevice>(StringComparer.Ordinal) { [match.Key] = match.Value };
        }

        // The rescan runs every few seconds, so a pinned-adapter message is only logged when it changes.
        private void LogPinnedAdapterOnce(string? key, Action log)
        {
            lock (_failureLogLock)
            {
                if (_lastPinnedAdapterLog == key)
                {
                    return;
                }

                _lastPinnedAdapterLog = key;
            }

            log();
        }

        private static bool MatchesAdapter(ILiveDevice device, string adapter)
        {
            return string.Equals(device.Name, adapter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetAdapterKey(device), adapter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Description, adapter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What settings store for a chosen adapter: the Windows friendly name such as "sing-box", because a VPN
        /// can recreate its tunnel with a new device id each time it connects; elsewhere the device name such as tun0.
        /// </summary>
        private static string GetAdapterKey(ILiveDevice device)
        {
            var friendlyName = (device as LibPcapLiveDevice)?.Interface?.FriendlyName;
            return string.IsNullOrWhiteSpace(friendlyName) ? device.Name : friendlyName;
        }

        private static string GetAdapterLabel(ILiveDevice device)
        {
            var key = GetAdapterKey(device);
            var description = device.Description;
            return string.IsNullOrWhiteSpace(description) || string.Equals(description, key, StringComparison.OrdinalIgnoreCase)
                ? key
                : $"{key} · {description}";
        }

        /// <summary>
        /// Notes adapters the system has up that the capture driver doesn't list. Npcap only sees adapters it is bound
        /// to, so a VPN tunnel it isn't bound to carries the game's traffic out of reach.
        /// </summary>
        private void UpdateAdaptersMissingFromCapture(IReadOnlyCollection<string> captureDeviceNames)
        {
            try
            {
                var missing = new List<string>();
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    // Npcap names devices \Device\NPF_{GUID}, where the GUID is the interface id.
                    var seen = OperatingSystem.IsWindows()
                        ? captureDeviceNames.Any(name => name.Contains(networkInterface.Id, StringComparison.OrdinalIgnoreCase))
                        : captureDeviceNames.Contains(networkInterface.Name);
                    if (seen)
                    {
                        continue;
                    }

                    var label = string.IsNullOrWhiteSpace(networkInterface.Description)
                        || networkInterface.Description == networkInterface.Name
                            ? networkInterface.Name
                            : $"{networkInterface.Name} ({networkInterface.Description})";
                    missing.Add(label);

                    var looksLikeTunnel = TrafficRouterDetector.MatchAdapter(label) is not null
                        || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Tunnel
                            or NetworkInterfaceType.Ppp
                            or NetworkInterfaceType.Unknown;
                    bool firstTime;
                    lock (_loggedMissingAdapters)
                    {
                        firstTime = _loggedMissingAdapters.Add(label);
                    }

                    if (looksLikeTunnel && firstTime)
                    {
                        Log.Warning(
                            "VPN adapter {Adapter} is up, but the packet capture driver doesn't list it, so game traffic routed through it can't be read. Update Npcap to the latest version, then restart the client with the VPN connected.",
                            label);
                    }
                }

                _adaptersMissingFromCapture = missing;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Unable to compare system network adapters with capture devices.");
            }
        }

        private static void DisposeUnownedCaptureDevices(IEnumerable<ILiveDevice> devices)
        {
            foreach (ILiveDevice device in devices)
            {
                DisposeUnownedCaptureDevice(device);
            }
        }

        private static void DisposeUnownedCaptureDevice(ILiveDevice device)
        {
            var displayName = "unknown capture device";
            try
            {
                displayName = GetDeviceDisplayName(device);
            }
            catch
            {
            }

            try
            {
                (device as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error disposing unopened network device {Device}.", displayName);
            }
        }

        private bool ShouldLogRepeatedFailure(string key)
        {
            var now = Stopwatch.GetTimestamp();

            lock (_failureLogLock)
            {
                if (_failureLogTimestamps.TryGetValue(key, out var previous)
                    && Stopwatch.GetElapsedTime(previous, now) < RepeatedFailureLogInterval)
                {
                    return false;
                }

                _failureLogTimestamps[key] = now;
                return true;
            }
        }

        private void ClearFailureLog(string key)
        {
            lock (_failureLogLock)
            {
                _failureLogTimestamps.Remove(key);
            }
        }

        private void LogRepeatedFailure(
            string key,
            Exception exception,
            string messageTemplate)
        {
            if (ShouldLogRepeatedFailure(key))
            {
                Log.Warning(exception, messageTemplate);
            }
            else
            {
                Log.Debug(exception, messageTemplate);
            }
        }

        private void CaptureStoppedHandler(
            CaptureSession session,
            CaptureDeviceRegistration registration,
            object sender,
            CaptureStoppedEventStatus status)
        {
            try
            {
                if (Volatile.Read(ref registration.IsRetired) != 0)
                {
                    return;
                }

                // Capture can stop immediately after StartCapture, before the new
                // session is published. Preserve that state so the first supervisor
                // pass does not mistake the registration for a live candidate.
                Volatile.Write(ref registration.CaptureStoppedUnexpectedly, 1);
                if (!session.IsReady
                    || !ReferenceEquals(Volatile.Read(ref _activeSession), session))
                {
                    SignalSupervisor();
                    return;
                }

                Log.Warning(
                    "Network capture stopped unexpectedly on {Device}. Status={Status}.",
                    registration.DisplayName,
                    status);
                SignalSupervisor();
            }
            catch (Exception ex)
            {
                // SharpPcap invokes this on its capture task. Never let a callback
                // exception fault the task without notifying the supervisor.
                Log.Error(ex, "Error handling capture-stop notification.");
            }
        }

        private void PacketHandler(
            CaptureSession session,
            CaptureDeviceRegistration registration,
            object? sender,
            PacketCapture e)
        {
            if (!session.IsReady
                || Volatile.Read(ref registration.IsRetired) != 0
                || !ReferenceEquals(Volatile.Read(ref _activeSession), session))
            {
                return;
            }

            lock (session.ProcessingLock)
            {
                if (!session.IsReady
                    || Volatile.Read(ref registration.IsRetired) != 0
                    || !ReferenceEquals(Volatile.Read(ref _activeSession), session))
                {
                    return;
                }

                ProcessPacket(session, registration, e);
            }
        }

        private void ProcessPacket(
            CaptureSession session,
            CaptureDeviceRegistration registration,
            PacketCapture e)
        {
            try
            {
                var capturedPacket = e.GetPacket();
                UdpPacket? packet = CapturedPacketParser.ExtractUdp(capturedPacket.LinkLayerType, capturedPacket.Data);
                if (packet is null)
                {
                    return;
                }

                var ipPacket = packet.ParentPacket as IPPacket;
                var datagram = new CapturedDatagram(
                    packet.PayloadData,
                    capturedPacket.Timeval.Date.ToUniversalTime(),
                    ipPacket is null ? null : new IPEndPoint(ipPacket.SourceAddress, packet.SourcePort),
                    ipPacket is null ? null : new IPEndPoint(ipPacket.DestinationAddress, packet.DestinationPort));

                var selectedRegistration = Volatile.Read(ref session.SelectedRegistration);
                if (selectedRegistration is not null
                    && !ReferenceEquals(selectedRegistration, registration))
                {
                    return;
                }

                if (selectedRegistration is null)
                {
                    PacketReceiveResult probeResult = registration.Probe.ReceivePacketDetailed(
                        packet.PayloadData);
                    LogPacketResult(probeResult, packet.PayloadData.Length);
                    if (!probeResult.HasValidPhotonTraffic)
                    {
                        return;
                    }

                    session.MarkValidTraffic();
                    var promotedRegistration = Interlocked.CompareExchange(
                        ref session.SelectedRegistration,
                        registration,
                        null);
                    if (promotedRegistration is not null
                        && !ReferenceEquals(promotedRegistration, registration))
                    {
                        return;
                    }

                    // Mark and schedule losing captures for closure before invoking
                    // handler-bearing production code, which is allowed to throw.
                    RetireUnselectedDevices(session, registration);

                    _playerState.LastPacketTime = DateTime.UtcNow;
                    UpdateAlbionServer(packet);

                    if (!ReferenceEquals(Volatile.Read(ref _activeSession), session))
                    {
                        return;
                    }

                    PacketReceiveResult productionResult = session.Receiver.Receive(datagram);
                    HandlePacketResult(productionResult, packet.PayloadData.Length);

                    Log.Information(
                        "Selected network capture device {Device} after receiving valid Albion traffic.",
                        registration.DisplayName);
                    return;
                }

                // Handlers need the incoming server identity before processing a new Join.
                UpdateAlbionServer(packet);
                PacketReceiveResult receiveResult = session.Receiver.Receive(datagram);
                if (receiveResult.HasValidPhotonTraffic)
                {
                    session.MarkValidTraffic();
                    _playerState.LastPacketTime = DateTime.UtcNow;
                }

                HandlePacketResult(receiveResult, packet.PayloadData.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while processing captured Albion packet");
            }
        }

        private void UpdateAlbionServer(UdpPacket packet)
        {
            if (packet.ParentPacket is not IPPacket ipPacket
                || ipPacket.SourceAddress is null
                || ipPacket.DestinationAddress is null)
            {
                Log.Verbose("Packet has no source or destination IP.");
                return;
            }

            // The server is the end on the game port. On IPv6 the player's own address is public
            // too, so it can't be told apart from the server by the address alone.
            var serverAddress = packet.DestinationPort == AlbionGamePort
                ? ipPacket.DestinationAddress
                : ipPacket.SourceAddress;

            // Packets sent to the server name it as well as packets coming from it.
            var server = AlbionServers.FindByAddress(ipPacket.SourceAddress)
                ?? AlbionServers.FindByAddress(ipPacket.DestinationAddress);
            Volatile.Write(ref _gameTrafficTransport, (int)GetTransport(serverAddress, server is not null));
            if (server is not null)
            {
                _clientCore.Core.Session.SetServer(server);
                _playerState.AlbionServer = server;
                return;
            }

            if (IsPrivateIp(serverAddress))
            {
                return;
            }

            var serverIp = serverAddress.ToString();
            lock (_unknownServerIpsLock)
            {
                if (_unknownServerIps.Add(serverIp))
                {
                    Log.Warning(
                        "Albion traffic uses unknown server IP {Ip}; could not determine Albion server. Known unknown IPs so far: {Ips}",
                        serverIp,
                        string.Join(", ", _unknownServerIps));
                }
            }
        }

        private static GameTrafficTransport GetTransport(IPAddress serverAddress, bool knownServer)
        {
            if (serverAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                return GameTrafficTransport.IPv4;
            }

            // A known server over IPv6 is its IPv4 address carried by NAT64 (see AlbionServers.FindByAddress).
            return serverAddress.IsIPv4MappedToIPv6 ? GameTrafficTransport.IPv4MappedIPv6
                : knownServer ? GameTrafficTransport.IPv6Nat64
                : GameTrafficTransport.IPv6;
        }

        private void HandlePacketResult(PacketReceiveResult result, int payloadBytes)
        {
            if (result.Status == PacketStatus.Encrypted)
            {
                // Albion also encrypts unrelated replies, such as on dungeon entry. Only an
                // encrypted reply to a market request means market orders cannot be read.
                if (result.IsMarketEncrypted)
                {
                    _playerState.HasEncryptedData = true;
                    Log.Warning("Encrypted market data received! You can't see market orders!");
                }
                else
                {
                    Log.Debug(
                        "Encrypted Photon message not tied to a market request. PayloadBytes={PayloadBytes}, HasValidPhotonTraffic={HasValidPhotonTraffic}",
                        payloadBytes,
                        result.HasValidPhotonTraffic);
                }

                return;
            }

            LogPacketResult(result, payloadBytes);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogPacketResult(PacketReceiveResult result, int payloadBytes)
        {
            if (result.Status != PacketStatus.InvalidHeader
                && result.Status != PacketStatus.InvalidCrc)
            {
                return;
            }

            if (result.HasValidPhotonTraffic)
            {
                Log.Debug(
                    "Photon UDP payload contained valid traffic plus malformed data. Status={PacketStatus}, PayloadBytes={PayloadBytes}",
                    result.Status,
                    payloadBytes);
                return;
            }

            Log.Debug(
                "Photon UDP payload rejected. Status={PacketStatus}, PayloadBytes={PayloadBytes}",
                result.Status,
                payloadBytes);
        }

        private void RetireUnselectedDevices(
            CaptureSession session,
            CaptureDeviceRegistration selectedRegistration)
        {
            foreach (var registration in session.GetDevicesSnapshot())
            {
                if (ReferenceEquals(registration, selectedRegistration))
                {
                    continue;
                }

                if (Interlocked.CompareExchange(ref registration.IsRetired, 1, 0) == 0)
                {
                    _ = Task.Run(() => TerminateDeviceCapture(registration));
                }
            }
        }

        private static bool IsPrivateIp(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
            }

            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }

        private void TerminateDeviceCapture(CaptureDeviceRegistration registration)
        {
            Volatile.Write(ref registration.IsRetired, 1);
            registration.InitializationCompleted.Task.GetAwaiter().GetResult();
            if (Interlocked.Exchange(ref registration.IsTerminated, 1) != 0)
            {
                registration.TerminationCompleted.Task.GetAwaiter().GetResult();
                return;
            }

            var device = registration.Device;

            try
            {
                Log.Debug("Closing network device: {Device}", registration.DisplayName);

                try
                {
                    device.OnPacketArrival -= registration.PacketHandler;
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "Error unsubscribing from network device {Device}.",
                        registration.DisplayName);
                }

                try
                {
                    device.OnCaptureStopped -= registration.CaptureStoppedHandler;
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "Error unsubscribing capture-stop handler from network device {Device}.",
                        registration.DisplayName);
                }

                try
                {
                    device.StopCapture();
                }
                catch (Exception ex)
                {
                    Log.Debug(
                        ex,
                        "Error stopping network device {Device}.",
                        registration.DisplayName);
                }

                try
                {
                    device.Close();
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "Error closing network device {Device}.",
                        registration.DisplayName);
                }

                try
                {
                    (device as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "Error disposing network device {Device}.",
                        registration.DisplayName);
                }
            }
            finally
            {
                registration.TerminationCompleted.TrySetResult(true);
            }
        }

        private void TerminateSession(CaptureSession? session)
        {
            if (session is null)
            {
                return;
            }

            if (Interlocked.Exchange(ref session.IsTerminated, 1) != 0)
            {
                session.TerminationCompleted.Task.GetAwaiter().GetResult();
                return;
            }

            try
            {
                session.IsReady = false;

                lock (session.ProcessingLock)
                {
                }

                if (Volatile.Read(ref _activeSession) is not { } activeSession || ReferenceEquals(activeSession, session))
                    _activityTracker.OnCaptureSuspended();

                foreach (var registration in session.GetDevicesSnapshot())
                {
                    try
                    {
                        TerminateDeviceCapture(registration);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(
                            ex,
                            "Unexpected error terminating network device {Device}.",
                            registration.DisplayName);
                    }
                }
            }
            finally
            {
                session.TerminationCompleted.TrySetResult(true);
            }
        }

        public void StopNetworkListening()
        {
            RequestStopAsync(markPowerSuspended: false, disposeService: false)
                .GetAwaiter()
                .GetResult();
        }

        private Task RequestStopAsync(bool markPowerSuspended, bool disposeService)
        {
            LifecycleRequest request;
            CancellationTokenSource? previousCancellation;
            CancellationTokenSource? supervisorCancellation;
            Task? supervisorTask;

            lock (_lifecycleStateLock)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                if (disposeService)
                {
                    _disposed = true;
                }

                if (markPowerSuspended || disposeService)
                {
                    _isPowerSuspended = true;
                }

                if (!_listeningRequested
                    && Volatile.Read(ref _activeSession) is null
                    && _lifecycleCancellation is null
                    && _supervisorTask is null
                    && !disposeService)
                {
                    Log.Information("Network listening is already stopped.");
                    return Task.CompletedTask;
                }

                _listeningRequested = false;
                (supervisorCancellation, supervisorTask) = DetachSupervisorLocked();
                request = CreateLifecycleRequestLocked();
                previousCancellation = ReplaceLifecycleCancellationLocked(request.Cancellation);
            }

            CancelLifecycleRequest(previousCancellation);
            CancelSupervisor(supervisorCancellation);
            return ApplyStopRequestAndSupervisorAsync(
                request,
                supervisorCancellation,
                supervisorTask);
        }

        private async Task ApplyStopRequestAndSupervisorAsync(
            LifecycleRequest request,
            CancellationTokenSource? supervisorCancellation,
            Task? supervisorTask)
        {
            try
            {
                // The supervisor can be enumerating adapters or scheduling a recovery.
                // Join it before closing capture handles so no monitor work can race
                // with device disposal during stop, suspend, or application exit.
                await AwaitSupervisorAsync(supervisorTask).ConfigureAwait(false);
            }
            finally
            {
                supervisorCancellation?.Dispose();
                await ApplyStopRequestAsync(request).ConfigureAwait(false);
            }
        }

        private async Task ApplyStopRequestAsync(LifecycleRequest request)
        {
            var acquiredGate = false;

            try
            {
                await _lifecycleGate.WaitAsync(request.Token).ConfigureAwait(false);
                acquiredGate = true;

                if (!IsCurrentRequest(request, listeningRequested: false))
                {
                    return;
                }

                Log.Information("Stopping network listening...");
                var session = Interlocked.Exchange(ref _activeSession, null);
                TerminateSession(session);
                Log.Information("Network listening stopped successfully.");
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
                Log.Debug("Network listener stop was superseded by a newer lifecycle request.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping network listening");
            }
            finally
            {
                if (acquiredGate)
                {
                    _lifecycleGate.Release();
                }

                CompleteLifecycleRequest(request, failedStart: false);
            }
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    switch (e.Mode)
                    {
                        case PowerModes.Suspend:
                            Log.Information("System is entering sleep/hibernate mode. Stopping network listening.");
                            RequestStopAsync(markPowerSuspended: true, disposeService: false)
                                .GetAwaiter()
                                .GetResult();
                            break;
                        case PowerModes.Resume:
                            Log.Information("System is resuming from sleep/hibernate. Starting network listening.");
                            _ = RequestResumeAsync();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error handling power mode change event");
                }
            }
        }

        private Task RequestResumeAsync()
        {
            lock (_lifecycleStateLock)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                _isPowerSuspended = false;
            }

            return RequestStartAsync(forceRestart: true, applyStartDelay: true);
        }

        public void Dispose()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            }

            _settingsManager.UserSettings.PropertyChanged -= OnUserSettingsPropertyChanged;

            RequestStopAsync(markPowerSuspended: true, disposeService: true)
                .GetAwaiter()
                .GetResult();

            Log.Information("Disposed {type}!", nameof(NetworkListenerService));
        }

        private void EnsureSupervisorStartedLocked()
        {
            if (_supervisorTask is not null && !_supervisorTask.IsCompleted)
            {
                return;
            }

            _supervisorCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _supervisorCancellation = cancellation;
            _supervisorTask = Task.Run(() => RunCaptureSupervisorAsync(cancellation.Token));
        }

        private (CancellationTokenSource? Cancellation, Task? Task) DetachSupervisorLocked()
        {
            var cancellation = _supervisorCancellation;
            var task = _supervisorTask;
            _supervisorCancellation = null;
            _supervisorTask = null;
            return (cancellation, task);
        }

        private async Task RunCaptureSupervisorAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await _supervisorSignal.WaitAsync(
                            GetDeviceRescanInterval(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!IsCaptureDesired())
                    {
                        return;
                    }

                    try
                    {
                        await EvaluateCaptureHealthAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error evaluating network capture health. The supervisor will retry.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Network capture supervisor stopped unexpectedly.");
            }
        }

        private async Task EvaluateCaptureHealthAsync(CancellationToken cancellationToken)
        {
            CaptureSession? session;

            lock (_lifecycleStateLock)
            {
                if (_disposed || _isPowerSuspended || !_listeningRequested)
                {
                    return;
                }

                // Do not supersede a start/stop already holding or waiting for the
                // lifecycle gate. The next supervisor pass will evaluate its result.
                if (_lifecycleCancellation is not null)
                {
                    return;
                }

                session = Volatile.Read(ref _activeSession);
            }

            if (session is null)
            {
                await RequestSupervisorRecoveryAsync(
                        expectedSession: null,
                        "no active capture session")
                    .ConfigureAwait(false);
                return;
            }

            if (!session.IsReady)
            {
                return;
            }

            var selectedRegistration = Volatile.Read(ref session.SelectedRegistration);
            if (selectedRegistration is not null)
            {
                if (Volatile.Read(ref selectedRegistration.CaptureStoppedUnexpectedly) != 0
                    || !IsCaptureStarted(selectedRegistration))
                {
                    await RequestSupervisorRecoveryAsync(
                            session,
                            $"capture stopped on {selectedRegistration.DisplayName}")
                        .ConfigureAwait(false);
                    return;
                }

                if (session.GetTimeSinceLastValidTraffic() >= GetTrafficTimeout())
                {
                    await RequestSupervisorRecoveryAsync(
                            session,
                            $"no valid Albion traffic on {selectedRegistration.DisplayName}")
                        .ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                RetireStoppedDiscoveryDevices(session);
            }

            Dictionary<string, ILiveDevice> availableDevices;
            try
            {
                availableDevices = EnumerateCaptureDevices();
                ClearFailureLog("enumeration");
            }
            catch (Exception ex)
            {
                LogRepeatedFailure(
                    "enumeration",
                    ex,
                    "Unable to rescan network capture devices. Keeping the current capture session.");
                return;
            }

            try
            {
                var availableDeviceNames = availableDevices.Keys.ToHashSet(StringComparer.Ordinal);
                if (!ReferenceEquals(Volatile.Read(ref _activeSession), session)
                    || !session.IsReady)
                {
                    return;
                }

                selectedRegistration = Volatile.Read(ref session.SelectedRegistration);
                if (selectedRegistration is not null)
                {
                    if (!availableDeviceNames.Contains(selectedRegistration.DeviceName))
                    {
                        await RequestSupervisorRecoveryAsync(
                                session,
                                $"selected adapter {selectedRegistration.DisplayName} disappeared")
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!session.HasSameAvailableDevices(availableDeviceNames))
                    {
                        session.ReplaceAvailableDevices(availableDeviceNames);
                        Log.Information(
                            "Network adapter inventory changed while selected device {Device} remains healthy.",
                            selectedRegistration.DisplayName);
                    }

                    return;
                }

                if (!session.HasSameAvailableDevices(availableDeviceNames))
                {
                    await RequestSupervisorRecoveryAsync(
                            session,
                            "network adapter inventory changed during discovery")
                        .ConfigureAwait(false);
                    return;
                }

                session.PruneCompletedDeviceTerminations();
                bool hasRetryableMissingDevice = availableDeviceNames.Any(deviceName =>
                    !session.HasActiveDevice(deviceName)
                    && !session.HasDeviceTerminationInProgress(deviceName));
                if (hasRetryableMissingDevice
                    && session.TryBeginOpenRetry(GetTrafficTimeout()))
                {
                    await RetryMissingDiscoveryDevicesAsync(
                            session,
                            availableDevices,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                DisposeUnownedCaptureDevices(availableDevices.Values);
                availableDevices.Clear();
            }
        }

        private void RetireStoppedDiscoveryDevices(CaptureSession session)
        {
            var registrationsToTerminate = new List<CaptureDeviceRegistration>();

            lock (session.ProcessingLock)
            {
                if (Volatile.Read(ref session.SelectedRegistration) is not null)
                {
                    return;
                }

                foreach (CaptureDeviceRegistration registration in session.GetDevicesSnapshot())
                {
                    if (Volatile.Read(ref registration.IsRetired) != 0
                        || (Volatile.Read(ref registration.CaptureStoppedUnexpectedly) == 0
                            && IsCaptureStarted(registration)))
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(ref registration.IsRetired, 1, 0) == 0)
                    {
                        registrationsToTerminate.Add(registration);
                    }
                }
            }

            foreach (CaptureDeviceRegistration registration in registrationsToTerminate)
            {
                Log.Warning(
                    "Retiring stopped discovery capture on {Device}; it will be retried without restarting healthy candidates.",
                    registration.DisplayName);
                _ = Task.Run(() => TerminateDeviceCapture(registration));
            }
        }

        private async Task RetryMissingDiscoveryDevicesAsync(
            CaptureSession session,
            Dictionary<string, ILiveDevice> availableDevices,
            CancellationToken cancellationToken)
        {
            var acquiredGate = false;
            var openedDeviceCount = 0;
            var failedDeviceCount = 0;
            var sawPermissionDenied = false;

            try
            {
                await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquiredGate = true;

                if (!IsCurrentDiscoverySession(session))
                {
                    return;
                }

                var filter = _settingsManager.AppSettings.PacketFilterPortText ?? string.Empty;
                foreach (KeyValuePair<string, ILiveDevice> entry in availableDevices.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentDiscoverySession(session))
                    {
                        break;
                    }

                    if (session.HasActiveDevice(entry.Key)
                        || session.HasDeviceTerminationInProgress(entry.Key))
                    {
                        continue;
                    }

                    var registration = new CaptureDeviceRegistration(entry.Value, session, this);
                    session.AddDevice(registration);
                    availableDevices.Remove(entry.Key);

                    CaptureDeviceOpenResult result = await StartDeviceCaptureAsync(
                            registration,
                            filter)
                        .ConfigureAwait(false);
                    if (result.Opened)
                    {
                        CaptureDeviceRegistration? selectedRegistration =
                            Volatile.Read(ref session.SelectedRegistration);
                        if (selectedRegistration is not null
                            && !ReferenceEquals(selectedRegistration, registration)
                            && Interlocked.CompareExchange(
                                ref registration.IsRetired,
                                1,
                                0) == 0)
                        {
                            _ = Task.Run(() => TerminateDeviceCapture(registration));
                        }
                        else if (Volatile.Read(ref registration.IsRetired) == 0)
                        {
                            openedDeviceCount++;
                        }
                    }
                    else
                    {
                        session.RemoveDevice(registration);
                        failedDeviceCount++;
                        sawPermissionDenied |= result.PermissionDenied;
                    }

                    if (Volatile.Read(ref session.SelectedRegistration) is not null)
                    {
                        break;
                    }
                }

                if (openedDeviceCount > 0)
                {
                    ClearFailureLog("no-open-devices");
                    SetMacOSCapturePermissionSetupRequired(false);
                    Log.Information(
                        "Added {OpenedDeviceCount} recovered network capture candidate(s) without restarting active discovery captures.",
                        openedDeviceCount);
                }

                if (failedDeviceCount > 0)
                {
                    if (session.GetActiveDeviceCount() == 0)
                    {
                        if (ShouldLogRepeatedFailure("no-open-devices"))
                        {
                            LogNoCaptureDevicesOpened(failedDeviceCount, sawPermissionDenied);
                        }
                    }
                    else if (ShouldLogRepeatedFailure("partial-open-devices"))
                    {
                        Log.Warning(
                            "Failed to reopen {FailedDeviceCount} network capture candidate(s); healthy discovery captures remain active.",
                            failedDeviceCount);
                    }
                }
            }
            finally
            {
                if (acquiredGate)
                {
                    _lifecycleGate.Release();
                }
            }
        }

        private bool IsCurrentDiscoverySession(CaptureSession session)
        {
            lock (_lifecycleStateLock)
            {
                return !_disposed
                    && !_isPowerSuspended
                    && _listeningRequested
                    && _lifecycleCancellation is null
                    && ReferenceEquals(Volatile.Read(ref _activeSession), session)
                    && session.IsReady
                    && Volatile.Read(ref session.SelectedRegistration) is null;
            }
        }

        private Task RequestSupervisorRecoveryAsync(
            CaptureSession? expectedSession,
            string reason)
        {
            LifecycleRequest request;
            CancellationTokenSource? previousCancellation;
            bool forceRestart;

            lock (_lifecycleStateLock)
            {
                if (_disposed
                    || _isPowerSuspended
                    || !_listeningRequested
                    || _lifecycleCancellation is not null)
                {
                    return Task.CompletedTask;
                }

                var activeSession = Volatile.Read(ref _activeSession);
                if (expectedSession is null)
                {
                    if (activeSession is not null)
                    {
                        return Task.CompletedTask;
                    }

                    forceRestart = false;
                }
                else
                {
                    if (!ReferenceEquals(activeSession, expectedSession)
                        || Interlocked.Exchange(ref expectedSession.RecoveryScheduled, 1) != 0)
                    {
                        return Task.CompletedTask;
                    }

                    forceRestart = true;
                }

                request = CreateLifecycleRequestLocked();
                previousCancellation = ReplaceLifecycleCancellationLocked(request.Cancellation);
            }

            Log.Warning("Re-evaluating network capture devices: {Reason}.", reason);
            Interlocked.Increment(ref _captureRestartCount);
            Volatile.Write(ref _lastCaptureRestart, new CaptureRestart(reason, DateTime.UtcNow));
            CancelLifecycleRequest(previousCancellation);
            return ApplyStartRequestAsync(
                request,
                forceRestart,
                applyStartDelay: false);
        }

        private bool IsCaptureDesired()
        {
            lock (_lifecycleStateLock)
            {
                return !_disposed && !_isPowerSuspended && _listeningRequested;
            }
        }

        private static bool IsCaptureStarted(CaptureDeviceRegistration registration)
        {
            try
            {
                return Volatile.Read(ref registration.IsRetired) == 0
                    && registration.Device.Started;
            }
            catch (Exception ex)
            {
                Log.Debug(
                    ex,
                    "Unable to inspect capture state for network device {Device}.",
                    registration.DisplayName);
                return false;
            }
        }

        private TimeSpan GetDeviceRescanInterval()
        {
            var seconds = _settingsManager.AppSettings.NetworkDevicesRescanSeconds;
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : 10);
        }

        private TimeSpan GetTrafficTimeout()
        {
            var seconds = _settingsManager.AppSettings.NetworkDevicesTrafficTimeoutSeconds;
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : 30);
        }

        private void SignalSupervisor()
        {
            try
            {
                _supervisorSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A pending signal already covers this state change.
            }
        }

        private static void CancelSupervisor(CancellationTokenSource? cancellation)
        {
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static async Task AwaitSupervisorAsync(Task? supervisorTask)
        {
            if (supervisorTask is null)
            {
                return;
            }

            try
            {
                await supervisorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while stopping the network capture supervisor.");
            }
        }

        private LifecycleRequest CreateLifecycleRequestLocked()
        {
            var cancellation = new CancellationTokenSource();
            return new LifecycleRequest(
                ++_lifecycleGeneration,
                cancellation,
                cancellation.Token);
        }

        private CancellationTokenSource? ReplaceLifecycleCancellationLocked(
            CancellationTokenSource cancellation)
        {
            var previousCancellation = _lifecycleCancellation;
            _lifecycleCancellation = cancellation;
            return previousCancellation;
        }

        private static void CancelLifecycleRequest(CancellationTokenSource? cancellation)
        {
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private bool IsCurrentRequest(LifecycleRequest request, bool listeningRequested)
        {
            lock (_lifecycleStateLock)
            {
                return _lifecycleGeneration == request.Generation
                    && _listeningRequested == listeningRequested
                    && (!_disposed || !listeningRequested);
            }
        }

        private bool TryPublishSession(
            LifecycleRequest request,
            CaptureSession session,
            out CaptureSession? replacedSession)
        {
            lock (_lifecycleStateLock)
            {
                if (_lifecycleGeneration != request.Generation
                    || !_listeningRequested
                    || _disposed)
                {
                    replacedSession = null;
                    return false;
                }

                session.IsReady = true;
                replacedSession = Interlocked.Exchange(ref _activeSession, session);
                return true;
            }
        }

        private void CompleteLifecycleRequest(LifecycleRequest request, bool failedStart)
        {
            var disposeCancellation = false;

            lock (_lifecycleStateLock)
            {
                if (_lifecycleGeneration == request.Generation && failedStart)
                {
                    _listeningRequested = false;
                }

                if (ReferenceEquals(_lifecycleCancellation, request.Cancellation))
                {
                    _lifecycleCancellation = null;
                    disposeCancellation = true;
                }
            }

            if (disposeCancellation)
            {
                request.Cancellation.Dispose();
            }
        }

        private sealed class CaptureSession
        {
            private int _isReady;
            private readonly object _availableDevicesLock = new object();
            private readonly object _devicesLock = new object();
            private HashSet<string> _availableDeviceNames;
            private readonly List<CaptureDeviceRegistration> _devices =
                new List<CaptureDeviceRegistration>();
            private long _lastValidTrafficTimestamp;
            private long _lastOpenRetryTimestamp;
            private readonly long _createdTimestamp;

            public CaptureSession(
                IPhotonReceiver receiver,
                IEnumerable<string> availableDeviceNames)
            {
                Receiver = receiver;
                _createdTimestamp = Stopwatch.GetTimestamp();
                _lastOpenRetryTimestamp = _createdTimestamp;
                _availableDeviceNames = new HashSet<string>(
                    availableDeviceNames,
                    StringComparer.Ordinal);
            }

            public IPhotonReceiver Receiver { get; }
            public TaskCompletionSource<bool> TerminationCompleted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public object ProcessingLock { get; } = new object();
            public CaptureDeviceRegistration? SelectedRegistration;
            public int RecoveryScheduled;
            public int IsTerminated;

            public int AvailableDeviceCount
            {
                get
                {
                    lock (_availableDevicesLock)
                    {
                        return _availableDeviceNames.Count;
                    }
                }
            }

            public bool IsReady
            {
                get => Volatile.Read(ref _isReady) != 0;
                set => Volatile.Write(ref _isReady, value ? 1 : 0);
            }

            public void MarkValidTraffic()
            {
                Volatile.Write(ref _lastValidTrafficTimestamp, Stopwatch.GetTimestamp());
            }

            public TimeSpan GetTimeSinceLastValidTraffic()
            {
                var timestamp = Volatile.Read(ref _lastValidTrafficTimestamp);
                return timestamp == 0
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(timestamp);
            }

            public void AddDevice(CaptureDeviceRegistration registration)
            {
                lock (_devicesLock)
                {
                    _devices.Add(registration);
                }
            }

            public void RemoveDevice(CaptureDeviceRegistration registration)
            {
                lock (_devicesLock)
                {
                    _devices.Remove(registration);
                }
            }

            public IReadOnlyList<CaptureDeviceRegistration> GetDevicesSnapshot()
            {
                lock (_devicesLock)
                {
                    return _devices.ToArray();
                }
            }

            public bool HasActiveDevice(string deviceName)
            {
                lock (_devicesLock)
                {
                    return _devices.Any(registration =>
                        string.Equals(registration.DeviceName, deviceName, StringComparison.Ordinal)
                        && Volatile.Read(ref registration.IsRetired) == 0
                        && Volatile.Read(ref registration.IsTerminated) == 0
                        && Volatile.Read(ref registration.CaptureStoppedUnexpectedly) == 0);
                }
            }

            public bool HasDeviceTerminationInProgress(string deviceName)
            {
                lock (_devicesLock)
                {
                    return _devices.Any(registration =>
                        string.Equals(registration.DeviceName, deviceName, StringComparison.Ordinal)
                        && (Volatile.Read(ref registration.IsRetired) != 0
                            || Volatile.Read(ref registration.IsTerminated) != 0)
                        && !registration.TerminationCompleted.Task.IsCompleted);
                }
            }

            public int GetActiveDeviceCount()
            {
                lock (_devicesLock)
                {
                    return _devices.Count(registration =>
                        Volatile.Read(ref registration.IsRetired) == 0
                        && Volatile.Read(ref registration.IsTerminated) == 0
                        && Volatile.Read(ref registration.CaptureStoppedUnexpectedly) == 0);
                }
            }

            public void PruneCompletedDeviceTerminations()
            {
                lock (_devicesLock)
                {
                    _devices.RemoveAll(registration =>
                        Volatile.Read(ref registration.IsTerminated) != 0
                        && registration.TerminationCompleted.Task.IsCompleted);
                }
            }

            public bool TryBeginOpenRetry(TimeSpan retryInterval)
            {
                var now = Stopwatch.GetTimestamp();

                while (true)
                {
                    var previous = Volatile.Read(ref _lastOpenRetryTimestamp);
                    if (Stopwatch.GetElapsedTime(previous, now) < retryInterval)
                    {
                        return false;
                    }

                    if (Interlocked.CompareExchange(
                            ref _lastOpenRetryTimestamp,
                            now,
                            previous) == previous)
                    {
                        return true;
                    }
                }
            }

            public bool HasSameAvailableDevices(IReadOnlySet<string> deviceNames)
            {
                lock (_availableDevicesLock)
                {
                    return _availableDeviceNames.SetEquals(deviceNames);
                }
            }

            public void ReplaceAvailableDevices(IEnumerable<string> deviceNames)
            {
                lock (_availableDevicesLock)
                {
                    _availableDeviceNames = new HashSet<string>(
                        deviceNames,
                        StringComparer.Ordinal);
                }
            }
        }

        private sealed class CaptureDeviceRegistration
        {
            public CaptureDeviceRegistration(
                ILiveDevice device,
                CaptureSession session,
                NetworkListenerService listener)
            {
                Device = device;
                DeviceName = device.Name;
                DisplayName = GetDeviceDisplayName(device);
                Probe = new PhotonTrafficProbe();
                PacketHandler = (sender, packet) =>
                    listener.PacketHandler(session, this, sender, packet);
                CaptureStoppedHandler = (sender, status) =>
                    listener.CaptureStoppedHandler(session, this, sender, status);
            }

            public ILiveDevice Device { get; }
            public string DeviceName { get; }
            public PacketArrivalEventHandler PacketHandler { get; }
            public CaptureStoppedEventHandler CaptureStoppedHandler { get; }
            public string DisplayName { get; }
            public PhotonTrafficProbe Probe { get; }
            public TaskCompletionSource<bool> InitializationCompleted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> TerminationCompleted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CaptureStoppedUnexpectedly;
            public int IsRetired;
            public int IsTerminated;
        }

        private sealed class PhotonTrafficProbe : PhotonParser
        {
            protected override void OnRequest(
                byte operationCode,
                Dictionary<byte, object> parameters)
            {
            }

            protected override void OnResponse(
                byte operationCode,
                short returnCode,
                string debugMessage,
                Dictionary<byte, object> parameters)
            {
            }

            protected override void OnEvent(
                byte code,
                Dictionary<byte, object> parameters)
            {
            }
        }

        private readonly record struct LifecycleRequest(
            long Generation,
            CancellationTokenSource Cancellation,
            CancellationToken Token);

        private readonly record struct CaptureDeviceOpenResult(bool Opened, bool PermissionDenied);
    }
}
