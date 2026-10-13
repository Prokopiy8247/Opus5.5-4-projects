using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace PG
{
    /// <summary>
    /// The galactic coordinate frame of the background sky. The Milky Way band,
    /// the band of denser stars in <see cref="Starfield"/>, the nebulae and the
    /// "zone of avoidance" free of galaxies all follow this one plane.
    /// </summary>
    public static class GalacticFrame
    {
        /// <summary>North galactic pole (normal of the Milky Way plane).</summary>
        public static readonly Vector3 Pole = new Vector3(0.31f, 0.92f, -0.24f).normalized;

        /// <summary>Direction of the galactic centre (bright bulge), in the plane.</summary>
        public static readonly Vector3 Centre = Vector3.ProjectOnPlane(new Vector3(-0.35f, 0f, 1f), Pole).normalized;

        /// <summary>Completes the right-handed frame; longitude 90 deg.</summary>
        public static Vector3 East => Vector3.Cross(Pole, Centre);

        /// <summary>Unit direction from galactic longitude / latitude in degrees.</summary>
        public static Vector3 Dir(float lonDeg, float latDeg)
        {
            float lo = lonDeg * Mathf.Deg2Rad, la = latDeg * Mathf.Deg2Rad;
            return (Mathf.Cos(la) * (Mathf.Cos(lo) * Centre + Mathf.Sin(lo) * East) + Mathf.Sin(la) * Pole).normalized;
        }

        public static float LatitudeDeg(Vector3 d) => Mathf.Asin(Mathf.Clamp(Vector3.Dot(d.normalized, Pole), -1f, 1f)) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// The deep sky behind the stars: Milky Way, nebulae and distant galaxies.
    ///
    /// At start-up the Milky Way and the nebulae are evaluated once on the GPU
    /// (Hidden/PG/DeepSkyBake) into an HDR cubemap, published as the global
    /// _PG_SkyCube and drawn by PG/DeepSky - per frame that is one texture
    /// sample per pixel. Galaxies are separate quads (PG/Galaxy) so that they
    /// stay sharp at any resolution. One more quad (PG/BlackHole) is a black hole
    /// with its accretion disk, bending the baked sky behind it. Like the stars
    /// everything is infinitely far away: no colliders, no gravity, never in
    /// navigation, faded under a bright day sky.
    /// </summary>
    public class DeepSky : MonoBehaviour
    {
        public enum NebulaType { Emission = 0, Reflection = 1, Planetary = 2, Remnant = 3, Dark = 4, DwarfGalaxy = 5 }
        public enum GalaxyType { Spiral = 0, Elliptical = 1 }

        public struct Nebula
        {
            public string name;
            public NebulaType type;
            public float lon, lat;      // galactic, degrees
            public float radius;        // angular, degrees
            public float intensity;
            public float axisRatio;     // minor / major
            public float angle;         // position angle, degrees
            public float oiii;          // teal OIII core amount (emission only)

            public Nebula(string name, NebulaType type, float lon, float lat, float radius, float intensity,
                          float axisRatio = 1f, float angle = 0f, float oiii = 0f)
            {
                this.name = name; this.type = type; this.lon = lon; this.lat = lat; this.radius = radius;
                this.intensity = intensity; this.axisRatio = axisRatio; this.angle = angle; this.oiii = oiii;
            }
        }

        public struct Galaxy
        {
            public GalaxyType type;
            public Vector3 dir;
            public float radius;        // angular, degrees
            public float angle;         // position angle, degrees
            public float cosIncl;       // spiral: cos(inclination); elliptical: axis ratio
            public float bulge;         // bulge radius in galaxy radii
            public float arms, twist, bar;
            public float brightness;
            public Color tint;
            public float seed;
        }

        /// <summary>
        /// Hand-placed nebulae, mostly near the galactic plane where young stars
        /// and gas are in reality, plus a few showpieces at higher latitude.
        /// </summary>
        public static readonly Nebula[] Nebulae =
        {
            new Nebula("Great Keel Nebula",   NebulaType.Emission,    -68f,  -1.0f,  9.0f, 1.00f, 0.85f,  35f, 0.30f),
            new Nebula("Lagoon",              NebulaType.Emission,     12f,  -4.0f,  4.5f, 1.30f, 0.70f, -10f, 0.15f),
            new Nebula("Swan Cloud",          NebulaType.Emission,     80f,   1.0f, 13.0f, 0.55f, 0.80f,  60f, 0.05f),
            new Nebula("Helion's Veil",       NebulaType.Emission,    150f,  22.0f, 22.0f, 1.00f, 0.75f, -25f, 0.55f),
            new Nebula("Rosette",             NebulaType.Emission,   -154f,  -2.0f,  3.2f, 0.90f, 1.00f,   0f, 0.20f),
            new Nebula("Seven Sisters Haze",  NebulaType.Reflection, -120f, -23.0f,  4.5f, 0.70f, 0.80f,  20f),
            new Nebula("Blue Wisp",           NebulaType.Reflection,   28f,  17.0f,  5.5f, 0.55f, 0.60f, -40f),
            new Nebula("Ring",                NebulaType.Planetary,  -150f,  36.0f,  1.6f, 1.20f, 0.82f,  30f),
            new Nebula("Little Eye",          NebulaType.Planetary,    95f, -40.0f,  0.9f, 1.10f, 0.90f, -15f),
            new Nebula("Veil Arcs",           NebulaType.Remnant,      74f,  -8.6f,  6.0f, 0.75f, 0.90f,   0f),
            new Nebula("Sail Remnant",        NebulaType.Remnant,    -100f,   4.0f,  8.5f, 0.50f, 1.00f,  45f),
            new Nebula("Coal Sack",           NebulaType.Dark,        -58f,  -0.5f,  3.5f, 1.00f, 0.80f,  10f),
            new Nebula("Pipe",                NebulaType.Dark,          0f,   5.5f,  4.0f, 0.80f, 0.35f,  70f),
            new Nebula("Snake",               NebulaType.Dark,         25f,   3.0f,  3.0f, 0.70f, 0.40f, -30f),
            new Nebula("Greater Cloud",       NebulaType.DwarfGalaxy, -80f, -33.0f,  5.5f, 1.00f, 0.75f,  20f),
            new Nebula("Lesser Cloud",        NebulaType.DwarfGalaxy, -57f, -44.0f,  2.8f, 0.80f, 0.60f, -30f),
        };

        const int MaxNebulae = 32;   // matches MAX_NEB in PG_DeepSkyBake.shader

        [Header("Assets")]
        public Shader bakeShader;
        public Material skyMaterial;
        public Material galaxyMaterial;
        public Material blackHoleMaterial;

        [Header("Look")]
        public int cubemapSize = 1536;
        public int seed = 20261012;
        [Range(0f, 3f)] public float milkyWay = 1f;
        [Range(0f, 3f)] public float dust = 1f;
        [Range(0f, 3f)] public float nebulae = 1f;
        [Range(0f, 3f)] public float starGrain = 1f;
        public int distantGalaxies = 220;

        [Header("Black hole (scenery only)")]
        public bool blackHole = true;
        public float blackHoleLon = -103f, blackHoleLat = -15f;         // galactic, degrees
        [Range(1f, 8f)] public float blackHoleShadowRadius = 4.4f;      // angular radius of the black shadow, degrees
        [Range(0f, 30f)] public float blackHoleDiskTilt = 7f;           // how far above the disk plane we look from, degrees
        public float blackHoleDiskAngle = -14f;                         // position angle of the disk on the sky, degrees
        [Range(1.2f, 3f)] public float blackHoleExtent = 1.9f;          // quad half-size in disk radii (room for the bent sky)

        public RenderTexture SkyCube { get; private set; }
        public Mesh SkyMesh { get; private set; }
        public Mesh GalaxyMesh { get; private set; }
        public List<Galaxy> Galaxies { get; } = new List<Galaxy>();
        public Mesh BlackHoleMesh { get; private set; }
        public Renderer BlackHoleRenderer { get; private set; }
        public Vector3 BlackHoleDir => GalacticFrame.Dir(blackHoleLon, blackHoleLat);

        static readonly int ID_SkyCube = Shader.PropertyToID("_PG_SkyCube");

        // Cubemap face bases: direction = Z + x X + y Y, x/y in [-1, 1] across the face.
        static readonly Vector3[] FaceZ = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Vector3[] FaceX = { Vector3.back, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.left };
        static readonly Vector3[] FaceY = { Vector3.down, Vector3.down, Vector3.forward, Vector3.back, Vector3.down, Vector3.down };

        readonly List<Object> owned = new List<Object>();

        void Awake()
        {
            float t = Time.realtimeSinceStartup;
            Build();
            Debug.Log("[PG] deep sky: " + (SkyCube != null ? SkyCube.width + "px cubemap" : "no cubemap (headless)") + ", " + Nebulae.Length + " nebulae, " + Galaxies.Count +
                      " galaxies, built in " + ((Time.realtimeSinceStartup - t) * 1000f).ToString("F0") + " ms (CPU side)");
        }

        System.Collections.IEnumerator Start()
        {
            // Where the opening view points, in galactic coordinates (for placing showpieces).
            for (int i = 0; i < 3; i++) yield return null;
            var cam = Camera.main;
            if (cam == null) yield break;
            Vector3 f = cam.transform.forward;
            float lon = Mathf.Atan2(Vector3.Dot(f, GalacticFrame.East), Vector3.Dot(f, GalacticFrame.Centre)) * Mathf.Rad2Deg;
            Debug.Log("[PG] deep sky: opening view at galactic lon " + lon.ToString("F0") + ", lat " + GalacticFrame.LatitudeDeg(f).ToString("F0"));
        }

        void OnDestroy()
        {
            Release();
        }

        /// <summary>Bakes the sky and (re)builds the renderers. Safe to call again after changing settings.</summary>
        public void Build(bool debugDirections = false)
        {
            Release();
            // Headless runs (-nographics) have nothing to bake with; the renderers are still built.
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                SkyCube = Bake(debugDirections);
                Shader.SetGlobalTexture(ID_SkyCube, SkyCube);
            }

            SkyMesh = BuildSphere(48, 24);
            owned.Add(SkyMesh);
            MakeRenderer("DeepSky_Background", SkyMesh, skyMaterial != null ? skyMaterial : Owned(new Material(Shader.Find("PG/DeepSky"))));

            BuildGalaxyCatalogue();
            GalaxyMesh = BuildGalaxyMesh(Galaxies);
            owned.Add(GalaxyMesh);
            MakeRenderer("DeepSky_Galaxies", GalaxyMesh, galaxyMaterial != null ? galaxyMaterial : Owned(new Material(Shader.Find("PG/Galaxy"))));

            BlackHoleRenderer = null;
            BlackHoleMesh = null;
            var holeShader = blackHoleMaterial != null ? blackHoleMaterial.shader : Shader.Find("PG/BlackHole");
            if (blackHole && holeShader != null)
            {
                var mat = blackHoleMaterial != null ? blackHoleMaterial : Owned(new Material(holeShader));
                BlackHoleMesh = BuildBlackHoleMesh(mat);
                owned.Add(BlackHoleMesh);
                BlackHoleRenderer = MakeRenderer("DeepSky_BlackHole", BlackHoleMesh, mat);
            }
        }

        Material Owned(Material m) { m.hideFlags = HideFlags.DontSave; owned.Add(m); return m; }

        void Release()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i).gameObject;
                if (c.name.StartsWith("DeepSky_")) { if (Application.isPlaying) Destroy(c); else DestroyImmediate(c); }
            }
            foreach (var o in owned) if (o != null) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
            owned.Clear();
            if (SkyCube != null) { SkyCube.Release(); if (Application.isPlaying) Destroy(SkyCube); else DestroyImmediate(SkyCube); }
            SkyCube = null;
            Galaxies.Clear();
        }

        MeshRenderer MakeRenderer(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        // ------------------------------------------------------------ bake

        RenderTexture Bake(bool debugDirections)
        {
            var shader = bakeShader != null ? bakeShader : Shader.Find("Hidden/PG/DeepSkyBake");
            int size = Mathf.Clamp(cubemapSize, 64, 4096);

            var fmt = GraphicsFormat.B10G11R11_UFloatPack32;
            if (!SystemInfo.IsFormatSupported(fmt, GraphicsFormatUsage.Render)) fmt = GraphicsFormat.R16G16B16A16_SFloat;
            var desc = new RenderTextureDescriptor(size, size, fmt, GraphicsFormat.None)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = "PG_DeepSkyCube",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            rt.Create();

            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetVector("_GalN", GalacticFrame.Pole);
            mat.SetVector("_GalC", GalacticFrame.Centre);
            mat.SetFloat("_MilkyWay", milkyWay);
            mat.SetFloat("_Dust", dust);
            mat.SetFloat("_NebulaGain", nebulae);
            mat.SetFloat("_StarDust", starGrain);
            mat.SetFloat("_Res", size);
            mat.SetFloat("_Debug", debugDirections ? 1f : 0f);

            var pos = new Vector4[MaxNebulae];
            var a = new Vector4[MaxNebulae];
            var b = new Vector4[MaxNebulae];
            int n = Mathf.Min(Nebulae.Length, MaxNebulae);
            for (int i = 0; i < n; i++)
            {
                var nb = Nebulae[i];
                Vector3 d = GalacticFrame.Dir(nb.lon, nb.lat);
                pos[i] = new Vector4(d.x, d.y, d.z, nb.radius * Mathf.Deg2Rad);
                a[i] = new Vector4((float)nb.type, 0.37f + i * 1.618f, nb.intensity, Mathf.Max(0.1f, nb.axisRatio));
                b[i] = new Vector4(nb.angle * Mathf.Deg2Rad, nb.oiii, 0f, 0f);
            }
            mat.SetFloat("_NebCount", n);
            mat.SetVectorArray("_NebPos", pos);
            mat.SetVectorArray("_NebA", a);
            mat.SetVectorArray("_NebB", b);

            var quad = new Mesh { name = "PG_BakeQuad" };
            quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
            quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };

            var cmd = new CommandBuffer { name = "PG DeepSky bake" };
            for (int f = 0; f < 6; f++)
            {
                var mpb = new MaterialPropertyBlock();
                mpb.SetVector("_FaceZ", FaceZ[f]);
                mpb.SetVector("_FaceX", FaceX[f]);
                mpb.SetVector("_FaceY", FaceY[f]);
                cmd.SetRenderTarget(rt, 0, (CubemapFace)f);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.DrawMesh(quad, Matrix4x4.identity, mat, 0, 0, mpb);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            rt.GenerateMips();

            DestroyImmediate(mat);
            DestroyImmediate(quad);
            return rt;
        }

        // ------------------------------------------------------ sky sphere

        static Mesh BuildSphere(int lonSeg, int latSeg)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int y = 0; y <= latSeg; y++)
            {
                float v = Mathf.PI * y / latSeg;
                for (int x = 0; x <= lonSeg; x++)
                {
                    float u = 2f * Mathf.PI * x / lonSeg;
                    verts.Add(new Vector3(Mathf.Sin(v) * Mathf.Cos(u), Mathf.Cos(v), Mathf.Sin(v) * Mathf.Sin(u)));
                }
            }
            for (int y = 0; y < latSeg; y++)
                for (int x = 0; x < lonSeg; x++)
                {
                    int i0 = y * (lonSeg + 1) + x, i1 = i0 + 1, i2 = i0 + lonSeg + 1, i3 = i2 + 1;
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    tris.Add(i1); tris.Add(i2); tris.Add(i3);
                }
            var m = new Mesh { name = "PG_DeepSkySphere" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            // Never frustum-culled: the shader ignores the object position.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e8f);
            return m;
        }

        // -------------------------------------------------------- galaxies

        static Galaxy Spiral(Vector3 dir, float radius, float angle, float inclDeg, float bulge, float arms, float twist,
                             float bar, float brightness, Color tint, float seed)
        {
            return new Galaxy
            {
                type = GalaxyType.Spiral, dir = dir.normalized, radius = radius, angle = angle,
                cosIncl = Mathf.Cos(inclDeg * Mathf.Deg2Rad), bulge = bulge, arms = arms, twist = twist, bar = bar,
                brightness = brightness, tint = tint, seed = seed
            };
        }

        static Galaxy Elliptical(Vector3 dir, float radius, float angle, float axisRatio, float brightness, float seed)
        {
            return new Galaxy
            {
                type = GalaxyType.Elliptical, dir = dir.normalized, radius = radius, angle = angle, cosIncl = axisRatio,
                brightness = brightness, tint = Color.white, seed = seed
            };
        }

        void BuildGalaxyCatalogue()
        {
            Galaxies.Clear();
            var blue = new Color(0.62f, 0.72f, 1.00f);
            var white = new Color(0.82f, 0.84f, 1.00f);

            // Showpieces. A large inclined spiral with two satellites (after M31, M32, M110),
            // its smaller face-on neighbour (M33), an edge-on spiral (NGC 4565), a face-on
            // grand-design spiral (M51), a barred spiral and a giant elliptical.
            Vector3 big = GalacticFrame.Dir(121f, -21.5f);
            Galaxies.Add(Spiral(big, 7.5f, 38f, 77f, 0.24f, 2f, 3.2f, 0.0f, 1.0f, blue, 1.3f));
            Galaxies.Add(Elliptical(Offset(big, -0.4f, -1.8f), 0.6f, 0f, 0.85f, 1.2f, 2.1f));
            Galaxies.Add(Elliptical(Offset(big, 2.0f, 3.2f), 1.1f, 60f, 0.5f, 0.7f, 2.7f));
            Galaxies.Add(Spiral(GalacticFrame.Dir(134f, -31f), 1.9f, -20f, 52f, 0.10f, 2f, 2.4f, 0.0f, 0.8f, new Color(0.58f, 0.70f, 1f), 3.3f));
            Galaxies.Add(Spiral(GalacticFrame.Dir(-130f, 55f), 3.0f, 25f, 88f, 0.24f, 2f, 3.0f, 0.0f, 1.0f, new Color(0.95f, 0.88f, 0.78f), 4.9f));
            Galaxies.Add(Spiral(GalacticFrame.Dir(105f, 68f), 1.7f, 0f, 22f, 0.12f, 2f, 2.0f, 0.0f, 1.0f, blue, 5.7f));
            Galaxies.Add(Spiral(GalacticFrame.Dir(40f, -55f), 2.2f, -50f, 42f, 0.14f, 2f, 2.6f, 0.8f, 0.9f, white, 6.2f));
            Galaxies.Add(Elliptical(GalacticFrame.Dir(-75f, 70f), 1.4f, 15f, 0.78f, 1.1f, 7.4f));

            // Distant galaxies: a few small clusters plus a field population. None in the
            // zone of avoidance - the Milky Way's own dust hides them there.
            var rnd = new System.Random(seed);
            float R() => (float)rnd.NextDouble();
            int clusters = 6;
            int inClusters = distantGalaxies / 3;
            var clusterCentres = new Vector3[clusters];
            for (int c = 0; c < clusters; c++) clusterCentres[c] = RandomHighLatitude(rnd, 25f);

            for (int i = 0; i < distantGalaxies; i++)
            {
                Vector3 d;
                if (i < inClusters)
                {
                    var cc = clusterCentres[i % clusters];
                    d = Offset(cc, (R() - 0.5f) * 5f, (R() - 0.5f) * 5f);
                }
                else d = RandomHighLatitude(rnd, 15f);

                float size = 0.12f + 0.55f * Mathf.Pow(R(), 3f);
                float angle = R() * 180f;
                float s = R() * 100f;
                if (R() < 0.55f)
                {
                    float incl = Mathf.Acos(R()) * Mathf.Rad2Deg;
                    var tint = Color.Lerp(blue, white, R());
                    Galaxies.Add(Spiral(d, size, angle, incl, 0.12f + 0.15f * R(), R() < 0.8f ? 2f : 3f, 1.8f + 1.8f * R(),
                                        R() < 0.25f ? 0.7f : 0f, 1.0f + 0.8f * R(), tint, s));
                }
                else
                {
                    Galaxies.Add(Elliptical(d, size * 0.8f, angle, 0.45f + 0.55f * R(), 1.0f + 1.0f * R(), s));
                }
            }
        }

        static Vector3 RandomHighLatitude(System.Random rnd, float minLat)
        {
            while (true)
            {
                double z = rnd.NextDouble() * 2.0 - 1.0;
                double a = rnd.NextDouble() * System.Math.PI * 2.0;
                double r = System.Math.Sqrt(1.0 - z * z);
                var d = new Vector3((float)(r * System.Math.Cos(a)), (float)z, (float)(r * System.Math.Sin(a)));
                float lat = Mathf.Abs(GalacticFrame.LatitudeDeg(d));
                // soft edge: galaxies thin out toward the plane
                if (lat > minLat + 10f * (float)rnd.NextDouble()) return d;
            }
        }

        /// <summary>Moves a direction by small angles (degrees) along its local east / north.</summary>
        static Vector3 Offset(Vector3 d, float eastDeg, float northDeg)
        {
            Tangents(d, out var t1, out var t2);
            return (d + (t1 * eastDeg + t2 * northDeg) * Mathf.Deg2Rad).normalized;
        }

        static void Tangents(Vector3 n, out Vector3 t1, out Vector3 t2)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(n, GalacticFrame.Pole)) < 0.95f ? GalacticFrame.Pole : Vector3.right;
            t1 = Vector3.Cross(up, n).normalized;
            t2 = Vector3.Cross(n, t1);
        }

        static Mesh BuildGalaxyMesh(List<Galaxy> list)
        {
            int n = list.Count;
            var verts = new Vector3[n * 4];
            var cols = new Color[n * 4];
            var uv0 = new Vector2[n * 4];
            var uv1 = new List<Vector4>(n * 4);
            var uv2 = new List<Vector4>(n * 4);
            var uv3 = new Vector2[n * 4];
            var tris = new int[n * 6];
            Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(1, 1) };

            for (int i = 0; i < n; i++)
            {
                var g = list[i];
                Tangents(g.dir, out var t1, out var t2);
                float pa = g.angle * Mathf.Deg2Rad;
                Vector3 ax = Mathf.Cos(pa) * t1 + Mathf.Sin(pa) * t2;
                Vector3 ay = -Mathf.Sin(pa) * t1 + Mathf.Cos(pa) * t2;
                float r = Mathf.Tan(g.radius * Mathf.Deg2Rad);
                float hy = g.type == GalaxyType.Spiral
                    ? Mathf.Max(Mathf.Sqrt(g.cosIncl * g.cosIncl + 0.09f * 0.09f) * 1.05f, g.bulge * 1.6f)
                    : Mathf.Max(g.cosIncl, 0.3f);
                hy = Mathf.Min(hy, 1f);

                for (int k = 0; k < 4; k++)
                {
                    Vector2 s = new Vector2(corners[k].x, corners[k].y * hy);
                    int v = i * 4 + k;
                    verts[v] = g.dir + (ax * s.x + ay * s.y) * r;
                    cols[v] = g.tint;
                    uv0[v] = s;
                    uv1.Add(new Vector4((float)g.type, g.cosIncl, g.seed, g.twist));
                    uv2.Add(new Vector4(Mathf.Max(g.bulge, 0.02f), g.brightness, g.arms, g.bar));
                    uv3[v] = corners[k];
                }
                int b0 = i * 4;
                tris[i * 6 + 0] = b0; tris[i * 6 + 1] = b0 + 2; tris[i * 6 + 2] = b0 + 1;
                tris[i * 6 + 3] = b0 + 1; tris[i * 6 + 4] = b0 + 2; tris[i * 6 + 5] = b0 + 3;
            }

            var m = new Mesh { name = "PG_Galaxies", indexFormat = IndexFormat.UInt32 };
            m.vertices = verts;
            m.colors = cols;
            m.uv = uv0;
            m.SetUVs(1, uv1);
            m.SetUVs(2, uv2);
            m.SetUVs(3, uv3);
            m.triangles = tris;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e8f);
            return m;
        }

        // ------------------------------------------------------ black hole

        /// <summary>
        /// One quad on the sky around the hole. The shader works in Schwarzschild radii:
        /// the shadow of a black hole is sqrt(27)/2 radii wide, so the wanted angular
        /// size fixes how far away the viewer is taken to be.
        /// </summary>
        Mesh BuildBlackHoleMesh(Material mat)
        {
            Vector3 dir = BlackHoleDir;
            Tangents(dir, out var t1, out var t2);
            float pa = blackHoleDiskAngle * Mathf.Deg2Rad, tilt = blackHoleDiskTilt * Mathf.Deg2Rad;
            // disk axis: up on the sky, turned by the position angle, leaning toward the viewer
            Vector3 axis = (Mathf.Cos(tilt) * (Mathf.Cos(pa) * t2 - Mathf.Sin(pa) * t1) - Mathf.Sin(tilt) * dir).normalized;
            Vector3 ax = Vector3.Cross(axis, dir).normalized, ay = Vector3.Cross(dir, ax);

            float dist = 2.598f / Mathf.Tan(blackHoleShadowRadius * Mathf.Deg2Rad);
            float diskOuter = mat.HasProperty("_DiskOuter") ? mat.GetFloat("_DiskOuter") : 13f;
            float half = Mathf.Min(blackHoleExtent * diskOuter / dist, 1.5f);

            Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(1, 1) };
            var verts = new Vector3[4];
            var uv1 = new List<Vector4>(4);
            var uv2 = new List<Vector4>(4);
            for (int k = 0; k < 4; k++)
            {
                verts[k] = dir + (ax * corners[k].x + ay * corners[k].y) * half;
                uv1.Add(new Vector4(dir.x, dir.y, dir.z, dist));
                uv2.Add(new Vector4(axis.x, axis.y, axis.z, 0f));
            }
            var m = new Mesh { name = "PG_BlackHole" };
            m.vertices = verts;
            m.uv = corners;
            m.SetUVs(1, uv1);
            m.SetUVs(2, uv2);
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e8f);
            return m;
        }
    }
}
