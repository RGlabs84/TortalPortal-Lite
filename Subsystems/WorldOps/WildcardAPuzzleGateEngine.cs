using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #142 The Tag Is Also The Lock - Puzzle And Progression Gates. Composes four independent levers
    /// (#142's own howItWorks) into real gate mechanics, admin-declared via a hot-reloaded
    /// wildcardA_puzzles.json (same file-poll shape as Foundations/NetworkModel.cs's own networks.json,
    /// kept fully independent so the two files can change on their own schedules).
    ///
    /// HARD LOCK (server-enforced): null the ring portals' connections. Genuinely enforced because
    /// TeleportWorld.Teleport gate 1 (:143519) reads the ZDO connection the server owns - but vanilla's
    /// pairer re-pairs a same-tag unconnected portal within 5s, so this engine re-asserts the null on a
    /// fast tick (WildcardAConfig.PuzzleReassertSeconds) rather than writing it once.
    /// SOFT LOCK (silent, no error to search for): delegates to WildcardADecoyGateEngine - the "sealed"
    /// state for a puzzle a player should discover by exploring rather than being told is locked.
    /// GLOBAL LOCK (client-honoured, world-wide): session-only global-key write via
    /// ZoneSystem.GlobalKeyAdd(name, canSaveToServerOptionKeys:false) + SendGlobalKeys(0L) - the same
    /// recipe Enforcement/LockdownGlobalKeyEngine.cs already proves safe, reimplemented locally here so
    /// this domain never depends on Enforcement's own engine state.
    /// STATE: solved/unsolved per puzzle persists on the FIRST ring portal's ZDO via
    /// Core/Data/DataStore.cs (namespace "wildcardA.puzzle") - #142's own cited prerequisite, extending
    /// that pattern rather than inventing a parallel one.
    /// INPUT: item-stand combination reading, gated to ONLY stands a puzzle definition explicitly lists
    /// by position (never PlayerInterface's own dial-board stands - no double-handling of the same
    /// physical stand for two different purposes).
    /// </summary>
    public sealed class WildcardAPuzzleStandRequirement
    {
        public float X;
        public float Y;
        public float Z;
        public string RequiredPrefabName = "";
        public Vector3 Pos() => new Vector3(X, Y, Z);
    }

    public sealed class WildcardAPuzzleRingMember
    {
        public float X;
        public float Y;
        public float Z;
        public Vector3 Pos() => new Vector3(X, Y, Z);
    }

    public sealed class WildcardAPuzzleDefinition
    {
        public string Name = "";
        public List<WildcardAPuzzleStandRequirement> Stands = new List<WildcardAPuzzleStandRequirement>();
        public List<WildcardAPuzzleRingMember> Ring = new List<WildcardAPuzzleRingMember>();
        public string SealedTag = "";
        public string OpenTag = "";
        public string? GlobalKeyOnSolve;
        public string? DiscoveryPinLabel;
    }

    public sealed class WildcardAPuzzlesFile
    {
        public int SchemaVersion = 1;
        public List<WildcardAPuzzleDefinition> Puzzles = new List<WildcardAPuzzleDefinition>();
    }

    public static class WildcardAPuzzleGateEngine
    {
        private const float FilePollInterval = 5f;
        private static float _filePollTimer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<WildcardAPuzzleDefinition> _puzzles = new List<WildcardAPuzzleDefinition>();
        private static readonly HashSet<string> _solved = new HashSet<string>();

        private static float _reassertTimer;
        private static bool _hookRegistered;

        public static void Initialize()
        {
            if (_hookRegistered)
            {
                return;
            }
            _hookRegistered = true;
            RpcZdoDataHook.RegisterPostfix(200, OnZdoDataFromClient);
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardAConfig.Enabled?.Value == false)
            {
                return;
            }

            _filePollTimer += dt;
            if (_filePollTimer >= FilePollInterval)
            {
                _filePollTimer = 0f;
                TryReloadFile();
            }

            _reassertTimer += dt;
            float interval = WildcardAConfig.PuzzleReassertSeconds?.Value ?? 2.0f;
            if (_reassertTimer >= interval)
            {
                _reassertTimer = 0f;
                ReassertAll();
            }
        }

        // ----------------------------------------------------------------------- reusable lock levers

        /// <summary>HARD LOCK: nulls <paramref name="portal"/>'s connection. Server-enforced, needs re-assertion (see ReassertAll) since vanilla re-pairs a same-tag unconnected portal within 5s.</summary>
        public static bool HardLock(ZDO portal)
        {
            if (portal == null || !portal.IsValid())
            {
                return false;
            }
            if (WildcardAWriteOps.IsNetworkManaged(portal))
            {
                PortalDebug.LogWarning($"[WildcardAPuzzleGateEngine] refusing to hard-lock {portal.m_uid} - it is NetworkReassertEngine-managed.");
                return false;
            }
            WildcardAWriteOps.ReassertTagAndConnection(portal, null, ZDOID.None);
            PortalOwnership.ClaimAndWrite(portal, z => z.Set(WildcardAZdoKeys.PuzzleHardLocked, 1));
            return true;
        }

        public static void HardUnlock(ZDO portal)
        {
            if (portal == null || !portal.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(portal, z => z.RemoveInt(WildcardAZdoKeys.PuzzleHardLocked));
        }

        /// <summary>SOFT LOCK: delegates to the Decoy Gate primitive - silent, no error to search for.</summary>
        public static bool SoftLock(ZDO portal) => WildcardADecoyGateEngine.MakeDecoy(portal);

        /// <summary>GLOBAL LOCK: session-only world-modifier key, never mirrored into the .fwl. Matching lift is GlobalUnlock.</summary>
        public static void GlobalLock(string keyName)
        {
            if (string.IsNullOrEmpty(keyName) || ZoneSystem.instance == null)
            {
                return;
            }
            if (ZoneSystem.instance.GetGlobalKey(keyName))
            {
                return;
            }
            try
            {
                ZoneSystem.instance.GlobalKeyAdd(keyName, canSaveToServerOptionKeys: false);
                ZoneSystem.instance.SendGlobalKeys(0L);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardAPuzzleGateEngine] GlobalLock('{keyName}') failed: {ex.Message}");
            }
        }

        public static void GlobalUnlock(string keyName)
        {
            if (string.IsNullOrEmpty(keyName) || ZoneSystem.instance == null || !ZoneSystem.instance.GetGlobalKey(keyName))
            {
                return;
            }
            try
            {
                ZoneSystem.instance.GlobalKeyRemove(keyName, canSaveToServerOptionKeys: false);
                ZoneSystem.instance.SendGlobalKeys(0L);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardAPuzzleGateEngine] GlobalUnlock('{keyName}') failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------------------- definitions

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(WildcardAConfig.PuzzlesFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                WildcardAConfig.PuzzlesFile?.Value ?? "wildcardA_puzzles.json");

        private static void TryReloadFile()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                string json = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<WildcardAPuzzlesFile>(json);
                if (parsed?.Puzzles == null)
                {
                    PortalDebug.LogWarning($"[WildcardAPuzzleGateEngine] '{path}' parsed but had no 'Puzzles' array - ignoring.");
                    return;
                }
                _puzzles = parsed.Puzzles;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[WildcardAPuzzleGateEngine] loaded {_puzzles.Count} puzzle definition(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardAPuzzleGateEngine] failed to load puzzle definitions: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------------------- input (stands)

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (ZDOMan.instance == null || _puzzles.Count == 0)
            {
                return;
            }
            ZDO changed = ZDOMan.instance.GetZDO(zdoid);
            if (changed == null || !changed.IsValid())
            {
                return;
            }
            Vector3 changedPos = changed.GetPosition();

            foreach (WildcardAPuzzleDefinition puzzle in _puzzles)
            {
                bool touchesThisPuzzle = false;
                foreach (WildcardAPuzzleStandRequirement stand in puzzle.Stands)
                {
                    if ((stand.Pos() - changedPos).sqrMagnitude < 0.25f)
                    {
                        touchesThisPuzzle = true;
                        break;
                    }
                }
                if (touchesThisPuzzle)
                {
                    EvaluatePuzzle(puzzle);
                }
            }
        }

        private static void EvaluatePuzzle(WildcardAPuzzleDefinition puzzle)
        {
            if (ZDOMan.instance == null || puzzle.Stands.Count == 0)
            {
                return;
            }

            bool allCorrect = true;
            foreach (WildcardAPuzzleStandRequirement stand in puzzle.Stands)
            {
                if (!TryFindStandZdo(stand.Pos(), out ZDO standZdo))
                {
                    allCorrect = false;
                    break;
                }
                int itemHash = standZdo.GetInt(ZDOVars.s_item, 0);
                int requiredHash = string.IsNullOrEmpty(stand.RequiredPrefabName) ? 0 : stand.RequiredPrefabName.GetStableHashCode();
                if (itemHash != requiredHash || requiredHash == 0)
                {
                    allCorrect = false;
                    break;
                }
            }

            bool wasSolved = _solved.Contains(puzzle.Name);
            if (allCorrect && !wasSolved)
            {
                Solve(puzzle);
            }
            else if (!allCorrect && wasSolved)
            {
                Reset(puzzle);
            }
        }

        private static bool TryFindStandZdo(Vector3 pos, out ZDO stand)
        {
            stand = null;
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(pos, 1.0f);
            foreach (ZDO z in nearby)
            {
                if ((z.GetPosition() - pos).sqrMagnitude < 0.25f)
                {
                    stand = z;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------------------- ring reassert

        private static void Solve(WildcardAPuzzleDefinition puzzle)
        {
            _solved.Add(puzzle.Name);
            if (puzzle.Ring.Count > 0 && TryFindPortal(puzzle.Ring[0].Pos(), out ZDO hub))
            {
                DataStore.Set(hub, "wildcardA.puzzle", "solved");
            }
            if (!string.IsNullOrEmpty(puzzle.GlobalKeyOnSolve))
            {
                GlobalLock(puzzle.GlobalKeyOnSolve!);
            }
            PortalDebug.LogAlways($"[WildcardAPuzzleGateEngine] puzzle '{puzzle.Name}' SOLVED.");
            ApplyRingState(puzzle, solved: true);

            if (!string.IsNullOrEmpty(puzzle.DiscoveryPinLabel) && puzzle.Ring.Count > 0)
            {
                Vector3 pos = puzzle.Ring[0].Pos();
                foreach (ConnectedCharacter cc in ConnectedCharacters.All())
                {
                    if ((cc.Position - pos).sqrMagnitude < 4900f) // within 70m
                    {
                        WildcardAMapSurfaceEngine.PushDiscoveryPin(cc, puzzle.DiscoveryPinLabel!, pos);
                    }
                }
            }
        }

        private static void Reset(WildcardAPuzzleDefinition puzzle)
        {
            _solved.Remove(puzzle.Name);
            if (puzzle.Ring.Count > 0 && TryFindPortal(puzzle.Ring[0].Pos(), out ZDO hub))
            {
                DataStore.Set(hub, "wildcardA.puzzle", "unsolved");
            }
            if (!string.IsNullOrEmpty(puzzle.GlobalKeyOnSolve))
            {
                GlobalUnlock(puzzle.GlobalKeyOnSolve!);
            }
            PortalDebug.LogAlways($"[WildcardAPuzzleGateEngine] puzzle '{puzzle.Name}' reset to unsolved.");
            ApplyRingState(puzzle, solved: false);
        }

        private static bool TryFindPortal(Vector3 pos, out ZDO zdo)
        {
            zdo = null;
            if (!PortalCensus.TryGetByPosition(pos, out PortalRecord record) || ZDOMan.instance == null)
            {
                return false;
            }
            zdo = ZDOMan.instance.GetZDO(record.Uid);
            return zdo != null && zdo.IsValid();
        }

        private static void ApplyRingState(WildcardAPuzzleDefinition puzzle, bool solved)
        {
            int n = puzzle.Ring.Count;
            if (n == 0)
            {
                return;
            }

            var members = new ZDO[n];
            for (int i = 0; i < n; i++)
            {
                TryFindPortal(puzzle.Ring[i].Pos(), out members[i]);
            }

            string tag = solved ? puzzle.OpenTag : puzzle.SealedTag;
            for (int i = 0; i < n; i++)
            {
                ZDO member = members[i];
                if (member == null)
                {
                    continue;
                }
                if (WildcardAWriteOps.IsNetworkManaged(member))
                {
                    continue;
                }
                if (!solved || n == 1)
                {
                    WildcardAWriteOps.ReassertTagAndConnection(member, tag, ZDOID.None);
                    continue;
                }
                ZDO next = members[(i + 1) % n];
                if (next != null)
                {
                    WildcardAWriteOps.ReassertTagAndConnection(member, tag, next.m_uid);
                }
            }
        }

        private static void ReassertAll()
        {
            if (_puzzles.Count == 0)
            {
                return;
            }
            foreach (WildcardAPuzzleDefinition puzzle in _puzzles)
            {
                ApplyRingState(puzzle, _solved.Contains(puzzle.Name));
            }
        }
    }
}
