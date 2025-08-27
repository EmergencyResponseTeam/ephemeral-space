using Content.Shared._ES.Lobby;
using Content.Shared._ES.Multistation;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client._ES.Multistation;

/// <inheritdoc/>
public sealed class ESMultistationSystem : ESSharedMultistationSystem
{
    public Dictionary<ProtoId<JobPrototype>, int?> AvailableRoundstartJobs { get; private set; } = new();
    public Dictionary<ProtoId<JobPrototype>, int> ReadiedJobCounts { get; private set; } = new();

    public event Action? OnAvailableRoundstartJobsChanged;
    public event Action? OnReadiedJobCountsChanged;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ESUpdateAvailableRoundstartJobs>(OnUpdateAvailableRoundstartJobs);
        SubscribeNetworkEvent<ESUpdatePlayerReadiedJobCounts>(OnUpdatePlayerReadiedJobCounts);
    }

    private void OnUpdateAvailableRoundstartJobs(ESUpdateAvailableRoundstartJobs ev)
    {
        AvailableRoundstartJobs = new(ev.Jobs);
        OnAvailableRoundstartJobsChanged?.Invoke();
    }

    private void OnUpdatePlayerReadiedJobCounts(ESUpdatePlayerReadiedJobCounts ev)
    {
        ReadiedJobCounts = new(ev.ReadiedJobCounts);
        OnReadiedJobCountsChanged?.Invoke();
    }
}
