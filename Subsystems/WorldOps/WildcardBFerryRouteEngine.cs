using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #236 "The Ferry Route - Server-Steered Ships That Move A Crew". `Ship.Start` registers "Stop",
    /// "Forward", "Backward" and `Rudder(float)` on the ship's OWN ZNetView with no owner/sender check
    /// (SERVER decompile :140482-140485, :140538-140593 - confirmed directly by this build) - the
    /// handlers just mutate `m_speed`/`m_rudderValue` on whichever instance receives the RPC. Physics
    /// (`CustomFixedUpdate`) is OWNER-ONLY (`if (m_nview && !m_nview.IsOwner()) return;`, :140601-140604),
    /// so a command has to be ADDRESSED TO THE OWNING PEER, not broadcast: this engine calls
    /// `ZRoutedRpc.instance.InvokeRoutedRPC(shipZdo.GetOwner(), shipZdo.m_uid, "Forward"/"Backward"/"Stop"/"Rudder", ...)`
    /// exactly as Core/Data/PlayerNotify.cs already does for the unrelated "Message" RPC - no Harmony
    /// patch, no new hook broker, this is calling an already-registered RPC channel with our own data.
    ///
    /// Two hard gates confirmed directly in `CustomFixedUpdate` mean this can only ferry a CREWED ship:
    /// `if (m_players.Count == 0) { m_speed = Stop; m_rudderValue = 0; }` (:140607-140611) - an empty
    /// ship cannot be driven at all, full stop, no exception - and `if (!HaveControllingPlayer() &amp;&amp;
    /// (Slow || Back)) m_speed = Stop;` (:140612-140615) - without a human at the helm
    /// (`ShipControlls.HaveValidUser`/`s_user`) only Half/Full sail run, so docking manoeuvres are
    /// sail-only. `Ship.UpdateOwner` re-homes ownership to a rider every 2s (:140968-140977), so this
    /// engine re-resolves `shipZdo.GetOwner()` before every command rather than caching it, and yields
    /// (sends nothing) whenever `ZDOVars.s_user != 0` (a human is actively steering) - the catalog's own
    /// "the route never fights a player" rule.
    ///
    /// Confirmation loop: `s_forward`/`s_rudder` (ZDOVars, :78431/:78577) are written by the OWNER every
    /// tick (`UpdateControlls`, :140863-140876) and read back here to know whether the last command
    /// actually landed, rather than assuming an RPC with no ack succeeded.
    /// </summary>
    public static class WildcardBFerryRouteEngine
    {
        public enum ShipSpeed { Stop = 0, Back = 1, Slow = 2, Half = 3, Full = 4 }

        public sealed class Waypoint
        {
            public Vector3 Position;
            public ShipSpeed CruiseSpeed = ShipSpeed.Full;
        }

        private sealed class Route
        {
            public ZDOID ShipId;
            public List<Waypoint> Waypoints = new List<Waypoint>();
            public int CurrentIndex;
        }

        private static readonly Dictionary<ZDOID, Route> _routes = new Dictionary<ZDOID, Route>();
        private static float _timer;

        /// <summary>Declares (or replaces) a route for a ship ZDO. The controller only ever drives while WildcardBConfig.FerryRouteEnabled is on and the ship is currently crewed with nobody at the helm.</summary>
        public static void DeclareRoute(ZDOID shipId, List<Waypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0)
            {
                _routes.Remove(shipId);
                return;
            }
            _routes[shipId] = new Route { ShipId = shipId, Waypoints = waypoints, CurrentIndex = 0 };
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.FerryRouteEnabled?.Value != true || ZDOMan.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardBConfig.FerryCommandIntervalSeconds?.Value ?? 0.25f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            foreach (Route route in _routes.Values)
            {
                DriveOne(route);
            }
        }

        private static void DriveOne(Route route)
        {
            ZDO ship = ZDOMan.instance.GetZDO(route.ShipId);
            if (ship == null || !ship.IsValid())
            {
                return;
            }
            if (ship.GetLong(ZDOVars.s_user, 0L) != 0L)
            {
                return; // a human is steering - yield entirely, per the catalog's own "never fight a player" rule
            }

            long ownerPeer = ship.GetOwner();
            if (ownerPeer == 0L)
            {
                return; // no owning client at all right now - nothing to address
            }

            Waypoint wp = route.Waypoints[route.CurrentIndex];
            Vector3 shipPos = ship.GetPosition();
            Vector2 flatToWaypoint = new Vector2(wp.Position.x - shipPos.x, wp.Position.z - shipPos.z);
            float distance = flatToWaypoint.magnitude;

            float arriveRadius = WildcardBConfig.FerryWaypointArriveRadius?.Value ?? 8f;
            if (distance <= arriveRadius)
            {
                SendCommand(ownerPeer, route.ShipId, "Stop");
                route.CurrentIndex = (route.CurrentIndex + 1) % route.Waypoints.Count;
                return;
            }

            Quaternion shipRot = ship.GetRotation();
            Vector3 shipForward = shipRot * Vector3.forward;
            float desiredHeading = Mathf.Atan2(flatToWaypoint.x, flatToWaypoint.y);
            float currentHeading = Mathf.Atan2(shipForward.x, shipForward.z);
            float headingError = Mathf.DeltaAngle(currentHeading * Mathf.Rad2Deg, desiredHeading * Mathf.Rad2Deg) / 180f; // [-1,1]
            float gain = WildcardBConfig.FerryRudderGain?.Value ?? 1f;
            float rudder = Mathf.Clamp(headingError * gain, -1f, 1f);
            SendCommand(ownerPeer, route.ShipId, "Rudder", rudder);

            int currentSpeed = ship.GetInt(ZDOVars.s_forward, 0);
            int wantSpeed = (int)wp.CruiseSpeed;
            if (currentSpeed < wantSpeed)
            {
                SendCommand(ownerPeer, route.ShipId, "Forward");
            }
            else if (currentSpeed > wantSpeed)
            {
                SendCommand(ownerPeer, route.ShipId, "Backward");
            }
        }

        private static void SendCommand(long ownerPeer, ZDOID shipId, string rpcName, params object[] args)
        {
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeer, shipId, rpcName, args);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBFerryRouteEngine] '{rpcName}' to ship {shipId} (owner {ownerPeer}) failed: {ex.Message}");
            }
        }
    }
}
