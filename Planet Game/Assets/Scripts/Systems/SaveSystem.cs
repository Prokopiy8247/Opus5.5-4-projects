using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Plain-data save file. Everything is stored in LOGICAL coordinates with
    /// full double precision, plus the terrain seed of every body, so a load
    /// reconstructs exactly the same terrain at exactly the same place.
    /// </summary>
    [Serializable]
    public class SaveData
    {
        public int version = 2;
        public string systemName;
        public string savedAtUtc;

        // ship
        public double px, py, pz;
        public double vx, vy, vz;
        public float rotX, rotY, rotZ, rotW;
        public string soiBodyId;
        public float hull;
        public float throttle;
        public bool landed;
        public bool flightAssist = true;
        public string targetBodyId;
        public string selectedBeaconId;

        // origin
        public double ox, oy, oz;

        // world
        public List<BodySave> bodies = new List<BodySave>();
        public List<BeaconRegistry.Beacon> beacons = new List<BeaconRegistry.Beacon>();

        // stats for the report / HUD
        public int originShifts;
        public float playSeconds;
    }

    [Serializable]
    public class BodySave
    {
        public string id;
        public int seed;
        public double radius;
        public string poiName;
        public string poiDesc;
        public bool discovered;
    }

    /// <summary>
    /// Save / load. Works both in space and on a surface; it never changes the
    /// world, it only restores where things were.
    ///
    /// Location: the file goes next to the game (Application.persistentDataPath)
    /// and the path is printed to the HUD so it is never a mystery.
    /// </summary>
    public class SaveSystem : MonoBehaviour
    {
        public static SaveSystem Instance { get; private set; }

        /// <summary>Clears the static handle. Used only by automated tests.</summary>
        public static void ResetInstance() { Instance = null; }

        public WorldStreamer streamer;
        public ShipController ship;
        public CameraRig rig;
        public BeaconRegistry beacons;
        public float playSeconds;

        public string LastSavePath { get; private set; }
        public int LoadCount { get; private set; }
        public string LastStatus { get; private set; } = "";

        /// <summary>Set by tests and automated runs so they never overwrite the player's own save.</summary>
        public static string PathOverride;

        public static string SavePath => !string.IsNullOrEmpty(PathOverride) ? PathOverride : Path.Combine(Application.persistentDataPath, PGConst.SaveFileName);

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LastSavePath = SavePath;
        }

        void Start() { LastSavePath = SavePath; }

        void Update() { playSeconds += Time.unscaledDeltaTime; }

        public SaveData Capture()
        {
            var d = new SaveData
            {
                systemName = streamer.system.systemName,
                savedAtUtc = DateTime.UtcNow.ToString("o"),
                px = ship.LogicalPosition.x,
                py = ship.LogicalPosition.y,
                pz = ship.LogicalPosition.z,
                vx = ship.LogicalVelocity.x,
                vy = ship.LogicalVelocity.y,
                vz = ship.LogicalVelocity.z,
                soiBodyId = ship.SoiBody != null ? ship.SoiBody.id : "",
                hull = ship.HullIntegrity,
                throttle = ship.inThrottle,
                landed = ship.Landed,
                flightAssist = ship.flightAssist,
                targetBodyId = streamer.TargetBody != null ? streamer.TargetBody.id : "",
                selectedBeaconId = beacons.Selected != null ? beacons.Selected.id : "",
                originShifts = FloatingOrigin.Instance.ShiftCount,
                playSeconds = playSeconds
            };

            var r = ship.Orientation;
            d.rotX = r.x; d.rotY = r.y; d.rotZ = r.z; d.rotW = r.w;

            var o = FloatingOrigin.Instance.Origin;
            d.ox = o.x; d.oy = o.y; d.oz = o.z;

            foreach (var b in streamer.system.bodies)
            {
                if (b.kind == BodyKind.Star) continue;
                var ws = WorldStreamer.Instance;
                d.bodies.Add(new BodySave
                {
                    id = b.id,
                    seed = b.seed,
                    radius = b.radius,
                    poiName = b.poiName,
                    poiDesc = b.poiDesc,
                    discovered = ws != null && ws.Discovered.Contains(b.id)
                });
            }

            d.beacons = new List<BeaconRegistry.Beacon>(beacons.beacons);
            return d;
        }

        public bool Save()
        {
            try
            {
                var d = Capture();
                string json = JsonUtility.ToJson(d, true);
                File.WriteAllText(SavePath, json);
                LastSavePath = SavePath;
                LastStatus = "SAVED (" + (json.Length / 1024) + " KB) -> " + SavePath;
                Debug.Log("[PG] saved: " + SavePath);
                return true;
            }
            catch (Exception e)
            {
                LastStatus = "SAVE FAILED: " + e.Message;
                Debug.LogError("[PG] save failed: " + e);
                return false;
            }
        }

        public bool Load()
        {
            try
            {
                if (!File.Exists(SavePath)) { LastStatus = "NO SAVE AT " + SavePath; return false; }
                string json = File.ReadAllText(SavePath);
                var d = JsonUtility.FromJson<SaveData>(json);
                if (d == null) { LastStatus = "SAVE FILE UNREADABLE"; return false; }

                // Re-seed terrain from the file before anything is streamed, so
                // the ground under the restored position is the same ground.
                bool terrainChanged = false;
                foreach (var bs in d.bodies)
                {
                    var b = streamer.system.Get(bs.id);
                    if (b == null) continue;
                    if (b.seed != bs.seed || Math.Abs(b.radius - bs.radius) > 1e-6) terrainChanged = true;
                    b.seed = bs.seed;
                    b.radius = bs.radius;
                    b.poiName = bs.poiName;
                    b.poiDesc = bs.poiDesc;
                    if (bs.discovered) streamer.Discovered.Add(bs.id);
                }
                // Only a different seed means different ground; otherwise the
                // streamed terrain is already the saved terrain.
                if (terrainChanged) streamer.RebuildAfterSaveLoad();

                // Restore the exact saved state. This is a load, not travel: it is
                // counted separately from debug teleports.
                var rot = new Quaternion(d.rotX, d.rotY, d.rotZ, d.rotW);
                ship.RestoreState(new DVec3(d.px, d.py, d.pz), new DVec3(d.vx, d.vy, d.vz), rot);
                // Held controls belong to the moment before the load, not to the
                // restored state: clear them so a landed save stays landed.
                ship.inYaw = ship.inPitch = ship.inRoll = ship.inLift = 0f;
                ship.brake = ship.boost = false;
                ship.takeoffRequested = ship.landingRequested = false;
                ship.HullIntegrity = d.hull;
                ship.inThrottle = d.throttle;
                ship.flightAssist = d.flightAssist;
                ship.SoiBody = streamer.system.Get(d.soiBodyId) ?? streamer.system.DominantBody(ship.LogicalPosition);
                if (d.landed) ship.EnterLanded(); else ship.EnterLandedIfTouchingGround();
                var tgt = streamer.system.Get(d.targetBodyId);
                if (tgt != null) streamer.SetTarget(tgt);
                LoadCount++;

                beacons.Clear();
                if (d.beacons != null)
                    foreach (var b in d.beacons) beacons.beacons.Add(b);
                beacons.RebuildAll();
                beacons.Selected = beacons.ById(d.selectedBeaconId);

                if (rig != null) rig.SnapImmediate();

                playSeconds = d.playSeconds;
                LastStatus = "LOADED (" + d.savedAtUtc + ")";
                Debug.Log("[PG] loaded: " + SavePath);
                return true;
            }
            catch (Exception e)
            {
                LastStatus = "LOAD FAILED: " + e.Message;
                Debug.LogError("[PG] load failed: " + e);
                return false;
            }
        }

        public void DeleteSave()
        {
            try
            {
                if (File.Exists(SavePath)) File.Delete(SavePath);
                LastStatus = "SAVE DELETED";
            }
            catch (Exception e) { LastStatus = "DELETE FAILED: " + e.Message; }
        }
    }
}
