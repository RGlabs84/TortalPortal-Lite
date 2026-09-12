using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    public sealed class UxSocketDef
    {
        public float PortalX;
        public float PortalY;
        public float PortalZ;
        public float OffsetX;
        public float OffsetY;
        public float OffsetZ;

        /// <summary>Empty = accept any Piece-carrying prefab. Non-empty = only these prefab names count.</summary>
        public List<string> AcceptedPrefabNames = new List<string>();

        public string Destination = "";
    }

    public sealed class UxSocketsFileModel
    {
        public int SchemaVersion = 1;
        public List<UxSocketDef> Sockets = new List<UxSocketDef>();
    }

    /// <summary>
    /// #165 Build-shape input - placing a piece as a latching switch. The non-obvious part, per the
    /// catalog: a dedicated server never sees `Player.PlacePiece` or `ZNetScene.Instantiate` for a
    /// client-placed piece (no Player instance, and the client instantiates locally - only the ZDO
    /// arrives). The correct hook is the moment `ZDO.Deserialize` first carries the real prefab hash,
    /// which for THIS mod means the ordinary `RpcZdoDataHook` postfix, already firing for every incoming
    /// client ZDO write regardless of prefab. Socket matching uses a radius (never exact equality) since
    /// snap-grid placement and rotation make a hard position match miss almost everything (the catalog's
    /// own explicit warning).
    ///
    /// Also implements the catalog's own noted "same hook = the server-enforced portal cap" as an
    /// OPT-IN, off-by-default convenience (`UxConfig.EnforcePortalCap`) - portal-count POLICY reads as
    /// more of an access-domain call than a UX one, so this mod defaults it off and documents the
    /// overlap rather than silently deciding it.
    /// </summary>
    public static class UxBuildShapeSwitchEngine
    {
        private static List<UxSocketDef> _sockets = new List<UxSocketDef>();
        private static DateTime _fileStamp = DateTime.MinValue;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(150, OnZdoDataFromClient);
        }

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.BuildShapeEnabled?.Value == false)
            {
                return;
            }
            TryReload();
        }

        private static void TryReload()
        {
            try
            {
                string path = UxFilePaths.Resolve(UxConfig.SocketsFile, "ux_sockets.json");
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                UxSocketsFileModel parsed = JsonConvert.DeserializeObject<UxSocketsFileModel>(File.ReadAllText(path));
                if (parsed == null)
                {
                    return;
                }
                _sockets = parsed.Sockets ?? new List<UxSocketDef>();
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[UxBuildShapeSwitchEngine] loaded {_sockets.Count} socket(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxBuildShapeSwitchEngine] failed to load '{UxConfig.SocketsFile?.Value}': {ex.Message}");
            }
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (UxConfig.Enabled?.Value == false || ZDOMan.instance == null || ZNetScene.instance == null)
            {
                return;
            }
            ZDO piece = ZDOMan.instance.GetZDO(zdoid);
            if (piece == null || !piece.IsValid())
            {
                return;
            }

            if (UxConfig.EnforcePortalCap?.Value == true)
            {
                EnforcePortalCapIfNeeded(piece, sender);
            }

            if (UxConfig.BuildShapeEnabled?.Value == false || _sockets.Count == 0 || !TargetedPrefabDiscovery.IsPiece(piece.GetPrefab()))
            {
                return;
            }
            GameObject prefab = ZNetScene.instance.GetPrefab(piece.GetPrefab());
            if (prefab == null)
            {
                return;
            }

            Vector3 piecePos = piece.GetPosition();
            float matchRadius = UxConfig.SocketMatchRadius?.Value ?? 0.75f;

            foreach (UxSocketDef socket in _sockets)
            {
                if (socket.AcceptedPrefabNames.Count > 0 && !socket.AcceptedPrefabNames.Contains(prefab.name))
                {
                    continue;
                }
                Vector3 expected = new Vector3(socket.PortalX + socket.OffsetX, socket.PortalY + socket.OffsetY, socket.PortalZ + socket.OffsetZ);
                if ((piecePos - expected).sqrMagnitude > matchRadius * matchRadius)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(socket.Destination))
                {
                    continue;
                }
                Vector3 portalPos = new Vector3(socket.PortalX, socket.PortalY, socket.PortalZ);
                if (!UxAddressBook.TryNearestAnyPortal(portalPos, 3f, out PortalRecord source) ||
                    !UxAddressBook.TryGet(socket.Destination, out PortalRecord dest))
                {
                    continue;
                }
                ZDO sourceZdo = ZDOMan.instance.GetZDO(source.Uid);
                if (sourceZdo == null || !sourceZdo.IsValid())
                {
                    continue;
                }
                ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
                UxDialAction.TryDialToRecord(sourceZdo, dest, requester, out _);
                break; // one socket match per placement
            }
        }

        /// <summary>Strictly stronger than a client-side cap, per the catalog: this reads `s_creator` off the ZDO itself and destroys server-side, which no client can refuse.</summary>
        private static void EnforcePortalCapIfNeeded(ZDO zdo, ZNetPeer? sender)
        {
            if (Game.instance == null || !Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
            {
                return;
            }
            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (creator == 0L)
            {
                return;
            }
            int cap = UxConfig.PortalCapPerPlayer?.Value ?? 25;
            int count = 0;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                ZDO other = ZDOMan.instance.GetZDO(rec.Uid);
                if (other != null && other.IsValid() && other.GetLong(ZDOVars.s_creator, 0L) == creator)
                {
                    count++;
                }
            }
            if (count <= cap)
            {
                return;
            }

            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
            ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
            if (requester.HasValue)
            {
                UxFeedback.Toast(requester.Value, $"Portal cap reached ({cap}) - that one was removed.");
            }
        }

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
