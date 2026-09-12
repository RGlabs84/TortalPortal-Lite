using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #15 Per-Player Private Networks. Every player-placed portal's builder is readable server-side,
    /// offline, forever: Piece.SetCreator writes ZDOVars.s_creator (SERVER decompile :136415-136424,
    /// identical to ZDOVars.s_playerID on that player's own character ZDO). This engine partitions every
    /// UNMANAGED portal (nothing already carrying a Foundations/Shape NetworkId - this is deliberately
    /// only for portals nobody has wired into a declared network) into a per-creator namespace using
    /// TopologiesTagShardEngine's invisible suffix, so two players who both build a portal called
    /// "farm" never pair with each other: FindRandomUnconnectedPortal's candidate filter is an EXACT
    /// string match (:100669), so "farm&lt;pXYZ&gt;" and "farm&lt;pABC&gt;" can never be selected as each
    /// other's partner.
    ///
    /// Writes ONLY via PortalOwnership.ClaimAndWrite (never a bare zdo.Set), and only the tag - this
    /// engine never touches a portal's connection itself, unlike TopologiesShapeEngine; vanilla's own
    /// pass 2 (or the player's own previously-formed pair) does the actual pairing once the tag is
    /// correct, exactly like doing nothing at all except for the namespacing.
    /// </summary>
    public static class TopologiesPrivateNetworksEngine
    {
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (!TopologiesDefinitions.Current.PrivateNetworks.Enabled)
            {
                return;
            }
            _timer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Normalize();
        }

        private static void Normalize()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (written >= budget)
                {
                    break;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                // Never re-shard a portal a declared Shape/Foundations network already owns - see this
                // class's own doc comment.
                if (!string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo)))
                {
                    continue;
                }

                long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                // Creator 0 covers world-gen/admin-spawned portals AND anything placed before the field
                // existed (Piece.SetCreator only writes when GetCreator()==0 && IsOwner(), :136415-136424)
                // - treated as the shared public namespace, never as "owned by nobody in particular".
                string payload = creator == 0L ? "" : "p" + TopologiesTagShardEngine.ShortCode(creator, 3);
                string baseDisplay = TopologiesTagShardEngine.BaseOf(record.Tag);
                string desired = TopologiesTagShardEngine.FitAndShard(baseDisplay, payload);

                if (record.Tag == desired)
                {
                    continue;
                }

                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, desired));
                    written++;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesPrivateNetworksEngine] failed to shard {record.Uid}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
