using UnityEngine;

namespace PG
{
    /// <summary>
    /// A gas giant, drawn: one sphere at the top of its haze carrying the
    /// PG/GasGiant volume shader. There is no terrain stream and no collider -
    /// the body has nothing to stand on, so the ship flies straight through it
    /// (ShipController handles the drag and the buffeting inside).
    /// </summary>
    public class GasGiantBody : MonoBehaviour
    {
        public BodyDef Body { get; private set; }
        public MeshRenderer Renderer { get; private set; }

        public double OuterRadius => Body.radius + Body.atmosphereHeight;

        static readonly int ID_Center = Shader.PropertyToID("_PlanetCenter");

        public void Build(BodyDef b, Material template)
        {
            Body = b;
            var m = PlanetShells.Instance(template, "PG/GasGiant", "GasGiant_" + b.id);
            m.SetFloat("_DeckRadius", (float)b.radius);
            m.SetFloat("_OuterRadius", (float)OuterRadius);
            m.SetColor("_BandA", b.lowColor);
            m.SetColor("_BandB", b.midColor);
            m.SetColor("_BandC", b.highColor);
            m.SetColor("_StormColor", b.cloudColor);
            m.SetColor("_HazeColor", b.atmosphereColor);
            m.SetColor("_DeepColor", b.fogColor);

            // Storm position and noise offset are seeded, like the terrain.
            var rnd = new System.Random(b.seed);
            float lat = Mathf.Lerp(-0.45f, -0.25f, (float)rnd.NextDouble());   // southern belt
            float lon = (float)rnd.NextDouble() * Mathf.PI * 2f;
            float cl = Mathf.Sqrt(1f - lat * lat);
            m.SetVector("_StormDir", new Vector4(cl * Mathf.Cos(lon), lat, cl * Mathf.Sin(lon), 0.20f));
            m.SetVector("_Seed", new Vector4((float)rnd.NextDouble() * 40f, (float)rnd.NextDouble() * 40f, (float)rnd.NextDouble() * 40f, 0f));

            var go = new GameObject("GasVolume_" + b.displayName);
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * (float)OuterRadius;
            go.AddComponent<MeshFilter>().sharedMesh = PlanetShells.UnitSphere;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            Renderer = mr;
        }

        /// <summary>Follow the floating origin; push the render-space centre to the shader.</summary>
        public void Tick(DVec3 origin)
        {
            Vector3 c = (Body.LogicalPos - origin).ToRender(DVec3.zero);
            transform.SetPositionAndRotation(c, Quaternion.identity);
            Renderer.sharedMaterial.SetVector(ID_Center, new Vector4(c.x, c.y, c.z, 0f));
        }

        /// <summary>
        /// 0 at the haze top, rising to 1 at the cloud deck and staying 1 below
        /// it; 0 outside the body.
        /// </summary>
        public static float GasDepth(BodyDef b, DVec3 logicalPos)
        {
            double r = (logicalPos - b.LogicalPos).magnitude;
            double top = b.radius + b.atmosphereHeight;
            if (r >= top) return 0f;
            return (float)PGNoise.Clamp01((top - r) / System.Math.Max(b.atmosphereHeight, 1.0));
        }

        void OnDestroy()
        {
            if (Renderer != null) Destroy(Renderer.sharedMaterial);
        }
    }
}
