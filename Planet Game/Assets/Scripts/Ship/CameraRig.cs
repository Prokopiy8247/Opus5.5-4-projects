using UnityEngine;

namespace PG
{
    /// <summary>
    /// External chase camera and cockpit camera.
    ///
    /// The chase camera is rigidly attached to the ship's interpolated position;
    /// only its ROTATION lags slightly behind the ship. (Smoothing the position
    /// itself, as before, made the camera fall hundreds of metres behind at
    /// cruise speed.) Both views are derived each frame from the same transform,
    /// so an origin shift moves them together and never jolts the view.
    /// Switching views never touches the controls.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CameraRig : MonoBehaviour
    {
        public static CameraRig Instance { get; private set; }
        public static void ResetInstance() { Instance = null; }

        public enum View { External, Cockpit }

        public Camera cam;
        public ShipController ship;
        public WorldStreamer streamer;
        public AtmosphereSystem atmosphere;
        /// <summary>The pilot on foot; while they walk, the camera follows them instead of the ship.</summary>
        public PlayerCharacter character;

        public View view = View.External;

        [Header("External")]
        public float distance = 26f;
        public float height = 6.0f;
        public float lookAhead = 40f;
        public float rotateLerp = 7f;
        public float baseFov = 62f;
        public float cockpitFov = 70f;

        [Header("Cockpit")]
        public Vector3 cockpitFallback = new Vector3(0f, 1.3f, 3.2f);

        [Header("On foot")]
        public float footDistance = 4.6f;
        public float footFov = 66f;
        public float blendTime = 0.6f;

        Quaternion smoothRot;
        bool init;
        float shakeT;
        float blend = 1f;
        Vector3 blendFromPos;
        Quaternion blendFromRot;

        /// <summary>Camera's logical position, published for lighting and HUD.</summary>
        public DVec3 LogicalPosition { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (cam == null) cam = GetComponent<Camera>();
        }

        void LateUpdate()
        {
            if (ship == null || cam == null) return;
            var fo = FloatingOrigin.Instance;
            if (fo == null) return;
            float dt = Time.deltaTime;

            if (character != null && character.OnFoot) { FootCamera(fo, dt); return; }

            Vector3 shipPos = ship.transform.position;
            Quaternion shipRot = ship.transform.rotation;
            if (!init) { smoothRot = shipRot; init = true; }
            smoothRot = Quaternion.Slerp(smoothRot, shipRot, 1f - Mathf.Exp(-rotateLerp * dt));

            float speed = ship.Speed;
            Vector3 pos;
            Quaternion rot;

            if (view == View.Cockpit)
            {
                Vector3 local = ship.cockpitAnchor != null
                    ? ship.transform.InverseTransformPoint(ship.cockpitAnchor.position)
                    : cockpitFallback;
                pos = shipPos + shipRot * local;
                rot = shipRot;
                cam.nearClipPlane = 0.05f;
            }
            else
            {
                float pull = Mathf.Clamp(speed / 140f, 0f, 7f);
                Vector3 offset = smoothRot * new Vector3(0f, height, -(distance + pull));
                pos = shipPos + offset;
                Vector3 look = shipPos + shipRot * (Vector3.forward * lookAhead) + smoothRot * (Vector3.up * height * 0.3f);
                rot = Quaternion.LookRotation((look - pos).normalized, smoothRot * Vector3.up);

                // Never let the chase camera dip under the ground on a slope.
                if (ship.SoiBody != null && ship.SoiBody.landable && streamer != null)
                {
                    DVec3 camLogical = fo.ToLogical(pos);
                    double agl = streamer.AltitudeAboveGround(ship.SoiBody, camLogical);
                    if (agl < 2.0)
                    {
                        Vector3 up = (camLogical - ship.SoiBody.LogicalPos).normalized.ToVector3();
                        pos += up * (float)(2.0 - agl);
                        rot = Quaternion.LookRotation((look - pos).normalized, smoothRot * Vector3.up);
                    }
                }
                cam.nearClipPlane = 0.3f;
            }

            // Engine / entry vibration: small, smooth, scaled by what the ship is doing.
            float heat = atmosphere != null ? atmosphere.EntryHeat : 0f;
            float amp = ship.ThrustLoad * 0.02f + (ship.boost ? 0.03f : 0f) + (ship.PulseEngaged ? 0.05f : 0f) + heat * 0.18f
                      + ship.Buffet * 0.35f;
            if (view == View.Cockpit) amp *= 0.5f;
            amp += ship.Blast * 1.6f;
            shakeT += dt * 23f;
            pos += rot * new Vector3((Mathf.PerlinNoise(shakeT, 0.3f) - 0.5f) * amp,
                                     (Mathf.PerlinNoise(0.7f, shakeT) - 0.5f) * amp, 0f);

            cam.farClipPlane = 400000f;
            float wantFov = (view == View.Cockpit ? cockpitFov : baseFov) + Mathf.Clamp01(speed / PGConst.SpeedPulse) * 10f;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, wantFov, 1f - Mathf.Exp(-3f * dt));

            Blend(ref pos, ref rot, dt);
            transform.SetPositionAndRotation(pos, rot);
            LogicalPosition = fo.ToLogical(pos);
        }

        /// <summary>
        /// On foot: the mouse looks around freely. Third person orbits the
        /// character's head, first person sits at eye height.
        /// </summary>
        void FootCamera(FloatingOrigin fo, float dt)
        {
            Vector3 up = character.Up;
            Vector3 look = character.LookDirection;
            Vector3 eye = character.transform.position + up * character.eyeHeight;
            Quaternion rot = Quaternion.LookRotation(look, up);
            Vector3 pos = eye;

            if (character.firstPerson) cam.nearClipPlane = 0.05f;
            else
            {
                pos = eye + up * 0.35f + Vector3.Cross(up, look).normalized * 0.55f - look * footDistance;
                // Looking up swings the camera down behind the character: keep it above the ground.
                DVec3 camLogical = fo.ToLogical(pos);
                double agl = streamer != null ? streamer.AltitudeAboveGround(character.Body, camLogical) : 1.0;
                if (agl < 0.5) pos += (camLogical - character.Body.LogicalPos).normalized.ToVector3() * (float)(0.5 - agl);
                cam.nearClipPlane = 0.15f;
            }

            cam.farClipPlane = 400000f;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, footFov, 1f - Mathf.Exp(-3f * dt));

            Blend(ref pos, ref rot, dt);
            transform.SetPositionAndRotation(pos, rot);
            LogicalPosition = fo.ToLogical(pos);
        }

        /// <summary>Glide from the current pose to the new subject (stepping out of / into the ship) instead of cutting.</summary>
        public void BeginBlend()
        {
            blendFromPos = transform.position;
            blendFromRot = transform.rotation;
            blend = 0f;
            if (ship != null) smoothRot = ship.transform.rotation;
        }

        void Blend(ref Vector3 pos, ref Quaternion rot, float dt)
        {
            if (blend >= 1f) return;
            blend = Mathf.Min(1f, blend + dt / Mathf.Max(blendTime, 0.01f));
            float k = blend * blend * (3f - 2f * blend);
            pos = Vector3.Lerp(blendFromPos, pos, k);
            rot = Quaternion.Slerp(blendFromRot, rot, k);
        }

        public void CycleView()
        {
            view = view == View.External ? View.Cockpit : View.External;
        }

        /// <summary>Snap without smoothing - after a spawn or a load.</summary>
        public void SnapImmediate()
        {
            init = false;
            blend = 1f;
            if (ship != null) smoothRot = ship.transform.rotation;
            LateUpdate();
        }
    }
}
