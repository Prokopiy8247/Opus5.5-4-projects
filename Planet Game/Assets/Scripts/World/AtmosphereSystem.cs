using UnityEngine;

namespace PG
{
    /// <summary>
    /// Continuous atmosphere state. There is no trigger volume, no material swap
    /// and no fade to black: ambient light, haze on the ship, star visibility
    /// and the entry plasma are smooth functions of the camera's and the ship's
    /// measured altitude inside the current body's air, evaluated every frame.
    /// (The sky itself is the PG/Atmosphere shell, which is the same continuity
    /// expressed per pixel.)
    /// </summary>
    [DefaultExecutionOrder(250)]
    public class AtmosphereSystem : MonoBehaviour
    {
        public WorldStreamer streamer;
        public ShipController ship;
        public CameraRig rig;
        public PGGlobalLighting lighting;

        /// <summary>Plasma sheath, a child of the ship; aimed into the airflow each frame.</summary>
        public ParticleSystem entryParticles;

        public float AirDensity { get; private set; }
        public float Dayness { get; private set; } = 1f;
        public float EntryHeat { get; private set; }
        public double AltitudeAboveDatum { get; private set; }

        static readonly Color SpaceTop = new Color(0.020f, 0.024f, 0.036f);
        static readonly Color SpaceBot = new Color(0.006f, 0.007f, 0.010f);

        void Awake()
        {
            if (lighting == null) lighting = FindAnyObjectByType<PGGlobalLighting>();
        }

        void LateUpdate()
        {
            if (streamer == null || ship == null || lighting == null) return;
            var body = streamer.CurrentBody;
            DVec3 camPos = rig != null ? rig.LogicalPosition : ship.LogicalPosition;

            if (body == null || !body.landable || body.atmosphereHeight < 1.0)
            {
                AirDensity = 0f; EntryHeat = 0f; Dayness = 1f;
                lighting.curAmbientTop = SpaceTop;
                lighting.curAmbientBottom = SpaceBot;
                lighting.curFogDensity = 0f;
                lighting.curStarFade = 1f;
                UpdatePlasma(0f);
                return;
            }

            AltitudeAboveDatum = (camPos - body.LogicalPos).magnitude - body.radius;
            float t = Mathf.Clamp01((float)(1.0 - AltitudeAboveDatum / body.atmosphereHeight));
            AirDensity = t * t * (3f - 2f * t);

            var star = streamer.system.Star;
            DVec3 up = (camPos - body.LogicalPos).normalized;
            DVec3 toSun = star != null ? (star.LogicalPos - camPos).normalized : up;
            Dayness = Mathf.Clamp01((float)(DVec3.Dot(up, toSun) * 2.2 + 0.4));

            float amb = (float)body.ambient;
            Color skyTop = body.atmosphereColor * Mathf.Lerp(amb * 0.15f, amb * 1.4f, Dayness);
            Color skyBot = body.fogColor * Mathf.Lerp(amb * 0.10f, amb * 0.9f, Dayness);
            lighting.curAmbientTop = Color.Lerp(SpaceTop, skyTop, AirDensity);
            lighting.curAmbientBottom = Color.Lerp(SpaceBot, skyBot, AirDensity);
            lighting.curFogDensity = (float)body.fogDensity * 0.6f * Mathf.Pow(AirDensity, 1.4f);
            lighting.curFogColor = body.atmosphereColor * Mathf.Lerp(0.05f, 0.85f, Dayness);
            lighting.curStarFade = 1f - AirDensity * Dayness * 0.97f;

            // Entry plasma: dense air AND real speed through it.
            float shipAir = ship.AirDensity;
            float heat = Mathf.Clamp01(shipAir * 1.6f) * Mathf.Clamp01((ship.Speed - 160f) / 320f);
            UpdatePlasma(heat);
        }

        void UpdatePlasma(float heat)
        {
            EntryHeat = Mathf.Lerp(EntryHeat, heat, 1f - Mathf.Exp(-Time.deltaTime * 5f));
            if (entryParticles == null) return;
            bool show = EntryHeat > 0.03f;
            if (entryParticles.gameObject.activeSelf != show) entryParticles.gameObject.SetActive(show);
            if (!show) return;

            // Sit ahead of the hull along the velocity and stream backwards with the flow.
            Vector3 v = ship.LogicalVelocity.ToVector3();
            if (v.sqrMagnitude < 1f) return;
            Vector3 dir = v.normalized;
            Vector3 nose = ship.noseAnchor != null ? ship.noseAnchor.position : ship.transform.position;
            var tr = entryParticles.transform;
            tr.SetPositionAndRotation(nose + dir * 1.5f, Quaternion.LookRotation(-dir, ship.transform.up));
            var em = entryParticles.emission;
            em.rateOverTime = Mathf.Lerp(0f, 260f, EntryHeat);
            var main = entryParticles.main;
            main.startSpeed = Mathf.Lerp(10f, 45f, EntryHeat);
            main.startColor = Color.Lerp(new Color(1f, 0.55f, 0.25f, 0.5f), new Color(1f, 0.85f, 0.65f, 0.9f), EntryHeat);
        }
    }
}
