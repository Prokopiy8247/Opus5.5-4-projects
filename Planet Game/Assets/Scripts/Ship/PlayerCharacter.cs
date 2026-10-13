using System;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// The pilot on foot.
    ///
    /// Leaves a landed ship (E), walks, runs, jumps and swims on the planet and
    /// boards again (E next to the ship). Like the ship, the character is a
    /// double-precision logical state and its transform is a render puppet;
    /// the ground is the analytic terrain function, so walking never depends on
    /// a collider having streamed in and the feet rest on the same surface the
    /// ship's gear does.
    ///
    /// Created at runtime by GameManager - the scene and the ship prefab do not
    /// know about it. The body is assembled from primitives in the ship's own
    /// materials.
    /// </summary>
    [DefaultExecutionOrder(-15)]
    public class PlayerCharacter : MonoBehaviour, FloatingOrigin.ITracked
    {
        public static PlayerCharacter Instance { get; private set; }
        public static void ResetInstance() { Instance = null; }

        /// <summary>Where the player is: the character on foot, the ship otherwise.</summary>
        public static DVec3 FocusOf(ShipController ship)
        {
            return Instance != null && Instance.OnFoot ? Instance.LogicalPosition : ship.LogicalPosition;
        }

        public ShipController ship;
        public WorldStreamer streamer;
        public ShipInput input;
        public CameraRig rig;

        [Header("Movement")]
        public float walkSpeed = 4.6f;
        public float runSpeed = 9.5f;
        public float jumpSpeed = 5.2f;
        public float maxSlope = 48f;
        public float eyeHeight = 1.66f;
        public float lookSensitivity = 2.0f;
        public float boardDistance = 8f;

        const float SwimDepth = 1.3f;      // feet this far under the surface when afloat
        const float HullRadius = 2.4f;     // keep-out capsule along the parked ship's axis
        const float HullHalfLength = PGConst.ShipLength * 0.5f - HullRadius;

        // ---- logical state ---------------------------------------------------
        public bool OnFoot { get; private set; }
        /// <summary>Position of the feet.</summary>
        public DVec3 LogicalPosition;
        public DVec3 LogicalVelocity;
        public BodyDef Body { get; private set; }
        public bool Grounded { get; private set; }
        public bool Swimming { get; private set; }
        public bool firstPerson;
        /// <summary>Look pitch in degrees, + = up.</summary>
        public float Pitch { get; private set; }

        // ---- control inputs (written by ReadInput, or by tests) ----------------
        [HideInInspector] public Vector2 inMove;   // x: strafe right, y: forward
        [HideInInspector] public bool inRun;
        [HideInInspector] public bool inJump;

        // ---- readouts -----------------------------------------------------------
        public float Speed => (float)LogicalVelocity.magnitude;
        public Vector3 Up { get; private set; } = Vector3.up;
        /// <summary>Heading: unit vector in the local horizontal plane.</summary>
        public Vector3 Heading { get; private set; } = Vector3.forward;
        public Vector3 LookDirection => Quaternion.AngleAxis(-Pitch, Vector3.Cross(Up, Heading)) * Heading;
        public double DistanceToShip => ship != null ? (LogicalPosition - ship.LogicalPosition).magnitude : 1e9;
        public bool CanBoard => OnFoot && DistanceToShip < boardDistance;
        /// <summary>Largest logical displacement of one fixed step since the last exit (teleport detector).</summary>
        public double MaxStepDisplacement { get; private set; }

        DVec3 prevLogical, currLogical;
        float lastFixedTime;

        Transform visual, legL, legR, armL, armR;
        float stride, strideAmp;

        public static PlayerCharacter Create(ShipController ship, WorldStreamer streamer, ShipInput input, CameraRig rig)
        {
            var go = new GameObject("PlayerCharacter");
            var pc = go.AddComponent<PlayerCharacter>();
            pc.ship = ship; pc.streamer = streamer; pc.input = input; pc.rig = rig;
            pc.BuildVisual();
            return pc;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnEnable() { FloatingOrigin.Instance?.Register(this); }
        void OnDisable() { FloatingOrigin.Instance?.Unregister(this); }
        void OnDestroy() { if (Instance == this) Instance = null; }

        // ----------------------------------------------------------------- visual

        void BuildVisual()
        {
            Material suit = null, dark = null, accent = null, glass = null;
            foreach (var r in ship.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.name.StartsWith("PG_ShipHull")) suit = m;
                    else if (m.name.StartsWith("PG_ShipDark")) dark = m;
                    else if (m.name.StartsWith("PG_ShipAccent")) accent = m;
                    else if (m.name.StartsWith("PG_ShipGlass")) glass = m;
                    if (suit == null && m.shader != null && m.shader.name == "PG/Ship") suit = m;
                }
            }
            if (dark == null) dark = suit;
            if (accent == null) accent = suit;
            if (glass == null) glass = dark;

            visual = new GameObject("Visual").transform;
            visual.SetParent(transform, false);

            Part(PrimitiveType.Capsule, visual, new Vector3(0f, 1.15f, 0f), new Vector3(0.42f, 0.36f, 0.30f), suit);
            Part(PrimitiveType.Sphere, visual, new Vector3(0f, 1.62f, 0f), new Vector3(0.34f, 0.34f, 0.34f), suit);
            Part(PrimitiveType.Sphere, visual, new Vector3(0f, 1.63f, 0.08f), new Vector3(0.27f, 0.22f, 0.24f), glass);
            Part(PrimitiveType.Cube, visual, new Vector3(0f, 1.22f, -0.21f), new Vector3(0.32f, 0.42f, 0.16f), accent);
            legL = Limb("Leg_L", new Vector3(-0.11f, 0.82f, 0f), 0.82f, 0.17f, dark);
            legR = Limb("Leg_R", new Vector3(0.11f, 0.82f, 0f), 0.82f, 0.17f, dark);
            armL = Limb("Arm_L", new Vector3(-0.28f, 1.42f, 0f), 0.66f, 0.13f, suit);
            armR = Limb("Arm_R", new Vector3(0.28f, 1.42f, 0f), 0.66f, 0.13f, suit);
            visual.gameObject.SetActive(false);
        }

        /// <summary>A capsule hanging from a pivot, so the limb swings about the joint.</summary>
        Transform Limb(string name, Vector3 joint, float length, float width, Material mat)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(visual, false);
            pivot.localPosition = joint;
            Part(PrimitiveType.Capsule, pivot, new Vector3(0f, -length * 0.5f, 0f), new Vector3(width, length * 0.5f, width), mat);
            return pivot;
        }

        static void Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mr = go.GetComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ------------------------------------------------------- leave / board

        /// <summary>Step out of the landed ship onto the ground beside the cockpit.</summary>
        public bool ExitShip()
        {
            if (OnFoot || ship == null || streamer == null) return false;
            if (!ship.Landed) { GameManager.Toast("Land the ship before leaving it (L)"); return false; }
            Body = ship.SoiBody;
            if (Body == null || !Body.landable) return false;

            DVec3 side = DVec3.From(ship.Orientation * new Vector3(-(HullRadius + 1.4f), 0f, 1.5f));
            LogicalPosition = OnFloor(ship.LogicalPosition + side);
            LogicalVelocity = DVec3.zero;
            Up = (LogicalPosition - Body.LogicalPos).normalized.ToVector3();
            Vector3 h = Vector3.ProjectOnPlane(ship.Orientation * Vector3.forward, Up);
            Heading = h.sqrMagnitude > 1e-4f ? h.normalized : Vector3.Cross(Up, Vector3.right).normalized;
            Pitch = 0f;
            Grounded = true;
            inMove = Vector2.zero; inRun = false; inJump = false;
            prevLogical = currLogical = LogicalPosition;
            lastFixedTime = Time.fixedTime;
            MaxStepDisplacement = 0;

            // The parked ship must not react to keys meant for the legs.
            ship.inThrottle = ship.inYaw = ship.inPitch = ship.inRoll = ship.inLift = 0f;
            ship.brake = ship.boost = false;
            ship.takeoffRequested = ship.landingRequested = ship.pulseToggleRequested = false;
            if (input != null) { input.onFoot = true; input.ResetThrottle(); }

            OnFoot = true;
            visual.gameObject.SetActive(true);
            ApplyRender(1f);
            if (rig != null) rig.BeginBlend();
            GameManager.Toast("On foot - E next to the ship to board");
            return true;
        }

        /// <summary>Board the ship if it is within reach.</summary>
        public bool TryBoard()
        {
            if (!OnFoot) return false;
            if (!CanBoard)
            {
                GameManager.Toast("Ship is " + DistanceToShip.ToString("F0") + " m away - walk to it to board");
                return false;
            }
            Board();
            if (rig != null) rig.BeginBlend();
            GameManager.Toast("Aboard - R or K to take off");
            return true;
        }

        /// <summary>Back in the seat at once, wherever the character was: the ship is being relocated (load, debug teleport).</summary>
        public void BoardImmediate()
        {
            if (OnFoot) Board();
        }

        void Board()
        {
            OnFoot = false;
            LogicalVelocity = DVec3.zero;
            inMove = Vector2.zero; inRun = false; inJump = false;
            visual.gameObject.SetActive(false);
            if (input != null) input.onFoot = false;
        }

        // ============================================================ SIMULATION

        /// <summary>Walkable height above the datum: the ground, or swimming level over deep water.</summary>
        double FloorHeight(DVec3 logicalPos, out bool afloat)
        {
            double h = streamer.ElevationAt(Body, logicalPos);
            afloat = Body.seaLevel > -1e8 && h < Body.seaLevel - SwimDepth;
            return afloat ? Body.seaLevel - SwimDepth : h;
        }

        DVec3 OnFloor(DVec3 logicalPos)
        {
            DVec3 dir = (logicalPos - Body.LogicalPos).normalized;
            return Body.LogicalPos + dir * (Body.radius + FloorHeight(logicalPos, out _));
        }

        void FixedUpdate()
        {
            if (!OnFoot || FloatingOrigin.Instance == null || streamer == null) return;
            float dt = Time.fixedDeltaTime;
            DVec3 prevPos = LogicalPosition;

            DVec3 rel = LogicalPosition - Body.LogicalPos;
            double radius = rel.magnitude;
            DVec3 up = rel / radius;
            Vector3 upV = up.ToVector3();
            Up = upV;
            // Carry the heading along the sphere: it stays horizontal as "up" turns.
            Vector3 h = Vector3.ProjectOnPlane(Heading, upV);
            if (h.sqrMagnitude > 1e-6f) Heading = h.normalized;
            Vector3 right = Vector3.Cross(upV, Heading);

            double elevation = streamer.ElevationAt(Body, LogicalPosition);
            bool wading = Body.seaLevel > -1e8 && elevation < Body.seaLevel - 0.35;

            Vector3 wish = Heading * inMove.y + right * inMove.x;
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            float top = (inRun ? runSpeed : walkSpeed) * (Swimming ? 0.45f : wading ? 0.65f : 1f);
            DVec3 target = DVec3.From(wish) * top;

            double vUp = DVec3.Dot(LogicalVelocity, up);
            DVec3 vT = LogicalVelocity - up * vUp;

            if (Grounded && !Swimming)
            {
                // Too steep to stand on: no climbing, and the character slides off.
                DVec3 n = streamer.SurfaceNormalAt(Body, LogicalPosition);
                if (TerrainFunc.AngleBetween(n, up) * Mathf.Rad2Deg > maxSlope)
                {
                    DVec3 downhill = (n - up * DVec3.Dot(n, up)).normalized;
                    double climb = -DVec3.Dot(target, downhill);
                    if (climb > 0.0) target += downhill * climb;
                    target += downhill * 4.0;
                }
            }

            double accel = Grounded ? 42.0 : 5.0;   // little steering in the air
            DVec3 dv = target - vT;
            double dvm = dv.magnitude;
            if (dvm > 1e-9) vT += dv * Math.Min(1.0, accel * dt / dvm);

            bool wasGrounded = Grounded;
            if (Grounded)
            {
                vUp = 0.0;
                if (inJump && !Swimming) { vUp = jumpSpeed; wasGrounded = false; }
            }
            else vUp -= Body.SurfaceGravityAt(radius) * dt;
            inJump = false;

            LogicalPosition += (vT + up * vUp) * dt;
            KeepOutOfShip(ref vT);

            // Ground contact against the analytic surface.
            rel = LogicalPosition - Body.LogicalPos;
            radius = rel.magnitude;
            up = rel / radius;
            double floor = FloorHeight(LogicalPosition, out bool afloat);
            double alt = radius - (Body.radius + floor);
            // A walker follows the ground down a slope instead of hopping off every crest.
            if (alt <= 0.0 || (wasGrounded && alt < 0.55))
            {
                LogicalPosition = Body.LogicalPos + up * (Body.radius + floor);
                vUp = 0.0;
                Grounded = true;
            }
            else Grounded = false;
            Swimming = afloat && Grounded;

            vT -= up * DVec3.Dot(vT, up);
            LogicalVelocity = vT + up * vUp;

            double step = (LogicalPosition - prevPos).magnitude;
            if (step > MaxStepDisplacement) MaxStepDisplacement = step;
            prevLogical = currLogical;
            currLogical = LogicalPosition;
            lastFixedTime = Time.fixedTime;
        }

        /// <summary>The parked hull is solid: a capsule along the ship's axis pushes the walker out sideways.</summary>
        void KeepOutOfShip(ref DVec3 vT)
        {
            if (ship == null) return;
            Vector3 l = Quaternion.Inverse(ship.Orientation) * (LogicalPosition - ship.LogicalPosition).ToVector3();
            if (Mathf.Abs(l.y) > 6f) return;
            Vector2 r = new Vector2(l.x, l.z - Mathf.Clamp(l.z, -HullHalfLength, HullHalfLength));
            float m = r.magnitude;
            if (m >= HullRadius) return;
            r = m > 1e-3f ? r / m : Vector2.left;
            DVec3 outward = DVec3.From(ship.Orientation * new Vector3(r.x, 0f, r.y));
            LogicalPosition += outward * (HullRadius - m);
            double inward = DVec3.Dot(vT, outward);
            if (inward < 0.0) vT -= outward * inward;
        }

        // ========================================================== INPUT / RENDER

        void Update()
        {
            if (input == null || (input.acceptInput && !input.uiCaptureMouse)) ReadInput();
            if (!OnFoot) return;

            float alpha = Time.fixedDeltaTime > 0f
                ? Mathf.Clamp01((Time.time - lastFixedTime) / Time.fixedDeltaTime)
                : 1f;
            ApplyRender(alpha);
            Animate(Time.deltaTime);
        }

        void ReadInput()
        {
            if (!OnFoot)
            {
                // E is "roll right" in flight; on the ground it opens the hatch.
                if (ship != null && ship.Landed && Input.GetKeyDown(KeyCode.E)) ExitShip();
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 look = input != null ? input.MouseDelta : new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
                Turn(look.x * lookSensitivity, look.y * lookSensitivity);
            }

            Vector2 move = Vector2.zero;
            if (Input.GetKey(KeyCode.W)) move.y += 1f;
            if (Input.GetKey(KeyCode.S)) move.y -= 1f;
            if (Input.GetKey(KeyCode.D)) move.x += 1f;
            if (Input.GetKey(KeyCode.A)) move.x -= 1f;
            inMove = move;
            inRun = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Space)) inJump = true;
            if (Input.GetKeyDown(KeyCode.C)) firstPerson = !firstPerson;
            if (Input.GetKeyDown(KeyCode.E)) TryBoard();
        }

        /// <summary>Turn the view: yaw to the right and pitch up, in degrees.</summary>
        public void Turn(float yawDeg, float pitchDeg)
        {
            Heading = (Quaternion.AngleAxis(yawDeg, Up) * Heading).normalized;
            Pitch = Mathf.Clamp(Pitch + pitchDeg, -80f, 80f);
        }

        void ApplyRender(float alpha)
        {
            var fo = FloatingOrigin.Instance;
            if (fo == null) return;
            DVec3 p = alpha >= 1f ? currLogical : DVec3.Lerp(prevLogical, currLogical, alpha);
            transform.SetPositionAndRotation(fo.ToRender(p), Quaternion.LookRotation(Heading, Up));
        }

        void Animate(float dt)
        {
            if (visual.gameObject.activeSelf == firstPerson) visual.gameObject.SetActive(!firstPerson);
            DVec3 up = DVec3.From(Up);
            float ground = (float)(LogicalVelocity - up * DVec3.Dot(LogicalVelocity, up)).magnitude;
            float want = Grounded ? Mathf.Clamp01(ground / walkSpeed) : 0.35f;
            strideAmp = Mathf.Lerp(strideAmp, want, 1f - Mathf.Exp(-dt * 10f));
            stride += ground * dt * 2.4f;
            float swing = Mathf.Sin(stride) * 36f * strideAmp;
            legL.localRotation = Quaternion.Euler(swing, 0f, 0f);
            legR.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            armL.localRotation = Quaternion.Euler(-swing * 0.8f, 0f, 0f);
            armR.localRotation = Quaternion.Euler(swing * 0.8f, 0f, 0f);
        }

        void FloatingOrigin.ITracked.OnOriginShift(DVec3 delta)
        {
            if (OnFoot) ApplyRender(1f);
        }
    }
}
