using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// The non-terrain layers of one planet: ocean, atmosphere and clouds.
    ///
    /// Each is a rigid sphere with a fixed radius, drawn from one shared unit
    /// cube-sphere mesh scaled by its transform. None of them is a second copy of
    /// the terrain: the ocean sits exactly on radius + seaLevel for any sign of
    /// seaLevel, which is the bug that used to turn the water into torn blue fans.
    ///
    /// Every layer has its own material instance so per-planet constants (radii,
    /// colours, the render-space centre that follows the floating origin) are
    /// set once per frame on a material instead of per renderer.
    /// </summary>
    public class PlanetShells : MonoBehaviour
    {
        public BodyDef Body { get; private set; }
        public MeshRenderer Water { get; private set; }
        public MeshRenderer Atmosphere { get; private set; }
        public MeshRenderer Clouds { get; private set; }
        public Material TerrainMaterial { get; private set; }

        public double WaterRadius => Body.radius + Body.seaLevel;
        public double AtmosphereRadius => Body.radius + Body.atmosphereHeight;
        public double CloudRadius => Body.radius + Body.cloudHeight;

        static readonly int ID_Center = Shader.PropertyToID("_PlanetCenter");
        static readonly List<Material> scratch = new List<Material>(4);

        static Mesh unitSphere;

        /// <summary>Outward-facing unit cube-sphere shared by every shell and by the star.</summary>
        public static Mesh UnitSphere
        {
            get
            {
                if (unitSphere == null) unitSphere = BuildUnitSphere(64);
                return unitSphere;
            }
        }

        public static Mesh BuildUnitSphere(int res)
        {
            int side = res + 1;
            var verts = new Vector3[6 * side * side];
            var tris = new int[6 * res * res * 6];
            int v = 0, t = 0;
            for (int f = 0; f < 6; f++)
            {
                int b0 = v;
                for (int j = 0; j < side; j++)
                    for (int i = 0; i < side; i++)
                    {
                        DVec3 d = CubeSphere.FacePoint(f, (double)i / res, (double)j / res);
                        verts[v++] = new Vector3((float)d.x, (float)d.y, (float)d.z);
                    }
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                    {
                        int a = b0 + j * side + i, b = a + 1, c = a + side, dd = c + 1;
                        tris[t++] = a; tris[t++] = b; tris[t++] = c;
                        tris[t++] = b; tris[t++] = dd; tris[t++] = c;
                    }
            }
            var normals = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) normals[i] = verts[i];
            var m = new Mesh { name = "PG_UnitSphere", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = verts;
            m.normals = normals;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        public const double AtmoScaleFrac = 0.17;

        public void Build(BodyDef b, Material terrainTemplate, Material waterTemplate,
                          Material atmoTemplate, Material cloudTemplate)
        {
            Body = b;
            TerrainMaterial = Instance(terrainTemplate, "PG/Terrain", "Terrain_" + b.id);
            TerrainMaterial.SetColor("_AtmoColor", b.atmosphereColor);
            TerrainMaterial.SetFloat("_AtmoRadius", (float)AtmosphereRadius);
            TerrainMaterial.SetFloat("_HazeDensity", (float)(b.fogDensity * 0.45));
            TerrainMaterial.SetFloat("_AmbientSky", (float)(b.ambient * 2.6));

            if (b.seaLevel > -1e8)
            {
                var m = Instance(waterTemplate, "PG/Water", "Water_" + b.id);
                m.SetColor("_BaseColor", b.waterColor * 0.7f);
                m.SetColor("_AtmoColor", b.atmosphereColor);
                m.SetFloat("_AtmoRadius", (float)AtmosphereRadius);
                m.SetFloat("_HazeDensity", (float)(b.fogDensity * 0.45));
                Water = MakeShell("Water_" + b.displayName, m, (float)WaterRadius);
            }

            if (b.atmosphereHeight > 1.0)
            {
                var m = Instance(atmoTemplate, "PG/Atmosphere", "Atmo_" + b.id);
                // The visible air is concentrated near the ground (scale height
                // 17% of the flight atmosphere), so from space the planet keeps a
                // thin glowing limb instead of a thick bubble with a hard edge,
                // while the sky seen from the ground still has the body's
                // vertical optical depth.
                double scaleH = b.atmosphereHeight * AtmoScaleFrac;
                m.SetFloat("_PlanetRadius", (float)b.radius);
                m.SetFloat("_AtmoRadius", (float)AtmosphereRadius);
                m.SetFloat("_ScaleFrac", (float)AtmoScaleFrac);
                m.SetColor("_AtmoColor", b.atmosphereColor);
                m.SetColor("_SunsetColor", b.sunsetColor);
                m.SetFloat("_Density", (float)(b.skyOpticalDepth / scaleH));
                Atmosphere = MakeShell("Atmosphere_" + b.displayName, m, (float)AtmosphereRadius);
            }

            if (b.cloudCover > 0.01)
            {
                var m = Instance(cloudTemplate, "PG/Clouds", "Clouds_" + b.id);
                m.SetFloat("_CloudRadius", (float)CloudRadius);
                m.SetFloat("_Coverage", (float)b.cloudCover);
                m.SetColor("_CloudColor", b.cloudColor);
                m.SetFloat("_CloudScale", 9.5f * (float)(b.radius / 3000.0));   // keeps cloud cells ~300 m wide
                var rnd = new System.Random(b.seed);
                m.SetVector("_Seed", new Vector4((float)rnd.NextDouble() * 40f, (float)rnd.NextDouble() * 40f, (float)rnd.NextDouble() * 40f, 0f));
                Clouds = MakeShell("Clouds_" + b.displayName, m, (float)CloudRadius);
            }
        }

        static Material Instance(Material template, string shaderName, string name)
        {
            Material m;
            if (template != null) m = new Material(template);
            else
            {
                var sh = Shader.Find(shaderName);
                m = new Material(sh != null ? sh : Shader.Find("Hidden/InternalErrorShader"));
            }
            m.name = name;
            return m;
        }

        MeshRenderer MakeShell(string name, Material mat, float radius)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * radius;
            go.AddComponent<MeshFilter>().sharedMesh = UnitSphere;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return mr;
        }

        /// <summary>Follow the floating origin; push the render-space centre to every layer.</summary>
        public void Tick(DVec3 origin)
        {
            Vector3 c = (Body.LogicalPos - origin).ToRender(DVec3.zero);
            transform.SetPositionAndRotation(c, Quaternion.identity);
            Vector4 cv = new Vector4(c.x, c.y, c.z, 0f);

            scratch.Clear();
            scratch.Add(TerrainMaterial);
            if (Water != null) scratch.Add(Water.sharedMaterial);
            if (Atmosphere != null) scratch.Add(Atmosphere.sharedMaterial);
            if (Clouds != null) scratch.Add(Clouds.sharedMaterial);
            for (int i = 0; i < scratch.Count; i++) scratch[i].SetVector(ID_Center, cv);
        }

        void OnDestroy()
        {
            if (TerrainMaterial != null) Destroy(TerrainMaterial);
            if (Water != null) Destroy(Water.sharedMaterial);
            if (Atmosphere != null) Destroy(Atmosphere.sharedMaterial);
            if (Clouds != null) Destroy(Clouds.sharedMaterial);
        }
    }
}
