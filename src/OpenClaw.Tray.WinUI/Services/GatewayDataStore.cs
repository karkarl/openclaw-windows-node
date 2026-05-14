using OpenClaw.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Services;

internal sealed class GatewayDataStore : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private ChannelHealth[] _channels = [];
    private SessionInfo[] _sessions = [];
    private GatewayNodeInfo[] _nodes = [];
    private GatewayUsageInfo? _usage;
    private GatewayUsageStatusInfo? _usageStatus;
    private GatewayCostUsageInfo? _usageCost;
    private GatewaySelfInfo? _gatewaySelf;
    private PairingListInfo? _nodePairList;
    private DevicePairingListInfo? _devicePairList;
    private ModelsListInfo? _modelsList;
    private PresenceEntry[] _presence = [];
    private AgentActivity? _currentActivity;
    private UpdateCommandCenterInfo _lastUpdateInfo = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChannelHealth[] Channels
    {
        get { lock (_gate) return _channels; }
    }

    public SessionInfo[] Sessions
    {
        get { lock (_gate) return _sessions; }
    }

    public GatewayNodeInfo[] Nodes
    {
        get { lock (_gate) return _nodes; }
    }

    public GatewayUsageInfo? Usage
    {
        get { lock (_gate) return _usage; }
    }

    public GatewayUsageStatusInfo? UsageStatus
    {
        get { lock (_gate) return _usageStatus; }
    }

    public GatewayCostUsageInfo? UsageCost
    {
        get { lock (_gate) return _usageCost; }
    }

    public GatewaySelfInfo? GatewaySelf
    {
        get { lock (_gate) return _gatewaySelf; }
    }

    public PairingListInfo? NodePairList
    {
        get { lock (_gate) return _nodePairList; }
    }

    public DevicePairingListInfo? DevicePairList
    {
        get { lock (_gate) return _devicePairList; }
    }

    public ModelsListInfo? ModelsList
    {
        get { lock (_gate) return _modelsList; }
    }

    public PresenceEntry[] Presence
    {
        get { lock (_gate) return _presence; }
    }

    public AgentActivity? CurrentActivity
    {
        get { lock (_gate) return _currentActivity; }
    }

    public UpdateCommandCenterInfo LastUpdateInfo
    {
        get { lock (_gate) return _lastUpdateInfo; }
    }

    public ObservableCollection<AgentEventInfo> AgentEvents { get; } = new();

    public IReadOnlyDictionary<string, AgentActivity> SessionActivities
    {
        get
        {
            lock (_gate)
                return _sessionActivities.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal);
        }
    }

    public IReadOnlyDictionary<string, SessionPreviewInfo> SessionPreviews
    {
        get
        {
            lock (_gate)
                return _sessionPreviews.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal);
        }
    }

    private readonly Dictionary<string, AgentActivity> _sessionActivities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionPreviewInfo> _sessionPreviews = new(StringComparer.Ordinal);

    public void SetChannels(ChannelHealth[] channels) => SetField(ref _channels, channels);

    public void SetSessions(SessionInfo[] sessions)
    {
        string[] stale;
        lock (_gate)
        {
            _sessions = sessions;
            var activeKeys = new HashSet<string>(sessions.Select(s => s.Key), StringComparer.Ordinal);
            stale = _sessionPreviews.Keys.Where(key => !activeKeys.Contains(key)).ToArray();
            foreach (var key in stale)
                _sessionPreviews.Remove(key);
        }

        OnPropertyChanged(nameof(Sessions));
        if (stale.Length > 0)
            OnPropertyChanged(nameof(SessionPreviews));
    }

    public void SetNodes(GatewayNodeInfo[] nodes) => SetField(ref _nodes, nodes);

    public void SetUsage(GatewayUsageInfo usage) => SetField(ref _usage, usage);

    public void SetUsageStatus(GatewayUsageStatusInfo usageStatus) => SetField(ref _usageStatus, usageStatus);

    public void SetUsageCost(GatewayCostUsageInfo usageCost) => SetField(ref _usageCost, usageCost);

    public void SetGatewaySelf(GatewaySelfInfo gatewaySelf) => SetField(ref _gatewaySelf, gatewaySelf);

    public void SetNodePairList(PairingListInfo? data) => SetField(ref _nodePairList, data);

    public void SetDevicePairList(DevicePairingListInfo? data) => SetField(ref _devicePairList, data);

    public void SetModelsList(ModelsListInfo? data) => SetField(ref _modelsList, data);

    public void SetPresence(PresenceEntry[]? data) => SetField(ref _presence, data ?? []);

    public void SetCurrentActivity(AgentActivity? activity)
    {
        lock (_gate)
            _currentActivity = activity;
        OnPropertyChanged(nameof(CurrentActivity));
    }

    public void SetLastUpdateInfo(UpdateCommandCenterInfo info) => SetField(ref _lastUpdateInfo, info);

    public void SetSessionActivity(string sessionKey, AgentActivity activity)
    {
        lock (_gate)
            _sessionActivities[sessionKey] = activity;
        OnPropertyChanged(nameof(SessionActivities));
    }

    public void RemoveSessionActivity(string sessionKey)
    {
        var removed = false;
        lock (_gate)
            removed = _sessionActivities.Remove(sessionKey);
        if (removed)
            OnPropertyChanged(nameof(SessionActivities));
    }

    public void UpsertSessionPreviews(IEnumerable<SessionPreviewInfo> previews)
    {
        lock (_gate)
        {
            foreach (var preview in previews)
                _sessionPreviews[preview.Key] = preview;
        }

        OnPropertyChanged(nameof(SessionPreviews));
    }

    public void AddAgentEvent(AgentEventInfo evt, int maxEvents)
    {
        AgentEvents.Insert(0, evt);
        while (AgentEvents.Count > maxEvents)
            AgentEvents.RemoveAt(AgentEvents.Count - 1);
        OnPropertyChanged(nameof(AgentEvents));
    }

    public void ClearAgentEvents()
    {
        AgentEvents.Clear();
        OnPropertyChanged(nameof(AgentEvents));
    }

    public void ClearPairingAndAgentCaches()
    {
        SetNodePairList(null);
        SetDevicePairList(null);
        SetModelsList(null);
        ClearAgentEvents();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? memberName = null)
    {
        lock (_gate)
            field = value;

        if (memberName is { Length: > 3 } && memberName.StartsWith("Set", StringComparison.Ordinal))
            memberName = memberName[3..];
        OnPropertyChanged(memberName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
