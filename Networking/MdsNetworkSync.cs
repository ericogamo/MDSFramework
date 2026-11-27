using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MDSFramework.Api;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using MDSFramework.Wta;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace MDSFramework.Networking
{
    internal struct MdsNetMessage
    {
        public string Text;
    }

    public static class MdsNetworkSync
    {
        private const char Sep = '\u001f';
        private const string PartVerb = "part";
        private const int MaxWireBytes = 1050;
        private const int MaxPiecesPerPayload = 256;
        private const int MaxOpenPayloadsPerSender = 8;
        private const float PieceTimeoutSeconds = 30f;
        private const float HelloIntervalSeconds = 5.0f;
        private const float SiteSyncIntervalSeconds = 0.25f;
        private const float FullSyncIntervalSeconds = 4.0f;
        private const string WireProtocolVersion = "mds-1";

        private static readonly string SepText = Sep.ToString();

        private sealed class SiteSyncState
        {
            internal int Flags;
            internal int AnimPct;
            internal int Yaw10;
            internal int Pitch10;
            internal int Rcs100;
            internal int Ammo1;
            internal int Ammo2;
            internal int FcuCnt;
            internal int ActiveFcuCnt;
            internal int EffCnt;
            internal int NetId;
        }

        private static bool messageRegistered;
        private static MessageHandler? boundServer;
        private static MessageHandler? boundClient;
        private static bool wasServerActive;
        private static bool wasClientActive;

        private static readonly HashSet<INetworkPlayer> ModdedGuests = new HashSet<INetworkPlayer>();
        private static readonly HashSet<INetworkPlayer> KnownAuthPlayers = new HashSet<INetworkPlayer>();
        private static readonly List<INetworkPlayer> PrunePlayersScratch = new List<INetworkPlayer>();

        private static readonly Dictionary<string, string?[]> PieceBuffers = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> PieceStartedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<string> PieceScratch = new List<string>();
        private static readonly List<string> StalePieceScratch = new List<string>();
        private static int nextPieceId;

        private static readonly Dictionary<uint, SiteSyncState> LastSentSiteStates = new Dictionary<uint, SiteSyncState>();
        private static readonly HashSet<uint> DirtySiteIds = new HashSet<uint>();
        private static readonly Dictionary<uint, float> LastBeamSentAt = new Dictionary<uint, float>();
        private static readonly List<string> BatchScratch = new List<string>(32);

        private static readonly Dictionary<string, FactionAffinity> SavedLocalPackageFactions = new Dictionary<string, FactionAffinity>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, FactionAffinity> RemotePackageFactions = new Dictionary<string, FactionAffinity>(StringComparer.OrdinalIgnoreCase);

        private static float nextHelloAt;
        private static float nextSiteSyncAt;
        private static float nextFullSiteSyncAt;
        private static string lastAbmWarnDigest = string.Empty;

        public static bool HostHasMds { get; private set; }
        public static string? CatalogMismatchWarning { get; private set; }
        internal static bool RemoteEnableNativeAircraftAirdrop { get; private set; } = true;
        internal static bool RemoteEnableMissionEditorCatalog { get; private set; } = true;
        internal static bool RemoteEnableAutoPopUp { get; private set; } = true;

        internal static bool IsPureClient =>
            GameManager.gameState == GameState.Multiplayer &&
            NetworkManagerNuclearOption.i != null &&
            NetworkManagerNuclearOption.i.Client != null &&
            NetworkManagerNuclearOption.i.Client.Active &&
            (NetworkManagerNuclearOption.i.Server == null || !NetworkManagerNuclearOption.i.Server.Active);

        internal static bool GetEffectiveEnableNativeAircraftAirdrop()
        {
            if (IsPureClient && HostHasMds)
            {
                return RemoteEnableNativeAircraftAirdrop;
            }

            return MdsConfig.EnableNativeAircraftAirdrop.Value;
        }

        internal static bool GetEffectiveEnableAutoPopUp()
        {
            if (IsPureClient && HostHasMds)
            {
                return RemoteEnableAutoPopUp;
            }

            return MdsConfig.EnableAutoPopUp.Value;
        }

        internal static void Initialize()
        {
            if (messageRegistered)
            {
                return;
            }

            try
            {
                Writer<MdsNetMessage>.Write = (writer, msg) => writer.WriteString(msg.Text);
                Reader<MdsNetMessage>.Read = reader => new MdsNetMessage
                {
                    Text = reader.ReadString() ?? string.Empty
                };

                int id = MessagePacker.GetId<MdsNetMessage>();
                if (MessagePacker.MessageTypes.TryGetValue(id, out Type? existing) && existing != typeof(MdsNetMessage))
                {
                    MdsLog.Error($"[Net] MdsNetMessage ID {id} collides with {existing.FullName}; MDS multiplayer sync disabled.");
                    return;
                }

                MessagePacker.RegisterMessage<MdsNetMessage>();
                messageRegistered = true;
            }
            catch (Exception ex)
            {
                MdsLog.Error($"[Net] Failed registering MdsNetMessage: {ex.Message}");
            }
        }

        internal static void ResetSession()
        {
            ModdedGuests.Clear();
            KnownAuthPlayers.Clear();
            PieceBuffers.Clear();
            PieceStartedAt.Clear();
            LastSentSiteStates.Clear();
            DirtySiteIds.Clear();
            LastBeamSentAt.Clear();
            RestoreLocalPackageFactions();
            RemotePackageFactions.Clear();
            HostHasMds = false;
            CatalogMismatchWarning = null;
            RemoteEnableNativeAircraftAirdrop = true;
            RemoteEnableMissionEditorCatalog = true;
            RemoteEnableAutoPopUp = true;
            lastAbmWarnDigest = string.Empty;
            nextHelloAt = 0f;
            nextSiteSyncAt = 0f;
            nextFullSiteSyncAt = 0f;
        }

        internal static void Tick()
        {
            if (!messageRegistered)
            {
                return;
            }

            NetworkManagerNuclearOption? net = NetworkManagerNuclearOption.i;
            NetworkServer? server = net != null ? net.Server : null;
            NetworkClient? client = net != null ? net.Client : null;

            bool serverActive = server != null && server.Active;
            bool clientActive = client != null && client.Active;

            if ((wasServerActive && !serverActive) || (wasClientActive && !clientActive && !serverActive))
            {
                boundServer = null;
                boundClient = null;
                MdsRuntimeController.Instance?.TriggerNetworkSessionReset();
            }

            wasServerActive = serverActive;
            wasClientActive = clientActive;

            if (serverActive && server != null)
            {
                TickServer(server);
                return;
            }

            if (clientActive && client != null)
            {
                TickClient(client);
            }
            else
            {
                boundClient = null;
                if (HostHasMds)
                {
                    RestoreLocalPackageFactions();
                    HostHasMds = false;
                    CatalogMismatchWarning = null;
                }
            }
        }

        private static void TickServer(NetworkServer server)
        {
            if (!ReferenceEquals(boundServer, server.MessageHandler) && server.MessageHandler != null)
            {
                boundServer = server.MessageHandler;
                boundServer.RegisterHandler<MdsNetMessage>(OnHostMessage, allowUnauthenticated: false);
            }

            float now = Time.unscaledTime;
            IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
            bool newPlayerJoined = false;
            for (int i = 0; i < players.Count; i++)
            {
                INetworkPlayer p = players[i];
                if (p != null && !p.IsHost && KnownAuthPlayers.Add(p))
                {
                    newPlayerJoined = true;
                }
            }

            if (newPlayerJoined || now >= nextHelloAt)
            {
                nextHelloAt = now + HelloIntervalSeconds;
                PruneDisconnectedPlayers(players);
                SendHelloToAll(players);
            }

            if (ModdedGuests.Count == 0)
            {
                return;
            }

            if (now >= nextSiteSyncAt)
            {
                bool forceFull = now >= nextFullSiteSyncAt;
                nextSiteSyncAt = now + SiteSyncIntervalSeconds;
                if (forceFull)
                {
                    nextFullSiteSyncAt = now + FullSyncIntervalSeconds;
                }

                FlushSiteSync(forceFull, targetPlayer: null);
                BroadcastAbmWarningsIfChanged(forceFull, targetPlayer: null);
            }
        }

        private static void TickClient(NetworkClient client)
        {
            if (!ReferenceEquals(boundClient, client.MessageHandler) && client.MessageHandler != null)
            {
                boundClient = client.MessageHandler;
                boundClient.RegisterHandler<MdsNetMessage>(OnGuestMessage, allowUnauthenticated: false);
            }
        }

        public static bool RequestClientHoldFire(Unit? unit, bool holdFire)
        {
            if (unit == null || unit.disabled)
            {
                return false;
            }

            if (GameInteropHelpers.IsServerAuthority())
            {
                ModularSiteBehavior? site = unit.GetComponent<ModularSiteBehavior>();
                if (site != null)
                {
                    site.SetHoldFire(holdFire);
                    MarkSiteDirty(site);
                    return true;
                }

                return false;
            }

            if (!IsPureClient || !HostHasMds || !unit.persistentID.IsValid)
            {
                return false;
            }

            SendToHost(Join("req_holdfire", unit.persistentID.Id.ToString(CultureInfo.InvariantCulture), holdFire ? "1" : "0"));
            return true;
        }

        internal static void MarkSiteDirty(ModularSiteBehavior? site)
        {
            if (site != null && site.Unit != null && site.Unit.persistentID.IsValid)
            {
                DirtySiteIds.Add(site.Unit.persistentID.Id);
            }
        }

        internal static void BroadcastHelBeam(ModularSiteBehavior? site, Vector3 targetWorldPos, float durationSec, float widthMeters)
        {
            if (!messageRegistered || ModdedGuests.Count == 0 || !GameInteropHelpers.IsServerAuthority() ||
                site == null || site.Unit == null || !site.Unit.persistentID.IsValid)
            {
                return;
            }

            uint pid = site.Unit.persistentID.Id;
            float now = Time.unscaledTime;
            if (LastBeamSentAt.TryGetValue(pid, out float prevAt) && now - prevAt < 0.12f)
            {
                return;
            }

            LastBeamSentAt[pid] = now;
            GlobalPosition gp = targetWorldPos.ToGlobalPosition();
            string payload = Join(
                "vfx_beam",
                pid.ToString(CultureInfo.InvariantCulture),
                gp.x.ToString("0.##", CultureInfo.InvariantCulture),
                gp.y.ToString("0.##", CultureInfo.InvariantCulture),
                gp.z.ToString("0.##", CultureInfo.InvariantCulture),
                durationSec.ToString("0.##", CultureInfo.InvariantCulture),
                widthMeters.ToString("0.##", CultureInfo.InvariantCulture));
            SendToAllModdedGuests(payload);
        }

        internal static void BroadcastAheadBurst(
            ModularSiteBehavior? site,
            Vector3 muzzleWorldPos,
            Vector3 baseDir,
            float muzzleVel,
            float baseFuseSeconds)
        {
            if (!messageRegistered || ModdedGuests.Count == 0 || !GameInteropHelpers.IsServerAuthority() ||
                site == null || site.Unit == null || !site.Unit.persistentID.IsValid)
            {
                return;
            }

            GlobalPosition gp = muzzleWorldPos.ToGlobalPosition();
            string payload = Join(
                "vfx_ahead",
                site.Unit.persistentID.Id.ToString(CultureInfo.InvariantCulture),
                gp.x.ToString("0.##", CultureInfo.InvariantCulture),
                gp.y.ToString("0.##", CultureInfo.InvariantCulture),
                gp.z.ToString("0.##", CultureInfo.InvariantCulture),
                baseDir.x.ToString("0.####", CultureInfo.InvariantCulture),
                baseDir.y.ToString("0.####", CultureInfo.InvariantCulture),
                baseDir.z.ToString("0.####", CultureInfo.InvariantCulture),
                muzzleVel.ToString("0.#", CultureInfo.InvariantCulture),
                baseFuseSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            SendToAllModdedGuests(payload);
        }

        internal static void BroadcastContainerLanded(uint persistentId, float yawDeg, float lengthMeters)
        {
            if (!messageRegistered || ModdedGuests.Count == 0 || !GameInteropHelpers.IsServerAuthority() || persistentId == 0)
            {
                return;
            }

            string payload = Join(
                "container_land",
                persistentId.ToString(CultureInfo.InvariantCulture),
                yawDeg.ToString("0.##", CultureInfo.InvariantCulture),
                lengthMeters.ToString("0.##", CultureInfo.InvariantCulture));
            SendToAllModdedGuests(payload);
        }

        internal static void BroadcastMissileIgnition(uint persistentId)
        {
            if (!messageRegistered || ModdedGuests.Count == 0 || !GameInteropHelpers.IsServerAuthority() || persistentId == 0)
            {
                return;
            }

            SendToAllModdedGuests(Join("missile_ign", persistentId.ToString(CultureInfo.InvariantCulture)));
        }

        private static void SendHelloToAll(IReadOnlyList<INetworkPlayer> players)
        {
            string pkgFactions = BuildPackageFactionString();
            string payload = Join(
                "hello",
                WireProtocolVersion,
                MdsCatalogCoordinator.CatalogWireHash.ToString(CultureInfo.InvariantCulture),
                pkgFactions,
                MdsConfig.EnableNativeAircraftAirdrop.Value ? "1" : "0",
                MdsConfig.EnableMissionEditorCatalog.Value ? "1" : "0",
                MdsConfig.EnableAutoPopUp.Value ? "1" : "0");

            for (int i = 0; i < players.Count; i++)
            {
                INetworkPlayer p = players[i];
                if (p != null && !p.IsHost)
                {
                    SendToPlayer(p, payload);
                }
            }
        }

        private static string BuildPackageFactionString()
        {
            var sb = new StringBuilder();
            var list = new List<ContractorPackage>(ContractorRegistry.GetPackages());
            list.Sort((a, b) => string.Compare(a?.PackageId, b?.PackageId, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < list.Count; i++)
            {
                ContractorPackage pkg = list[i];
                if (pkg == null || string.IsNullOrEmpty(pkg.PackageId))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(';');
                }

                sb.Append(pkg.PackageId).Append('=').Append((int)pkg.DefaultFaction);
            }

            return sb.ToString();
        }

        private static void PruneDisconnectedPlayers(IReadOnlyList<INetworkPlayer> players)
        {
            var activeSet = new HashSet<INetworkPlayer>();
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null)
                {
                    activeSet.Add(players[i]);
                }
            }

            PrunePlayersScratch.Clear();
            foreach (INetworkPlayer p in ModdedGuests)
            {
                if (!activeSet.Contains(p))
                {
                    PrunePlayersScratch.Add(p);
                }
            }

            for (int i = 0; i < PrunePlayersScratch.Count; i++)
            {
                ModdedGuests.Remove(PrunePlayersScratch[i]);
            }

            PrunePlayersScratch.Clear();
            foreach (INetworkPlayer p in KnownAuthPlayers)
            {
                if (!activeSet.Contains(p))
                {
                    PrunePlayersScratch.Add(p);
                }
            }

            for (int i = 0; i < PrunePlayersScratch.Count; i++)
            {
                KnownAuthPlayers.Remove(PrunePlayersScratch[i]);
            }
        }

        private static void FlushSiteSync(bool forceFull, INetworkPlayer? targetPlayer)
        {
            SensorNetworkManager? mgr = SensorNetworkManager.Instance;
            if (mgr == null || mgr.AllSites.Count == 0)
            {
                return;
            }

            BatchScratch.Clear();
            IReadOnlyList<ModularSiteBehavior> sites = mgr.AllSites;
            for (int i = 0; i < sites.Count; i++)
            {
                ModularSiteBehavior site = sites[i];
                if (site == null || site.Unit == null || site.Unit.disabled || !site.Unit.persistentID.IsValid)
                {
                    continue;
                }

                uint pid = site.Unit.persistentID.Id;
                int flags = 0;
                if (site.PopUpRequested) flags |= 1 << 0;
                if (site.IsRadarEmitting) flags |= 1 << 1;
                if (site.IsHoldFireActive) flags |= 1 << 2;
                if (site.SuspendedInTransit) flags |= 1 << 3;
                if (site.IsInCommandNet) flags |= 1 << 4;
                if (site.HasActiveFcuLink) flags |= 1 << 5;
                if (site.IsVehicleMoving) flags |= 1 << 6;
                if (site.Animator != null && site.Animator.TargetDeployed) flags |= 1 << 7;

                int animPct = Mathf.RoundToInt(Mathf.Clamp01(site.Animator != null ? site.Animator.NormalizedProgress : 1f) * 100f);
                int yaw10 = Mathf.RoundToInt((site.TurretAim != null ? site.TurretAim.CurrentYawDeg : 0f) * 10f);
                int pitch10 = Mathf.RoundToInt((site.TurretAim != null ? site.TurretAim.CurrentPitchDeg : 0f) * 10f);
                int rcs100 = Mathf.RoundToInt(Mathf.Clamp(site.Unit.RCS, 0.01f, 500f) * 100f);
                int ammo1 = Mathf.Max(0, site.PrimaryAmmoRemaining);
                int ammo2 = Mathf.Max(0, site.SecondaryAmmoRemaining);
                int fcuCnt = site.CommandNetFcuCount;
                int activeFcuCnt = site.CommandNetActiveFcuCount;
                int effCnt = site.CommandNetEffectorCount;
                int netId = site.CommandNetId;

                bool dirty = forceFull || DirtySiteIds.Remove(pid);
                if (!LastSentSiteStates.TryGetValue(pid, out SiteSyncState prev))
                {
                    prev = new SiteSyncState();
                    LastSentSiteStates[pid] = prev;
                    dirty = true;
                }
                else if (prev.Flags != flags ||
                         Math.Abs(prev.AnimPct - animPct) >= 2 ||
                         Math.Abs(prev.Yaw10 - yaw10) >= 8 ||
                         Math.Abs(prev.Pitch10 - pitch10) >= 8 ||
                         Math.Abs(prev.Rcs100 - rcs100) >= 2 ||
                         prev.Ammo1 != ammo1 ||
                         prev.Ammo2 != ammo2 ||
                         prev.FcuCnt != fcuCnt ||
                         prev.ActiveFcuCnt != activeFcuCnt ||
                         prev.EffCnt != effCnt ||
                         prev.NetId != netId)
                {
                    dirty = true;
                }

                if (!dirty)
                {
                    continue;
                }

                if (targetPlayer == null)
                {
                    prev.Flags = flags;
                    prev.AnimPct = animPct;
                    prev.Yaw10 = yaw10;
                    prev.Pitch10 = pitch10;
                    prev.Rcs100 = rcs100;
                    prev.Ammo1 = ammo1;
                    prev.Ammo2 = ammo2;
                    prev.FcuCnt = fcuCnt;
                    prev.ActiveFcuCnt = activeFcuCnt;
                    prev.EffCnt = effCnt;
                    prev.NetId = netId;
                }

                BatchScratch.Add(
                    $"{pid.ToString(CultureInfo.InvariantCulture)}," +
                    $"{flags.ToString(CultureInfo.InvariantCulture)}," +
                    $"{animPct.ToString(CultureInfo.InvariantCulture)}," +
                    $"{yaw10.ToString(CultureInfo.InvariantCulture)}," +
                    $"{pitch10.ToString(CultureInfo.InvariantCulture)}," +
                    $"{rcs100.ToString(CultureInfo.InvariantCulture)}," +
                    $"{ammo1.ToString(CultureInfo.InvariantCulture)}," +
                    $"{ammo2.ToString(CultureInfo.InvariantCulture)}," +
                    $"{fcuCnt.ToString(CultureInfo.InvariantCulture)}," +
                    $"{activeFcuCnt.ToString(CultureInfo.InvariantCulture)}," +
                    $"{effCnt.ToString(CultureInfo.InvariantCulture)}," +
                    $"{netId.ToString(CultureInfo.InvariantCulture)}");

                if (BatchScratch.Count >= 18)
                {
                    SendSiteBatch(BatchScratch, targetPlayer);
                    BatchScratch.Clear();
                }
            }

            if (BatchScratch.Count > 0)
            {
                SendSiteBatch(BatchScratch, targetPlayer);
                BatchScratch.Clear();
            }
        }

        private static void SendSiteBatch(List<string> records, INetworkPlayer? targetPlayer)
        {
            string payload = Join("site_sync", string.Join(";", records));
            if (targetPlayer != null)
            {
                SendToPlayer(targetPlayer, payload);
            }
            else
            {
                SendToAllModdedGuests(payload);
            }
        }

        private static void BroadcastAbmWarningsIfChanged(bool force, INetworkPlayer? targetPlayer)
        {
            AbmEarlyWarningService? svc = AbmEarlyWarningService.Instance;
            if (svc == null)
            {
                return;
            }

            var sb = new StringBuilder();
            IReadOnlyList<AbmEarlyWarningService.BallisticWarningEntry> list = svc.ActiveWarnings;
            for (int i = 0; i < list.Count; i++)
            {
                AbmEarlyWarningService.BallisticWarningEntry w = list[i];
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(w.ThreatId.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(((int)w.TargetFaction).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(w.LastPosition.x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(w.LastPosition.y.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(w.LastPosition.z.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(w.BlastYield.ToString("0", CultureInfo.InvariantCulture));
            }

            string body = sb.ToString();
            if (!force && targetPlayer == null && string.Equals(body, lastAbmWarnDigest, StringComparison.Ordinal))
            {
                return;
            }

            if (targetPlayer == null)
            {
                lastAbmWarnDigest = body;
            }

            string payload = Join("abm_warn", body);
            if (targetPlayer != null)
            {
                SendToPlayer(targetPlayer, payload);
            }
            else
            {
                SendToAllModdedGuests(payload);
            }
        }

        private static void OnHostMessage(INetworkPlayer sender, MdsNetMessage message)
        {
            string? full = TryReassemble(sender.ToString() ?? "guest", message.Text ?? string.Empty);
            if (string.IsNullOrEmpty(full))
            {
                return;
            }

            string[] args = full!.Split(Sep);
            string verb = args.Length > 0 ? args[0] : string.Empty;

            if (verb == "hi")
            {
                bool added = ModdedGuests.Add(sender);
                if (added)
                {
                    MdsLog.Info($"[Net] Guest {sender} verified MDS sync ({WireProtocolVersion}). Sending initial state.");
                }

                FlushSiteSync(forceFull: true, targetPlayer: sender);
                BroadcastAbmWarningsIfChanged(force: true, targetPlayer: sender);
                return;
            }

            if (verb == "req_holdfire" && args.Length >= 3)
            {
                FactionHQ? senderHq = ResolveHq(sender);
                if (senderHq == null)
                {
                    return;
                }

                if (uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pidId) &&
                    pidId != 0)
                {
                    var pid = new PersistentID { Id = pidId };
                    if (pid.TryGetUnit(out Unit unit) &&
                        unit != null &&
                        !unit.disabled &&
                        ReferenceEquals(unit.NetworkHQ, senderHq))
                    {
                        ModularSiteBehavior? site = unit.GetComponent<ModularSiteBehavior>();
                        if (site != null)
                        {
                            bool hold = args[2] == "1";
                            site.SetHoldFire(hold);
                            MarkSiteDirty(site);
                            FlushSiteSync(forceFull: false, targetPlayer: null);
                        }
                    }
                }
            }
        }

        private static void OnGuestMessage(INetworkPlayer _, MdsNetMessage message)
        {
            if (GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            string? full = TryReassemble("host", message.Text ?? string.Empty);
            if (string.IsNullOrEmpty(full))
            {
                return;
            }

            string[] args = full!.Split(Sep);
            if (args.Length == 0)
            {
                return;
            }

            string verb = args[0];
            if (verb == "hello")
            {
                HandleHelloOnClient(args);
                return;
            }

            if (!HostHasMds)
            {
                return;
            }

            switch (verb)
            {
                case "site_sync":
                    HandleSiteSyncOnClient(args);
                    break;
                case "vfx_beam":
                    HandleBeamOnClient(args);
                    break;
                case "vfx_ahead":
                    HandleAheadBurstOnClient(args);
                    break;
                case "container_land":
                    HandleContainerLandedOnClient(args);
                    break;
                case "missile_ign":
                    HandleMissileIgnitionOnClient(args);
                    break;
                case "abm_warn":
                    HandleAbmWarnOnClient(args);
                    break;
            }
        }

        private static void HandleHelloOnClient(string[] args)
        {
            string hostProto = args.Length > 1 ? args[1] : string.Empty;
            uint hostCatalogHash = args.Length > 2 && uint.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint h) ? h : 0u;
            string pkgFactions = args.Length > 3 ? args[3] : string.Empty;
            RemoteEnableNativeAircraftAirdrop = args.Length <= 4 || args[4] == "1";
            RemoteEnableMissionEditorCatalog = args.Length <= 5 || args[5] == "1";
            RemoteEnableAutoPopUp = args.Length <= 6 || args[6] == "1";

            MdsCatalogCoordinator.EnsureAllRegisteredCanonical();
            if (hostCatalogHash != 0 && hostCatalogHash != MdsCatalogCoordinator.CatalogWireHash)
            {
                CatalogMismatchWarning =
                    $"MDS CATALOG MISMATCH: Host hash={hostCatalogHash:x8}, Local={MdsCatalogCoordinator.CatalogWireHash:x8}. Ensure same contractor mods/versions!";
                MdsLog.Warn($"[Net] {CatalogMismatchWarning}");
            }
            else
            {
                CatalogMismatchWarning = null;
            }

            ApplyRemotePackageFactions(pkgFactions);

            bool firstHandshake = !HostHasMds;
            HostHasMds = string.Equals(hostProto, WireProtocolVersion, StringComparison.Ordinal);
            if (HostHasMds && firstHandshake)
            {
                MdsLog.Info($"[Net] Connected to MDS host ({hostProto}, hash={hostCatalogHash:x8}). Sending 'hi'.");
                SendToHost(Join("hi", WireProtocolVersion, MdsCatalogCoordinator.CatalogWireHash.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private static void ApplyRemotePackageFactions(string encoded)
        {
            RemotePackageFactions.Clear();
            if (!string.IsNullOrEmpty(encoded))
            {
                string[] pairs = encoded.Split(';');
                for (int i = 0; i < pairs.Length; i++)
                {
                    int eq = pairs[i].IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string pkgId = pairs[i].Substring(0, eq);
                    if (int.TryParse(pairs[i].Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fInt) &&
                        Enum.IsDefined(typeof(FactionAffinity), fInt))
                    {
                        RemotePackageFactions[pkgId] = (FactionAffinity)fInt;
                    }
                }
            }

            foreach (ContractorPackage pkg in ContractorRegistry.GetPackages())
            {
                if (pkg == null || string.IsNullOrEmpty(pkg.PackageId))
                {
                    continue;
                }

                if (!SavedLocalPackageFactions.ContainsKey(pkg.PackageId))
                {
                    SavedLocalPackageFactions[pkg.PackageId] = pkg.DefaultFaction;
                }

                if (RemotePackageFactions.TryGetValue(pkg.PackageId, out FactionAffinity remoteFaction))
                {
                    pkg.DefaultFaction = remoteFaction;
                }
            }
        }

        private static void RestoreLocalPackageFactions()
        {
            if (SavedLocalPackageFactions.Count == 0)
            {
                return;
            }

            foreach (ContractorPackage pkg in ContractorRegistry.GetPackages())
            {
                if (pkg != null && !string.IsNullOrEmpty(pkg.PackageId) &&
                    SavedLocalPackageFactions.TryGetValue(pkg.PackageId, out FactionAffinity orig))
                {
                    pkg.DefaultFaction = orig;
                }
            }

            SavedLocalPackageFactions.Clear();
        }

        private static void HandleSiteSyncOnClient(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
            {
                return;
            }

            string[] rows = args[1].Split(';');
            for (int i = 0; i < rows.Length; i++)
            {
                string[] cols = rows[i].Split(',');
                if (cols.Length < 12)
                {
                    continue;
                }

                if (!uint.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pidId) || pidId == 0)
                {
                    continue;
                }

                int flags = ParseInt(cols[1]);
                float animPct = ParseInt(cols[2]) / 100f;
                float yawDeg = ParseInt(cols[3]) / 10f;
                float pitchDeg = ParseInt(cols[4]) / 10f;
                float rcs = ParseInt(cols[5]) / 100f;
                int ammo1 = ParseInt(cols[6]);
                int ammo2 = ParseInt(cols[7]);
                int fcuCnt = ParseInt(cols[8]);
                int activeFcuCnt = ParseInt(cols[9]);
                int effCnt = ParseInt(cols[10]);
                int netId = ParseInt(cols[11]);

                MdsClientSiteProxy? proxy = MdsClientSiteProxy.FindByPersistentId(pidId);
                if (proxy == null)
                {
                    var pid = new PersistentID { Id = pidId };
                    if (pid.TryGetUnit(out Unit unit) && unit != null)
                    {
                        proxy = unit.GetComponent<MdsClientSiteProxy>();
                    }
                }

                proxy?.ApplyNetworkSnapshot(
                    flags,
                    animPct,
                    yawDeg,
                    pitchDeg,
                    rcs,
                    ammo1,
                    ammo2,
                    fcuCnt,
                    activeFcuCnt,
                    effCnt,
                    netId);
            }
        }

        private static void HandleBeamOnClient(string[] args)
        {
            if (args.Length < 7)
            {
                return;
            }

            uint pidId = ParseUInt(args[1]);
            float gx = ParseFloat(args[2]);
            float gy = ParseFloat(args[3]);
            float gz = ParseFloat(args[4]);
            float dur = Mathf.Clamp(ParseFloat(args[5]), 0.05f, 1.5f);
            float width = Mathf.Clamp(ParseFloat(args[6]), 0.02f, 2f);

            MdsClientSiteProxy? proxy = MdsClientSiteProxy.FindByPersistentId(pidId);
            if (proxy == null)
            {
                var pid = new PersistentID { Id = pidId };
                if (pid.TryGetUnit(out Unit unit) && unit != null)
                {
                    proxy = unit.GetComponent<MdsClientSiteProxy>();
                }
            }

            if (proxy != null && proxy.TurretAim != null)
            {
                Vector3 localTarget = new GlobalPosition(gx, gy, gz).ToLocalPosition();
                proxy.TurretAim.ShowBeamOrTracer(localTarget, dur, width);
            }
        }

        private static void HandleAheadBurstOnClient(string[] args)
        {
            if (args.Length < 10)
            {
                return;
            }

            uint pidId = ParseUInt(args[1]);
            Vector3 muzzleLocal = new GlobalPosition(ParseFloat(args[2]), ParseFloat(args[3]), ParseFloat(args[4])).ToLocalPosition();
            var dir = new Vector3(ParseFloat(args[5]), ParseFloat(args[6]), ParseFloat(args[7]));
            if (dir.sqrMagnitude < 0.0001f)
            {
                dir = Vector3.forward;
            }
            else
            {
                dir.Normalize();
            }

            float vel = Mathf.Max(400f, ParseFloat(args[8]));
            float fuse = Mathf.Max(0.025f, ParseFloat(args[9]));

            MdsClientSiteProxy? proxy = MdsClientSiteProxy.FindByPersistentId(pidId);
            AheadAirburstManager.SpawnClientVisualBurst(
                proxy != null ? proxy.Unit : null,
                proxy != null ? proxy.Package : null,
                proxy != null ? proxy.Spec : null,
                muzzleLocal,
                dir,
                vel,
                fuse);
        }

        private static void HandleContainerLandedOnClient(string[] args)
        {
            if (args.Length < 4)
            {
                return;
            }

            uint pidId = ParseUInt(args[1]);
            float yawDeg = ParseFloat(args[2]);
            float lenMeters = ParseFloat(args[3]);

            MdsClientSiteProxy? proxy = MdsClientSiteProxy.FindByPersistentId(pidId);
            if (proxy == null)
            {
                var pid = new PersistentID { Id = pidId };
                if (pid.TryGetUnit(out Unit unit) && unit != null)
                {
                    proxy = unit.GetComponent<MdsClientSiteProxy>();
                }
            }

            proxy?.ApplyContainerLanded(yawDeg, lenMeters);
        }

        private static void HandleMissileIgnitionOnClient(string[] args)
        {
            if (args.Length < 2)
            {
                return;
            }

            uint pidId = ParseUInt(args[1]);
            if (pidId == 0)
            {
                return;
            }

            var pid = new PersistentID { Id = pidId };
            if (pid.TryGetUnit(out Unit unit) && unit is Missile m && CustomMissileTracker.TryGet(m, out CustomMissileBehavior beh))
            {
                beh.ForceClientMotorIgnition();
            }
        }

        private static void HandleAbmWarnOnClient(string[] args)
        {
            string body = args.Length > 1 ? args[1] : string.Empty;
            AbmEarlyWarningService.Instance?.ApplyClientSnapshot(body);
        }

        private static FactionHQ? ResolveHq(INetworkPlayer sender)
        {
            NetworkIdentity? identity = ((IObjectOwner)sender).Identity;
            if (identity != null && identity.TryGetComponent(out Player p) && p.HQ != null)
            {
                return p.HQ;
            }

            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null)
                {
                    continue;
                }

                List<Player> players = hq.GetPlayers(false);
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i] != null && players[i].Owner == sender)
                    {
                        return hq;
                    }
                }
            }

            return null;
        }

        private static void SendToAllModdedGuests(string text)
        {
            foreach (INetworkPlayer player in ModdedGuests)
            {
                if (player != null && !player.IsHost)
                {
                    SendToPlayer(player, text);
                }
            }
        }

        private static void SendToPlayer(INetworkPlayer player, string text)
        {
            SendPieces(text, piece =>
            {
                ((IMessageSender)player).Send(new MdsNetMessage { Text = piece }, Mirage.Channel.Reliable);
            });
        }

        private static void SendToHost(string text)
        {
            NetworkClient? client = NetworkManagerNuclearOption.i != null ? NetworkManagerNuclearOption.i.Client : null;
            if (client == null || !client.Active)
            {
                return;
            }

            SendPieces(text, piece =>
            {
                client.Send(new MdsNetMessage { Text = piece }, Mirage.Channel.Reliable);
            });
        }

        private static void SendPieces(string text, Action<string> send)
        {
            try
            {
                if (Encoding.UTF8.GetByteCount(text) <= MaxWireBytes)
                {
                    send(text);
                    return;
                }

                SplitByBytes(text, MaxWireBytes - 64, PieceScratch);
                string pid = (nextPieceId++).ToString(CultureInfo.InvariantCulture);
                string total = PieceScratch.Count.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < PieceScratch.Count; i++)
                {
                    send(Join(PartVerb, pid, i.ToString(CultureInfo.InvariantCulture), total, PieceScratch[i]));
                }

                PieceScratch.Clear();
            }
            catch (Exception ex)
            {
                MdsLog.Debug($"[Net] Send failed: {ex.Message}");
            }
        }

        private static void SplitByBytes(string text, int budget, List<string> pieces)
        {
            pieces.Clear();
            var sb = new StringBuilder();
            int byteCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int charLen = char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1;
                int bytes = charLen == 2 ? 4 : (c < '\u0080' ? 1 : (c < '\u0800' ? 2 : 3));
                if (byteCount + bytes > budget && sb.Length > 0)
                {
                    pieces.Add(sb.ToString());
                    sb.Length = 0;
                    byteCount = 0;
                }

                sb.Append(text, i, charLen);
                byteCount += bytes;
                i += charLen - 1;
            }

            if (sb.Length > 0)
            {
                pieces.Add(sb.ToString());
            }
        }

        private static string? TryReassemble(string senderKey, string text)
        {
            if (!text.StartsWith(PartVerb + SepText, StringComparison.Ordinal))
            {
                return text;
            }

            string[] parts = text.Split(new[] { Sep }, 5);
            if (parts.Length < 5 ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total) ||
                total <= 0 || total > MaxPiecesPerPayload || idx < 0 || idx >= total)
            {
                return null;
            }

            DropStalePieces();
            string bufKey = $"{senderKey}{Sep}{parts[1]}";
            if (!PieceBuffers.TryGetValue(bufKey, out string?[]? buf) || buf.Length != total)
            {
                if (buf == null && CountOpenPayloads(senderKey) >= MaxOpenPayloadsPerSender)
                {
                    return null;
                }

                buf = new string?[total];
                PieceBuffers[bufKey] = buf;
                PieceStartedAt[bufKey] = Time.realtimeSinceStartup;
            }

            buf[idx] = parts[4];
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == null)
                {
                    return null;
                }
            }

            PieceBuffers.Remove(bufKey);
            PieceStartedAt.Remove(bufKey);
            return string.Concat(buf);
        }

        private static int CountOpenPayloads(string senderKey)
        {
            string prefix = senderKey + SepText;
            int count = 0;
            foreach (string key in PieceBuffers.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void DropStalePieces()
        {
            if (PieceStartedAt.Count == 0)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            StalePieceScratch.Clear();
            foreach (KeyValuePair<string, float> kv in PieceStartedAt)
            {
                if (now - kv.Value > PieceTimeoutSeconds)
                {
                    StalePieceScratch.Add(kv.Key);
                }
            }

            for (int i = 0; i < StalePieceScratch.Count; i++)
            {
                PieceBuffers.Remove(StalePieceScratch[i]);
                PieceStartedAt.Remove(StalePieceScratch[i]);
            }

            StalePieceScratch.Clear();
        }

        private static string Join(params string[] args) => string.Join(SepText, args);

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static uint ParseUInt(string s) =>
            uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : 0u;

        private static float ParseFloat(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
            !float.IsNaN(v) && !float.IsInfinity(v)
                ? v
                : 0f;
    }
}
