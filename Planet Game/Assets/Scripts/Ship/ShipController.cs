using System;
using UnityEngine;

namespace PG
{
    public enum FlightMode { Space, Atmosphere, Pulse, Landed }

    /// <summary>
    /// The player ship.
    ///
    /// Model convention (verified by tests): the ship root has +Y up and +Z
    /// forward; the Nose anchor is at +Z, engines at -Z, the external camera
    /// sits behind on -Z. The orientation lives in ONE place -
    /// <see cref="Orientation"/> - and the root transform is a render puppet of
    /// it. (The previous version rotated a child and then copied the child's
    /// world rotation back onto its own parent, which compounded every input
    /// quadratically and made the ship spin out of control.)
    ///
    /// Position and velocity are double-precision logical state; ground contact
    /// is analytic (the terrain function), so it never depends on a collider
    /// having streamed in.
    ///
    /// Flight assist (default ON) is what makes the ship steerable for anyone:
    /// the velocity vector is continuously turned toward the nose, W/S set a
    /// target forward speed, and the thrusters hold altitude against gravity.
    /// With assist OFF the ship is inertial: thrust only, gravity applies.
    ///
    /// The ship can be lost: touching the ground faster than
    /// <see cref="PGConst.ExplodeSpeed"/>, touching the star, or a hull worn
    /// down to 0% blows it up. The wreck stays where it is (no motion, no
    /// controls) until GameManager hands out a new ship.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(-20)]
    public class ShipController : MonoBehaviour, FloatingOrigin.ITracked
    {
        public static ShipController Instance { get; private set; }
        public static void ResetInstance() { Instance = null; }

        /// <summary>Debug relocations this session (test-menu teleports). Normal play never increments it.</summary>
        public static int TeleportCount { get; private set; }

        [Header("Wiring")]
        public WorldStreamer streamer;
        public Transform shipVisual;
        public Transform cockpitAnchor;
        public Transform noseAnchor;
        public Transform[] engineAnchors;
        public Transform[] gearFeet;
        public ParticleSystem[] thrusterParticles;
        public Renderer[] engineGlowRenderers;
        public GearAnimator gearAnimator;

        // ---- logical state ---------------------------------------------------
        public DVec3 LogicalPosition;
        public DVec3 LogicalVelocity;
        public Quaternion Orientation = Quaternion.identity;
        /// <summary>Angular velocity in local axes, deg/s (x: pitch-down, y: yaw-right, z: roll-left).</summary>
        public Vector3 AngularVelocity;

        public BodyDef SoiBody;
        public FlightMode Mode { get; private set; } = FlightMode.Space;

        // ---- control inputs (written by ShipInput, or by tests / the route pilot) ----
        [HideInInspector] public float inThrottle;   // forward speed set point, -0.3..1 (assist) / thrust (no assist)
        [HideInInspector] public float inYaw;        // -1..1, + = nose right
        [HideInInspector] public float inPitch;      // -1..1, + = nose up
        [HideInInspector] public float inRoll;       // -1..1, + = roll right
        [HideInInspector] public float inLift;       // -1..1, + = up
        [HideInInspector] public bool  brake;
        [HideInInspector] public bool  boost;
        [HideInInspector] public bool  pulseToggleRequested;
        [HideInInspector] public bool  landingRequested;
        [HideInInspector] public bool  takeoffRequested;
        public bool flightAssist = true;

        // ---- readouts (all measured) ------------------------------------------
        public float Speed => (float)LogicalVelocity.magnitude;
        public float ForwardSpeed => (float)DVec3.Dot(LogicalVelocity, DVec3.From(Orientation * Vector3.forward));
        public float PulseCharge { get; private set; }
        public bool  PulseSpooling { get; private set; }
        public bool  PulseEngaged => Mode == FlightMode.Pulse;
        public string PulseStatus { get; private set; } = "";
        public string ModeLabel { get; private set; } = "SPACE";
        public string LandStatus { get; set; } = "";
        public float LastImpactSpeed { get; private set; }
        public string LastImpactNote { get; private set; } = "";
        public float HullIntegrity { get; set; } = 1f;
        public bool  GearDown { get; private set; } = true;
        public bool  Landed => Mode == FlightMode.Landed;
        public bool  Assisting => LandStatus == "ALIGNING" || LandStatus == "DESCENDING";
        public bool  MotorDisabled { get; set; }
        /// <summary>True from the explosion until the respawn.</summary>
        public bool  Destroyed { get; private set; }
        public string DestroyedReason { get; private set; } = "";
        public float DestroyedSeconds { get; private set; }
        public bool  DestroyedByStar { get; private set; }
        /// <summary>1 at the moment of the explosion, decaying to 0 (drives the camera shake).</summary>
        public float Blast { get; private set; }
        public double CurrentGravity { get; private set; }
        public double AltitudeAGL { get; private set; } = 1e9;
        public double AltitudeDatum { get; private set; } = 1e9;
        public float VerticalSpeed { get; private set; }
        public float AirDensity { get; private set; }
        public float ThrustLoad { get; private set; }
        public float TimeToImpact { get; private set; } = float.PositiveInfinity;
        public bool  CollisionWarning { get; private set; }
        public bool  StarWarning { get; private set; }
        /// <summary>Gas giant whose haze the ship is inside, null outside every one.</summary>
        public BodyDef GasBody { get; private set; }
        /// <summary>0 at the gas giant's haze top, 1 at and below its cloud deck.</summary>
        public float GasDepth { get; private set; }
        /// <summary>0..1 buffeting inside a gas giant (also drives the camera shake).</summary>
        public float Buffet { get; private set; }
        public float GroundClearance { get; private set; } = PGConst.ShipGroundClearance;
        public float GearSpan { get; private set; } = PGConst.ShipGearSpan;

