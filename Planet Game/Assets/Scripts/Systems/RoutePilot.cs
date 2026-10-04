using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Automated test pilot for the acceptance route, enabled only by the
    /// command-line flag -pgRoute (or by the long integration test).
    ///
    /// It flies exclusively through the ship's CONTROL INPUTS - the same fields
    /// the keyboard and mouse write (throttle, yaw, pitch, roll, lift, J, L, K,
    /// target selection, B, F5/F9 equivalents). It never calls a relocation
    /// API; ShipController.TeleportCount, scene loads and the per-step
    /// displacement bound are recorded and reported, so the run itself proves
    /// that travel was continuous flight.
    ///
    /// Route: fresh start in space -> turning check -> Tarn-Veth landing ->
    /// take-off -> Mirvalis landing on land near water -> take-off -> Kryosyne
    /// landing -> beacon + save/load on the surface -> take-off, fly away and
    /// return to the beacon -> landing.
    /// </summary>
    public class RoutePilot : MonoBehaviour
    {
        public GameManager game;
        public ShipController ship;
        public ShipInput input;
        public WorldStreamer streamer;
        public SaveSystem save;
        public BeaconRegistry beacons;
        public CameraRig rig;
        public HUD hud;

        public string shotDir;
        public string logPath;
        public bool quitWhenDone;
        public string[] targets = { "tarnveth", "mirvalis", "kryosyne" };
        public bool fullRoute = true;

        public string Phase { get; private set; } = "init";
        public bool Done { get; private set; }
        public bool Failed { get; private set; }
        public string FailReason { get; private set; } = "";
        public int CoverageFailures { get; private set; }
        public string FirstCoverageFailure { get; private set; } = "";
        public readonly List<string> Report = new List<string>();
        public readonly Dictionary<string, float> HopSeconds = new Dictionary<string, float>();

        // Desired control state, applied every fixed step.
        Vector3 steerDir;          // world-space direction to point the nose at (zero = none)
        float throttleCmd, liftCmd;
        bool brakeCmd, boostCmd;
        int teleportsAtStart, loadsAtStart;
        float t0;
        StringBuilder csv = new StringBuilder();
        float csvTimer;
        double hopStart;

        public static bool CommandLineWants(string flag) => Array.IndexOf(Environment.GetCommandLineArgs(), flag) >= 0;

        public static string CommandLineValue(string flag)
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, flag);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        void Start()
        {
            if (input != null) input.acceptInput = false;
            teleportsAtStart = ShipController.TeleportCount;
            loadsAtStart = save != null ? save.LoadCount : 0;
            t0 = Time.time;
            Log("render: screen " + Screen.width + "x" + Screen.height + ", render " + Screen.currentResolution.width + "x" + Screen.currentResolution.height +
                ", quality " + QualitySettings.names[QualitySettings.GetQualityLevel()] + ", msaa " + QualitySettings.antiAliasing);
            csv.AppendLine("t,phase,body,alt_agl,speed,mode,fps,frame_ms,worst_ms,patches,pending,nodes,meshes,objects,colliders,managed_mb,teleports,scene_loads,coverage_failures");
            StartCoroutine(Run());
        }

        /// <summary>Invariant formatting: the CSV must parse on any machine locale.</summary>
        static string N(double v, string fmt) => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);

        void Log(string s)
        {
            string line = "[ROUTE " + (Time.time - t0).ToString("F1") + "s] " + s;
            Report.Add(line);
            Debug.Log(line);
        }

        // ============================================================ control

        void FixedUpdate()
        {
            if (ship == null || Done) return;
            ship.inThrottle = throttleCmd;
            ship.inLift = liftCmd;
            ship.brake = brakeCmd;
            ship.boost = boostCmd;
            ship.inRoll = 0f;
            if (steerDir.sqrMagnitude > 0.5f)
            {
                Vector3 local = Quaternion.Inverse(ship.Orientation) * steerDir.normalized;
                float yaw = Mathf.Atan2(local.x, local.z);
                float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude);
                if (local.z < 0f && Mathf.Abs(yaw) > 2.6f) yaw = Mathf.Sign(yaw == 0f ? 1f : yaw) * Mathf.PI;
                ship.inYaw = Mathf.Clamp(yaw * 2.6f, -1f, 1f);
                ship.inPitch = Mathf.Clamp(pitch * 2.6f, -1f, 1f);
            }
            else { ship.inYaw = 0f; ship.inPitch = 0f; }
        }

        void Update()
        {
            if (Done || ship == null) return;
            // Every frame: the current body's quadtree must cover the sphere,
            // with no overlapping LODs and no orphaned patch objects.
            var pr = streamer.RuntimeOf(streamer.CurrentBody);
            if (pr != null && !pr.ValidateTree(out string err))
            {
                CoverageFailures++;
                if (string.IsNullOrEmpty(FirstCoverageFailure)) FirstCoverageFailure = err;
            }
            csvTimer -= Time.unscaledDeltaTime;
            if (csvTimer <= 0f)
            {
                csvTimer = 1f;
                csv.AppendLine(N(Time.time - t0, "F1") + "," + Phase + "," + (streamer.CurrentBody != null ? streamer.CurrentBody.id : "-") + "," +
                    N(ship.AltitudeAGL, "F0") + "," + N(ship.Speed, "F0") + "," + ship.ModeLabel.Replace(' ', '_') + "," +
                    N(game.Fps, "F0") + "," + N(game.FrameMsAvg, "F2") + "," + N(game.FrameMsMax, "F1") + "," +
                    streamer.TotalActivePatches + "," + streamer.TotalPendingBuilds + "," + streamer.TotalLiveNodes + "," + streamer.TotalMeshesAllocated + "," +
                    streamer.TotalObjectsAllocated + "," + streamer.TotalColliders + "," + (game.ManagedMemoryBytes >> 20) + "," +
                    (ShipController.TeleportCount - teleportsAtStart) + "," + GameManager.SceneLoadsSinceStart + "," + CoverageFailures);
            }
        }

        BodyDef Body(string id) => streamer.system.Get(id);
        Vector3 Up => (ship.LogicalPosition - (streamer.CurrentBody ?? streamer.system.Star).LogicalPos).normalized.ToVector3();
        Vector3 Forward => ship.Orientation * Vector3.forward;

        static float Angle(Vector3 a, Vector3 b) => Vector3.Angle(a, b);

        IEnumerator WaitFixed(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return new WaitForFixedUpdate();
        }

        IEnumerator Shot(string name, bool cockpit = false, bool map = false)
        {
            if (string.IsNullOrEmpty(shotDir)) yield break;
            var prevView = rig.view;
            if (cockpit) rig.view = CameraRig.View.Cockpit;
            if (map) hud.showMap = true;
            for (int i = 0; i < 6; i++) yield return null;
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(shotDir, name + ".png");
            game.SuppressPerfFrames(3);
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Log("screenshot " + name + " (" + Screen.width + "x" + Screen.height + ")");
            if (cockpit) rig.view = prevView;
            if (map) hud.showMap = false;
        }

        void Fail(string why)
        {
            Failed = true;
            FailReason = why;
            Log("FAIL: " + why);
        }

        // ============================================================== route

        IEnumerator Run()
        {
            Phase = "start";
            yield return WaitFixed(2.5f);
            Log("fresh start: body " + streamer.CurrentBody.displayName + ", alt " + ship.AltitudeAGL.ToString("F0") + " m, target " + streamer.TargetBody.displayName);
            yield return Shot("A01_space_start_sun_planets_navigation");
            yield return Shot("A02_system_map", map: true);
            yield return Shot("A03_cockpit_view_space", cockpit: true);

            yield return TurnCheck();
            if (Failed) { Finish(); yield break; }
            yield return FastEntry(streamer.CurrentBody ?? Body("mirvalis"));
            if (Failed) { Finish(); yield break; }

            for (int i = 0; i < targets.Length; i++)
            {
                var tb = Body(targets[i]);
                yield return FlyTo(tb, i);
                if (Failed) break;
                yield return LandOn(tb, i);
                if (Failed) break;
                if (!fullRoute) break;
                if (i < targets.Length - 1)
                {
                    yield return TakeOff(tb, i);
                    if (Failed) break;
                }
            }

            if (!Failed && fullRoute)
            {
                yield return SaveLoadAndBeacon();
            }
            Finish();
        }

        IEnumerator TurnCheck()
        {
            Phase = "turn_check";
            throttleCmd = 0.6f;
            yield return WaitFixed(6f);
            Vector3 f0 = Forward, v0 = ship.LogicalVelocity.ToVector3().normalized;
            Vector3 right = ship.Orientation * Vector3.right;
            steerDir = right;                              // ask for a 90 degree right turn
            float start = Time.time;
            while (Angle(Forward, right) > 4f && Time.time - start < 8f) yield return new WaitForFixedUpdate();
            yield return WaitFixed(2.0f);
            float noseTurn = Angle(f0, Forward), velTurn = Angle(v0, ship.LogicalVelocity.ToVector3().normalized);
            Log("turn check: nose turned " + noseTurn.ToString("F0") + " deg, velocity turned " + velTurn.ToString("F0") + " deg in " + (Time.time - start).ToString("F1") + " s");
            if (noseTurn < 75f || velTurn < 65f) Fail("turn check: the course did not follow the nose");
            yield return Shot("A04_after_90deg_turn_external");
            steerDir = Vector3.zero;
        }

        /// <summary>
        /// Fast atmosphere entry: dive into the current planet's air at full
        /// space speed with boost. The speed envelope must bring the ship down to
        /// the entry cap without a jump, the terrain must stay fully covered while
        /// the LOD refines at speed, then the ship pulls up and climbs out.
        /// </summary>
        IEnumerator FastEntry(BodyDef b)
        {
            Phase = "fast_entry_" + b.id;
            float t = Time.time;
            float maxSpeedAbove = 0f, speedAtEntry = -1f;
            bool shot = false;
            boostCmd = true; throttleCmd = 1f;
            while (Time.time - t < 90f)
            {
                DVec3 down = (b.LogicalPos - ship.LogicalPosition).normalized;
                Vector3 fwd = Forward;
                // 50 degree dive toward the planet, keeping the current heading.
                Vector3 horiz = Vector3.ProjectOnPlane(fwd, -down.ToVector3()).normalized;
                steerDir = (down.ToVector3() * 1.2f + horiz).normalized;
                if (ship.AirDensity <= 0.01f) maxSpeedAbove = Mathf.Max(maxSpeedAbove, ship.Speed);
                if (speedAtEntry < 0f && ship.AirDensity > 0.02f) speedAtEntry = ship.Speed;
                if (!shot && ship.AirDensity > 0.12f && ship.Speed > 250f) { shot = true; yield return Shot("C01_fast_atmosphere_entry_" + b.id); }
                if (ship.AltitudeAGL < 900.0) break;
                yield return new WaitForFixedUpdate();
            }
            boostCmd = false;
            Log("fast entry into " + b.displayName + ": " + maxSpeedAbove.ToString("F0") + " m/s above the air, " + speedAtEntry.ToString("F0") +
                " m/s at the boundary, " + ship.Speed.ToString("F0") + " m/s at " + ship.AltitudeAGL.ToString("F0") + " m AGL, coverage failures so far " + CoverageFailures);
            if (speedAtEntry < 250f) Fail("fast entry: did not enter the atmosphere fast (" + speedAtEntry.ToString("F0") + " m/s)");
            // Pull out of the dive and fly level a few hundred metres up.
            float u = Time.time;
            bool lowShot = false;
            while (Time.time - u < 14f)
            {
                Vector3 upv = Up;
                Vector3 level = Vector3.ProjectOnPlane(Forward, upv).normalized;
                float climb = ship.AltitudeAGL < 450.0 ? 0.35f : (ship.AltitudeAGL > 800.0 ? -0.12f : 0.05f);
                if (Time.time - u > 5.0f && !lowShot) climb = -0.40f;      // look down at the terrain for the shot
                steerDir = (level + upv * climb).normalized;
                throttleCmd = 0.45f;
                brakeCmd = ship.Speed > 260f;
                if (!lowShot && Time.time - u > 7f) { lowShot = true; yield return Shot("C02_after_fast_entry_low_" + b.id); }
                yield return new WaitForFixedUpdate();
            }
            brakeCmd = false;
            Log("after the fast entry: level at " + ship.AltitudeAGL.ToString("F0") + " m AGL, " + ship.Speed.ToString("F0") + " m/s, hull " + (ship.HullIntegrity * 100f).ToString("F0") + "%");
            steerDir = Vector3.zero;
        }

        IEnumerator FlyTo(BodyDef tb, int index)
        {
            Phase = "to_" + tb.id;
            hopStart = Time.timeAsDouble;
            game.SetTarget(tb);
            Log("target " + tb.displayName + " selected, distance " + (ship.DistanceToTargetSurface() / 1000.0).ToString("F1") + " km");

            // Climb out of the current air if needed, then align.
            boostCmd = true;
            throttleCmd = 1f;
            float t = Time.time;
            while (Time.time - t < 120f)
            {
                var cur = streamer.CurrentBody;
                bool low = cur != null && cur != tb && ship.AltitudeAGL < cur.atmosphereHeight + 700.0;
                Vector3 toT = (tb.LogicalPos - ship.LogicalPosition).normalized.ToVector3();
                steerDir = low ? (Up * 1.4f + toT).normalized : toT;
                if (!low && ship.AngleToTarget() < 6.0) break;
                yield return new WaitForFixedUpdate();
            }
            boostCmd = false;

            // Engage the pulse drive (J) and hold the nose on the target.
            for (int attempt = 0; attempt < 5 && !ship.PulseEngaged; attempt++)
            {
                ship.pulseToggleRequested = true;
                float w = Time.time;
                while (!ship.PulseEngaged && Time.time - w < 3f)
                {
                    steerDir = (tb.LogicalPos - ship.LogicalPosition).normalized.ToVector3();
                    yield return new WaitForFixedUpdate();
                }
                if (!ship.PulseEngaged) Log("pulse not engaged yet: " + ship.PulseStatus);
            }
            if (!ship.PulseEngaged) { Fail("pulse drive would not engage: " + ship.PulseStatus); yield break; }
            Log("pulse engaged toward " + tb.displayName);

            bool shotCruise = false, shotApproach = false;
            float cruiseStart = Time.time;
            while (ship.PulseEngaged && Time.time - cruiseStart < 240f)
            {
                steerDir = (tb.LogicalPos - ship.LogicalPosition).normalized.ToVector3();
                if (!shotCruise && Time.time - cruiseStart > 10f) { shotCruise = true; yield return Shot("B" + index + "1_pulse_cruise_to_" + tb.id); }
                if (!shotApproach && ship.DistanceToTargetSurface() < 9000.0) { shotApproach = true; yield return Shot("B" + index + "2_approach_" + tb.id + "_from_space"); }
                yield return new WaitForFixedUpdate();
            }
            Log("pulse dropped: " + ship.PulseStatus + ", " + (ship.DistanceToTargetSurface() / 1000.0).ToString("F1") + " km above " + tb.displayName);
            HopSeconds[tb.id] = (float)(Time.timeAsDouble - hopStart);
            Log("interplanetary leg to " + tb.displayName + " took " + HopSeconds[tb.id].ToString("F0") + " s of flight");
        }

        DVec3 PickSite(BodyDef b, bool nearWater)
        {
            var pr = streamer.RuntimeOf(b);
            DVec3 toShip = (ship.LogicalPosition - b.LogicalPos).normalized;
            DVec3 toSun = (streamer.system.Star.LogicalPos - b.LogicalPos).normalized;
            DVec3 prefer = (toShip * 0.75 + toSun * 0.25).normalized;
            if (!nearWater) return TerrainFunc.FindFlatSpotOnLand(b, pr.Perm, prefer, b.radius * 0.12, 160);

            // Land 6-45 m above the sea with water within ~260 m: a coastal site.
            DVec3 best = prefer; double bestScore = double.MaxValue;
            DVec3 t1 = DVec3.Cross(prefer, new DVec3(0, 1, 0)).normalized;
            DVec3 t2 = DVec3.Cross(prefer, t1).normalized;
            for (int i = 0; i < 900; i++)
            {
                double ang = i * 2.399963;               // golden-angle spiral
                double rad = Math.Sqrt(i / 900.0) * 0.55;
                DVec3 d = (prefer + t1 * (Math.Cos(ang) * rad) + t2 * (Math.Sin(ang) * rad)).normalized;
                double h = TerrainFunc.Elevation(b, pr.Perm, d);
                if (h < b.seaLevel + 6.0 || h > b.seaLevel + 45.0) continue;
                DVec3 up = d;
                var p = LandingAssistant.Probe(streamer, b, b.LogicalPos + d * (b.radius + h + ship.GroundClearance), up, 1f);
                if (!p.ok) continue;
                bool water = false;
                DVec3 a1 = DVec3.Cross(d, t1).normalized, a2 = DVec3.Cross(d, a1).normalized;
                for (int k = 0; k < 12 && !water; k++)
                {
                    double aa = k * Math.PI / 6.0;
                    DVec3 q = (d + (a1 * Math.Cos(aa) + a2 * Math.Sin(aa)) * (260.0 / b.radius)).normalized;
                    if (TerrainFunc.Elevation(b, pr.Perm, q) < b.seaLevel - 2.0) water = true;
                }
                if (!water) continue;
                double score = (d - prefer).magnitude + p.slopeDegrees * 0.002;
                if (score < bestScore) { bestScore = score; best = d; }
            }
            return best;
        }

        IEnumerator LandOn(BodyDef b, int index)
        {
            Phase = "approach_" + b.id;
            bool coast = b.seaLevel > -1e8 && b.kind == BodyKind.Ocean;
            DVec3 site = PickSite(b, coast);
            var pr = streamer.RuntimeOf(b);
            double siteH = TerrainFunc.Elevation(b, pr.Perm, site);
            Log("landing site on " + b.displayName + ": h=" + siteH.ToString("F0") + " m" + (coast ? " (coast)" : ""));

            bool shotEntry = false, shotLow = false, shotCoast = false;
            float start = Time.time;
            while (Time.time - start < 240f)
            {
                DVec3 sitePos = b.LogicalPos + site * (b.radius + siteH);
                DVec3 wp = sitePos + site * 550.0;
                DVec3 to = wp - ship.LogicalPosition;
                double dist = to.magnitude;
                DVec3 up = (ship.LogicalPosition - b.LogicalPos).normalized;
                double horiz = (to - up * DVec3.Dot(to, up)).magnitude;
                Vector3 dir = (to / dist).ToVector3();
                if (ship.AltitudeAGL < 260.0) dir = (dir + up.ToVector3() * 0.8f).normalized;   // terrain clearance
                steerDir = dir;
                throttleCmd = horiz > 6000 ? 1f : horiz > 2500 ? 0.6f : horiz > 900 ? 0.35f : 0.18f;
                brakeCmd = horiz < 1200 && ship.Speed > 140f;

                if (!shotEntry && ship.AirDensity > 0.10f) { shotEntry = true; yield return Shot("C" + index + "1_atmosphere_entry_" + b.id); }
                if (!shotLow && ship.AltitudeAGL < 1200.0 && ship.AirDensity > 0.5f) { shotLow = true; yield return Shot("C" + index + "2_low_flight_" + b.id); }
                if (coast && !shotCoast && horiz < 1600.0 && ship.AltitudeAGL < 750.0) { shotCoast = true; yield return Shot("C" + index + "3_coast_approach_" + b.id); }

                if (horiz < 450.0 && ship.AltitudeAGL < 900.0 && ship.Speed < 110f)
                {
                    brakeCmd = false;
                    throttleCmd = 0f;
                    ship.landingRequested = true;
                    yield return new WaitForFixedUpdate();
                    yield return new WaitForFixedUpdate();
                    if (ship.Assisting) break;
                    Log("landing refused: " + ship.LandStatus);
                    site = TerrainFunc.FindFlatSpotOnLand(b, pr.Perm, up, 600.0, 120);
                    siteH = TerrainFunc.Elevation(b, pr.Perm, site);
                }
                yield return new WaitForFixedUpdate();
            }
            brakeCmd = false; steerDir = Vector3.zero; throttleCmd = 0f;
            if (!ship.Assisting && !ship.Landed) { Fail("could not start a landing on " + b.displayName + ": " + ship.LandStatus); yield break; }

            Phase = "landing_" + b.id;
            float w = Time.time;
            while (!ship.Landed && Time.time - w < 90f) yield return new WaitForFixedUpdate();
            if (!ship.Landed) { Fail("landing on " + b.displayName + " did not complete: " + ship.LandStatus); yield break; }
            yield return WaitFixed(2.5f);
            var probe = LandingAssistant.Probe(streamer, b, ship.LogicalPosition, (ship.LogicalPosition - b.LogicalPos).normalized, 1f);
            double gearGap = ship.AltitudeAGL - ship.GroundClearance;
            Log("LANDED on " + b.displayName + ": slope " + probe.slopeDegrees.ToString("F0") + " deg, gear-ground gap " + gearGap.ToString("F2") + " m, water depth " + probe.waterDepth.ToString("F1"));
            yield return Shot("D" + index + "1_landed_" + b.id + "_external");
            yield return Shot("D" + index + "2_landed_" + b.id + "_cockpit", cockpit: true);
        }

        /// <summary>Tangent direction from the ship to the nearest sea within ~600 m (zero if none).</summary>
        Vector3 WaterDirection(BodyDef b)
        {
            var pr = streamer.RuntimeOf(b);
            DVec3 up = (ship.LogicalPosition - b.LogicalPos).normalized;
            DVec3 t1 = DVec3.Cross(up, new DVec3(0, 1, 0)).normalized;
            if (t1.sqrMagnitude < 1e-6) t1 = DVec3.Cross(up, new DVec3(1, 0, 0)).normalized;
            DVec3 t2 = DVec3.Cross(up, t1).normalized;
            for (double r = 100.0; r <= 600.0; r += 50.0)
                for (int k = 0; k < 32; k++)
                {
                    double a = k * Math.PI / 16.0;
                    DVec3 tan = t1 * Math.Cos(a) + t2 * Math.Sin(a);
                    DVec3 d = (up + tan * (r / b.radius)).normalized;
                    if (TerrainFunc.Elevation(b, pr.Perm, d) < b.seaLevel - 2.0) return tan.ToVector3();
                }
            return Vector3.zero;
        }

        IEnumerator TakeOff(BodyDef b, int index)
        {
            Phase = "takeoff_" + b.id;
            ship.takeoffRequested = true;
            liftCmd = 1f;
            if (b.kind == BodyKind.Ocean && b.seaLevel > -1e8)
            {
                // Hop up, turn the nose toward the nearest shore and look down at
                // the coastline: land, beach and the water shell meeting it.
                yield return WaitFixed(1.4f);
                liftCmd = 0f;
                Vector3 toWater = WaterDirection(b);
                if (toWater != Vector3.zero)
                {
                    float t = Time.time;
                    while (Time.time - t < 4.5f)
                    {
                        Vector3 upv = Up;
                        steerDir = (Vector3.ProjectOnPlane(toWater, upv).normalized - upv * 0.5f).normalized;
                        yield return new WaitForFixedUpdate();
                    }
                    Log("coast check: hovering " + ship.AltitudeAGL.ToString("F0") + " m above the shore of " + b.displayName);
                    yield return Shot("C" + index + "3_coast_shoreline_" + b.id);
                    yield return Shot("C" + index + "4_coast_shoreline_" + b.id + "_cockpit", cockpit: true);
                    steerDir = Vector3.zero;
                }
                else Log("coast check: no water found within 600 m");
                liftCmd = 1f;
            }
            yield return WaitFixed(4f);
            Log("lifted off " + b.displayName + " from the same spot, AGL " + ship.AltitudeAGL.ToString("F0") + " m");
            yield return Shot("E" + index + "1_takeoff_" + b.id);
            liftCmd = 0f;
        }

        IEnumerator SaveLoadAndBeacon()
        {
            Phase = "save_load";
            var b = streamer.CurrentBody;
            var beacon = game.DropBeacon();
            DVec3 landedPos = ship.LogicalPosition;
            Quaternion landedRot = ship.Orientation;
            bool saved = save.Save();
            Log("beacon dropped and game saved on the surface: " + saved + " -> " + save.LastSavePath);

            // Fly away, then load: the ship must be back on the saved spot, landed.
            ship.takeoffRequested = true;
            liftCmd = 1f;
            yield return WaitFixed(5f);
            liftCmd = 0f;
            ship.inLift = 0f;
            bool loaded = save.Load();
            yield return WaitFixed(1.5f);
            double posErr = (ship.LogicalPosition - landedPos).magnitude;
            float rotErr = Quaternion.Angle(ship.Orientation, landedRot);
            Log("loaded: " + loaded + ", landed " + ship.Landed + ", position error " + posErr.ToString("F3") + " m, rotation error " + rotErr.ToString("F2") + " deg, beacons " + beacons.Count);
            if (!loaded || !ship.Landed || posErr > 2.5) { Fail("save/load on the surface did not restore the landed state"); yield break; }
            yield return Shot("F01_after_load_on_surface");

            // Take off, fly ~3 km away, then target the beacon (N) and return to it.
            Phase = "beacon_return";
            ship.takeoffRequested = true;
            liftCmd = 1f;
            yield return WaitFixed(4f);
            liftCmd = 0f;
            DVec3 up0 = (ship.LogicalPosition - b.LogicalPos).normalized;
            DVec3 away = DVec3.Cross(up0, new DVec3(0.3, 1, 0.1)).normalized;
            float t = Time.time;
            throttleCmd = 0.9f;
            while (Time.time - t < 60f && (ship.LogicalPosition - landedPos).magnitude < 3200.0)
            {
                DVec3 up = (ship.LogicalPosition - b.LogicalPos).normalized;
                Vector3 dir = (away - up * DVec3.Dot(away, up)).normalized.ToVector3();
                if (ship.AltitudeAGL < 500.0) dir = (dir + up.ToVector3() * 0.6f).normalized;
                steerDir = dir;
                yield return new WaitForFixedUpdate();
            }
            Log("flew " + ((ship.LogicalPosition - landedPos).magnitude / 1000.0).ToString("F1") + " km away from the beacon");
            game.TargetNearestBeacon();
            DVec3 bpos = new DVec3(beacon.x, beacon.y, beacon.z);
            t = Time.time;
            while (Time.time - t < 120f)
            {
                DVec3 up = (ship.LogicalPosition - b.LogicalPos).normalized;
                DVec3 wp = bpos + up * 450.0;
                DVec3 to = wp - ship.LogicalPosition;
                double horiz = (to - up * DVec3.Dot(to, up)).magnitude;
                Vector3 dir = to.normalized.ToVector3();
                if (ship.AltitudeAGL < 260.0) dir = (dir + up.ToVector3() * 0.8f).normalized;
                steerDir = dir;
                throttleCmd = horiz > 1500 ? 0.8f : horiz > 600 ? 0.35f : 0.15f;
                brakeCmd = horiz < 800 && ship.Speed > 120f;
                if (horiz < 120.0 && ship.AltitudeAGL < 900.0 && ship.Speed < 110f)
                {
                    ship.landingRequested = true;
                    yield return new WaitForFixedUpdate();
                    yield return new WaitForFixedUpdate();
                    if (ship.Assisting) break;
                }
                yield return new WaitForFixedUpdate();
            }
            brakeCmd = false; steerDir = Vector3.zero; throttleCmd = 0f;
            t = Time.time;
            while (!ship.Landed && Time.time - t < 90f) yield return new WaitForFixedUpdate();
            double back = (ship.LogicalPosition - bpos).magnitude;
            Log("returned to beacon '" + beacon.label + "': landed " + ship.Landed + ", " + back.ToString("F0") + " m from it");
            if (!ship.Landed || back > 400.0) Fail("did not return to the saved beacon");
            yield return Shot("F02_back_at_beacon");
            hud.showDebug = true;
            yield return Shot("G01_telemetry_after_long_route");
            hud.showDebug = false;
        }

        void Finish()
        {
            Phase = Failed ? "failed" : "done";
            steerDir = Vector3.zero; throttleCmd = 0f; liftCmd = 0f; brakeCmd = false;
            int tele = ShipController.TeleportCount - teleportsAtStart;
            Log("SUMMARY " + (Failed ? "FAIL (" + FailReason + ")" : "PASS"));
            Log("teleports during route: " + tele + ", scene loads: " + GameManager.SceneLoadsSinceStart +
                ", max step/bound ratio: " + ship.MaxStepRatio.ToString("F3") + ", tree coverage failures: " + CoverageFailures +
                (CoverageFailures > 0 ? " (first: " + FirstCoverageFailure + ")" : ""));
            foreach (var kv in HopSeconds) Log("hop to " + kv.Key + ": " + kv.Value.ToString("F0") + " s");
            Log("session: avg " + game.SessionAvgFps.ToString("F0") + " fps, worst frame " + game.SessionWorstMs.ToString("F1") +
                " ms, frames > 33 ms: " + game.LongFrames33 + ", peak patches " + game.PeakPatches + ", peak nodes " + game.PeakNodes +
                ", peak pending " + game.PeakPending + ", peak meshes " + game.PeakMeshes + ", peak objects " + game.PeakObjects +
                ", peak colliders " + game.PeakColliders + ", peak managed " + (game.PeakManagedBytes >> 20) + " MB" +
                ", screenshot frames excluded " + game.CaptureFramesExcluded);
            Done = true;
            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    File.WriteAllLines(logPath, Report);
                    File.WriteAllText(Path.ChangeExtension(logPath, ".csv"), csv.ToString());
                }
                catch (Exception e) { Debug.LogWarning("route log write failed: " + e.Message); }
            }
            if (quitWhenDone) StartCoroutine(QuitSoon());
        }

        IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }
    }
}
