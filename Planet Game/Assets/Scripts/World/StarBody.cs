using UnityEngine;

namespace PG
{
    /// <summary>
    /// Helion, drawn: an HDR emissive photosphere at the star's real logical
    /// position and radius, plus a camera-facing corona. The directional light
    /// and every PG shader take the sun direction from this same position, so
    /// the bright disc in the sky is exactly where the light comes from.
    /// </summary>
    [DefaultExecutionOrder(260)]
    public class StarBody : MonoBehaviour
    {
        public PlanetSystem system;
        public Material SunMaterial;
        public Material CoronaMaterial;

        public BodyDef Body { get; private set; }
        public Renderer Photosphere { get; private set; }
        public Renderer Corona { get; private set; }

        Transform sphere, corona;

        void Start()
        {
            if (system == null) system = PlanetSystem.Instance;
            Body = system != null ? system.Star : null;
            if (Body == null) { enabled = false; return; }

            var s = new GameObject("Helion_Photosphere");
            s.transform.SetParent(transform, false);
            s.transform.localScale = Vector3.one * (float)Body.radius;
            s.AddComponent<MeshFilter>().sharedMesh = PlanetShells.UnitSphere;
            var mr = s.AddComponent<MeshRenderer>();
            mr.sharedMaterial = SunMaterial != null ? SunMaterial : new Material(Shader.Find("PG/Sun"));
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            sphere = s.transform;
            Photosphere = mr;

            var c = new GameObject("Helion_Corona");
            c.transform.SetParent(transform, false);
            c.AddComponent<MeshFilter>().sharedMesh = BuildQuad();
            var cr = c.AddComponent<MeshRenderer>();
            cr.sharedMaterial = CoronaMaterial != null ? CoronaMaterial : new Material(Shader.Find("PG/Glow"));
            cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cr.receiveShadows = false;
            corona = c.transform;
            Corona = cr;

            LateUpdate();
        }

        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "PG_GlowQuad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one);
            return m;
        }

        void LateUpdate()
        {
            if (Body == null || FloatingOrigin.Instance == null) return;
            transform.SetPositionAndRotation(FloatingOrigin.Instance.ToRender(Body.LogicalPos), Quaternion.identity);

            var cam = Camera.main;
            if (cam != null && corona != null)
            {
                Vector3 toCam = cam.transform.position - corona.position;
                corona.rotation = Quaternion.LookRotation(-toCam.normalized, cam.transform.up);
                corona.localScale = Vector3.one * (float)(Body.radius * 6.5);
            }
        }
    }
}
