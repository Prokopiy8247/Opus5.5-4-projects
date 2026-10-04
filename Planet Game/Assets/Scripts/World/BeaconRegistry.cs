using System;
using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Beacons and discoveries. A beacon is a player-placed marker at a logical
    /// position; it survives origin shifts and saves exactly because it stores
    /// logical coordinates and never a Unity transform.
    /// </summary>
    public class BeaconRegistry : MonoBehaviour, FloatingOrigin.ITracked
    {
        [Serializable]
        public class Beacon
        {
            public string id;
            public string label;
            public string bodyId;
            public double x, y, z;              // logical
            public string kind;                 // "beacon" | "discovery"
            public string note;
        }

        public static BeaconRegistry Instance { get; private set; }

        /// <summary>Clears the static handle. Used only by automated tests.</summary>
        public static void ResetInstance() { Instance = null; }

        public readonly List<Beacon> beacons = new List<Beacon>();

        readonly Dictionary<string, GameObject> markers = new Dictionary<string, GameObject>();
        public Material BeaconMaterial;
        public Material DiscoveryMaterial;

        public int Count => beacons.Count;
        public Beacon Selected { get; set; }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnEnable() { FloatingOrigin.Instance?.Register(this); }
        void OnDisable() { FloatingOrigin.Instance?.Unregister(this); }

        public Beacon Add(string label, BodyDef body, DVec3 logicalPos, string kind, string note)
        {
            var b = new Beacon
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12),
                label = label,
                bodyId = body != null ? body.id : "",
                x = logicalPos.x, y = logicalPos.y, z = logicalPos.z,
                kind = kind,
                note = note
            };
            beacons.Add(b);
            SpawnMarker(b);
            return b;
        }

        public void Clear()
        {
            foreach (var kv in markers) if (kv.Value != null) Destroy(kv.Value);
            markers.Clear();
            beacons.Clear();
            Selected = null;
        }

        public void RebuildAll()
        {
            foreach (var kv in markers) if (kv.Value != null) Destroy(kv.Value);
            markers.Clear();
            foreach (var b in beacons) SpawnMarker(b);
        }

        void SpawnMarker(Beacon b)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Marker_" + b.label;
            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(3.2f, 0.16f, 3.2f);

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = b.kind == "discovery" ? DiscoveryMaterial : BeaconMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            markers[b.id] = go;
            PositionMarker(b);
        }

        void PositionMarker(Beacon b)
        {
            if (!markers.TryGetValue(b.id, out var go) || go == null) return;
            var fo = FloatingOrigin.Instance;
            if (fo == null) return;
            var ws = WorldStreamer.Instance;
            var body = ws != null && ws.system != null ? ws.system.Get(b.bodyId) : null;
            DVec3 lp = new DVec3(b.x, b.y, b.z);

            // Sit the marker on the actual ground under it, so it does not float
            // when the terrain under that coordinate is not flat.
            if (body != null && ws != null && ws.RuntimeOf(body) != null)
            {
                double h = ws.ElevationAt(body, lp);
                double r = (lp - body.LogicalPos).magnitude;
                if (r > 1.0)
                {
                    DVec3 dir = (lp - body.LogicalPos) / r;
                    lp = body.LogicalPos + dir * (body.radius + h + 0.35);
                }
            }

            go.transform.position = fo.ToRender(lp);
            go.transform.rotation = Quaternion.identity;
        }

        void FloatingOrigin.ITracked.OnOriginShift(DVec3 delta)
        {
            foreach (var b in beacons) PositionMarker(b);
        }

        public void CycleSelection()
        {
            if (beacons.Count == 0) { Selected = null; return; }
            int idx = Selected == null ? -1 : beacons.IndexOf(Selected);
            idx = (idx + 1) % beacons.Count;
            Selected = beacons[idx];
        }

        public Beacon Nearest(DVec3 logicalPos)
        {
            Beacon best = null; double bd = double.MaxValue;
            foreach (var b in beacons)
            {
                double d = (new DVec3(b.x, b.y, b.z) - logicalPos).magnitude;
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        public Beacon ById(string id)
        {
            foreach (var b in beacons) if (b.id == id) return b;
            return null;
        }
    }
}
