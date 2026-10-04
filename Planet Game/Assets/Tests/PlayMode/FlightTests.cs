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
