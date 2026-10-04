using UnityEngine;

namespace PG
{
    /// <summary>
    /// Decorative background stars, generated at start-up.
    ///
    /// Every star is four vertices on one unit direction; PG/Stars expands them
    /// into a soft round sprite of a fixed PIXEL size (about 1-3 px for normal
    /// stars), rotated by the view only. They are infinitely far away, carry no
    /// collider and never appear in navigation, which is what separates them from
    /// the real, reachable bodies of the system.
    /// </summary>
    public class Starfield : MonoBehaviour
    {
        public Material StarMaterial;
        public int count = 7000;
        public int seed = 20261003;

        public Mesh Mesh { get; private set; }

        void Awake()
        {
            Mesh = Build(count, seed);
            var mf = GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = Mesh;
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = StarMaterial != null ? StarMaterial : new Material(Shader.Find("PG/Stars"));
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        public static Mesh Build(int n, int seed)
        {
            var rnd = new System.Random(seed);
            var verts = new Vector3[n * 4];
            var cols = new Color[n * 4];
            var uv = new Vector2[n * 4];
            var uv2 = new Vector2[n * 4];
            var tris = new int[n * 6];

            // A tilted galactic band holds ~45% of the stars.
            Vector3 bandNormal = new Vector3(0.31f, 0.92f, -0.24f).normalized;
            Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(1, 1) };

            for (int i = 0; i < n; i++)
            {
                Vector3 d;
                bool band = rnd.NextDouble() < 0.45;
                do
                {
                    double z = rnd.NextDouble() * 2.0 - 1.0;
                    double a = rnd.NextDouble() * System.Math.PI * 2.0;
                    double r = System.Math.Sqrt(1.0 - z * z);
                    d = new Vector3((float)(r * System.Math.Cos(a)), (float)z, (float)(r * System.Math.Sin(a)));
                }
                while (band && Mathf.Abs(Vector3.Dot(d, bandNormal)) > 0.22f * (float)rnd.NextDouble() + 0.03f);

                // Magnitude: most stars faint, a few bright.
                double u = rnd.NextDouble();
                float mag = (float)System.Math.Pow(u, 3.2);
                float bright = Mathf.Lerp(0.10f, 1.6f, mag);
                float halfPx = Mathf.Lerp(1.15f, 2.6f, mag);

                // Colour: from cool blue-white to warm orange.
                float temp = (float)rnd.NextDouble();
                Color c = temp < 0.18f ? new Color(0.70f, 0.80f, 1.00f)
                        : temp < 0.72f ? new Color(1.00f, 0.97f, 0.92f)
                        : temp < 0.92f ? new Color(1.00f, 0.86f, 0.66f)
                        : new Color(1.00f, 0.70f, 0.50f);
                c *= bright;

                for (int k = 0; k < 4; k++)
                {
                    verts[i * 4 + k] = d;
                    cols[i * 4 + k] = c;
                    uv[i * 4 + k] = corners[k];
                    uv2[i * 4 + k] = new Vector2(halfPx, 0f);
                }
                int b0 = i * 4;
                tris[i * 6 + 0] = b0; tris[i * 6 + 1] = b0 + 2; tris[i * 6 + 2] = b0 + 1;
                tris[i * 6 + 3] = b0 + 1; tris[i * 6 + 4] = b0 + 2; tris[i * 6 + 5] = b0 + 3;
            }

            var m = new Mesh { name = "PG_Starfield", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = verts;
            m.colors = cols;
            m.uv = uv;
            m.uv2 = uv2;
            m.triangles = tris;
            // Never frustum-culled: the shader ignores the object position.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e8f);
            return m;
        }
    }
}