        /// <summary>Largest logical displacement of one fixed step since the last reset (teleport detector).</summary>
        public double MaxStepDisplacement { get; private set; }
        /// <summary>Largest ratio of step displacement to the speed-bound for that step.</summary>
        public double MaxStepRatio { get; private set; }
        public void ResetStepStats() { MaxStepDisplacement = 0; MaxStepRatio = 0; }

        public event Action Touchdown;
        public event Action LiftOff;
        public event Action Exploded;

        Rigidbody rb;
        DVec3 landingSite = new DVec3(0, 1, 0);
        Renderer[] wreckHidden;

        // Render interpolation between fixed steps.
        DVec3 prevLogical, currLogical;
        Quaternion prevRot = Quaternion.identity, currRot = Quaternion.identity;
        float lastFixedTime;

        MaterialPropertyBlock glowBlock;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            rb = GetComponent<Rigidbody>();
            // A kinematic puppet of the logical state: one authority for motion.
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            glowBlock = new MaterialPropertyBlock();
        }

        void OnEnable() { FloatingOrigin.Instance?.Register(this); }
        void OnDisable() { FloatingOrigin.Instance?.Unregister(this); }

        void Start()
        {
            ResolveAnchors();
            MeasureGear();
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindDeep(root.GetChild(i), name);
                if (t != null) return t;
            }
            return null;
        }

        void ResolveAnchors()
        {
            if (shipVisual == null) shipVisual = transform;
            if (noseAnchor == null) noseAnchor = FindDeep(shipVisual, "Nose");
            if (cockpitAnchor == null) cockpitAnchor = FindDeep(shipVisual, "Cockpit");
            if (engineAnchors == null || engineAnchors.Length == 0)
            {
                var l = FindDeep(shipVisual, "Engine_L");
                var r = FindDeep(shipVisual, "Engine_R");
                if (l != null && r != null) engineAnchors = new[] { l, r };
            }
            if (gearFeet == null || gearFeet.Length == 0)
            {
                var f = FindDeep(shipVisual, "Gear_F");
                var l = FindDeep(shipVisual, "Gear_L");
                var r = FindDeep(shipVisual, "Gear_R");
                if (f != null && l != null && r != null) gearFeet = new[] { f, l, r };
            }
        }

        /// <summary>
        /// Parked height = how far below the root the deployed gear feet reach,
        /// so the feet rest exactly on the ground instead of floating or sinking.
        /// </summary>
        void MeasureGear()
        {
            if (gearFeet == null || gearFeet.Length == 0) return;
            float lowest = 0f, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var f in gearFeet)
            {
                if (f == null) continue;
                Vector3 lp = transform.InverseTransformPoint(f.position);
                lowest = Mathf.Min(lowest, lp.y);
                minZ = Mathf.Min(minZ, lp.z);
                maxZ = Mathf.Max(maxZ, lp.z);
            }
            if (lowest < -0.2f) GroundClearance = -lowest;
            if (maxZ > minZ) GearSpan = maxZ - minZ;
        }

        // ------------------------------------------------------------ placement

        /// <summary>One-time spawn before play starts (not a gameplay teleport).</summary>
        public void SpawnAt(DVec3 pos, Quaternion rot)
        {
            Place(pos, DVec3.zero, rot);
        }

        /// <summary>Restore an exact saved state (save/load). Logged separately from teleports.</summary>
        public void RestoreState(DVec3 pos, DVec3 vel, Quaternion rot)
        {
            Place(pos, vel, rot);
        }

        /// <summary>
        /// Debug relocation - only the explicitly labelled test menu calls this.
        /// Counted, so tests can prove normal travel never does.
        /// </summary>
        public void TeleportLogical(DVec3 pos, DVec3 vel)
        {
            TeleportCount++;
            Place(pos, vel, Orientation);
        }

        void Place(DVec3 pos, DVec3 vel, Quaternion rot)
        {
            // The ship never leaves its pilot behind on a load or a debug jump.
            if (PlayerCharacter.Instance != null) PlayerCharacter.Instance.BoardImmediate();
            LogicalPosition = pos;
            LogicalVelocity = vel;
            Orientation = rot;
            AngularVelocity = Vector3.zero;
            prevLogical = currLogical = pos;
            prevRot = currRot = rot;
            Mode = FlightMode.Space;
            PulseSpooling = false; PulseCharge = 0f;
            Revive();
            var fo = FloatingOrigin.Instance;
            if (fo != null) fo.SetOriginHard(pos);
            if (streamer != null) SoiBody = streamer.system.DominantBody(pos);
            ApplyRender(1f);
        }

        // ============================================================ SIMULATION

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (FloatingOrigin.Instance == null || streamer == null) return;
            DVec3 prevPos = LogicalPosition;

            SoiBody = streamer.CurrentBody ?? streamer.system.DominantBody(LogicalPosition);
            DVec3 toCentre = SoiBody.LogicalPos - LogicalPosition;
            double radius = toCentre.magnitude;
            DVec3 up = radius > 1e-3 ? -toCentre / radius : new DVec3(0, 1, 0);

            AltitudeDatum = radius - SoiBody.radius;
            AltitudeAGL = streamer.AltitudeAboveGround(SoiBody, LogicalPosition);
            double atmoH = SoiBody.atmosphereHeight;
            bool inAtmo = SoiBody.landable && atmoH > 1.0 && AltitudeDatum < atmoH;
            float t = atmoH > 1.0 ? Mathf.Clamp01((float)(1.0 - AltitudeDatum / atmoH)) : 0f;
            AirDensity = t * t * (3f - 2f * t);
            GasBody = streamer.GasGiantAt(LogicalPosition, out float gasDepth);
            GasDepth = gasDepth;

            if (Destroyed)
            {
                HoldWreck(dt);
                EndStep(prevPos, dt, up);
                return;
            }

            if (Mode == FlightMode.Landed)
            {
                HandleLanded(dt, up);
                EndStep(prevPos, dt, up);
                return;
            }

            UpdatePulse(dt, inAtmo, up);

            if (landingRequested)
            {
                landingRequested = false;
                TryStartLanding(up);
            }
            takeoffRequested = false;   // only meaningful while landed

            if (Assisting)
            {
                Mode = FlightMode.Atmosphere;
                LandingAssistant.Step(this, streamer, ref landingSite, AirDensity, dt);
                UpdateGear();
                EndStep(prevPos, dt, up);
                return;
            }

            if (Mode != FlightMode.Pulse) Mode = inAtmo ? FlightMode.Atmosphere : FlightMode.Space;
            if (LandStatus == "LIFTING OFF" && AltitudeAGL > 150.0) LandStatus = "";

            UpdateRotation(dt, up);
            ApplyThrust(dt, up, inAtmo);
            if (flightAssist && !MotorDisabled) ApplyAssist(dt, up, inAtmo);
            ApplyGravity(dt, up, radius, atmoH);
            ClampSpeed(inAtmo);
            GasGiantFlight(dt);
            PulseSkirtStar();

            LogicalPosition += LogicalVelocity * dt;

            AltitudeAGL = streamer.AltitudeAboveGround(SoiBody, LogicalPosition);
            ResolveGroundContact(up);
            StarGuard(dt);
            UpdateGear();
            UpdateHull(dt);
            EndStep(prevPos, dt, up);
        }

        void EndStep(DVec3 prevPos, float dt, DVec3 up)
        {
            double step = (LogicalPosition - prevPos).magnitude;
            if (step > MaxStepDisplacement) MaxStepDisplacement = step;
            // Bound: what the velocity could have carried the ship (before or after
            // this step's acceleration) plus a constant for push-out corrections.
            double bound = Math.Max(Speed, 1.0) * dt * 1.6 + 2.5;
            double ratio = step / bound;
            if (ratio > MaxStepRatio) MaxStepRatio = ratio;

            VerticalSpeed = (float)DVec3.Dot(LogicalVelocity, up);
            float closing = -VerticalSpeed;
            TimeToImpact = closing > 1f ? (float)(Math.Max(AltitudeAGL - GroundClearance, 0.0) / closing) : float.PositiveInfinity;
            CollisionWarning = Mode != FlightMode.Landed && !Assisting &&
                               ((closing > 25f && TimeToImpact < 7f) || (AltitudeAGL < 120.0 && closing > 16f));

            UpdateModeLabel();

            prevLogical = currLogical; prevRot = currRot;
            currLogical = LogicalPosition; currRot = Orientation;
            lastFixedTime = Time.fixedTime;
            rb.position = FloatingOrigin.Instance.ToRender(LogicalPosition);
            rb.rotation = Orientation;
        }

        void UpdateModeLabel()
        {
            if (Destroyed) { ModeLabel = "DESTROYED"; return; }
            if (GasBody != null && Mode != FlightMode.Pulse)
            {
                ModeLabel = GasDepth >= 1f ? "INSIDE GAS GIANT" : "GAS GIANT HAZE";
                return;
            }
            switch (Mode)
            {
                case FlightMode.Pulse: ModeLabel = "PULSE"; break;
                case FlightMode.Landed: ModeLabel = "LANDED"; break;
                case FlightMode.Atmosphere:
                    ModeLabel = AirDensity > 0.6f ? "ATMOSPHERE"
                              : (VerticalSpeed < -40f && Speed > 150f ? "ATMOSPHERIC ENTRY" : "UPPER ATMOSPHERE");
                    break;
                default: ModeLabel = PulseSpooling ? "PULSE SPOOL" : "SPACE"; break;
            }
        }

        // ------------------------------------------------------------ rotation

        void UpdateRotation(float dt, DVec3 up)
        {
            bool pulse = Mode == FlightMode.Pulse;
            float pitchRate = pulse ? 26f : 62f;
            float yawRate   = pulse ? 26f : 52f;
            float rollRate  = pulse ? 60f : 120f;
            float authority = MotorDisabled ? 0.3f : 1f;

            Vector3 target = new Vector3(
                -Mathf.Clamp(inPitch, -1f, 1f) * pitchRate,
                 Mathf.Clamp(inYaw, -1f, 1f) * yawRate,
                -Mathf.Clamp(inRoll, -1f, 1f) * rollRate) * authority;

            // First-order response: quick but not twitchy.
            float k = 1f - Mathf.Exp(-dt / 0.13f);
            AngularVelocity += (target - AngularVelocity) * k;

            float ang = AngularVelocity.magnitude * dt;
            if (ang > 1e-5f)
                Orientation = Orientation * Quaternion.AngleAxis(ang, AngularVelocity.normalized);

            // Roll-only auto-level near and inside an atmosphere. It only ever
            // rotates about the nose axis, so it never fights pitch or yaw.
            if (flightAssist && Mathf.Abs(inRoll) < 0.05f && AltitudeAGL < 6000.0)
            {
                Vector3 upV = up.ToVector3();
                Vector3 fwd = Orientation * Vector3.forward;
                Vector3 wantUp = upV - fwd * Vector3.Dot(upV, fwd);
                if (wantUp.sqrMagnitude > 0.02f)
                {
                    float err = Vector3.SignedAngle(Orientation * Vector3.up, wantUp.normalized, fwd);
                    float rate = Mathf.Lerp(0.5f, 2.2f, AirDensity);
                    Orientation = Quaternion.AngleAxis(err * Mathf.Clamp01(rate * dt), fwd) * Orientation;
                }
            }
            Orientation = Normalize(Orientation);
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        // -------------------------------------------------------------- thrust

        float MaxSpeedForMode(bool inAtmo)
        {
            if (Mode == FlightMode.Pulse) return PGConst.SpeedPulse;
            if (inAtmo) return boost ? PGConst.SpeedAtmoBoost : PGConst.SpeedAtmoMax;
            return boost ? PGConst.SpeedOrbitMax * 1.7f : PGConst.SpeedOrbitMax;
        }

        void ApplyThrust(float dt, DVec3 up, bool inAtmo)
        {
            DVec3 fwd = DVec3.From(Orientation * Vector3.forward);
            float accel = Mode == FlightMode.Pulse ? PGConst.AccelPulse : (inAtmo ? PGConst.AccelAtmo : PGConst.AccelOrbit);
            if (boost && Mode != FlightMode.Pulse) accel *= 1.6f;
            if (MotorDisabled) accel *= 0.25f;

            float load = 0f;
            if (flightAssist)
            {
                // W/S set a forward speed; the engines chase it.
                double vf = DVec3.Dot(LogicalVelocity, fwd);
                double target = Mode == FlightMode.Pulse ? PulseSetPoint() : inThrottle * MaxSpeedForMode(inAtmo);
                double dv = target - vf;
                double maxUp = accel * dt, maxDown = accel * dt * 1.35;
                double stepV = Math.Max(-maxDown, Math.Min(maxUp, dv));
                LogicalVelocity += fwd * stepV;
                load = Mathf.Clamp01((float)(Math.Abs(stepV) / Math.Max(maxUp, 1e-4))) * 0.7f + Mathf.Clamp01(Mathf.Abs(inThrottle)) * 0.3f;
            }
            else
            {
                LogicalVelocity += fwd * (inThrottle * accel * dt);
                load = Mathf.Abs(inThrottle);
            }

            if (Mathf.Abs(inLift) > 0.01f)
            {
                // Vertical thrust along the local vertical near a planet, along the
                // ship's own up axis in open space.
                DVec3 axis = (inAtmo || AltitudeAGL < 3000.0) ? up : DVec3.From(Orientation * Vector3.up);
                float liftAccel = (inAtmo ? 42f : 34f) * (MotorDisabled ? 0.3f : 1f);
                LogicalVelocity += axis * (inLift * liftAccel * dt);
                load = Mathf.Max(load, Mathf.Abs(inLift) * 0.6f);
            }

            if (brake)
            {
                double sp = LogicalVelocity.magnitude;
                if (sp > 0.01)
                {
                    double dec = Math.Min(sp, PGConst.BrakeAccel * dt * (1.0 + sp / 400.0));
                    LogicalVelocity -= LogicalVelocity / sp * dec;
                }
                load = Mathf.Max(load, 0.5f);
            }

            if (Mode == FlightMode.Pulse) load = 1f;
            ThrustLoad = Mathf.Lerp(ThrustLoad, Mathf.Clamp01(load), 1f - Mathf.Exp(-dt * 6f));

            // Light passive drag in air only; space is frictionless.
            if (inAtmo)
                LogicalVelocity *= Math.Max(0.0, 1.0 - 0.04 * AirDensity * dt);
        }

        /// <summary>
        /// The flight assist: turn the velocity vector toward the nose with a time
        /// constant of well under a second, converting most of the sideways
        /// momentum into forward motion. This is what makes a 90-degree yaw
        /// actually change the direction of travel.
        /// </summary>
        void ApplyAssist(float dt, DVec3 up, bool inAtmo)
        {
            double sp = LogicalVelocity.magnitude;
            if (sp < 0.01) return;
            DVec3 fwd = DVec3.From(Orientation * Vector3.forward);
            double k = Mode == FlightMode.Pulse ? 3.0 : (inAtmo ? Mathf.Lerp(1.5f, 2.6f, AirDensity) : 1.5);

            double vf = DVec3.Dot(LogicalVelocity, fwd);
            DVec3 vLat = LogicalVelocity - fwd * vf;
            if (Math.Abs(inLift) > 0.05)
            {
                DVec3 axis = (inAtmo || AltitudeAGL < 3000.0) ? up : DVec3.From(Orientation * Vector3.up);
                vLat -= axis * DVec3.Dot(vLat, axis);   // keep the deliberate climb/descent
            }
            double r = 1.0 - Math.Exp(-k * dt);
            LogicalVelocity -= vLat * r;
            if (vf > 0.0) LogicalVelocity += fwd * (vLat.magnitude * r * 0.8);
        }

        void ApplyGravity(float dt, DVec3 up, double radius, double atmoH)
        {
            if (!SoiBody.landable) { CurrentGravity = 0; return; }
            double g = SoiBody.SurfaceGravityAt(radius);
            double falloff = atmoH > 1.0 ? PGNoise.Clamp01(1.0 - AltitudeDatum / (atmoH * 2.4)) : 0.0;
            CurrentGravity = g * falloff;
            // With flight assist the thrusters hover the ship: no net sink.
            if (!flightAssist || MotorDisabled)
                LogicalVelocity -= up * (CurrentGravity * dt);
        }

        /// <summary>
        /// Safe-approach envelope: a continuous function of the altitude, so a
        /// fast arrival bleeds off speed visibly instead of hitting a wall.
        /// </summary>
        void ClampSpeed(bool inAtmo)
        {
            double speed = LogicalVelocity.magnitude;
            if (speed < 0.001) return;

            double cap = PGConst.SpeedPulse;
            if (AltitudeAGL < 12000.0)
                cap = Math.Min(cap, Lerp(PGConst.SpeedApproachCap, PGConst.SpeedPulse, AltitudeAGL / 12000.0));
            if (inAtmo)
                cap = Math.Min(cap, Lerp(PGConst.SpeedEntryCap, PGConst.SpeedApproachCap, AltitudeDatum / Math.Max(SoiBody.atmosphereHeight, 1.0)));
            if (AltitudeAGL < 600.0)
                cap = Math.Min(cap, Lerp(PGConst.SpeedAtmoBoost, PGConst.SpeedApproachCap, AltitudeAGL / 600.0));

            if (speed > cap)
            {
                double over = speed - cap;
                double reduce = Math.Min(over, (over * 2.4 + 6.0) * Time.fixedDeltaTime);
                LogicalVelocity -= LogicalVelocity / speed * reduce;
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * PGNoise.Clamp01(t);

        /// <summary>
        /// Inside a gas giant: the thick gas bleeds speed toward a low cap and
        /// buffets the ship - smooth random kicks to attitude and course that
        /// grow with depth and speed. There is no ground and no damage, so the
        /// ship can always fly out the other side.
        /// </summary>
        void GasGiantFlight(float dt)
        {
            if (GasBody == null)
            {
                Buffet = Mathf.Lerp(Buffet, 0f, 1f - Mathf.Exp(-dt * 3f));
                return;
            }

            double speed = LogicalVelocity.magnitude;
            double cap = Lerp(PGConst.SpeedOrbitMax * 1.7, PGConst.SpeedGasMax, GasDepth);
            if (speed > cap)
            {
                double over = speed - cap;
                // The constant term outweighs full boosted thrust (~180 m/s²),
                // so the engines can never push past the cap.
                double reduce = Math.Min(over, (over * 3.0 + 220.0) * dt);
                LogicalVelocity -= LogicalVelocity / speed * reduce;
                speed -= reduce;
            }

            float want = GasDepth * (0.3f + 0.7f * Mathf.Clamp01((float)speed / 180f));
            Buffet = Mathf.Lerp(Buffet, want, 1f - Mathf.Exp(-dt * 3f));
            if (MotorDisabled || Buffet < 0.01f) return;

            float tn = Time.time * 1.7f;
            Vector3 kick = new Vector3(Mathf.PerlinNoise(tn, 3.1f) - 0.5f,
                                       Mathf.PerlinNoise(7.3f, tn) - 0.5f,
                                       Mathf.PerlinNoise(tn, 11.9f) - 0.5f);
            AngularVelocity += kick * (Buffet * 90f * dt);
            LogicalVelocity += DVec3.From(Orientation * new Vector3(kick.x, kick.y, 0f)) * (Buffet * 14.0 * dt);
        }

        // --------------------------------------------------------- pulse drive

        BodyDef PulseTarget => streamer != null ? streamer.TargetBody : null;

        public double AngleToTarget()
        {
            var tb = PulseTarget;
            if (tb == null) return 180.0;
            Vector3 fwd = Orientation * Vector3.forward;
            Vector3 to = (tb.LogicalPos - LogicalPosition).normalized.ToVector3();
            return Vector3.Angle(fwd, to);
        }

        public double DistanceToTargetSurface()
        {
            var tb = PulseTarget;
            if (tb == null) return 0.0;
            return (tb.LogicalPos - LogicalPosition).magnitude - tb.radius;
        }

        /// <summary>Pulse cruise speed, tapering with distance so the arrival is a visible deceleration.</summary>
        double PulseSetPoint()
        {
            double d = DistanceToTargetSurface();
            return Math.Max(500.0, Math.Min(PGConst.SpeedPulse, d * 0.22));
        }

        void UpdatePulse(float dt, bool inAtmo, DVec3 up)
        {
            if (pulseToggleRequested)
            {
                pulseToggleRequested = false;
                if (Mode == FlightMode.Pulse || PulseSpooling) DropPulse("PULSE DRIVE OFF");
                else TryEngagePulse(inAtmo);
            }

            if (PulseSpooling)
            {
                if (brake) { DropPulse("PULSE CANCELLED"); return; }
                if (AngleToTarget() > PGConst.PulseDropCone) { DropPulse("PULSE CANCELLED - OFF COURSE"); return; }
                PulseCharge = Mathf.Min(1f, PulseCharge + dt / PGConst.PulseChargeTime);
                PulseStatus = "PULSE SPOOLING " + (PulseCharge * 100f).ToString("F0") + "%";
                if (PulseCharge >= 1f)
                {
                    PulseSpooling = false;
                    Mode = FlightMode.Pulse;
                    PulseStatus = "PULSE ENGAGED -> " + PulseTarget.displayName;
                }
                return;
            }

            if (Mode != FlightMode.Pulse) { PulseCharge = Mathf.Max(0f, PulseCharge - dt * 2f); return; }

            var tb = PulseTarget;
            if (tb == null) { DropPulse("PULSE DROPPED - NO TARGET"); return; }
            if (brake) { DropPulse("PULSE DROPPED - BRAKE"); return; }
            if (AngleToTarget() > PGConst.PulseDropCone) { DropPulse("PULSE DROPPED - OFF COURSE"); return; }
            if (DistanceToTargetSurface() < tb.atmosphereHeight + 1600.0) { DropPulse("ARRIVING AT " + tb.displayName.ToUpperInvariant()); return; }
            if (inAtmo || AltitudeAGL < SoiBody.atmosphereHeight + 300.0) { DropPulse("PULSE DROPPED - PLANET PROXIMITY"); return; }
            if (GasBody != null) { DropPulse("PULSE DROPPED - " + GasBody.displayName.ToUpperInvariant() + " ATMOSPHERE"); return; }
            var star = streamer.system.Star;
            if (star != null && (LogicalPosition - star.LogicalPos).magnitude < star.radius + PGConst.StarHeatMargin) { DropPulse("PULSE DROPPED - STAR PROXIMITY"); return; }
        }

        /// <summary>
        /// A pulse course that cuts through the star's keep-out sphere is bent
        /// round the outside of it: the closing speed is limited in proportion
        /// to the remaining gap, so the ship eases onto the sphere, slides along
        /// it under its own thrust and leaves where the line to the target
        /// comes clear. Continuous flight - only the velocity is touched.
        /// </summary>
        void PulseSkirtStar()
        {
            if (Mode != FlightMode.Pulse) return;
            var star = streamer.system.Star;
            if (star == null) return;
            DVec3 d = LogicalPosition - star.LogicalPos;
            double dist = d.magnitude;
            DVec3 n = d / dist;
            double closing = -DVec3.Dot(LogicalVelocity, n);
            double maxClosing = Math.Max(0.0, (dist - star.radius - PGConst.StarPulseMargin) * 2.0);
            string skirting = "PULSE - SKIRTING " + star.displayName.ToUpperInvariant();
            if (closing > maxClosing)
            {
                LogicalVelocity += n * (closing - maxClosing);
                PulseStatus = skirting;
            }
            else if (PulseStatus == skirting) PulseStatus = "PULSE ENGAGED -> " + PulseTarget.displayName;
        }

        void TryEngagePulse(bool inAtmo)
        {
            var tb = PulseTarget;
            if (MotorDisabled) { PulseStatus = "PULSE UNAVAILABLE - ENGINES OFFLINE"; return; }
            if (tb == null) { PulseStatus = "PULSE NEEDS A TARGET (TAB)"; return; }
            if (GasBody != null) { PulseStatus = "LEAVE " + GasBody.displayName.ToUpperInvariant() + " TO PULSE"; return; }
            if (inAtmo || AltitudeAGL < SoiBody.atmosphereHeight + 300.0)
            {
                PulseStatus = "CLIMB ABOVE " + ((SoiBody.atmosphereHeight + 300.0) / 1000.0).ToString("F1") + " km TO PULSE";
                return;
            }
            if (DistanceToTargetSurface() < tb.atmosphereHeight + 2200.0) { PulseStatus = "TARGET TOO CLOSE FOR PULSE"; return; }
            double ang = AngleToTarget();
            if (ang > PGConst.PulseAlignCone)
            {
                PulseStatus = "ALIGN WITH " + tb.displayName.ToUpperInvariant() + " (" + ang.ToString("F0") + " deg OFF)";
                return;
            }
            PulseSpooling = true;
            PulseCharge = 0f;
        }

        void DropPulse(string why)
        {
            PulseSpooling = false;
            PulseStatus = why;
            if (Mode == FlightMode.Pulse) Mode = FlightMode.Space;
        }

        // ------------------------------------------------------------ contact

        void ResolveGroundContact(DVec3 up)
        {
            if (!SoiBody.landable) return;
            if (AltitudeAGL > GroundClearance + 0.02) return;

            double penetration = GroundClearance - AltitudeAGL;
            LogicalPosition += up * penetration;
            AltitudeAGL = GroundClearance;

            // Any touch at speed is fatal - a dive and a hillside met in level
            // flight alike, so this is the whole speed, not its vertical part.
            if (Speed > PGConst.ExplodeSpeed)
            {
                LastImpactSpeed = Speed;
                LastImpactNote = "CRASHED AT " + Speed.ToString("F0") + " m/s";
                Explode("HIT " + SoiBody.displayName.ToUpperInvariant() + " AT " + Speed.ToString("F0") + " m/s", false);
                return;
            }

            double vertical = DVec3.Dot(LogicalVelocity, up);
            if (vertical < 0.0)
            {
                double impact = -vertical;
                if (impact > PGConst.CrashSpeed)
                {
                    LastImpactSpeed = (float)impact;
                    LastImpactNote = "HARD IMPACT " + impact.ToString("F0") + " m/s";
                    HullIntegrity = Mathf.Clamp01(HullIntegrity - (float)((impact - PGConst.CrashSpeed) / 80.0 + 0.05));
                    if (HullIntegrity <= 0.001f)
                    {
                        Explode("HULL BROKE UP ON " + SoiBody.displayName.ToUpperInvariant(), false);
                        return;
                    }
                }
                else if (impact > 3.0)
                {
                    LastImpactSpeed = (float)impact;
                    LastImpactNote = "TOUCHDOWN " + impact.ToString("F1") + " m/s";
                }
                DVec3 tangential = LogicalVelocity - up * vertical;
                LogicalVelocity = tangential * 0.6;
            }

            // A gentle manual touchdown on suitable ground is a landing.
            if (Speed < PGConst.SpeedLandingMax && GearDown && !MotorDisabled)
            {
                var probe = LandingAssistant.Probe(streamer, SoiBody, LogicalPosition, up, AirDensity);
                if (probe.ok) EnterLanded();
                else LandStatus = "UNSAFE GROUND: " + probe.reason;
            }
        }

        void StarGuard(float dt)
        {
            var star = streamer.system.Star;
            StarWarning = false;
            if (star == null) return;
            DVec3 d = LogicalPosition - star.LogicalPos;
            double dist = d.magnitude;
            double minR = star.radius + PGConst.StarContactMargin;
            if (dist < minR)
            {
                Explode("BURNED UP IN " + star.displayName.ToUpperInvariant(), true);
                return;
            }
            if (dist < star.radius + PGConst.StarHeatMargin)
            {
                // The hull heats faster the deeper the ship goes: a slow burn at
                // the edge of the zone, seconds from the photosphere.
                StarWarning = true;
                float depth = (float)PGNoise.Clamp01(1.0 - (dist - minR) / (PGConst.StarHeatMargin - PGConst.StarContactMargin));
                HullIntegrity = Mathf.Clamp01(HullIntegrity - dt * Mathf.Lerp(0.04f, 0.6f, depth * depth));
                if (HullIntegrity <= 0.001f) Explode("HULL MELTED NEAR " + star.displayName.ToUpperInvariant(), true);
            }
        }

        void UpdateGear()
        {
            bool want = Mode == FlightMode.Landed || Assisting ||
                        (AltitudeAGL < 160.0 && Speed < 110f && Mode != FlightMode.Pulse);
            bool stow = AltitudeAGL > 260.0 || Speed > 170f || Mode == FlightMode.Pulse;
            if (want) GearDown = true;
            else if (stow) GearDown = false;
            if (gearAnimator != null) gearAnimator.SetDeployed(GearDown);
        }

        void UpdateHull(float dt)
        {
            if (!Destroyed && HullIntegrity <= 0.001f) Explode("HULL FAILURE", false);
        }

        // -------------------------------------------------------- destruction

        void Explode(string reason, bool byStar)
        {
            if (Destroyed) return;
            Destroyed = true;
            DestroyedByStar = byStar;
            DestroyedReason = reason;
            DestroyedSeconds = 0f;
            Blast = 1f;
            HullIntegrity = 0f;
            DropPulse("");
            PulseCharge = 0f;
            LandStatus = "SHIP DESTROYED - " + reason;
            LogicalVelocity = DVec3.zero;
            AngularVelocity = Vector3.zero;
            ThrustLoad = 0f;
            Buffet = 0f;
            StarWarning = false;

            // Only the hull disappears; the anchors stay, so the camera keeps
            // looking at the spot.
            var hidden = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>())
                if (r.enabled && !(r is ParticleSystemRenderer)) { r.enabled = false; hidden.Add(r); }
            wreckHidden = hidden.ToArray();
            ShipExplosion.Play(this);
            Debug.Log("[PG] ship destroyed: " + reason);
            Exploded?.Invoke();
        }

        /// <summary>The wreck: no motion, no controls, until the respawn.</summary>
        void HoldWreck(float dt)
        {
            DestroyedSeconds += dt;
            Blast = Mathf.Exp(-DestroyedSeconds * 2.2f);
            LogicalVelocity = DVec3.zero;
            AngularVelocity = Vector3.zero;
            ThrustLoad = 0f;
            pulseToggleRequested = landingRequested = takeoffRequested = false;
        }

        /// <summary>Every placement (respawn, load, debug relocation) hands back a whole ship.</summary>
        void Revive()
        {
            if (!Destroyed) return;
            Destroyed = false;
            DestroyedByStar = false;
            DestroyedReason = "";
            DestroyedSeconds = 0f;
            Blast = 0f;
            HullIntegrity = 1f;
            MotorDisabled = false;
            LandStatus = "";
            if (wreckHidden != null)
                foreach (var r in wreckHidden) if (r != null) r.enabled = true;
            wreckHidden = null;
        }

        /// <summary>
        /// A new ship after the old one was lost. Not travel and not a debug
        /// tool, so it is not counted as a teleport.
        /// </summary>
        public void Respawn(DVec3 pos, Quaternion rot)
        {
            Place(pos, DVec3.zero, rot);
        }

        // ------------------------------------------------------------ landing

        void TryStartLanding(DVec3 up)
        {
            if (!SoiBody.landable) { LandStatus = "NOTHING TO LAND ON"; return; }
            if (AltitudeAGL > 900.0)
            {
                LandStatus = "TOO HIGH TO LAND - DESCEND BELOW 900 m (" + AltitudeAGL.ToString("F0") + " m)";
                return;
            }
            if (Speed > 120f)
            {
                LandStatus = "TOO FAST TO LAND - SLOW BELOW 120 m/s (" + Speed.ToString("F0") + ")";
                return;
            }
            DropPulse("");
            double searchRadius = Math.Min(320.0, AltitudeAGL * 1.2 + 90.0);
            if (LandingAssistant.FindSuitableSite(streamer, SoiBody, LogicalPosition, up, AirDensity,
                                                  searchRadius, out DVec3 site, out var probe))
            {
                landingSite = site;
                LandStatus = "ALIGNING";
                Mode = FlightMode.Atmosphere;
                GearDown = true;
                return;
            }
            LandStatus = "CANNOT LAND HERE: " + probe.reason;
        }

        void HandleLanded(float dt, DVec3 up)
        {
            // Rest on the analytic ground, every step, so the gear stays in
            // contact with the visible surface.
            double ground = streamer.ElevationAt(SoiBody, LogicalPosition);
            DVec3 dir = (LogicalPosition - SoiBody.LogicalPos).normalized;
            LogicalPosition = SoiBody.LogicalPos + dir * (SoiBody.radius + ground + GroundClearance);
            LogicalVelocity = DVec3.zero;
            AngularVelocity = Vector3.zero;
            AltitudeAGL = GroundClearance;

            // Settle the hull onto the local slope, keeping the heading.
            var pr = streamer.RuntimeOf(SoiBody);
            if (pr != null)
            {
                Vector3 n = TerrainFunc.SurfaceNormal(SoiBody, pr.Perm, dir, Math.Max(2.0, GearSpan * 0.5)).ToVector3();
                Vector3 fwd = Vector3.ProjectOnPlane(Orientation * Vector3.forward, n);
                if (fwd.sqrMagnitude > 1e-4f)
                {
                    Quaternion want = Quaternion.LookRotation(fwd.normalized, n);
                    Orientation = Quaternion.Slerp(Orientation, want, 1f - Mathf.Exp(-dt * 3f));
                }
            }
            UpdateGear();

            bool liftOff = takeoffRequested || inLift > 0.3f || inThrottle > 0.15f;
            takeoffRequested = false;
            if (liftOff && !MotorDisabled)
            {
                Mode = FlightMode.Atmosphere;
                LandStatus = "LIFTING OFF";
                LogicalVelocity = up * 8.0;
                LiftOff?.Invoke();
            }
        }

        public void EnterLanded()
        {
            Mode = FlightMode.Landed;
            LandStatus = "LANDED - E to step out, R or K to take off";
            LogicalVelocity = DVec3.zero;
            AngularVelocity = Vector3.zero;
            inThrottle = 0f;
            inLift = 0f;
            takeoffRequested = false;
            PulseSpooling = false;
            Touchdown?.Invoke();
        }

        /// <summary>After a load: snap into the landed state if the saved position rests on the ground.</summary>
        public void EnterLandedIfTouchingGround()
        {
            if (streamer == null) return;
            SoiBody = streamer.system.DominantBody(LogicalPosition);
            if (SoiBody == null || !SoiBody.landable) return;
            double alt = streamer.AltitudeAboveGround(SoiBody, LogicalPosition);
            if (alt <= GroundClearance + 0.6) EnterLanded();
        }

        /// <summary>Debug-menu recovery: places the ship 60 m above flat ground. Counted as a teleport.</summary>
        public void RecoverFromCrash()
        {
            var b = SoiBody ?? streamer.system.DominantBody(LogicalPosition);
            var pr = streamer.RuntimeOf(b);
            DVec3 dir = (LogicalPosition - b.LogicalPos).normalized;
            DVec3 spot = TerrainFunc.FindFlatSpotOnLand(b, pr.Perm, dir, 900.0, 90);
            DVec3 surf = b.LogicalPos + TerrainFunc.SurfacePoint(b, pr.Perm, spot);
            HullIntegrity = 1f;
            MotorDisabled = false;
            Vector3 fwd = Vector3.ProjectOnPlane(Orientation * Vector3.forward, spot.ToVector3());
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.Cross(spot.ToVector3(), Vector3.right);
            Orientation = Quaternion.LookRotation(fwd.normalized, spot.ToVector3());
            TeleportLogical(surf + spot * (GroundClearance + 60.0), DVec3.zero);
            Mode = FlightMode.Atmosphere;
            LandStatus = "DEBUG RECOVERY - " + b.displayName;
        }

        public void ToggleMotor()
        {
            MotorDisabled = !MotorDisabled;
            LandStatus = MotorDisabled ? "ENGINES OFFLINE" : "ENGINES ONLINE";
        }

        // =============================================================== RENDER

        void Update()
        {
            // Interpolate between the last two fixed steps so a 60 Hz simulation
            // looks smooth at any frame rate. The camera reads this transform.
            float alpha = Time.fixedDeltaTime > 0f
                ? Mathf.Clamp01((Time.time - lastFixedTime) / Time.fixedDeltaTime)
                : 1f;
            ApplyRender(alpha);
            UpdateEffects();
        }

        void ApplyRender(float alpha)
        {
            var fo = FloatingOrigin.Instance;
            if (fo == null) return;
            DVec3 p = DVec3.Lerp(prevLogical, currLogical, alpha);
            if (alpha >= 1f || (currLogical - prevLogical).sqrMagnitude > 1e8) p = currLogical;
            transform.SetPositionAndRotation(fo.ToRender(p), Quaternion.Slerp(prevRot, currRot, alpha));
            if (shipVisual != null && shipVisual != transform && shipVisual.localRotation != Quaternion.identity)
                shipVisual.localRotation = Quaternion.identity;
        }

        /// <summary>Interpolated render-space position used by the camera this frame.</summary>
        public DVec3 RenderLogicalPosition => FloatingOrigin.Instance != null ? FloatingOrigin.Instance.ToLogical(transform.position) : LogicalPosition;

        void UpdateEffects()
        {
            float load = Destroyed ? 0f : MotorDisabled ? 0.1f : ThrustLoad;
            float boostK = boost ? 1.35f : 1f;
            if (thrusterParticles != null)
            {
                foreach (var ps in thrusterParticles)
                {
                    if (ps == null) continue;
                    var em = ps.emission;
                    em.rateOverTime = Mode == FlightMode.Landed || Destroyed ? 0f : Mathf.Lerp(8f, 150f, load) * boostK;
                    var main = ps.main;
                    main.startSpeed = Mathf.Lerp(8f, 46f, load) * boostK;
                    main.startSize = Mathf.Lerp(0.7f, 1.6f, load);
                }
            }
            if (engineGlowRenderers != null)
            {
                float glow = Mode == FlightMode.Landed ? 0.6f : Mathf.Lerp(1.4f, 5.5f, load) * boostK;
                foreach (var r in engineGlowRenderers)
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(glowBlock);
                    glowBlock.SetFloat("_EmissiveBoost", glow);
                    r.SetPropertyBlock(glowBlock);
                }
            }
        }

        // ------------------------------------------------------- floating origin

        void FloatingOrigin.ITracked.OnOriginShift(DVec3 delta)
        {
            // Logical state is untouched by definition; only render space moves.
            ApplyRender(1f);
            rb.position = transform.position;
        }
    }
}
