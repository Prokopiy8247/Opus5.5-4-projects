using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PG
{
    /// <summary>
    /// Ties the systems together and owns the frame order:
    ///   streamer tick -> floating origin tick -> gameplay keys -> telemetry.
    ///
    /// Also owns the labelled debug tools of the test menu (F4). They are the
    /// only code that relocates the ship, they go through
    /// ShipController.TeleportLogical (which counts them) and show a banner.
    /// Normal travel never calls them.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }
        public static void ResetInstance() { Instance = null; }

        public PlanetSystem system;
        public WorldStreamer streamer;
        public ShipController ship;
        public CameraRig rig;
        public ShipInput input;
        public SaveSystem save;
        public BeaconRegistry beacons;
        public PropScatter props;
        public AtmosphereSystem atmosphere;
        public PGGlobalLighting lighting;
        public HUD hud;

        [Header("Start state")]
        public string startBodyId = "mirvalis";
        public string firstTargetId = "tarnveth";
        public float startAltitude = 6500f;

        // ---- measured telemetry ---------------------------------------------
        public float Fps { get; private set; }
        public float FrameMsAvg { get; private set; }
        public float FrameMsMax { get; private set; }
        public long ManagedMemoryBytes { get; private set; }
        public long GraphicsMemoryBytes { get; private set; }

        public double SessionFrames { get; private set; }
        public double SessionSeconds { get; private set; }
        public float SessionWorstMs { get; private set; }
        public int LongFrames33 { get; private set; }
        public int PeakPatches { get; private set; }
        public int PeakPending { get; private set; }
        public int PeakNodes { get; private set; }
        public int PeakMeshes { get; private set; }
        public int PeakObjects { get; private set; }
        public int PeakColliders { get; private set; }
        public long PeakManagedBytes { get; private set; }
        public static int SceneLoadsSinceStart { get; private set; }
        /// <summary>Frames spent encoding test screenshots (excluded from the frame-time stats, reported separately).</summary>
        public int CaptureFramesExcluded { get; private set; }
        int perfSkipUntilFrame = -1;
        public void SuppressPerfFrames(int frames) { perfSkipUntilFrame = Mathf.Max(perfSkipUntilFrame, Time.frameCount + frames); }

        public string CoordinateMode => FloatingOrigin.Instance != null && FloatingOrigin.Instance.ShiftCount > 0
            ? "SHIFTED x" + FloatingOrigin.Instance.ShiftCount
            : "PRIMARY";

        readonly float[] frameSamples = new float[120];
        int frameIdx;
        float fpsTimer;
        int fpsFrames;
        bool started;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) { SceneLoadsSinceStart++; }

        void Start()
        {
            Time.fixedDeltaTime = 1f / 60f;
            bool noVsync = Array.IndexOf(Environment.GetCommandLineArgs(), "-pgNoVsync") >= 0;
            QualitySettings.vSyncCount = noVsync ? 0 : 1;
            Application.targetFrameRate = noVsync ? -1 : 240;

            if (system == null) system = FindAnyObjectByType<PlanetSystem>();
            streamer.system = system;

            bool alreadyUp = system.bodies.Count > 1 && streamer.RuntimeOf(system.bodies[1]) != null;
            if (!alreadyUp)
            {
                streamer.Init();
                if (props != null) props.Build();
                PlaceShipAtStart();
            }
            if (rig != null) rig.SnapImmediate();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            SceneLoadsSinceStart = 0;
            started = true;

            // Automation hooks, all opt-in from the command line.
            string savePath = RoutePilot.CommandLineValue("-pgSave");
            if (!string.IsNullOrEmpty(savePath)) SaveSystem.PathOverride = savePath;
            probePath = RoutePilot.CommandLineValue("-pgTelemetry");
            if (RoutePilot.CommandLineWants("-pgRoute")) StartRoutePilot(RoutePilot.CommandLineValue("-pgShots"), RoutePilot.CommandLineValue("-pgLog"), RoutePilot.CommandLineWants("-pgQuit"));
        }

        /// <summary>Starts the automated input-level test pilot (see RoutePilot). Never used in normal play.</summary>
        public RoutePilot StartRoutePilot(string shots, string log, bool quit)
        {
            var rp = gameObject.AddComponent<RoutePilot>();
            rp.game = this; rp.ship = ship; rp.input = input; rp.streamer = streamer; rp.save = save;
            rp.beacons = beacons; rp.rig = rig; rp.hud = hud;
            rp.shotDir = shots; rp.logPath = log; rp.quitWhenDone = quit;
            if (!string.IsNullOrEmpty(shots)) System.IO.Directory.CreateDirectory(shots);
            return rp;
        }

        const string HEADER = "t,fx,fy,fz,vx,vy,vz,inYaw,inPitch,throttle,stickX,stickY,yawErrDeg,pitchErrDeg,angleToTarget,distKm,altAgl,speed,vspeed,mode,land,landStatus,teleports,lock,focus,rawMouseX,rawMouseY,mouseFrames,cursorX,cursorY\n";
        string probePath;
        float probeTimer;
        bool probeStarted;
        readonly System.Text.StringBuilder probe = new System.Text.StringBuilder();

        /// <summary>
        /// With -pgTelemetry FILE: every 0.2 s the ship's heading, velocity
        /// direction and inputs are appended, so an external OS-level input test
        /// can verify that real keyboard/mouse events turned the ship.
        /// </summary>
        void WriteProbe()
        {
            if (string.IsNullOrEmpty(probePath)) return;
            probeTimer -= Time.unscaledDeltaTime;
            if (probeTimer > 0f) return;
            probeTimer = 0.2f;
            Vector3 f = ship.Orientation * Vector3.forward;
            Vector3 v = ship.LogicalVelocity.ToVector3();
            DVec3 tgt = streamer.TargetBody != null ? (streamer.TargetBody.LogicalPos - ship.LogicalPosition).normalized : DVec3.From(f);
            var camRig = CameraRig.Instance;
            Vector3 tLocal = camRig != null ? Quaternion.Inverse(camRig.transform.rotation) * tgt.ToVector3() : Vector3.forward;
            double yawErr = Mathf.Atan2(tLocal.x, tLocal.z) * Mathf.Rad2Deg;
            double pitchErr = Mathf.Atan2(tLocal.y, new Vector2(tLocal.x, tLocal.z).magnitude) * Mathf.Rad2Deg;
            // Machine-readable files use the invariant culture: with a locale that
            // writes a decimal comma (ru-RU) the CSV columns and the probe parser
            // would both break.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            probe.Append(Time.time.ToString("F2", inv)).Append(',')
                 .Append(f.x.ToString("F4", inv)).Append(',').Append(f.y.ToString("F4", inv)).Append(',').Append(f.z.ToString("F4", inv)).Append(',')
                 .Append(v.x.ToString("F1", inv)).Append(',').Append(v.y.ToString("F1", inv)).Append(',').Append(v.z.ToString("F1", inv)).Append(',')
                 .Append(ship.inYaw.ToString("F2", inv)).Append(',').Append(ship.inPitch.ToString("F2", inv)).Append(',')
                 .Append(ship.inThrottle.ToString("F2", inv)).Append(',')
                 .Append((input != null ? input.Stick.x.ToString("F2", inv) + "," + input.Stick.y.ToString("F2", inv) : "0,0")).Append(',')
                 .Append(yawErr.ToString("F1", inv)).Append(',').Append(pitchErr.ToString("F1", inv)).Append(',')
                 .Append(ship.AngleToTarget().ToString("F1", inv)).Append(',')
                 .Append((ship.DistanceToTargetSurface() / 1000.0).ToString("F2", inv)).Append(',')
                 .Append(ship.AltitudeAGL.ToString("F0", inv)).Append(',')
                 .Append(ship.Speed.ToString("F0", inv)).Append(',')
                 .Append(ship.VerticalSpeed.ToString("F0", inv)).Append(',')
                 .Append(ship.ModeLabel.Replace(' ', '_')).Append(',')
                 .Append(ship.Landed ? "LANDED" : ship.Assisting ? "ASSIST" : "-").Append(',')
                 .Append(ship.LandStatus).Append(',')
                 .Append(ShipController.TeleportCount.ToString(inv)).Append(',')
                 .Append(Cursor.lockState.ToString()).Append(',')
                 .Append(Application.isFocused ? "1" : "0").Append(',')
                 .Append(input != null ? input.RawMouseSinceProbe.x.ToString("F2", inv) : "0").Append(',')
                 .Append(input != null ? input.RawMouseSinceProbe.y.ToString("F2", inv) : "0").Append(',')
                 .Append(input != null ? input.MouseFramesSinceProbe.ToString(inv) : "0").Append(',')
                 .Append(Input.mousePosition.x.ToString("F0", inv)).Append(',')
                 .Append(Input.mousePosition.y.ToString("F0", inv));
            probe.Append((char)10);
            if (input != null) { input.RawMouseSinceProbe = Vector2.zero; input.MouseFramesSinceProbe = 0; }
            // Append only the new line: rewriting the whole history every 0.2 s
            // let a reader catch a truncated file whose last complete line was an
            // old sample (the external test then acted on stale state).
            try
            {
                if (!probeStarted) { System.IO.File.WriteAllText(probePath, HEADER); probeStarted = true; }
                System.IO.File.AppendAllText(probePath, probe.ToString());
            }
            catch { }
            probe.Clear();
        }

        /// <summary>
        /// Initial spawn: in space on the sunward side of Mirvalis, nose on the
        /// first target, so Helion, a planet below and the target marker are all
        /// in view on the first frame.
        /// </summary>
        public void PlaceShipAtStart()
        {
            var home = system.Get(startBodyId) ?? system.bodies[1];
            var target = system.Get(firstTargetId) ?? home;
            DVec3 toStar = (system.Star.LogicalPos - home.LogicalPos).normalized;
            DVec3 side = DVec3.Cross(toStar, new DVec3(0, 1, 0)).normalized;
            DVec3 dir = (toStar * 0.8 + side * 0.45 + new DVec3(0, 0.38, 0)).normalized;
            DVec3 pos = home.LogicalPos + dir * (home.radius + startAltitude);

            Vector3 fwd = (target.LogicalPos - pos).normalized.ToVector3();
            Vector3 up = dir.ToVector3();
            ship.SpawnAt(pos, Quaternion.LookRotation(fwd, up));
            streamer.SetTarget(target);
            ship.LandStatus = "";
        }

        void Update()
        {
            if (FloatingOrigin.Instance == null || ship == null || streamer == null) return;
            DVec3 travelDir = ship.LogicalVelocity.sqrMagnitude > 1e-6
                ? ship.LogicalVelocity.normalized
                : DVec3.From(ship.Orientation * Vector3.forward);

            streamer.Tick(ship.LogicalPosition, travelDir, ship.Speed, Time.deltaTime);
            FloatingOrigin.Instance.Tick(ship.LogicalPosition);

            bool keys = input == null || input.acceptInput;
            if (keys)
            {
                if (Input.GetKeyDown(KeyCode.F5)) { save.Save(); Toast(save.LastStatus); }
                if (Input.GetKeyDown(KeyCode.F9)) { save.Load(); Toast(save.LastStatus); }
                if (Input.GetKeyDown(KeyCode.B)) DropBeacon();
                if (Input.GetKeyDown(KeyCode.N)) TargetNearestBeacon();
                if (Input.GetKeyDown(KeyCode.Tab)) CycleTarget();
                if (Input.GetKeyDown(KeyCode.H)) ScanAhead();
            }

            UpdateTelemetry();
            WriteProbe();
        }

        // -------------------------------------------------------------- actions

        public void CycleTarget()
        {
            var planets = new List<BodyDef>(system.Planets);
            int idx = planets.IndexOf(streamer.TargetBody);
            idx = (idx + 1) % planets.Count;
            SetTarget(planets[idx]);
        }

        public void SetTarget(BodyDef b)
        {
            streamer.SetTarget(b);
            if (beacons != null) beacons.Selected = null;
            Toast("Target: " + b.displayName);
            if (hud != null) hud.Blip();
        }

        public BeaconRegistry.Beacon DropBeacon()
        {
            var body = ship.SoiBody;
            var b = beacons.Add("Beacon " + (beacons.Count + 1), body, ship.LogicalPosition, "beacon",
                                body != null ? body.displayName : "space");
            Toast("Beacon dropped: " + b.label + (body != null ? " on " + body.displayName : ""));
            if (hud != null) hud.Blip();
            return b;
        }

        public void TargetNearestBeacon()
        {
            var near = beacons.Nearest(ship.LogicalPosition);
            if (near == null) { Toast("No beacons placed yet (B)"); return; }
            var body = system.Get(near.bodyId);
            if (body != null) streamer.SetTarget(body);
            beacons.Selected = near;
            Toast("Beacon targeted: " + near.label);
            if (hud != null) hud.Blip();
        }

        public void ScanAhead()
        {
            BodyDef target = null;
            double best = double.MaxValue;
            Vector3 fwd = ship.Orientation * Vector3.forward;
            foreach (var b in system.bodies)
            {
                if (b.kind == BodyKind.Star) continue;
                DVec3 poi = b.LogicalPos + new DVec3(b.poiLocalDirX, b.poiLocalDirY, b.poiLocalDirZ) * b.radius;
                DVec3 to = poi - ship.LogicalPosition;
                double d = to.magnitude;
                double align = DVec3.Dot(to / Math.Max(d, 1.0), DVec3.From(fwd));
                bool onPlanet = ship.SoiBody == b && d < 6000.0;
                if (align < 0.5 && !onPlanet) continue;
                if (d < best) { best = d; target = b; }
            }
            if (target == null) { Toast("Scan: nothing aligned ahead"); return; }
            bool fresh = streamer.Discovered.Add(target.id);
            Toast("SCAN: " + target.displayName + (fresh ? " - NEW DISCOVERY" : " (already logged)") +
                  "\n" + target.poiName + " - " + target.poiDesc);
            if (fresh)
            {
                DVec3 poi = target.LogicalPos + new DVec3(target.poiLocalDirX, target.poiLocalDirY, target.poiLocalDirZ) * target.radius;
                beacons.Add(target.poiName, target, poi, "discovery", target.poiDesc);
            }
            if (hud != null) hud.Blip();
        }

        // ------------------------------------------------------------ telemetry

        void UpdateTelemetry()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            frameSamples[frameIdx] = ms;
            frameIdx = (frameIdx + 1) % frameSamples.Length;
            fpsFrames++;
            fpsTimer += Time.unscaledDeltaTime;

            if (started && Time.frameCount <= perfSkipUntilFrame) CaptureFramesExcluded++;
            else if (started && Time.realtimeSinceStartup > 8f)
            {
                SessionFrames++;
                SessionSeconds += Time.unscaledDeltaTime;
                if (ms > SessionWorstMs) SessionWorstMs = ms;
                if (ms > 33.3f) LongFrames33++;
            }

            PeakPatches = Math.Max(PeakPatches, streamer.TotalActivePatches);
            PeakPending = Math.Max(PeakPending, streamer.TotalPendingBuilds);
            PeakNodes = Math.Max(PeakNodes, streamer.TotalLiveNodes);
            PeakMeshes = Math.Max(PeakMeshes, streamer.TotalMeshesAllocated);
            PeakObjects = Math.Max(PeakObjects, streamer.TotalObjectsAllocated);
            PeakColliders = Math.Max(PeakColliders, streamer.TotalColliders);

            if (fpsTimer >= 0.5f)
            {
                Fps = fpsFrames / fpsTimer;
                fpsFrames = 0; fpsTimer = 0f;
                float sum = 0f, mx = 0f;
                for (int i = 0; i < frameSamples.Length; i++) { sum += frameSamples[i]; if (frameSamples[i] > mx) mx = frameSamples[i]; }
                FrameMsAvg = sum / frameSamples.Length;
                FrameMsMax = mx;
                ManagedMemoryBytes = GC.GetTotalMemory(false);
                PeakManagedBytes = Math.Max(PeakManagedBytes, ManagedMemoryBytes);
                GraphicsMemoryBytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            }
        }

        public float SessionAvgFps => SessionSeconds > 0.0 ? (float)(SessionFrames / SessionSeconds) : 0f;

        // ----------------------------------------------------------- debug menu

        /// <summary>DEBUG: back to the start of the demonstration route. Counted as a teleport.</summary>
        public void DebugJumpToRouteStart()
        {
            hud.ShowDebugRouteBanner(2.5f);
            var home = system.Get(startBodyId) ?? system.bodies[1];
            DVec3 toStar = (system.Star.LogicalPos - home.LogicalPos).normalized;
            ship.TeleportLogical(home.LogicalPos + toStar * (home.radius + startAltitude), DVec3.zero);
            if (rig != null) rig.SnapImmediate();
            ship.HullIntegrity = 1f;
            ship.MotorDisabled = false;
            ship.LandStatus = "DEBUG: ROUTE START";
        }

        /// <summary>DEBUG: 700 m above a flat spot on a named body. Counted as a teleport.</summary>
        public void DebugJumpToSurface(string bodyId, int spotIndex)
        {
            var b = system.Get(bodyId);
            var pr = b != null ? streamer.RuntimeOf(b) : null;
            if (pr == null) return;
            DVec3[] prefer =
            {
                new DVec3(0.85, 0.35, 0.38).normalized,
                new DVec3(-0.62, 0.60, 0.50).normalized,
                new DVec3(0.22, -0.48, -0.85).normalized,
                new DVec3(-0.30, -0.72, 0.62).normalized,
            };
            DVec3 dir = TerrainFunc.FindFlatSpotOnLand(b, pr.Perm, prefer[Mathf.Abs(spotIndex) % prefer.Length], b.radius * 0.10, 200);
            DVec3 surf = b.LogicalPos + TerrainFunc.SurfacePoint(b, pr.Perm, dir);
            Vector3 up = dir.ToVector3();
            Vector3 fwd = Vector3.Cross(up, Vector3.right);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.Cross(up, Vector3.forward);
            ship.Orientation = Quaternion.LookRotation(fwd.normalized, up);
            ship.TeleportLogical(surf + dir * 700.0, DVec3.zero);
            streamer.SetTarget(b);
            if (rig != null) rig.SnapImmediate();
            hud.ShowDebugRouteBanner(2.0f);
            ship.LandStatus = "DEBUG: 700 m OVER " + b.displayName;
        }

        public void DebugResetSave()
        {
            save.DeleteSave();
            beacons.Clear();
            streamer.Discovered.Clear();
            Toast("Test save reset");
        }

        // ------------------------------------------------------------- toasts

        public static string CurrentToast { get; private set; }
        static float toastUntil;

        public static void Toast(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            CurrentToast = msg;
            toastUntil = Time.unscaledTime + 4f;
        }

        void LateUpdate()
        {
            if (Time.unscaledTime > toastUntil) CurrentToast = null;
        }
    }
}
