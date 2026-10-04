using UnityEngine;

namespace PG
{
    /// <summary>
    /// Keyboard + mouse to ship controls (legacy Input manager: identical in
    /// Editor and player, no generated InputActions asset needed).
    ///
    /// Mouse semantics are unambiguous: with the cursor captured, the mouse
    /// moves a virtual stick (shown on the HUD as a small ring around the
    /// crosshair) that pitches and yaws the ship; it recentres slowly when the
    /// mouse is still. The mouse never "free-looks" - the cockpit view is
    /// fixed to the ship. The help overlay (F1) is rendered from <see cref="KeyMap"/>.
    /// </summary>
    public class ShipInput : MonoBehaviour
    {
        public ShipController ship;
        public CameraRig rig;

        public bool uiCaptureMouse = false;

        /// <summary>When false the reader leaves the ship's control fields alone (tests, route pilot).</summary>
        public bool acceptInput = true;

        [Header("Feel")]
        public float mouseSensitivity = 0.045f;
        public float stickRecentre = 0.55f;
        public float stickDeadzone = 0.05f;
        public bool invertY = false;
        public float throttleRate = 0.85f;

        public float throttle;
        public Vector2 Stick { get; private set; }

        // Diagnostics for the OS-level input test (read and reset by the
        // -pgTelemetry probe): raw mouse axis summed since the last sample,
        // and how many frames carried any mouse motion at all.
        [HideInInspector] public Vector2 RawMouseSinceProbe;
        [HideInInspector] public int MouseFramesSinceProbe;
        bool releasedByPlayer;
        int relockStep;           // 2: release this frame, 1: lock this frame, 0: idle
        int ignoreMouseFrames;    // frames after a (re)lock whose delta is the cursor warp, not the player
        public int RelockCount { get; private set; }

        public static readonly string[,] KeyMap = new string[,]
        {
            { "Mouse",          "Pitch / yaw (virtual stick, ring on HUD)" },
            { "W / S",          "Throttle: forward speed up / down" },
            { "X",              "Throttle to zero" },
            { "A / D",          "Yaw left / right" },
            { "Q / E",          "Roll left / right" },
            { "Up / Down",      "Pitch (keyboard)" },
            { "R / F",          "Vertical thrust up / down (R: take off)" },
            { "Space",          "Brake" },
            { "Left Shift",     "Boost" },
            { "Tab",            "Next target planet" },
            { "J",              "Pulse drive on/off (nose on target)" },
            { "L",              "Assisted landing (below 900 m)" },
            { "K",              "Take off" },
            { "M",              "System map" },
            { "C",              "Camera: external / cockpit" },
            { "V",              "Flight assist on / off" },
            { "B / N",          "Drop beacon / target nearest beacon" },
            { "H",              "Scan point of interest ahead" },
            { "F5 / F9",        "Quick save / load" },
            { "F1 / F3 / F4",   "Help / telemetry / test menu" },
            { "I",              "Invert mouse Y" },
            { "Esc",            "Release / capture the mouse" },
        };

        void OnEnable()
        {
            if (ship != null) ship.Touchdown += ResetThrottle;
        }

        void OnDisable()
        {
            if (ship != null) ship.Touchdown -= ResetThrottle;
        }

        /// <summary>
        /// Re-capture the mouse whenever the window comes back to the front, unless
        /// the player released it on purpose (Esc). A lock requested while the
        /// window had no focus (e.g. the game started behind another window) is
        /// otherwise never applied, and the mouse silently stops steering.
        /// </summary>
        void OnApplicationFocus(bool focus)
        {
            if (!focus || !acceptInput || uiCaptureMouse || releasedByPlayer) return;
            relockStep = 2;   // re-apply: a lock that is already "Locked" in the API is not re-sent to the OS
            ignoreMouseFrames = 3;
        }

        void Update()
        {
            if (ship == null || !acceptInput) return;
            float dt = Time.unscaledDeltaTime;
            bool typing = uiCaptureMouse;

            Vector2 rawMouse = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
            if (ignoreMouseFrames > 0) { ignoreMouseFrames--; rawMouse = Vector2.zero; }
            RawMouseSinceProbe += rawMouse;
            if (rawMouse.sqrMagnitude > 1e-8f) MouseFramesSinceProbe++;

            // ---------- capture -------------------------------------------------
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                bool lockNow = Cursor.lockState != CursorLockMode.Locked;
                Cursor.lockState = lockNow ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !lockNow;
                releasedByPlayer = !lockNow;
            }
            if (!typing && Input.GetMouseButtonDown(0) && (Cursor.lockState != CursorLockMode.Locked || CursorRoams()))
            {
                releasedByPlayer = false;
                relockStep = 2;
            }

