using System;
using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// The authored point of interest of every planet - the Blender landmarks
    /// (Rustglass Arch on Tarn-Veth, Sunken Spire Field on Mirvalis, Emberglass
    /// Rift on Kryosyne) - placed on the flattest ground near a seeded direction.
    /// Small ground detail (flora, boulders, spikes) is FloraField's job.
    /// </summary>
    public class PropScatter : MonoBehaviour, FloatingOrigin.ITracked
    {
        public WorldStreamer streamer;
        public Material PropMaterial;
        public Material CrystalMaterial;
        public Material AwesomeMaterial;
        [Header("Blender landmarks (Assets/Art/POI)")]
        public GameObject PoiArch;      // Tarn-Veth
        public GameObject PoiSpires;    // Mirvalis
        public GameObject PoiRift;      // Kryosyne

        class Placement
        {
            public string id;
            public string bodyId;
            public DVec3 logicalPos;
            public DVec3 up;
            public float scale;
            public int kind;          // 2 crystal, 3-6 primitive POI members, 10 Blender landmark
            public string poiName;
            public GameObject model;  // kind 10: the imported landmark prefab
            public float yaw;
            public GameObject go;
        }

        readonly List<Placement> placements = new List<Placement>();
        readonly Dictionary<string, List<Placement>> byBody = new Dictionary<string, List<Placement>>();
        readonly Dictionary<string, Mesh> propMeshes = new Dictionary<string, Mesh>();

        Transform root;

        void Awake()
        {
            root = new GameObject("Props").transform;
            root.SetParent(transform, false);
        }

        void OnEnable() { FloatingOrigin.Instance?.Register(this); }
        void OnDisable() { FloatingOrigin.Instance?.Unregister(this); }

        public int TotalProps => placements.Count;

        public void Build()
        {
            BuildMeshes();
            foreach (var b in streamer.system.bodies)
            {
                if (b.kind == BodyKind.Star) continue;
                ScatterBody(b);
            }
        }

        // ------------------------------------------------------------ meshes

        void BuildMeshes()
        {
            propMeshes["rock"] = MakeRock(3);
            propMeshes["rock_big"] = MakeRock(5);
            propMeshes["crystal"] = MakeCrystal();
            propMeshes["column"] = MakeColumn();
            propMeshes["shard"] = MakeShard();
            propMeshes["vent"] = MakeVent();
        }

        static Mesh MakeRock(int seed)
        {
            // A low-poly boulder: icosphere-ish lump with vertex jitter.
            var m = new Mesh { name = "PG_Rock" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int[] perm = PGNoise.BuildPerm(seed * 977 + 13);
            const int res = 6;
            for (int f = 0; f < 6; f++)
            {
                int b0 = verts.Count;
                for (int j = 0; j <= res; j++)
                    for (int i = 0; i <= res; i++)
                    {
                        double u = (double)i / res, v = (double)j / res;
                        DVec3 d = CubeSphere.FacePoint(f, u, v);
                        double n = PGNoise.Perlin3(perm, d.x * 3.1, d.y * 3.1, d.z * 3.1);
                        double r = 1.0 + n * 0.26;
                        verts.Add(new Vector3((float)(d.x * r), (float)(d.y * r * 0.78), (float)(d.z * r)));
                    }
                // Outward winding, every quad (the cube-sphere has no degenerate corners).
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                    {
                        int a = b0 + j * (res + 1) + i, bb = a + 1, c = a + res + 1, d2 = c + 1;
                        tris.Add(a); tris.Add(bb); tris.Add(c);
                        tris.Add(bb); tris.Add(d2); tris.Add(c);
                    }
            }
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeCrystal()
        {
            var m = new Mesh { name = "PG_Crystal" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int sides = 6;
            float h = 2.4f, r = 0.42f;
            verts.Add(new Vector3(0, h, 0));
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.PI * 2f * i / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
            verts.Add(new Vector3(0, h * 0.42f, 0));
            int apex = 0, mid = sides + 1;
            for (int i = 0; i < sides; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % sides;
                tris.Add(apex); tris.Add(mid); tris.Add(a);
                tris.Add(apex); tris.Add(b); tris.Add(mid);
                tris.Add(mid); tris.Add(b); tris.Add(a);
            }
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeColumn()
        {
            // Basalt column with the helical groove described in the POI text.
            var m = new Mesh { name = "PG_Column" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int sides = 7, rings = 10;
            float h = 14f, r = 1.5f;
            for (int j = 0; j <= rings; j++)
            {
                float t = (float)j / rings;
                float rr = r * (1f - t * 0.18f);
                for (int i = 0; i < sides; i++)
                {
                    float a = Mathf.PI * 2f * i / sides;
                    float groove = 1f + 0.09f * Mathf.Sin(a * 2f + t * 9f);
                    verts.Add(new Vector3(Mathf.Cos(a) * rr * groove, t * h, Mathf.Sin(a) * rr * groove));
                }
            }
            for (int j = 0; j < rings; j++)
                for (int i = 0; i < sides; i++)
                {
                    int a = j * sides + i, b = j * sides + (i + 1) % sides;
                    int c = (j + 1) * sides + i, d = (j + 1) * sides + (i + 1) % sides;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeShard()
        {
            var m = new Mesh { name = "PG_Shard" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            verts.Add(new Vector3(0, 5.0f, 0));
            verts.Add(new Vector3(1.6f, 0f, 0.4f));
            verts.Add(new Vector3(-1.1f, 0f, 1.4f));
            verts.Add(new Vector3(-0.7f, 0f, -1.5f));
            tris.Add(0); tris.Add(1); tris.Add(2);
            tris.Add(0); tris.Add(2); tris.Add(3);
            tris.Add(0); tris.Add(3); tris.Add(1);
            tris.Add(1); tris.Add(3); tris.Add(2);
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeVent()
        {
            var m = new Mesh { name = "PG_Vent" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int sides = 8;
            float r = 2.2f, h = 1.2f;
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.PI * 2f * i / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.PI * 2f * i / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * r * 0.5f, h, Mathf.Sin(a) * r * 0.5f));
            }
            for (int i = 0; i < sides; i++)
            {
                int a = i, b = (i + 1) % sides, c = sides + i, d = sides + (i + 1) % sides;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // ----------------------------------------------------------- scatter

        void ScatterBody(BodyDef b)
        {
            var pr = streamer.RuntimeOf(b);
            if (pr == null) return;
            var list = new List<Placement>();
            byBody[b.id] = list;

            uint s = (uint)(b.seed * 2654435761u) ^ 0x85EBCA6Bu;
            uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
            double Rnd() => (Next() % 100000) / 100000.0;

            // --- the authored point of interest ------------------------------
            BuildPoi(b, pr, list, Rnd);
            placements.AddRange(list);
            SpawnAll(b, list);
        }

        static DVec3 RandomDir(Func<double> rnd)
        {
            double z = rnd() * 2.0 - 1.0;
            double a = rnd() * Math.PI * 2.0;
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            return new DVec3(r * Math.Cos(a), z, r * Math.Sin(a));
        }

        void BuildPoi(BodyDef b, PlanetRuntime pr, List<Placement> list, Func<double> rnd)
        {
            // Each planet gets a distinct landmark built from primitives, placed
            // on the flattest spot the terrain function will give us near a
            // seeded direction so it is always reachable and always the same.
            DVec3 prefer = RandomDir(rnd);
            DVec3 spot = TerrainFunc.FindFlatSpotOnLand(b, pr.Perm, prefer, b.radius * 0.22, 160);
            DVec3 up = spot;
            DVec3 t1 = DVec3.Cross(up, new DVec3(0, 1, 0)).normalized;
            if (t1.sqrMagnitude < 1e-6) t1 = DVec3.Cross(up, new DVec3(1, 0, 0)).normalized;
            DVec3 t2 = DVec3.Cross(up, t1).normalized;

            b.poiName = string.IsNullOrEmpty(b.poiName) ? "Landmark" : b.poiName;
            b.poiLocalDirX = up.x; b.poiLocalDirY = up.y; b.poiLocalDirZ = up.z;

            // Blender landmark when the model was imported; the primitive
            // cluster below is only the fallback for a missing asset.
            GameObject model = b.kind == BodyKind.Rocky ? PoiArch : b.kind == BodyKind.Ocean ? PoiSpires : PoiRift;
            if (model != null)
            {
                double h0 = TerrainFunc.Elevation(b, pr.Perm, up);
                float scale = b.kind == BodyKind.Rocky ? 1.0f : b.kind == BodyKind.Ocean ? 1.7f : 1.6f;
                list.Add(new Placement
                {
                    id = b.id + "_poi_model",
                    bodyId = b.id,
                    logicalPos = b.LogicalPos + up * (b.radius + h0 - 2.0),   // footings sunk 2 m (+ the model's own skirt)
                    up = up,
                    scale = scale,
                    kind = 10,
                    model = model,
                    yaw = (float)(rnd() * 360.0),
                    poiName = b.poiName
                });
                return;
            }

            void Place(string tag, double a, double d, double sc, int kind)
            {
                DVec3 dir = (up + t1 * a + t2 * d).normalized;
                double h = TerrainFunc.Elevation(b, pr.Perm, dir);
                list.Add(new Placement
                {
                    id = b.id + "_poi_" + tag,
                    bodyId = b.id,
                    logicalPos = b.LogicalPos + dir * (b.radius + h),
                    up = dir,
                    scale = (float)sc,
                    kind = kind,
                    poiName = b.poiName
                });
            }

            switch (b.kind)
            {
                case BodyKind.Rocky:
                    // A rock arch: two legs and a lintel, built from scaled
                    // boulders so it reads as natural stone, not a prop kit.
                    {
                        double ang = 190.0 / b.radius;
                        Place("archL", -ang, 0, 9.5, 3);
                        Place("archR", +ang, 0, 9.5, 3);
                        Place("archTop", 0, 0, 12.0, 3);
                        Place("archA", -ang * 0.55, 0.006, 5.5, 3);
                        Place("archB", +ang * 0.55, 0.006, 5.5, 3);
                        for (int i = 0; i < 9; i++)
                        {
                            double a = (rnd() - 0.5) * ang * 4.0;
                            double d = (rnd() - 0.5) * ang * 4.0;
                            Place("rubble" + i, a, d, 1.4 + rnd() * 2.4, 3);
                        }
                    }
                    break;

                case BodyKind.Ocean:
                    // A field of helical basalt columns rising out of a shelf.
                    for (int i = 0; i < 14; i++)
                    {
                        double a = (rnd() - 0.5) * 0.055;
                        double d = (rnd() - 0.5) * 0.055;
                        Place("col" + i, a, d, 0.7 + rnd() * 0.9, 4);
                    }
                    for (int i = 0; i < 8; i++)
                    {
                        double a = (rnd() - 0.5) * 0.11;
                        double d = (rnd() - 0.5) * 0.11;
                        Place("crystal" + i, a, d, 0.8 + rnd() * 1.1, 2);
                    }
                    break;

                default: // Ice: a rift of obsidian shards and steam vents.
                    for (int i = 0; i < 16; i++)
                    {
                        double a = (rnd() - 0.5) * 0.06;
                        double d = (rnd() - 0.5) * 0.13;
                        Place("shard" + i, a, d, 0.9 + rnd() * 1.6, 5);
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        double a = (rnd() - 0.5) * 0.09;
                        double d = (rnd() - 0.5) * 0.09;
                        Place("vent" + i, a, d, 1.0 + rnd() * 0.7, 6);
                    }
                    break;
            }
        }

        void SpawnAll(BodyDef b, List<Placement> list)
        {
            foreach (var p in list)
            {
                if (p.kind == 10)
                {
                    // Holder aligned to the local up; the model keeps its importer
                    // root transform (axis conversion) as a child.
                    var holder = new GameObject("POI_" + b.id);
                    holder.transform.SetParent(root, false);
                    var inst = Instantiate(p.model, holder.transform, false);
                    inst.name = p.model.name;
                    foreach (var r in inst.GetComponentsInChildren<MeshRenderer>())
                    {
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                    p.go = holder;
                    continue;
                }
                var go = new GameObject("Prop_" + p.id);
                go.transform.SetParent(root, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                switch (p.kind)
                {
                    case 1: mf.sharedMesh = propMeshes["rock"]; mr.sharedMaterial = PropMaterial; break;
                    case 2: mf.sharedMesh = propMeshes["crystal"]; mr.sharedMaterial = CrystalMaterial; break;
                    case 3: mf.sharedMesh = propMeshes["rock_big"]; mr.sharedMaterial = AwesomeMaterial; break;
                    case 4: mf.sharedMesh = propMeshes["column"]; mr.sharedMaterial = AwesomeMaterial; break;
                    case 5: mf.sharedMesh = propMeshes["shard"]; mr.sharedMaterial = AwesomeMaterial; break;
                    case 6: mf.sharedMesh = propMeshes["vent"]; mr.sharedMaterial = CrystalMaterial; break;
                    default: mf.sharedMesh = propMeshes["rock"]; mr.sharedMaterial = PropMaterial; break;
                }
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                p.go = go;
            }
            PositionAll(list);
        }

        void PositionAll(List<Placement> list)
        {
            var fo = FloatingOrigin.Instance;
            if (fo == null) return;
            foreach (var p in list)
            {
                if (p.go == null) continue;
                Vector3 rp = fo.ToRender(p.logicalPos);
                // Landmarks are visible from far away; small members only nearby.
                float far = p.kind == 10 ? 30000f : 9000f;
                bool near = rp.sqrMagnitude < far * far;
                if (p.go.activeSelf != near) p.go.SetActive(near);
                if (!near) continue;
                p.go.transform.position = rp;
                p.go.transform.rotation = Quaternion.FromToRotation(Vector3.up, p.up.ToVector3()) * Quaternion.AngleAxis(p.yaw, Vector3.up);
                p.go.transform.localScale = Vector3.one * p.scale;
            }
        }

        void FloatingOrigin.ITracked.OnOriginShift(DVec3 delta)
        {
            foreach (var kv in byBody) PositionAll(kv.Value);
        }
    }
}
