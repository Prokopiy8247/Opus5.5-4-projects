using UnityEngine;

namespace PG
{
    /// <summary>
    /// Pushes the per-frame lighting state into global shader properties.
    ///
    /// The key value is the star's RENDER-SPACE POSITION: every PG shader
    /// derives the sun direction per pixel from it, so each planet is lit from
    /// where Helion actually is relative to that planet, and the bright disc in
    /// the sky is exactly where the light comes from. The Unity directional
    /// light is aimed the same way for anything using URP's own lighting.
    /// Runs after the camera has moved this frame.
    /// </summary>
    [DefaultExecutionOrder(300)]
    public class PGGlobalLighting : MonoBehaviour
    {
        public static PGGlobalLighting Instance { get; private set; }
        public static void ResetInstance() { Instance = null; }

        static readonly int ID_SunPos   = Shader.PropertyToID("_PG_SunPos");
        static readonly int ID_SunDir   = Shader.PropertyToID("_PG_SunDir");
        static readonly int ID_SunColor = Shader.PropertyToID("_PG_SunColor");
        static readonly int ID_Ambient  = Shader.PropertyToID("_PG_Ambient");
        static readonly int ID_Ground   = Shader.PropertyToID("_PG_Ground");
        static readonly int ID_FogColor = Shader.PropertyToID("_PG_FogColor");
        static readonly int ID_FogParams= Shader.PropertyToID("_PG_FogParams");
        static readonly int ID_LocalUp  = Shader.PropertyToID("_PG_LocalUp");
        static readonly int ID_StarFade = Shader.PropertyToID("_PG_StarFade");
        static readonly int ID_Detail   = Shader.PropertyToID("_PG_Detail");

        [Header("Star")]
        public Color sunColor = new Color(1.0f, 0.95f, 0.88f);
        public float sunIntensity = 1.35f;
        public Light directional;

        // ---- live state, blended by the atmosphere system ----
        [HideInInspector] public Color curAmbientTop = new Color(0.02f, 0.024f, 0.036f);
        [HideInInspector] public Color curAmbientBottom = new Color(0.006f, 0.007f, 0.010f);
        [HideInInspector] public Color curFogColor = Color.black;
        [HideInInspector] public float curFogDensity = 0f;
        [HideInInspector] public float curSunScale = 1f;
        [HideInInspector] public float curStarFade = 1f;

        public Texture2D detailTexture;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (detailTexture == null) detailTexture = BuildDetailTexture();
            Shader.SetGlobalTexture(ID_Detail, detailTexture);
            Push(DVec3.zero, Vector3.up);
        }

        /// <summary>Deterministic tiling value-noise used for terrain and water micro detail.</summary>
        static Texture2D BuildDetailTexture()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true, true)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                name = "PG_Detail"
            };
            var px = new Color32[S * S];
            int[] perm = PGNoise.BuildPerm(1337);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    double u = (double)x / S * 8.0, v = (double)y / S * 8.0;
                    // Periodic in 8 cells: sample on a torus-like wrap.
                    double n = 0.0, a = 0.5, f = 1.0, norm = 0.0;
                    for (int o = 0; o < 4; o++)
                    {
                        double p = PGNoise.Perlin3(perm, (u * f) % (8.0 * f), (v * f) % (8.0 * f), o * 7.31);
                        n += a * p; norm += a; a *= 0.5; f *= 2.0;
                    }
                    n = 0.5 + 0.5 * (n / norm) * 1.6;
                    byte b = (byte)(Mathf.Clamp01((float)n) * 255f);
                    px[y * S + x] = new Color32(b, b, b, 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        void LateUpdate()
        {
            var fo = FloatingOrigin.Instance;
            var sys = PlanetSystem.Instance;
            if (fo == null || sys == null || sys.Star == null) return;

            DVec3 cam = CameraRig.Instance != null ? CameraRig.Instance.LogicalPosition
                                                   : (Camera.main != null ? fo.ToLogical(Camera.main.transform.position) : DVec3.zero);
            var body = WorldStreamer.Instance != null ? WorldStreamer.Instance.CurrentBody : null;
            Vector3 up = body != null ? (cam - body.LogicalPos).normalized.ToVector3() : Vector3.up;
            Push(cam, up);
        }

        void Push(DVec3 camLogical, Vector3 localUp)
        {
            var fo = FloatingOrigin.Instance;
            var sys = PlanetSystem.Instance;
            DVec3 sunLogical = sys != null && sys.Star != null ? sys.Star.LogicalPos : DVec3.zero;
            Vector3 sunRender = fo != null ? fo.ToRender(sunLogical) : Vector3.zero;
            Vector3 toSun = (sunLogical - camLogical).normalized.ToVector3();

            Shader.SetGlobalVector(ID_SunPos, new Vector4(sunRender.x, sunRender.y, sunRender.z, 1f));
            Shader.SetGlobalVector(ID_SunDir, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            Shader.SetGlobalColor(ID_SunColor, sunColor * (sunIntensity * curSunScale));
            Shader.SetGlobalColor(ID_Ambient, curAmbientTop);
            Shader.SetGlobalColor(ID_Ground, curAmbientBottom);
            Shader.SetGlobalColor(ID_FogColor, curFogColor);
            Shader.SetGlobalVector(ID_FogParams, new Vector4(curFogDensity, 0f, 0f, 1f));
            Shader.SetGlobalVector(ID_LocalUp, new Vector4(localUp.x, localUp.y, localUp.z, 0f));
            Shader.SetGlobalFloat(ID_StarFade, curStarFade);

            if (directional != null && toSun.sqrMagnitude > 0.5f)
                directional.transform.rotation = Quaternion.LookRotation(-toSun);
        }
    }
}