            // Self-healing capture. A lock requested while the window was in the
            // background (start-up behind another window, alt-tab) can stay
            // un-applied by the OS although Cursor.lockState reads Locked: the
            // cursor then roams and the mouse stops steering. A locked cursor sits
            // at the window centre, so a cursor elsewhere means the lock is not
            // real; release it for one frame and lock again.
            if (relockStep == 2) { Cursor.lockState = CursorLockMode.None; relockStep = 1; }
            else if (relockStep == 1) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; relockStep = 0; RelockCount++; ignoreMouseFrames = 3; }
            else if (!releasedByPlayer && !typing && Application.isFocused && Cursor.lockState == CursorLockMode.Locked && CursorRoams())
                relockStep = 2;
            bool locked = Cursor.lockState == CursorLockMode.Locked && !typing;

            // ---------- virtual stick from the mouse ------------------------------
            Vector2 stick = Stick;
            if (locked)
            {
                Vector2 d = new Vector2(rawMouse.x, rawMouse.y * (invertY ? -1f : 1f));
                stick += d * mouseSensitivity;
                if (stick.magnitude > 1f) stick = stick.normalized;
                if (d.sqrMagnitude < 1e-6f) stick = Vector2.MoveTowards(stick, Vector2.zero, stickRecentre * dt);
            }
            else stick = Vector2.MoveTowards(stick, Vector2.zero, 4f * dt);
            Stick = stick;
            Vector2 s = stick.magnitude < stickDeadzone ? Vector2.zero : stick;

            if (typing) return;

            // ---------- throttle set point ---------------------------------------
            float t = 0f;
            if (Input.GetKey(KeyCode.W)) t += 1f;
            if (Input.GetKey(KeyCode.S)) t -= 1f;
            throttle = Mathf.Clamp(throttle + t * throttleRate * dt, -0.3f, 1f);
            if (Input.GetKeyDown(KeyCode.X)) throttle = 0f;
            ship.inThrottle = throttle;

            // ---------- rotation ---------------------------------------------------
            float yaw = 0f, pitch = 0f, roll = 0f, lift = 0f;
            if (Input.GetKey(KeyCode.A)) yaw -= 1f;
            if (Input.GetKey(KeyCode.D)) yaw += 1f;
            if (Input.GetKey(KeyCode.UpArrow)) pitch += 1f;
            if (Input.GetKey(KeyCode.DownArrow)) pitch -= 1f;
            if (Input.GetKey(KeyCode.Q)) roll -= 1f;
            if (Input.GetKey(KeyCode.E)) roll += 1f;
            if (Input.GetKey(KeyCode.R)) lift += 1f;
            if (Input.GetKey(KeyCode.F)) lift -= 1f;

            ship.inYaw = Mathf.Clamp(yaw + s.x, -1f, 1f);
            ship.inPitch = Mathf.Clamp(pitch + s.y, -1f, 1f);
            ship.inRoll = roll;
            ship.inLift = Mathf.MoveTowards(ship.inLift, lift, 5f * dt);
            ship.brake = Input.GetKey(KeyCode.Space);
            ship.boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (Input.GetKeyDown(KeyCode.J)) ship.pulseToggleRequested = true;
            if (Input.GetKeyDown(KeyCode.L)) ship.landingRequested = true;
            if (Input.GetKeyDown(KeyCode.K)) ship.takeoffRequested = true;
            if (Input.GetKeyDown(KeyCode.P)) ship.ToggleMotor();
            if (Input.GetKeyDown(KeyCode.V))
            {
                ship.flightAssist = !ship.flightAssist;
                GameManager.Toast("Flight assist " + (ship.flightAssist ? "ON" : "OFF - inertial flight, gravity applies"));
            }
            if (Input.GetKeyDown(KeyCode.I))
            {
                invertY = !invertY;
                GameManager.Toast("Mouse Y " + (invertY ? "inverted" : "normal"));
            }
            if (Input.GetKeyDown(KeyCode.C) && rig != null) rig.CycleView();
        }

        public void ResetThrottle() { throttle = 0f; if (ship != null) ship.inThrottle = 0f; }

        static bool CursorRoams()
        {
            Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            return ((Vector2)Input.mousePosition - c).sqrMagnitude > 64f;
        }
    }
}
