using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Ground props around the ship: trees, bushes, dry shrubs, boulders, hoodoos,
    /// ice spikes and basalt clusters from the Blender prop kit (PG_Props.fbx).
    ///
    /// The sphere is divided into equal-angle cube-face cells of ~80 m. Every
    /// cell's props are a pure function of (planet seed, cell): the same place
    /// always has the same trees, from any approach direction and after a
    /// save/load. Cells within ~1.5 km of the ship are generated on a worker
    /// thread (terrain height, slope and the planet's biome rules) and drawn
    /// with GPU instancing - a handful of draw calls for a few thousand props.
    /// Props fade in by scale at the edge of the field, so nothing pops.
    /// </summary>
    public class FloraField : MonoBehaviour
    {
        public WorldStreamer streamer;
        public ShipController ship;
        public Material material;
        public Mesh conifer, broadleaf, bush, dryShrub, boulder, hoodoo, iceSpikes, basalt;
        [Tooltip("Mesh-to-prop transform of each kind as imported (the FBX root's axis conversion), same order as the meshes.")]
        public Matrix4x4[] meshMatrices;

        public float radius = 1500f;
        public float maxAltitude = 2600f;

        public enum Kind { Conifer, Broadleaf, Bush, DryShrub, Boulder, Hoodoo, IceSpikes, Basalt }
        const int KindCount = 8;
        const double CellMetres = 80.0;
        const int Candidates = 5;

        struct Inst { public DVec3 pos; public Quaternion rot; public float scale; }

        sealed class Cell
        {
            public long key;
            public int generation;
            public DVec3 centre;                       // logical position of the cell centre on the ground
            public readonly List<Inst>[] byKind = new List<Inst>[KindCount];
        }

        BodyDef body;
        int[] perm;
        int generation;
        int gridN;
        readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
        readonly HashSet<long> requested = new HashSet<long>();
        readonly ConcurrentQueue<Cell> finished = new ConcurrentQueue<Cell>();
        readonly HashSet<long> wanted = new HashSet<long>();
        readonly List<long> toRequest = new List<long>();
        DVec3 lastCentre;
        bool haveCentre;
        int inFlight;

        Mesh[] meshes;
        readonly Dictionary<string, Material[]> mats = new Dictionary<string, Material[]>();
        readonly List<Matrix4x4>[] batch = new List<Matrix4x4>[KindCount];

        public int ActiveCells => cells.Count;
        public int PendingCells => inFlight;
        public int InstancesDrawn { get; private set; }
        public string BodyId => body != null ? body.id : "-";

        void Awake()
        {
            for (int i = 0; i < KindCount; i++) batch[i] = new List<Matrix4x4>(1024);
        }

        void Start()
        {
            meshes = new[] { conifer, broadleaf, bush, dryShrub, boulder, hoodoo, iceSpikes, basalt };
            if (meshMatrices == null || meshMatrices.Length != KindCount)
            {
                // Fallback: Blender Z-up meshes under a +90 degree X importer root.
                meshMatrices = new Matrix4x4[KindCount];
                for (int k = 0; k < KindCount; k++) meshMatrices[k] = Matrix4x4.Rotate(Quaternion.Euler(90f, 0f, 0f));
            }
        }

        Material[] MaterialsFor(BodyDef b)
        {
            if (mats.TryGetValue(b.id, out var arr)) return arr;
            arr = new Material[KindCount];
            for (int k = 0; k < KindCount; k++)
            {
                var m = new Material(material) { name = "Flora_" + b.id + "_" + (Kind)k, enableInstancing = true };
                m.SetColor("_Tint", TintFor(b, (Kind)k));
                arr[k] = m;
            }
            mats[b.id] = arr;
            return arr;
        }

        static Color TintFor(BodyDef b, Kind k)
        {
            if (k == Kind.Basalt) return new Color(1.7f, 1.55f, 1.5f);         // weathered basalt, not a black hole
            if (k != Kind.Boulder) return Color.white;
            switch (b.kind)
            {
                case BodyKind.Rocky: return new Color(1.30f, 0.80f, 0.58f);   // rust sandstone
                case BodyKind.Ice:   return new Color(0.78f, 0.84f, 0.98f);   // frosted granite
                default:             return new Color(0.92f, 0.92f, 0.90f);
            }
        }

        void ResetField(BodyDef b)
        {
            body = b;
            generation++;
            cells.Clear();
            requested.Clear();
            haveCentre = false;
            if (b == null) return;
            var pr = streamer.RuntimeOf(b);
            perm = pr != null ? pr.Perm : null;
            double faceArc = b.radius * Math.PI * 0.5;
            gridN = Math.Max(8, (int)Math.Ceiling(faceArc / CellMetres));
        }

        void Update()
        {
            if (streamer == null || ship == null || FloatingOrigin.Instance == null || meshes == null) return;

            var cur = streamer.CurrentBody;
            bool active = cur != null && cur.landable && ship.AltitudeAGL < maxAltitude;
            if (!active) { if (cells.Count > 0 || body != null) ResetField(null); InstancesDrawn = 0; return; }
            if (cur != body) ResetField(cur);
            if (perm == null) return;

            // Accept finished cells from the worker.
            while (finished.TryDequeue(out var c))
            {
                inFlight--;
                if (c.generation != generation) continue;
                requested.Remove(c.key);
                cells[c.key] = c;
            }

            DVec3 up = (ship.LogicalPosition - body.LogicalPos).normalized;
            if (!haveCentre || (up - lastCentre).magnitude * body.radius > 30.0)
            {
                lastCentre = up; haveCentre = true;
                CollectWanted(up);
                // Drop cells that left the field; request the new ones, nearest first.
                var drop = new List<long>();
                foreach (var kv in cells) if (!wanted.Contains(kv.Key)) drop.Add(kv.Key);
                foreach (var k in drop) cells.Remove(k);
                toRequest.Clear();
                foreach (var k in wanted) if (!cells.ContainsKey(k) && !requested.Contains(k)) toRequest.Add(k);
                if (toRequest.Count > 0) Dispatch(toRequest);
            }

            Draw(up);
        }

        // ------------------------------------------------------------ cells

        void CollectWanted(DVec3 centre)
        {
            wanted.Clear();
            DVec3 t1 = DVec3.Cross(centre, Math.Abs(centre.y) < 0.9 ? new DVec3(0, 1, 0) : new DVec3(1, 0, 0)).normalized;
            DVec3 t2 = DVec3.Cross(centre, t1).normalized;
            double reach = radius + CellMetres;
            double step = CellMetres * 0.5;
            for (double x = -reach; x <= reach; x += step)
                for (double y = -reach; y <= reach; y += step)
                {
                    if (x * x + y * y > reach * reach) continue;
                    DVec3 d = (centre + t1 * (x / body.radius) + t2 * (y / body.radius)).normalized;
                    wanted.Add(KeyOf(d));
                }
        }

        long KeyOf(DVec3 d)
        {
            Face(d, out int f, out double a, out double b);
            int ix = Math.Min(gridN - 1, Math.Max(0, (int)((a * 0.5 + 0.5) * gridN)));
            int iy = Math.Min(gridN - 1, Math.Max(0, (int)((b * 0.5 + 0.5) * gridN)));
            return ((long)f << 40) | ((long)ix << 20) | (long)iy;
        }

        /// <summary>Equal-angle cube mapping: face index and (a, b) in [-1, 1].</summary>
        static void Face(DVec3 d, out int f, out double a, out double b)
        {
            double ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            double u, v, m;
            if (ax >= ay && ax >= az) { f = d.x > 0 ? 0 : 1; m = ax; u = d.y; v = d.z; }
            else if (ay >= az)        { f = d.y > 0 ? 2 : 3; m = ay; u = d.x; v = d.z; }
            else                      { f = d.z > 0 ? 4 : 5; m = az; u = d.x; v = d.y; }
            a = Math.Atan(u / m) * (4.0 / Math.PI);
            b = Math.Atan(v / m) * (4.0 / Math.PI);
        }

        static DVec3 FromFace(int f, double a, double b)
        {
            double u = Math.Tan(a * Math.PI * 0.25), v = Math.Tan(b * Math.PI * 0.25);
            double s = (f & 1) == 0 ? 1.0 : -1.0;
            switch (f >> 1)
            {
                case 0: return new DVec3(s, u, v).normalized;
                case 1: return new DVec3(u, s, v).normalized;
                default: return new DVec3(u, v, s).normalized;
            }
        }

        void Dispatch(List<long> keys)
        {
            var b = body; var p = perm; int gen = generation, n = gridN;
            // Nearest cells first, in small batches so the first ring appears quickly.
            var centre = lastCentre;
            keys.Sort((x, y) => DistKey(x, centre, n).CompareTo(DistKey(y, centre, n)));
            const int BatchSize = 24;
            for (int i = 0; i < keys.Count; i += BatchSize)
            {
                int count = Math.Min(BatchSize, keys.Count - i);
                var slice = keys.GetRange(i, count).ToArray();
                foreach (var k in slice) requested.Add(k);
                inFlight += slice.Length;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var k in slice)
                    {
                        Cell c;
                        try { c = BuildCell(b, p, k, n, gen); }
                        catch (Exception) { c = new Cell { key = k, generation = gen }; }
                        finished.Enqueue(c);
                    }
                });
            }
        }

        static double DistKey(long key, DVec3 centre, int n)
        {
            int f = (int)(key >> 40), ix = (int)((key >> 20) & 0xFFFFF), iy = (int)(key & 0xFFFFF);
            DVec3 d = FromFace(f, (ix + 0.5) / n * 2.0 - 1.0, (iy + 0.5) / n * 2.0 - 1.0);
            return (d - centre).sqrMagnitude;
        }

        static uint Hash(long key, int i, uint salt)
        {
            unchecked
            {
                uint h = (uint)key * 0x9E3779B1u ^ (uint)(key >> 32) * 0x85EBCA77u ^ (uint)i * 0xC2B2AE3Du ^ salt;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return h;
            }
        }

        static double H01(long key, int i, uint salt) => (Hash(key, i, salt) & 0xFFFFFF) / 16777216.0;

        /// <summary>Pure function of (body, cell): safe on a worker thread.</summary>
        static Cell BuildCell(BodyDef b, int[] perm, long key, int n, int gen)
        {
            var c = new Cell { key = key, generation = gen };
            for (int k = 0; k < KindCount; k++) c.byKind[k] = new List<Inst>(2);
            int f = (int)(key >> 40), ix = (int)((key >> 20) & 0xFFFFF), iy = (int)(key & 0xFFFFF);
            DVec3 cd = FromFace(f, (ix + 0.5) / n * 2.0 - 1.0, (iy + 0.5) / n * 2.0 - 1.0);
            c.centre = b.LogicalPos + cd * b.radius;
            uint salt = (uint)b.seed * 2654435761u;
            bool hasSea = b.seaLevel > -1e8;
            DVec3 poi = new DVec3(b.poiLocalDirX, b.poiLocalDirY, b.poiLocalDirZ);
            bool hasPoi = poi.sqrMagnitude > 0.5;

            for (int i = 0; i < Candidates; i++)
            {
                double fa = H01(key, i, salt), fb = H01(key, i, salt ^ 0x51ED27u);
                DVec3 dir = FromFace(f, (ix + fa) / n * 2.0 - 1.0, (iy + fb) / n * 2.0 - 1.0);
                if (hasPoi && (dir - poi).magnitude * b.radius < 130.0) continue;   // keep the landmark clear

                double h = TerrainFunc.Elevation(b, perm, dir);
                if (hasSea && h < b.seaLevel + 2.5) continue;                       // never in the water
                DVec3 nrm = TerrainFunc.SurfaceNormal(b, perm, dir, h, 3.0);
                double slope = Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVec3.Dot(nrm, dir)))) * (180.0 / Math.PI);
                double r = H01(key, i, salt ^ 0xA3C59AC3u);
                double r2 = H01(key, i, salt ^ 0x3C6EF372u);
                int kind = -1;
                float scale = 1f;

                switch (b.kind)
                {
                    case BodyKind.Ocean:
                    {
                        double forest = PGNoise.Perlin3(perm, dir.x * b.radius / 650.0 + 31.7, dir.y * b.radius / 650.0 - 8.3, dir.z * b.radius / 650.0 + 14.1);
                        double dens = PGNoise.SmoothStep(-0.30, 0.30, forest);
                        bool beach = hasSea && h < b.seaLevel + 7.0;
                        if (beach) { if (r < 0.10) { kind = (int)Kind.Boulder; scale = 0.5f + (float)r2 * 0.6f; } break; }
                        if (slope > 30.0) { if (r < 0.40) { kind = (int)Kind.Boulder; scale = 0.8f + (float)r2 * 1.4f; } break; }
                        if (h > b.snowLine - 60.0) { if (r < 0.25) { kind = (int)Kind.Boulder; scale = 0.7f + (float)r2; } break; }
                        if (r < 0.62 * dens)
                        {
                            bool highland = h > b.seaLevel + 140.0;
                            kind = (int)((highland ? r2 < 0.75 : r2 < 0.30) ? Kind.Conifer : Kind.Broadleaf);
                            scale = 0.75f + (float)H01(key, i, salt ^ 0x1B873593u) * 0.6f;
                        }
                        else if (r < 0.62 * dens + 0.30) { kind = (int)Kind.Bush; scale = 0.8f + (float)r2 * 0.8f; }
                        else if (r < 0.62 * dens + 0.36) { kind = (int)Kind.Boulder; scale = 0.5f + (float)r2 * 1.0f; }
                        break;
                    }
                    case BodyKind.Rocky:
                    {
                        if (slope > 28.0) { if (r < 0.45) { kind = (int)Kind.Boulder; scale = 0.9f + (float)r2 * 1.6f; } break; }
                        if (r < 0.07 && slope < 14.0) { kind = (int)Kind.Hoodoo; scale = 0.8f + (float)r2 * 0.7f; }
                        else if (r < 0.42) { kind = (int)Kind.DryShrub; scale = 0.8f + (float)r2 * 0.7f; }
                        else if (r < 0.66) { kind = (int)Kind.Boulder; scale = 0.5f + (float)r2 * 1.5f; }
                        break;
                    }
                    default: // Ice
                    {
                        double volc = TerrainFunc.VolcanicMask(b, perm, dir);
                        if (volc > 0.55) { if (r < 0.55) { kind = (int)Kind.Basalt; scale = 0.7f + (float)r2 * 0.8f; } break; }
                        if (volc > 0.2) break;                                   // field margin: bare ground
                        if (slope > 32.0) { if (r < 0.30) { kind = (int)Kind.Boulder; scale = 0.8f + (float)r2 * 1.2f; } break; }
                        if (r < 0.30) { kind = (int)Kind.IceSpikes; scale = 0.7f + (float)r2 * 0.9f; }
                        else if (r < 0.42) { kind = (int)Kind.Boulder; scale = 0.6f + (float)r2 * 1.0f; }
                        break;
                    }
                }
                if (kind < 0) continue;

                // Trees and spikes stand upright; rocks lean into the slope.
                bool upright = kind == (int)Kind.Conifer || kind == (int)Kind.Broadleaf || kind == (int)Kind.Bush ||
                               kind == (int)Kind.DryShrub || kind == (int)Kind.Hoodoo;
                DVec3 upAxis = upright ? dir : (dir * 0.4 + nrm * 0.6).normalized;
                float yaw = (float)(H01(key, i, salt ^ 0x7F4A7C15u) * 360.0);
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, upAxis.ToVector3()) * Quaternion.AngleAxis(yaw, Vector3.up);
                double sink = 0.15 + scale * (upright ? 0.12 : 0.35) + Math.Min(slope, 35.0) * 0.03;
                c.byKind[kind].Add(new Inst { pos = b.LogicalPos + dir * (b.radius + h - sink), rot = rot, scale = scale });
            }
            return c;
        }

        // ------------------------------------------------------------- draw

        void Draw(DVec3 up)
        {
            var fo = FloatingOrigin.Instance;
            var arr = MaterialsFor(body);
            for (int k = 0; k < KindCount; k++) batch[k].Clear();

            DVec3 shipPos = ship.LogicalPosition;
            bool keepClear = ship.AltitudeAGL < 40.0;
            double r2max = (double)radius * radius;
            int total = 0;
            foreach (var kv in cells)
            {
                var c = kv.Value;
                for (int k = 0; k < KindCount; k++)
                {
                    var list = c.byKind[k];
                    if (list.Count == 0) continue;
                    double kindReach = k == (int)Kind.Bush || k == (int)Kind.DryShrub ? 0.55 : (k == (int)Kind.Boulder ? 0.75 : 1.0);
                    double reach2 = r2max * kindReach * kindReach;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var inst = list[i];
                        DVec3 rel = inst.pos - shipPos;
                        double d2 = rel.sqrMagnitude;
                        if (d2 > reach2) continue;
                        if (keepClear && d2 < 14.0 * 14.0) continue;            // never inside the parked ship
                        double d = Math.Sqrt(d2), rr = radius * kindReach;
                        float grow = (float)PGNoise.SmoothStep(rr, rr * 0.82, d);
                        float s = inst.scale * grow;
                        if (s < 0.02f) continue;
                        batch[k].Add(Matrix4x4.TRS(fo.ToRender(inst.pos), inst.rot, new Vector3(s, s, s)) * meshMatrices[k]);
                    }
                }
            }

            var cam = Camera.main;
            Vector3 centre = cam != null ? cam.transform.position : Vector3.zero;
            for (int k = 0; k < KindCount; k++)
            {
                var list = batch[k];
                if (list.Count == 0 || meshes[k] == null) continue;
                total += list.Count;
                var rp = new RenderParams(arr[k])
                {
                    worldBounds = new Bounds(centre, Vector3.one * (radius * 2.5f)),
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                    receiveShadows = false,
                    layer = gameObject.layer
                };
                for (int start = 0; start < list.Count; start += 1023)
                    Graphics.RenderMeshInstanced(rp, meshes[k], 0, list, Math.Min(1023, list.Count - start), start);
            }
            InstancesDrawn = total;
        }
    }
}
