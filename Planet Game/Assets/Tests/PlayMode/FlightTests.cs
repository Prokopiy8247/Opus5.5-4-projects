using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>
    /// Flight model tests: they check what a player experiences - the nose turns
    /// the way the stick says, the TRAJECTORY follows the nose, the model faces
    /// +Z, and normal flight never relocates the ship.
    /// </summary>
    public class FlightTests
    {
        [TearDown] public void TearDown() { Time.timeScale = 1f; }

        static Vector3 Fwd(ShipController s) => s.Orientation * Vector3.forward;

        [UnityTest]
        public IEnumerator ShipRotation_YawPitchRoll_TurnTheExpectedWay_WithoutRunaway()
        {
            yield return PGTestUtil.LoadGame();
            var ship = ShipController.Instance;
            PGTestUtil.ClearInputs(ship);
            yield return PGTestUtil.Fixed(10);

            // Yaw right for 1 s.
            Quaternion q0 = ship.Orientation;
            Vector3 f0 = q0 * Vector3.forward, r0 = q0 * Vector3.right, u0 = q0 * Vector3.up;
            ship.inYaw = 1f;
            yield return PGTestUtil.Fixed(60);
            ship.inYaw = 0f;
            Vector3 f1 = Fwd(ship);
            float yawDeg = Vector3.Angle(f0, f1);
            Assert.Greater(Vector3.Dot(f1 - f0, r0), 0f, "positive yaw must move the nose to the RIGHT");
            Assert.That(yawDeg, Is.InRange(30f, 60f), "1 s of full yaw at 52 deg/s: a runaway (compounding) rotation would be far larger");
            yield return PGTestUtil.Fixed(30);

            // Pitch up for 1 s.
            q0 = ship.Orientation; f0 = q0 * Vector3.forward; u0 = q0 * Vector3.up;
            ship.inPitch = 1f;
            yield return PGTestUtil.Fixed(60);
            ship.inPitch = 0f;
            f1 = Fwd(ship);
            Assert.Greater(Vector3.Dot(f1 - f0, u0), 0f, "positive pitch must raise the nose");
            Assert.That(Vector3.Angle(f0, f1), Is.InRange(35f, 70f));
            yield return PGTestUtil.Fixed(30);

            // Roll right for 0.5 s.
            q0 = ship.Orientation; r0 = q0 * Vector3.right; u0 = q0 * Vector3.up;
            ship.inRoll = 1f;
            yield return PGTestUtil.Fixed(30);
            ship.inRoll = 0f;
            Vector3 r1 = ship.Orientation * Vector3.right;
            Assert.Less(Vector3.Dot(r1, u0), -0.3f, "positive roll must drop the RIGHT wing");
        }

        [UnityTest]
        public IEnumerator CourseResponse_WithAssist_VelocityFollowsA90DegreeTurn()
        {
            yield return PGTestUtil.LoadGame();
            var ship = ShipController.Instance;
            PGTestUtil.ClearInputs(ship);
            ship.flightAssist = true;
            ship.inThrottle = 0.6f;
            yield return PGTestUtil.Fixed(60 * 6);
            float speed0 = ship.Speed;
            Assert.Greater(speed0, 250f, "throttle 60% should reach cruise speed");
            Vector3 v0 = ship.LogicalVelocity.ToVector3().normalized;
            Vector3 f0 = Fwd(ship);

            ship.inYaw = 1f;
            int guard = 0;
            while (Vector3.Angle(f0, Fwd(ship)) < 90f && guard++ < 600) yield return new WaitForFixedUpdate();
            ship.inYaw = 0f;
            yield return PGTestUtil.Fixed(90);

            Vector3 v1 = ship.LogicalVelocity.ToVector3().normalized;
            float velTurn = Vector3.Angle(v0, v1);
            float lag = Vector3.Angle(Fwd(ship), v1);
            Debug.Log($"[TEST] course response: nose 90 deg, velocity turned {velTurn:F1} deg, nose-velocity lag {lag:F1} deg, speed {speed0:F0}->{ship.Speed:F0}");
            Assert.Greater(velTurn, 70f, "the trajectory, not just the model, must turn");
            Assert.Less(lag, 20f);
            Assert.Greater(ship.Speed, speed0 * 0.6f, "a turn should not kill the speed");
        }

        [UnityTest]
        public IEnumerator CourseResponse_AssistOff_IsInertial()
        {
            yield return PGTestUtil.LoadGame();
            var ship = ShipController.Instance;
            PGTestUtil.ClearInputs(ship);
            ship.flightAssist = true;
            ship.inThrottle = 0.5f;
            yield return PGTestUtil.Fixed(60 * 5);
            ship.inThrottle = 0f;
            ship.flightAssist = false;
            Vector3 v0 = ship.LogicalVelocity.ToVector3().normalized;
            Vector3 f0 = Fwd(ship);
            ship.inYaw = 1f;
            int guard = 0;
            while (Vector3.Angle(f0, Fwd(ship)) < 90f && guard++ < 600) yield return new WaitForFixedUpdate();
            ship.inYaw = 0f;
            yield return PGTestUtil.Fixed(60);
            float velTurn = Vector3.Angle(v0, ship.LogicalVelocity.ToVector3().normalized);
            Assert.Less(velTurn, 15f, "with assist off the ship keeps its momentum");
        }

        [UnityTest]
        public IEnumerator ModelConvention_NoseForward_EnginesAft_CockpitInCanopy_CameraBehind()
        {
            yield return PGTestUtil.LoadGame();
            var ship = ShipController.Instance;
            Vector3 L(Transform t) => ship.transform.InverseTransformPoint(t.position);

            Assert.IsNotNull(ship.noseAnchor); Assert.IsNotNull(ship.cockpitAnchor);
            Assert.AreEqual(2, ship.engineAnchors.Length); Assert.AreEqual(3, ship.gearFeet.Length);
            Vector3 nose = L(ship.noseAnchor), cockpit = L(ship.cockpitAnchor);
            Vector3 eL = L(ship.engineAnchors[0]), eR = L(ship.engineAnchors[1]);
            Debug.Log($"[TEST] anchors nose {nose} cockpit {cockpit} engineL {eL} engineR {eR}");
            Assert.Greater(nose.z, 6f, "Nose must be at +Z");
            Assert.Less(Mathf.Abs(nose.x), 0.3f);
            Assert.Less(eL.z, -4f, "engines must be aft (-Z)"); Assert.Less(eR.z, -4f);
            Assert.Less(eL.x, -1f, "Engine_L on the left (-X)"); Assert.Greater(eR.x, 1f, "Engine_R on the right (+X)");
            Assert.Greater(cockpit.z, 0f); Assert.Greater(cockpit.y, 0.5f);
            foreach (var g in ship.gearFeet) Assert.Less(L(g).y, -1.5f, "gear feet below the hull");

            Renderer canopy = null;
            foreach (var r in ship.GetComponentsInChildren<MeshRenderer>()) if (r.name.Contains("Canopy") && !r.name.Contains("Frame")) canopy = r;
            Assert.IsNotNull(canopy, "canopy renderer");
            var cb = canopy.bounds; cb.Expand(0.1f);
            Assert.IsTrue(cb.Contains(ship.cockpitAnchor.position), "cockpit eye point must be inside the canopy volume");

            // Exhaust points backwards.
            foreach (var ps in ship.thrusterParticles)
                Assert.Less(Vector3.Dot(ps.transform.forward, ship.transform.forward), -0.9f, "exhaust must stream aft");

            // External camera sits behind the ship.
            var rig = CameraRig.Instance;
            rig.view = CameraRig.View.External;
            yield return null; yield return null;
            Vector3 cam = ship.transform.InverseTransformPoint(rig.transform.position);
            Assert.Less(cam.z, -8f, "chase camera must be behind (-Z)");

            // Thrust moves the ship toward its nose.
            PGTestUtil.ClearInputs(ship);
            ship.inThrottle = 0.4f;
            yield return PGTestUtil.Fixed(60);
            Vector3 vLocal = Quaternion.Inverse(ship.Orientation) * ship.LogicalVelocity.ToVector3();
            Assert.Greater(vLocal.z, 10f, "forward thrust must move the ship along +Z");
        }

        /// <summary>
        /// Vharos has no surface: flown at full throttle through its centre, the
        /// ship enters the haze, is slowed and buffeted in the cloud decks, and
        /// comes out of the far side - no ground contact, no damage, no relocation.
        /// </summary>
        [UnityTest, Timeout(300000)]
        public IEnumerator GasGiant_FlyStraightThroughTheCentre_ComesOutTheOtherSide()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            var gas = gm.system.Get("vharos");
            Assert.IsNotNull(gas); Assert.IsFalse(gas.landable);
            PGTestUtil.ClearInputs(ship);
            gm.DebugJumpToGasGiant("vharos");
            int tp0 = ShipController.TeleportCount;
            yield return PGTestUtil.Fixed(5);

            DVec3 entrySide = (ship.LogicalPosition - gas.LogicalPos).normalized;
            DVec3 exitPoint = gas.LogicalPos - entrySide * (gas.radius + gas.atmosphereHeight + 3000.0);
            double outer = gas.radius + gas.atmosphereHeight;
            float hull0 = ship.HullIntegrity;
            bool wasInside = false, cappedInDeck = true;
            float maxBuffet = 0f;
            double minR = double.MaxValue;

            Time.timeScale = 4f;
            ship.inThrottle = 1f; ship.boost = true;
            float start = Time.time;
            while (Time.time - start < 240f)
            {
                // Hold the nose on the far-side exit point (the buffeting pushes it around).
                Vector3 to = Quaternion.Inverse(ship.Orientation) * (exitPoint - ship.LogicalPosition).normalized.ToVector3();
                ship.inYaw = Mathf.Clamp(to.x * 4f, -1f, 1f);
                ship.inPitch = Mathf.Clamp(to.y * 4f, -1f, 1f);

                double r = (ship.LogicalPosition - gas.LogicalPos).magnitude;
                minR = System.Math.Min(minR, r);
                if (ship.GasBody == gas) wasInside = true;
                if (ship.GasDepth >= 1f && ship.Speed > PGConst.SpeedGasMax + 80f && r < gas.radius - 1500.0) cappedInDeck = false;
                maxBuffet = Mathf.Max(maxBuffet, ship.Buffet);
                if (wasInside && r > outer + 500.0 && DVec3.Dot(ship.LogicalPosition - gas.LogicalPos, entrySide) < 0.0) break;
                yield return new WaitForFixedUpdate();
            }
            Time.timeScale = 1f;
            PGTestUtil.ClearInputs(ship);

            double rEnd = (ship.LogicalPosition - gas.LogicalPos).magnitude;
            Debug.Log($"[TEST] Vharos fly-through: deepest {minR:F0} m from centre, exit r {rEnd:F0} m, max buffet {maxBuffet:F2}, hull {ship.HullIntegrity:P0}, {Time.time - start:F0} s");
            Assert.IsTrue(wasInside, "the ship must have been inside the gas");
            Assert.Less(minR, gas.radius * 0.25, "the straight line goes through the core - nothing solid stops it");
            Assert.Greater(rEnd, outer, "out of the haze again");
            Assert.Less(DVec3.Dot((ship.LogicalPosition - gas.LogicalPos).normalized, entrySide), -0.5, "out on the FAR side");
            Assert.IsTrue(cappedInDeck, "the thick gas holds the speed near the deck cap");
            Assert.Greater(maxBuffet, 0.3f, "the cloud decks buffet the ship");
            Assert.AreEqual(hull0, ship.HullIntegrity, 1e-4f, "flying through gas does no damage");
            Assert.AreEqual(tp0, ShipController.TeleportCount, "continuous flight, no relocation");
            Assert.IsFalse(ship.Landed);
        }

        /// <summary>Debug-places the ship <paramref name="agl"/> metres over Mirvalis, falling at <paramref name="sink"/> m/s with the assist off.</summary>
        static IEnumerator DropOntoMirvalis(GameManager gm, double agl, double sink)
        {
            var ship = gm.ship;
            PGTestUtil.ClearInputs(ship);
            gm.DebugJumpToSurface("mirvalis", 1);
            yield return PGTestUtil.Fixed(5);
            DVec3 up = (ship.LogicalPosition - ship.SoiBody.LogicalPos).normalized;
            ship.flightAssist = false;
            ship.TeleportLogical(ship.LogicalPosition - up * (ship.AltitudeAGL - agl), -up * sink);
        }

        [UnityTest]
        public IEnumerator PlanetImpact_AboveExplodeSpeed_DestroysTheShip_ThenRespawnsAboveTheCrashSite()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            yield return DropOntoMirvalis(gm, 300.0, 150.0);
            int tp0 = ShipController.TeleportCount;
            var body = ship.SoiBody;
            bool heard = false;
            ship.Exploded += () => heard = true;

            int guard = 0;
            while (!ship.Destroyed && guard++ < 600) yield return new WaitForFixedUpdate();
            Assert.IsTrue(ship.Destroyed, "hitting the ground at 150 m/s must destroy the ship");
            Debug.Log($"[TEST] planet impact: {ship.DestroyedReason}, impact {ship.LastImpactSpeed:F0} m/s");
            Assert.IsTrue(heard, "the Exploded event fires (audio)");
            Assert.Greater(ship.LastImpactSpeed, PGConst.ExplodeSpeed);
            StringAssert.Contains(body.displayName.ToUpperInvariant(), ship.DestroyedReason);
            Assert.IsFalse(ship.DestroyedByStar);
            Assert.AreEqual(0f, ship.HullIntegrity);
            foreach (var r in ship.GetComponentsInChildren<MeshRenderer>()) Assert.IsFalse(r.enabled, "the hull is gone: " + r.name);
            Assert.IsNotNull(ship.GetComponentInChildren<ShipExplosion>(), "the explosion effect is on the wreck");

            // The wreck does not move and does not answer the controls.
            DVec3 wreck = ship.LogicalPosition;
            ship.inThrottle = 1f; ship.inLift = 1f;
            yield return PGTestUtil.Fixed(60);
            Assert.IsTrue(ship.Destroyed);
            Assert.Less((ship.LogicalPosition - wreck).magnitude, 0.01, "the wreck stays put");
            Assert.AreEqual("DESTROYED", ship.ModeLabel);

            guard = 0;
            while (ship.Destroyed && guard++ < 600) yield return new WaitForFixedUpdate();
            yield return PGTestUtil.Fixed(5);
            Assert.IsFalse(ship.Destroyed, "a new ship arrives after the respawn delay");
            Assert.AreEqual(1f, ship.HullIntegrity);
            Assert.IsFalse(ship.MotorDisabled);
            Assert.AreEqual(0f, ship.inThrottle, "the new ship starts with the throttle closed");
            Assert.AreEqual(body, ship.SoiBody);
            Assert.Greater(ship.AltitudeDatum, body.atmosphereHeight, "respawned in space above the planet");
            Assert.Greater(DVec3.Dot((ship.LogicalPosition - body.LogicalPos).normalized, (wreck - body.LogicalPos).normalized), 0.999, "above the crash site");
            foreach (var r in ship.GetComponentsInChildren<MeshRenderer>()) Assert.IsTrue(r.enabled, "the new ship is visible: " + r.name);
            Assert.AreEqual(tp0, ShipController.TeleportCount, "a respawn is not a debug teleport");
        }

        [UnityTest]
        public IEnumerator PlanetImpact_BelowExplodeSpeed_DamagesTheHull_ButDoesNotDestroy()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            yield return DropOntoMirvalis(gm, 30.0, 34.0);

            int guard = 0;
            while (ship.LastImpactSpeed <= 0f && !ship.Destroyed && guard++ < 600) yield return new WaitForFixedUpdate();
            yield return PGTestUtil.Fixed(30);
            Debug.Log($"[TEST] hard landing: {ship.LastImpactNote}, hull {ship.HullIntegrity:P0}");
            Assert.That(ship.LastImpactSpeed, Is.InRange(PGConst.CrashSpeed, PGConst.ExplodeSpeed));
            Assert.IsFalse(ship.Destroyed, "a hard landing below the explode speed is survivable");
            Assert.That(ship.HullIntegrity, Is.InRange(0.3f, 0.99f), "but it damages the hull");
        }

        [UnityTest]
        public IEnumerator Star_HeatsTheHull_ThenDestroysTheShip_AndRespawnsOnTheStartOrbit()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            var star = gm.system.Star;
            PGTestUtil.ClearInputs(ship);

            // Just outside the heat zone, nose on the star's centre.
            DVec3 dir = (ship.LogicalPosition - star.LogicalPos).normalized;
            Vector3 fwd = (-dir).ToVector3();
            ship.Orientation = Quaternion.LookRotation(fwd, Vector3.Cross(fwd, Vector3.right).normalized);
            ship.TeleportLogical(star.LogicalPos + dir * (star.radius + PGConst.StarHeatMargin + 400.0), DVec3.zero);
            int tp0 = ShipController.TeleportCount;
            yield return PGTestUtil.Fixed(5);
            Assert.IsFalse(ship.StarWarning);
            Assert.AreEqual(1f, ship.HullIntegrity);

            ship.inThrottle = 0.25f;
            bool warned = false;
            float hullWhileWarned = 1f;
            int guard = 0;
            while (!ship.Destroyed && guard++ < 1800)
            {
                if (ship.StarWarning) { warned = true; hullWhileWarned = ship.HullIntegrity; }
                yield return new WaitForFixedUpdate();
            }
            Debug.Log($"[TEST] star: {ship.DestroyedReason}, hull before the end {hullWhileWarned:P0}, {guard} steps");
            Assert.IsTrue(warned, "the heat zone warns before it kills");
            Assert.Less(hullWhileWarned, 0.95f, "the hull heats on the way in");
            Assert.IsTrue(ship.Destroyed, "flying into the star must destroy the ship");
            Assert.IsTrue(ship.DestroyedByStar);
            StringAssert.Contains(star.displayName.ToUpperInvariant(), ship.DestroyedReason);
            Assert.IsFalse(ship.StarWarning);

            guard = 0;
            while (ship.Destroyed && guard++ < 600) yield return new WaitForFixedUpdate();
            yield return PGTestUtil.Fixed(5);
            Assert.IsFalse(ship.Destroyed);
            Assert.AreEqual(1f, ship.HullIntegrity);
            Assert.Greater((ship.LogicalPosition - star.LogicalPos).magnitude, star.radius + PGConst.StarPulseMargin, "the new ship is clear of the star");
            Assert.AreEqual(gm.system.Get(gm.startBodyId), ship.SoiBody, "back on the start orbit");
            Assert.IsFalse(ship.StarWarning);
            Assert.AreEqual(tp0, ShipController.TeleportCount);
        }

        [UnityTest]
        public IEnumerator NormalTravel_HasNoTeleport_NoSceneLoad_AndPhysicallyBoundedSteps()
        {
            yield return PGTestUtil.LoadGame();
            var ship = ShipController.Instance;
            int tp0 = ShipController.TeleportCount;
            int shifts0 = FloatingOrigin.Instance.ShiftCount;
            ship.ResetStepStats();
            PGTestUtil.ClearInputs(ship);

            ship.inThrottle = 1f; ship.boost = true;
            yield return PGTestUtil.Fixed(60 * 5);
            ship.inYaw = 0.7f; yield return PGTestUtil.Fixed(90);
            ship.inYaw = 0f; ship.inPitch = -0.5f; yield return PGTestUtil.Fixed(90);
            ship.inPitch = 0f; ship.boost = false; ship.brake = true; yield return PGTestUtil.Fixed(90);
            ship.brake = false; ship.inLift = 1f; yield return PGTestUtil.Fixed(60);
            PGTestUtil.ClearInputs(ship);

            Assert.AreEqual(tp0, ShipController.TeleportCount, "normal flight must never call TeleportLogical");
            Assert.AreEqual(0, GameManager.SceneLoadsSinceStart, "no scene loads during play");
            Assert.LessOrEqual(ship.MaxStepRatio, 1.0, "every fixed step moved no further than the velocity allows");
            Assert.Greater(FloatingOrigin.Instance.ShiftCount, shifts0, "the flight crossed origin shifts (render-space rebases, not travel)");
        }
    }
}
