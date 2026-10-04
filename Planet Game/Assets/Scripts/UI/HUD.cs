using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Flight HUD. Every number is read from the simulation each frame.
    ///
    /// In the 3D view: nose marker, velocity (prograde) marker, the virtual
    /// mouse-stick ring, the target bracket with distance and ETA (or an arrow
    /// at the screen edge when it is off-screen), labels for the other bodies
    /// and beacons. Around it: compact flight data, throttle/pulse bars,
    /// landing-zone validity, warnings, a star-centred mini map and a full
    /// system map (M). Help (F1) is hidden by default.
    ///
    /// IMGUI, scaled to a 1080p reference, so the scene can be rebuilt entirely
    /// from code with no canvas or font assets.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class HUD : MonoBehaviour
    {
        public GameManager game;
        public WorldStreamer streamer;
        public ShipController ship;
        public CameraRig rig;
        public SaveSystem save;
        public BeaconRegistry beacons;
        public PlanetSystem system;
        public ShipInput input;
        public ShipAudio shipAudio;

        public bool showHelp;
        public bool showDebug;
        public bool showTestMenu;
        public bool showMap;

        float routeBannerUntil;
        string routeBannerText = "";

        GUIStyle sLabel, sSmall, sBig, sTitle, sWarn, sOk, sCenter, sCenterSmall, sKey;
        Texture2D white, ring, dot, tri;
        float scale = 1f;
        float W, H;

        float probeTimer;
        string landingLine = "";
        bool landingOk;

        static readonly Color cPanel = new Color(0.02f, 0.035f, 0.06f, 0.62f);
        static readonly Color cHud = new Color(0.62f, 0.95f, 0.88f, 0.95f);
        static readonly Color cTarget = new Color(0.45f, 1f, 0.60f, 1f);
        static readonly Color cWarn = new Color(1f, 0.42f, 0.25f, 1f);
        static readonly Color cDim = new Color(0.75f, 0.82f, 0.90f, 0.85f);

        void Awake()
        {
            white = Tex(1, (x, y) => Color.white);
            ring = Tex(64, (x, y) =>
            {
                float d = new Vector2(x - 31.5f, y - 31.5f).magnitude;
                return new Color(1, 1, 1, Mathf.Clamp01(1.6f - Mathf.Abs(d - 28f)));
            });
            dot = Tex(32, (x, y) =>
            {
                float d = new Vector2(x - 15.5f, y - 15.5f).magnitude;
                return new Color(1, 1, 1, Mathf.Clamp01(15f - d));
            });
            tri = Tex(32, (x, y) =>
            {
                // Arrow pointing +x.
                float fx = x / 31f, fy = Mathf.Abs(y - 15.5f) / 15.5f;
                return new Color(1, 1, 1, fx < 1f && fy < (1f - fx) ? 1f : 0f);
            });
        }

        static Texture2D Tex(int n, System.Func<int, int, Color> f)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) t.SetPixel(x, y, f(x, y));
            t.Apply();
            return t;
        }

        public void Blip() { if (shipAudio != null) shipAudio.UiBlip(); }

        void Styles()
        {
            if (sLabel != null) return;
            sLabel = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true };
            sLabel.normal.textColor = new Color(0.88f, 0.93f, 0.98f);
            sSmall = new GUIStyle(sLabel) { fontSize = 12 };
            sSmall.normal.textColor = cDim;
            sBig = new GUIStyle(sLabel) { fontSize = 22, fontStyle = FontStyle.Bold };
            sTitle = new GUIStyle(sLabel) { fontSize = 17, fontStyle = FontStyle.Bold };
            sTitle.normal.textColor = new Color(0.80f, 0.92f, 1f);
            sWarn = new GUIStyle(sLabel) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            sWarn.normal.textColor = cWarn;
            sOk = new GUIStyle(sLabel) { fontStyle = FontStyle.Bold };
            sOk.normal.textColor = new Color(0.55f, 0.96f, 0.62f);
            sCenter = new GUIStyle(sLabel) { alignment = TextAnchor.MiddleCenter };
            sCenterSmall = new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter };
            sKey = new GUIStyle(sLabel) { fontSize = 13, fontStyle = FontStyle.Bold };
            sKey.normal.textColor = new Color(0.95f, 0.85f, 0.55f);
        }

        void Update()
        {
            bool keys = input == null || input.acceptInput || showTestMenu;
            if (Input.GetKeyDown(KeyCode.F1)) showHelp = !showHelp;
            if (Input.GetKeyDown(KeyCode.F3)) showDebug = !showDebug;
            if (keys && Input.GetKeyDown(KeyCode.M)) { showMap = !showMap; Blip(); }
            if (Input.GetKeyDown(KeyCode.F4))
            {
                showTestMenu = !showTestMenu;
                Cursor.lockState = showTestMenu ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = showTestMenu;
                if (input != null) input.uiCaptureMouse = showTestMenu;
            }

            probeTimer -= Time.unscaledDeltaTime;
            if (probeTimer <= 0f && ship != null && streamer != null)
            {
                probeTimer = 0.25f;
                UpdateLandingLine();
            }
        }

        void UpdateLandingLine()
        {
            landingLine = ""; landingOk = false;
            var b = ship.SoiBody;
            if (b == null || !b.landable || ship.Landed || ship.Assisting) return;
            if (ship.AltitudeAGL > 900.0) return;
            DVec3 up = (ship.LogicalPosition - b.LogicalPos).normalized;
            var p = LandingAssistant.Probe(streamer, b, ship.LogicalPosition, up, ship.AirDensity);
            if (ship.VerticalSpeed > 15f) return;                     // climbing out: not trying to land
            if (ship.Speed > 120f) { landingLine = "SLOW BELOW 120 m/s TO LAND"; return; }
            landingOk = p.ok;
            landingLine = p.ok ? "LANDING ZONE OK (" + p.slopeDegrees.ToString("F0") + " deg) - L to land"
                               : "BELOW: " + p.reason.ToUpperInvariant();
        }

        // ================================================================ GUI

        void OnGUI()
        {
            if (ship == null || streamer == null || FloatingOrigin.Instance == null || system == null) return;
            Styles();
            scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            W = Screen.width / scale;
            H = 1080f;

            var cam = rig != null ? rig.cam : Camera.main;
            if (cam != null && !showMap) DrawWorldMarkers(cam);

            DrawFlightPanel();
            DrawTargetBlock();
            DrawBottomBars();
            DrawStatusLeft();
            DrawWarnings();
            if (!showMap) DrawMiniMap();
            if (showMap) DrawSystemMap();
            if (showHelp) DrawHelp();
            if (showDebug) DrawDebug();
            if (showTestMenu) DrawTestMenu();
            if (GameManager.CurrentToast != null) DrawToast();
            if (Time.unscaledTime < routeBannerUntil) DrawRouteBanner();
        }

        // ---------------------------------------------------------- primitives

        void Fill(Rect r, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = p; }

        void Img(Texture t, Vector2 centre, float size, Color c)
        {
            var p = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(centre.x - size * 0.5f, centre.y - size * 0.5f, size, size), t);
            GUI.color = p;
        }

        void Line(Vector2 a, Vector2 b, float w, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            var m = GUI.matrix;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(ang, a * scale);
            Fill(new Rect(a.x, a.y - w * 0.5f, len, w), c);
            GUI.matrix = m;
        }

        void Arrow(Vector2 at, Vector2 dir, float size, Color c)
        {
            var m = GUI.matrix;
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(ang, at * scale);
            Img(tri, at, size, c);
            GUI.matrix = m;
        }

        void Text(Rect r, string s, GUIStyle st)
        {
            var c = st.normal.textColor;
            st.normal.textColor = new Color(0, 0, 0, 0.75f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        bool Project(Camera cam, Vector3 renderPos, out Vector2 gui, out Vector3 camLocal)
        {
            Vector3 sp = cam.WorldToScreenPoint(renderPos);
            gui = new Vector2(sp.x / scale, (Screen.height - sp.y) / scale);
            camLocal = cam.transform.InverseTransformPoint(renderPos);
            return sp.z > 0f && gui.x > 0 && gui.x < W && gui.y > 0 && gui.y < H;
        }

        static string Km(double m) => m >= 10000 ? (m / 1000.0).ToString("F0") + " km" : m >= 1000 ? (m / 1000.0).ToString("F1") + " km" : m.ToString("F0") + " m";

        static string Eta(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds > 5999) return "--:--";
            int s = (int)seconds;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // ------------------------------------------------------- world markers

        void DrawWorldMarkers(Camera cam)
        {
            var fo = FloatingOrigin.Instance;
            Vector3 shipR = ship.transform.position;

            // Other bodies (and the star) - name + distance, so the system is
            // readable at a glance. The target gets a bracket instead.
            foreach (var b in system.bodies)
            {
                if (b == streamer.TargetBody) continue;
                double dist = (b.LogicalPos - ship.LogicalPosition).magnitude;
                if (b != ship.SoiBody || dist - b.radius > 8000.0)
                {
                    if (Project(cam, fo.ToRender(b.LogicalPos), out var p, out _))
                    {
                        Img(dot, p, 7f, b.kind == BodyKind.Star ? new Color(1f, 0.85f, 0.5f) : BodyTint(b));
                        Text(new Rect(p.x + 8, p.y - 9, 220, 20), b.displayName + "  <size=12>" + Km(dist - b.radius) + "</size>", sSmall);
                    }
                }
            }

            // Beacons.
            if (beacons != null)
            {
                foreach (var bc in beacons.beacons)
                {
                    DVec3 lp = new DVec3(bc.x, bc.y, bc.z);
                    double d = (lp - ship.LogicalPosition).magnitude;
                    bool sel = beacons.Selected == bc;
                    if (Project(cam, fo.ToRender(lp), out var p, out var local))
                    {
                        Color c = bc.kind == "discovery" ? new Color(1f, 0.8f, 0.3f) : new Color(0.4f, 1f, 0.55f);
                        Img(ring, p, sel ? 22f : 14f, c);
                        Text(new Rect(p.x + 12, p.y - 9, 240, 20), bc.label + "  " + Km(d), sSmall);
                    }
                    else if (sel) EdgeArrow(local, bc.label + " " + Km(d), new Color(0.4f, 1f, 0.55f));
                }
            }

            // Target.
            var tb = streamer.TargetBody;
            // Inside the target's own air the "planet" is simply the ground below:
            // no bracket or edge arrow pointing at its centre (the top panel says ARRIVED).
            bool arrivedAtTarget = tb != null && ship.SoiBody == tb && ship.AltitudeDatum < tb.atmosphereHeight + 200.0;
            if (tb != null && !arrivedAtTarget)
            {
                DVec3 to = tb.LogicalPos - ship.LogicalPosition;
                double dist = to.magnitude;
                double surf = System.Math.Max(0.0, dist - tb.radius);
                double closing = DVec3.Dot(ship.LogicalVelocity, to / dist);
                string eta = closing > 5.0 ? Eta(surf / closing) : "--:--";
                if (Project(cam, fo.ToRender(tb.LogicalPos), out var p, out var local))
                {
                    float angR = Mathf.Atan((float)(tb.radius / dist)) * Mathf.Rad2Deg;
                    float px = Mathf.Clamp(angR / cam.fieldOfView * H * 2f, 26f, H * 0.8f);
                    Bracket(p, px, cTarget);
                    Text(new Rect(p.x - 150, p.y + px * 0.5f + 4, 300, 22), "<b>" + tb.displayName.ToUpperInvariant() + "</b>", sCenter);
                    Text(new Rect(p.x - 150, p.y + px * 0.5f + 24, 300, 20), Km(surf) + "   ETA " + eta, sCenterSmall);
                }
                else EdgeArrow(local, tb.displayName.ToUpperInvariant() + "  " + Km(surf), cTarget);
            }

            // Velocity (prograde) marker.
            if (ship.Speed > 2f)
            {
                Vector3 v = ship.LogicalVelocity.ToVector3().normalized;
                if (Project(cam, cam.transform.position + v * 2000f, out var p, out _))
                {
                    Img(ring, p, 20f, cHud);
                    Line(p + new Vector2(0, -10), p + new Vector2(0, -17), 2f, cHud);
                    Line(p + new Vector2(-10, 0), p + new Vector2(-17, 0), 2f, cHud);
                    Line(p + new Vector2(10, 0), p + new Vector2(17, 0), 2f, cHud);
                }
            }

            // Nose marker + virtual stick.
            Vector3 fwd = ship.transform.forward;
            if (Project(cam, shipR + fwd * 2000f, out var n, out _))
            {
                Line(n + new Vector2(-16, 0), n + new Vector2(-6, 0), 2f, Color.white);
                Line(n + new Vector2(6, 0), n + new Vector2(16, 0), 2f, Color.white);
                Line(n + new Vector2(0, 6), n + new Vector2(0, 12), 2f, Color.white);
                if (input != null && rig != null && rig.view == CameraRig.View.External)
                {
                    Img(ring, n, 96f, new Color(1f, 1f, 1f, 0.18f));
                    Vector2 st = input.Stick;
                    Img(dot, n + new Vector2(st.x, -st.y) * 46f, 8f, new Color(1f, 1f, 1f, 0.8f));
                }
            }
        }

        void Bracket(Vector2 c, float size, Color col)
        {
            float h = size * 0.5f, k = Mathf.Max(8f, size * 0.22f);
            Line(new Vector2(c.x - h, c.y - h), new Vector2(c.x - h + k, c.y - h), 2f, col);
            Line(new Vector2(c.x - h, c.y - h), new Vector2(c.x - h, c.y - h + k), 2f, col);
            Line(new Vector2(c.x + h, c.y - h), new Vector2(c.x + h - k, c.y - h), 2f, col);
            Line(new Vector2(c.x + h, c.y - h), new Vector2(c.x + h, c.y - h + k), 2f, col);
            Line(new Vector2(c.x - h, c.y + h), new Vector2(c.x - h + k, c.y + h), 2f, col);
            Line(new Vector2(c.x - h, c.y + h), new Vector2(c.x - h, c.y + h - k), 2f, col);
            Line(new Vector2(c.x + h, c.y + h), new Vector2(c.x + h - k, c.y + h), 2f, col);
            Line(new Vector2(c.x + h, c.y + h), new Vector2(c.x + h, c.y + h - k), 2f, col);
        }

        void EdgeArrow(Vector3 camLocal, string label, Color c)
        {
            Vector2 d = new Vector2(camLocal.x, -camLocal.y);
            if (d.sqrMagnitude < 1e-6f) d = new Vector2(0, 1);
            d.Normalize();
            Vector2 centre = new Vector2(W * 0.5f, H * 0.5f);
            float rx = W * 0.5f - 70f, ry = H * 0.5f - 70f;
            float t = Mathf.Min(rx / Mathf.Max(Mathf.Abs(d.x), 1e-4f), ry / Mathf.Max(Mathf.Abs(d.y), 1e-4f));
            Vector2 p = centre + d * t;
            Arrow(p, d, 26f, c);
            Text(new Rect(p.x - d.x * 40 - 120, p.y - d.y * 34 - 10, 240, 20), label, sCenterSmall);
        }

        static Color BodyTint(BodyDef b)
        {
            switch (b.kind)
            {
                case BodyKind.Rocky: return new Color(0.95f, 0.55f, 0.32f);
                case BodyKind.Ocean: return new Color(0.40f, 0.75f, 1f);
                case BodyKind.Ice: return new Color(0.80f, 0.92f, 1f);
                case BodyKind.Star: return new Color(1f, 0.85f, 0.45f);
                default: return Color.white;
            }
        }

        // ------------------------------------------------------------ panels

        void DrawFlightPanel()
        {
            var body = ship.SoiBody;
            Fill(new Rect(14, 14, 372, 158), cPanel);
            string place = body == null ? "DEEP SPACE"
                         : ship.AltitudeDatum < body.atmosphereHeight ? body.displayName.ToUpperInvariant()
                         : "NEAR " + body.displayName.ToUpperInvariant();
            Text(new Rect(26, 18, 300, 30), place, sBig);
            float set = ship.inThrottle;
            string alt = body != null && body.landable ? Km(ship.AltitudeAGL) + " AGL" : "-";
            Text(new Rect(26, 50, 300, 22), "ALT  <b>" + alt + "</b>    V/S " + ship.VerticalSpeed.ToString("+0;-0") + " m/s", sLabel);
            Text(new Rect(26, 72, 300, 22), "SPD  <b>" + ship.Speed.ToString("N0") + " m/s</b>    SET " + (set * 100f).ToString("F0") + "%", sLabel);
            Text(new Rect(26, 94, 356, 22), "MODE <b>" + ship.ModeLabel + "</b>   ASSIST " + (ship.flightAssist ? "ON" : "<color=#ffb070>OFF</color>"), sLabel);
            Text(new Rect(26, 116, 300, 22), "GRAV " + ship.CurrentGravity.ToString("F1") + " m/s²    AIR " + (ship.AirDensity * 100f).ToString("F0") + "%", sLabel);
            Text(new Rect(26, 140, 300, 20), "F1 controls   M map   F3 telemetry", sSmall);
        }

        void DrawTargetBlock()
        {
            var tb = streamer.TargetBody;
            if (tb == null) return;
            DVec3 to = tb.LogicalPos - ship.LogicalPosition;
            double dist = to.magnitude, surf = dist - tb.radius;
            double closing = DVec3.Dot(ship.LogicalVelocity, to / dist);
            double ang = ship.AngleToTarget();
            // Inside the target's air the planet centre is "below", so the
            // course readout would only say TURN 90; report arrival instead.
            bool arrived = ship.SoiBody == tb && ship.AltitudeDatum < tb.atmosphereHeight + 200.0;
            Fill(new Rect(W * 0.5f - 210, 14, 420, 52), cPanel);
            if (arrived)
            {
                Text(new Rect(W * 0.5f - 200, 16, 400, 24), "TARGET <b>" + tb.displayName.ToUpperInvariant() + "</b>   ALT " + Km(ship.AltitudeAGL), sCenter);
                Text(new Rect(W * 0.5f - 200, 40, 400, 22), "<color=#8cff9c>" + (ship.Landed ? "LANDED" : "ARRIVED") + "</color>    L land  ·  TAB next target", sCenterSmall);
                return;
            }
            string align = ang <= PGConst.PulseAlignCone ? "<color=#8cff9c>ON COURSE</color>" : "<color=#ffcf70>TURN " + ang.ToString("F0") + " deg</color>";
            Text(new Rect(W * 0.5f - 200, 16, 400, 24), "TARGET <b>" + tb.displayName.ToUpperInvariant() + "</b>   " + Km(surf) + "   ETA " + (closing > 5 ? Eta(surf / closing) : "--:--"), sCenter);
            Text(new Rect(W * 0.5f - 200, 40, 400, 22), align + "    TAB next target  ·  J pulse", sCenterSmall);
        }

        void DrawBottomBars()
        {
            float cx = W * 0.5f, y = H - 92;
            Fill(new Rect(cx - 220, y, 440, 78), cPanel);

            // Throttle set point and actual speed relative to the mode maximum.
            float max = ship.PulseEngaged ? PGConst.SpeedPulse : (ship.AirDensity > 0f ? PGConst.SpeedAtmoBoost : PGConst.SpeedOrbitMax * 1.7f);
            Text(new Rect(cx - 210, y + 4, 80, 20), "THROTTLE", sSmall);
            Fill(new Rect(cx - 130, y + 9, 300, 10), new Color(1, 1, 1, 0.12f));
            float t = Mathf.Clamp(ship.inThrottle, -0.3f, 1f);
            if (t >= 0) Fill(new Rect(cx - 130, y + 9, 300 * t, 10), cHud);
            else Fill(new Rect(cx - 130 + 300 * (1 + t) - 300, y + 9, -300 * t, 10), cWarn);
            float spd = Mathf.Clamp01(ship.Speed / max);
            Fill(new Rect(cx - 130 + 300 * spd - 1, y + 5, 3, 18), Color.white);
            Text(new Rect(cx + 176, y + 2, 60, 20), ship.Speed.ToString("N0"), sSmall);

            // Pulse drive.
            Text(new Rect(cx - 210, y + 28, 80, 20), "PULSE", sSmall);
            Fill(new Rect(cx - 130, y + 33, 300, 10), new Color(1, 1, 1, 0.12f));
            Fill(new Rect(cx - 130, y + 33, 300 * (ship.PulseEngaged ? 1f : ship.PulseCharge), 10), new Color(0.55f, 0.75f, 1f));
            string ps = ship.PulseStatus;
            if (string.IsNullOrEmpty(ps)) ps = ship.PulseEngaged ? "ENGAGED" : "READY - align nose with target, press J";
            Text(new Rect(cx - 210, y + 50, 420, 22), ps, sCenterSmall);
        }

        void DrawStatusLeft()
        {
            float y = H - 112;
            Fill(new Rect(14, y, 420, 98), cPanel);
            Text(new Rect(26, y + 6, 400, 22), "HULL <b>" + (ship.HullIntegrity * 100f).ToString("F0") + "%</b>    GEAR <b>"
                + (ship.GearDown ? "DOWN" : "UP") + "</b>    CAM " + (rig != null ? rig.view.ToString().ToUpperInvariant() : "-"), sLabel);
            string ls = ship.LandStatus;
            if (!string.IsNullOrEmpty(ls))
                Text(new Rect(26, y + 30, 400, 22), ls, ls.Contains("CANNOT") || ls.Contains("TOO") || ls.Contains("UNSAFE") || ls.Contains("CRITICAL") ? sWarnLeft : sOk);
            if (!string.IsNullOrEmpty(landingLine))
                Text(new Rect(26, y + 52, 400, 22), landingLine, landingOk ? sOk : sWarnLeft);
            if (!string.IsNullOrEmpty(ship.LastImpactNote))
                Text(new Rect(26, y + 74, 400, 20), ship.LastImpactNote, sSmall);
        }

        GUIStyle _warnLeft;
        GUIStyle sWarnLeft
        {
            get
            {
                if (_warnLeft == null) { _warnLeft = new GUIStyle(sLabel) { fontStyle = FontStyle.Bold }; _warnLeft.normal.textColor = new Color(1f, 0.7f, 0.4f); }
                return _warnLeft;
            }
        }

        void DrawWarnings()
        {
            string msg = null;
            if (ship.StarWarning) msg = "STAR PROXIMITY - HULL HEATING - TURN AWAY";
            else if (ship.CollisionWarning && !(ship.PulseEngaged && ship.TimeToImpact > 2.5f))
                msg = "TERRAIN - " + (-ship.VerticalSpeed).ToString("F0") + " m/s DOWN - IMPACT " + ship.TimeToImpact.ToString("F1") + " s  (R up / Space brake)";
            if (msg == null) return;
            float a = 0.55f + 0.35f * Mathf.Sin(Time.unscaledTime * 10f);
            Fill(new Rect(W * 0.5f - 300, 74, 600, 34), new Color(0.45f, 0.06f, 0.03f, a));
            Text(new Rect(W * 0.5f - 300, 74, 600, 34), msg, sWarn);
        }

        // --------------------------------------------------------------- maps

        void DrawMiniMap()
        {
            float s = 230f, x = W - s - 14, y = 14;
            Fill(new Rect(x, y, s, s + 22), cPanel);
            Text(new Rect(x + 8, y + 2, s, 20), "SYSTEM  (M)", sSmall);
            DrawMapContent(new Rect(x + 6, y + 20, s - 12, s - 12), false);
        }

        void DrawSystemMap()
        {
            float s = Mathf.Min(H - 160f, 820f);
            var r = new Rect(W * 0.5f - s * 0.5f, 80, s, s);
            Fill(new Rect(r.x - 12, r.y - 40, r.width + 24, r.height + 52), new Color(0.01f, 0.02f, 0.04f, 0.88f));
            Text(new Rect(r.x, r.y - 34, r.width, 26), "<b>" + system.systemName.ToUpperInvariant() + " SYSTEM MAP</b>  -  top view, Helion at centre  (M to close, TAB to change target)", sLabel);
            DrawMapContent(r, true);
        }

        void DrawMapContent(Rect r, bool big)
        {
            Vector2 c = r.center;
            double extent = 0;
            foreach (var b in system.bodies) extent = System.Math.Max(extent, b.LogicalPos.magnitude + b.radius);
            extent = System.Math.Max(extent, new Vector2((float)ship.LogicalPosition.x, (float)ship.LogicalPosition.z).magnitude) * 1.1;
            float k = (float)(r.width * 0.5 / extent);
            Vector2 P(DVec3 p) => c + new Vector2((float)p.x * k, -(float)p.z * k);

            // Orbits (the planets are stationary; rings show their distance from Helion).
            foreach (var b in system.bodies)
            {
                if (b.kind == BodyKind.Star) continue;
                float rr = new Vector2((float)b.LogicalPos.x, (float)b.LogicalPos.z).magnitude * k;
                Img(ring, c, rr * 2f * (64f / 56f), new Color(1, 1, 1, big ? 0.12f : 0.08f));
            }

            var tb = streamer.TargetBody;
            Vector2 sp = P(ship.LogicalPosition);
            if (tb != null) Line(sp, P(tb.LogicalPos), big ? 2f : 1.5f, new Color(cTarget.r, cTarget.g, cTarget.b, 0.6f));

            foreach (var b in system.bodies)
            {
                Vector2 p = P(b.LogicalPos);
                float size = b.kind == BodyKind.Star ? (big ? 26f : 12f) : Mathf.Max(big ? 12f : 7f, (float)b.radius * k * 2f);
                Img(dot, p, size, BodyTint(b));
                if (b == tb) Img(ring, p, size + 12f, cTarget);
                double d = (b.LogicalPos - ship.LogicalPosition).magnitude - b.radius;
                string label = big ? b.displayName + "\n" + Km(d) + (streamer.Discovered.Contains(b.id) ? "  [scanned]" : "")
                                   : b.displayName;
                Text(new Rect(p.x + size * 0.5f + 3, p.y - (big ? 18 : 8), 200, big ? 40 : 18), label, big ? sLabel : sSmall);
            }

            // Ship with heading.
            Vector3 f = ship.Orientation * Vector3.forward;
            Vector2 hd = new Vector2(f.x, -f.z);
            if (hd.sqrMagnitude < 1e-4f) hd = new Vector2(0, -1);
            Arrow(sp, hd.normalized, big ? 18f : 12f, Color.white);

            if (beacons != null)
                foreach (var bc in beacons.beacons)
                    Img(ring, P(new DVec3(bc.x, bc.y, bc.z)), big ? 10f : 6f, new Color(0.4f, 1f, 0.55f));
        }

        // ------------------------------------------------------- help / debug

        void DrawHelp()
        {
            int n = ShipInput.KeyMap.GetLength(0);
            int half = (n + 1) / 2;
            float pw = 760, ph = 40 + half * 22;
            float x = W * 0.5f - pw * 0.5f, y = H * 0.5f - ph * 0.5f;
            Fill(new Rect(x, y, pw, ph), new Color(0.02f, 0.03f, 0.06f, 0.86f));
            Text(new Rect(x + 14, y + 6, pw, 24), "CONTROLS  (F1 to close)", sTitle);
            for (int i = 0; i < n; i++)
            {
                float cx = x + 14 + (i < half ? 0 : pw * 0.5f);
                float cy = y + 34 + (i % half) * 22;
                Text(new Rect(cx, cy, 120, 22), ShipInput.KeyMap[i, 0], sKey);
                Text(new Rect(cx + 118, cy, pw * 0.5f - 130, 22), ShipInput.KeyMap[i, 1], sLabel);
            }
        }

        FloraField flora;

        void DrawDebug()
        {
            float pw = 330, ph = 384, x = W - pw - 14, y = 280;
            if (flora == null) flora = FindAnyObjectByType<FloraField>();
            Fill(new Rect(x, y, pw, ph), new Color(0.02f, 0.03f, 0.06f, 0.78f));
            GUILayout.BeginArea(new Rect(x + 12, y + 8, pw - 24, ph - 16));
            GUILayout.Label("TELEMETRY  (F3)", sTitle);
            GUILayout.Label("FPS " + game.Fps.ToString("F0") + "  avg " + game.FrameMsAvg.ToString("F2") + " ms  max(120) " + game.FrameMsMax.ToString("F1") + " ms", sSmall);
            GUILayout.Label("Session: " + game.SessionAvgFps.ToString("F0") + " fps avg, worst " + game.SessionWorstMs.ToString("F1") + " ms, >33ms: " + game.LongFrames33, sSmall);
            GUILayout.Label("Patches drawn " + streamer.TotalActivePatches + " (peak " + game.PeakPatches + ")", sSmall);
            GUILayout.Label("Nodes " + streamer.TotalLiveNodes + " (peak " + game.PeakNodes + ")   pending " + streamer.TotalPendingBuilds + " (peak " + game.PeakPending + ")", sSmall);
            GUILayout.Label("Meshes alloc " + streamer.TotalMeshesAllocated + "   objects " + streamer.TotalObjectsAllocated, sSmall);
            GUILayout.Label("Colliders " + streamer.TotalColliders + "   stale jobs rejected " + streamer.TotalStaleRejected, sSmall);
            if (flora != null) GUILayout.Label("Ground props " + flora.InstancesDrawn + " in " + flora.ActiveCells + " cells (" + flora.BodyId + "), generating " + flora.PendingCells, sSmall);
            GUILayout.Label("Managed " + (game.ManagedMemoryBytes >> 20) + " MB (peak " + (game.PeakManagedBytes >> 20) + ")   GPU " + (game.GraphicsMemoryBytes >> 20) + " MB", sSmall);
            GUILayout.Label("Origin " + game.CoordinateMode + "   teleports " + ShipController.TeleportCount + "   loads " + (save != null ? save.LoadCount : 0) + "   scene loads " + GameManager.SceneLoadsSinceStart, sSmall);
            GUILayout.Label("Pos " + ship.LogicalPosition, sSmall);
            GUILayout.Label("Vel " + ship.LogicalVelocity + "  |" + ship.Speed.ToString("F1") + "|", sSmall);
            GUILayout.Label("Max step " + ship.MaxStepDisplacement.ToString("F2") + " m (ratio " + ship.MaxStepRatio.ToString("F2") + ")", sSmall);
            GUILayout.Label("Save: " + (save != null ? save.LastSavePath : "-"), sSmall);
            if (save != null && !string.IsNullOrEmpty(save.LastStatus)) GUILayout.Label(save.LastStatus, sSmall);
            GUILayout.EndArea();
        }

        void DrawTestMenu()
        {
            float pw = 470, ph = 360;
            float x = W * 0.5f - pw * 0.5f, y = H * 0.5f - ph * 0.5f;
            Fill(new Rect(x, y, pw, ph), new Color(0.03f, 0.04f, 0.08f, 0.94f));
            GUILayout.BeginArea(new Rect(x + 16, y + 12, pw - 32, ph - 24));
            GUILayout.Label("TEST MENU  (F4 to close)", sTitle);
            GUILayout.Label("Teleport buttons are DEBUG TOOLS, counted and bannered. Normal travel is flying.", sSmall);
            GUILayout.Space(6);
            if (GUILayout.Button("DEBUG TELEPORT: start of demonstration route")) game.DebugJumpToRouteStart();
            GUILayout.Label("DEBUG TELEPORT: 700 m above a landing area", sSmall);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Tarn-Veth")) game.DebugJumpToSurface("tarnveth", 0);
            if (GUILayout.Button("Mirvalis")) game.DebugJumpToSurface("mirvalis", 1);
            if (GUILayout.Button("Kryosyne")) game.DebugJumpToSurface("kryosyne", 2);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Mirvalis A")) game.DebugJumpToSurface("mirvalis", 0);
            if (GUILayout.Button("B")) game.DebugJumpToSurface("mirvalis", 1);
            if (GUILayout.Button("C")) game.DebugJumpToSurface("mirvalis", 2);
            if (GUILayout.Button("D")) game.DebugJumpToSurface("mirvalis", 3);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save (F5)")) save.Save();
            if (GUILayout.Button("Load (F9)")) save.Load();
            if (GUILayout.Button("Reset test save")) game.DebugResetSave();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("DEBUG: recover ship (teleport to flat ground)")) ship.RecoverFromCrash();
            GUILayout.BeginHorizontal();
            showDebug = GUILayout.Toggle(showDebug, " telemetry (F3)");
            showHelp = GUILayout.Toggle(showHelp, " help (F1)");
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawToast()
        {
            float w = 560, h = 56;
            Fill(new Rect(W * 0.5f - w * 0.5f, H - 200, w, h), new Color(0.03f, 0.05f, 0.09f, 0.78f));
            Text(new Rect(W * 0.5f - w * 0.5f + 10, H - 196, w - 20, h - 8), GameManager.CurrentToast, sCenter);
        }

        void DrawRouteBanner()
        {
            Fill(new Rect(W * 0.5f - 300, H * 0.5f - 150, 600, 40), new Color(0.55f, 0.25f, 0.02f, 0.85f));
            Text(new Rect(W * 0.5f - 300, H * 0.5f - 150, 600, 40), "DEBUG: " + routeBannerText, sCenter);
        }

        public void ShowDebugRouteBanner(float seconds)
        {
            routeBannerUntil = Time.unscaledTime + seconds;
            routeBannerText = "TELEPORT USED - debug shortcut, not normal travel";
        }
    }
}
