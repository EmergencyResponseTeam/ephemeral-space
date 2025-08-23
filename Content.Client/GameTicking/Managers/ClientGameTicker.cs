using Content.Client._ES.Lobby;
using Content.Client.Administration.Managers;
using Content.Client.Gameplay;
using Content.Client.Lobby;
using Content.Client.RoundEnd;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.GameWindow;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client.GameTicking.Managers
{
    [UsedImplicitly]
    public sealed class ClientGameTicker : SharedGameTicker
    {
        [Dependency] private readonly IStateManager _stateManager = default!;
        [Dependency] private readonly IClientAdminManager _admin = default!;
        [Dependency] private readonly IClyde _clyde = default!;
        [Dependency] private readonly IUserInterfaceManager _userInterfaceManager = default!;
        // ES START
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IConfigurationManager _cfg = default!;
        private ESLobbyCurtainsUIController _lobbyCurtains = default!;
        // ES END

        private Dictionary<NetEntity, Dictionary<ProtoId<JobPrototype>, int?>>  _jobsAvailable = new();
        private Dictionary<NetEntity, string> _stationNames = new();

        // ES START
        [ViewVariables] public PlayerGameStatus ReadyStatus { get; private set; }
        // ES END
        [ViewVariables] public bool IsGameStarted { get; private set; }
        [ViewVariables] public ResolvedSoundSpecifier? RestartSound { get; private set; }
        [ViewVariables] public string? LobbyBackground { get; private set; }
        [ViewVariables] public bool DisallowedLateJoin { get; private set; }
        [ViewVariables] public string? ServerInfoBlob { get; private set; }
        [ViewVariables] public TimeSpan StartTime { get; private set; }
        [ViewVariables] public new bool Paused { get; private set; }

        public override IReadOnlyList<(TimeSpan, string)> AllPreviousGameRules => new List<(TimeSpan, string)>();

        // ES START
        [ViewVariables] public TimeSpan? ESExpectedRoundEndTime { get; private set; }

        // time to start the opening curtain animation, a few seconds after the screen actually changes
        // (so we dont lag)
        private TimeSpan? _startOpenAnimationTime;

        // ES END

        [ViewVariables] public IReadOnlyDictionary<NetEntity, Dictionary<ProtoId<JobPrototype>, int?>> JobsAvailable => _jobsAvailable;
        [ViewVariables] public IReadOnlyDictionary<NetEntity, string> StationNames => _stationNames;

        public event Action? InfoBlobUpdated;
        public event Action? LobbyStatusUpdated;
        public event Action? LobbyLateJoinStatusUpdated;
        public event Action<IReadOnlyDictionary<NetEntity, Dictionary<ProtoId<JobPrototype>, int?>>>? LobbyJobsAvailableUpdated;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeNetworkEvent<TickerJoinLobbyEvent>(JoinLobby);
            SubscribeNetworkEvent<TickerJoinGameEvent>(JoinGame);
            SubscribeNetworkEvent<TickerConnectionStatusEvent>(ConnectionStatus);
            SubscribeNetworkEvent<TickerLobbyStatusEvent>(LobbyStatus);
            SubscribeNetworkEvent<TickerLobbyInfoEvent>(LobbyInfo);
            SubscribeNetworkEvent<TickerLobbyCountdownEvent>(LobbyCountdown);
            SubscribeNetworkEvent<RoundEndMessageEvent>(RoundEnd);
            SubscribeNetworkEvent<RequestWindowAttentionEvent>(OnAttentionRequest);
            SubscribeNetworkEvent<TickerLateJoinStatusEvent>(LateJoinStatus);
            SubscribeNetworkEvent<TickerJobsAvailableEvent>(UpdateJobsAvailable);

            _admin.AdminStatusUpdated += OnAdminUpdated;
            // ES START
            _lobbyCurtains = _userInterfaceManager.GetUIController<ESLobbyCurtainsUIController>();
            // ES END
            OnAdminUpdated();
        }

        // ES START
        // Handles playing the closing curtain animations at the correct time to preempt a screen change
        // the opening animations
        public override void FrameUpdate(float frameTime)
        {
            base.FrameUpdate(frameTime);

            var curTime = _timing.CurTime;
            var realTime = _timing.RealTime;

            // lobby->game closing
            if (ReadyStatus is (PlayerGameStatus.Observing or PlayerGameStatus.ReadyToPlay)
                && StartTime > curTime
                && _lobbyCurtains.CurtainState == LobbyCurtainState.Open
                && StartTime - curTime <= TimeSpan.FromSeconds(3))
            {
                _lobbyCurtains.StartCurtainAnimation(false);
            }

            // roundend->lobby closing
            if (ESExpectedRoundEndTime > curTime
                && _lobbyCurtains.CurtainState == LobbyCurtainState.Open
                && ESExpectedRoundEndTime - curTime <= TimeSpan.FromSeconds(3))
            {
                _lobbyCurtains.StartCurtainAnimation(false);
                ESExpectedRoundEndTime = null;
            }

            // queued opening anim
            // realtime bc not tied to any existing timespan
            if (_startOpenAnimationTime != null
                && realTime > _startOpenAnimationTime.Value)
            {
                _lobbyCurtains.StartCurtainAnimation(true);
                _startOpenAnimationTime = null;
            }
        }
        // ES END

        public override void Shutdown()
        {
            _admin.AdminStatusUpdated -= OnAdminUpdated;
            base.Shutdown();
        }

        private void OnAdminUpdated()
        {
            // Hide some map/grid related logs from clients. This is to try prevent some easy metagaming by just
            // reading the console. E.g., logs like this one could leak the nuke station/grid:
            // > Grid NT-Arrivals 1101 (122/n25896) changed parent. Old parent: map 10 (121/n25895). New parent: FTL (123/n26470)
#if !DEBUG
            EntityManager.System<SharedMapSystem>().Log.Level = _admin.IsAdmin() ? LogLevel.Info : LogLevel.Warning;
#endif
        }

        private void OnAttentionRequest(RequestWindowAttentionEvent ev)
        {
            _clyde.RequestWindowAttention();
        }

        private void LateJoinStatus(TickerLateJoinStatusEvent message)
        {
            DisallowedLateJoin = message.Disallowed;
            LobbyLateJoinStatusUpdated?.Invoke();
        }

        private void UpdateJobsAvailable(TickerJobsAvailableEvent message)
        {
            _jobsAvailable.Clear();

            foreach (var (job, data) in message.JobsAvailableByStation)
            {
                _jobsAvailable[job] = data;
            }

            _stationNames.Clear();
            foreach (var weh in message.StationNames)
            {
                _stationNames[weh.Key] = weh.Value;
            }

            LobbyJobsAvailableUpdated?.Invoke(JobsAvailable);
        }

        private void JoinLobby(TickerJoinLobbyEvent message)
        {
            _stateManager.RequestStateChange<LobbyState>();
            // ES START
            _startOpenAnimationTime = _timing.RealTime + TimeSpan.FromSeconds(0.5);
            // ES END
        }

        private void ConnectionStatus(TickerConnectionStatusEvent message)
        {
            RoundStartTimeSpan = message.RoundStartTimeSpan;
        }

        private void LobbyStatus(TickerLobbyStatusEvent message)
        {
            StartTime = message.StartTime;
            RoundStartTimeSpan = message.RoundStartTimeSpan;
            IsGameStarted = message.IsRoundStarted;
            ReadyStatus = message.ReadyStatus;
            LobbyBackground = message.LobbyBackground;
            Paused = message.Paused;

            LobbyStatusUpdated?.Invoke();
        }

        private void LobbyInfo(TickerLobbyInfoEvent message)
        {
            ServerInfoBlob = message.TextBlob;

            InfoBlobUpdated?.Invoke();
        }

        private void JoinGame(TickerJoinGameEvent message)
        {
            _stateManager.RequestStateChange<GameplayState>();
            // ES START
            _startOpenAnimationTime = _timing.RealTime + TimeSpan.FromSeconds(1.5);
            // ES END
        }

        private void LobbyCountdown(TickerLobbyCountdownEvent message)
        {
            StartTime = message.StartTime;
            Paused = message.Paused;
        }

        private void RoundEnd(RoundEndMessageEvent message)
        {
            // Force an update in the event of this song being the same as the last.
            RestartSound = message.RestartSound;

            // ES START
            // if you are testing this you need to use restartround not endround. endround doesnt start the countdown Lol
            // emisse said its possible to delay this
            // but i think its only possible to delay the shuttle timer going off?
            // not the round restart timer.
            ESExpectedRoundEndTime = _timing.CurTime + TimeSpan.FromSeconds(_cfg.GetCVar(CCVars.RoundRestartTime));
            // ES END
            _userInterfaceManager.GetUIController<RoundEndSummaryUIController>().OpenRoundEndSummaryWindow(message);
        }
    }
}
