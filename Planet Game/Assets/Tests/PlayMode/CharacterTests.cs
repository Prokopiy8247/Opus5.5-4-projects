using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>
    /// On-foot tests: the pilot leaves the landed ship, walks on the same
    /// analytic ground the ship stands on, jumps, cannot board from far away,
    /// walks back, boards and flies off - all without relocating anything.
    /// </summary>
    public class CharacterTests
    {
        [TearDown] public void TearDown() { Time.timeScale = 1f; }

        static IEnumerator Land(GameManager gm)
        {
            var ship = gm.ship;
            PGTestUtil.ClearInputs(ship);
            gm.DebugJumpToSurface("mirvalis", 1);       // narrow debug placement for these tests only
            yield return PGTestUtil.Fixed(10);
            Time.timeScale = 4f;
            ship.landingRequested = true;
            float t = Time.time;
            while (!ship.Landed && Time.time - t < 90f) yield return new WaitForFixedUpdate();
            Time.timeScale = 1f;
            Assert.IsTrue(ship.Landed, "assisted landing must complete: " + ship.LandStatus);
            yield return PGTestUtil.Fixed(30);
        }

        static double GroundGap(GameManager gm, PlayerCharacter pc)
        {
            return gm.streamer.AltitudeAboveDatum(pc.Body, pc.LogicalPosition) - gm.streamer.ElevationAt(pc.Body, pc.LogicalPosition);
        }

        [UnityTest]
        public IEnumerator OnFoot_CannotLeaveAShipInFlight()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance;
            Assert.IsNotNull(gm.character, "GameManager creates the character at start");
            Assert.IsFalse(gm.ship.Landed);
            Assert.IsFalse(gm.character.ExitShip(), "the hatch stays shut in flight");
            Assert.IsFalse(gm.OnFoot);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator OnFoot_ExitWalkJumpBoard_AndTakeOff()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship; var pc = gm.character;
            yield return Land(gm);
            int teleports = ShipController.TeleportCount;
            DVec3 shipPos = ship.LogicalPosition;

            // ---- step out ---------------------------------------------------
            Assert.IsTrue(pc.ExitShip(), "leaving a landed ship");
            yield return PGTestUtil.Fixed(30);
            Assert.IsTrue(gm.OnFoot);
            Assert.IsTrue(pc.Grounded, "standing on the ground beside the ship");
            Assert.Less(System.Math.Abs(GroundGap(gm, pc)), 0.05, "feet on the analytic surface");
            Assert.Less(pc.DistanceToShip, pc.boardDistance, "stepped out next to the ship");
            Assert.Greater(pc.DistanceToShip, 2.0, "not inside the hull");
            Assert.Less((gm.PlayerLogical - pc.LogicalPosition).magnitude, 1e-6, "the world streams around the walker");

            // ---- walk away from the ship (it is on the right) ------------------
            DVec3 start = pc.LogicalPosition;
            pc.inMove = new Vector2(-1f, 0f);
            yield return PGTestUtil.Fixed(60 * 4);
            pc.inMove = Vector2.zero;
            yield return PGTestUtil.Fixed(30);
            double walked = (pc.LogicalPosition - start).magnitude;
            Assert.That(walked, Is.InRange(8.0, pc.walkSpeed * 4.0 + 1.0), "4 s of walking at " + pc.walkSpeed + " m/s");
            Assert.IsTrue(pc.Grounded);
            Assert.Less(System.Math.Abs(GroundGap(gm, pc)), 0.05, "still on the ground after walking");
            Assert.Less(pc.Speed, 0.05f, "stops when the keys are released");
            yield return null;
            Assert.Less((gm.rig.transform.position - pc.transform.position).magnitude, 12f, "the camera follows the character");
            Assert.Less(pc.MaxStepDisplacement, 0.5, "walking is continuous, never a jump in position");
            Assert.IsTrue(ship.Landed, "the ship stays parked");
            Assert.Less((ship.LogicalPosition - shipPos).magnitude, 0.01, "the ship did not move");

            // ---- running is faster ---------------------------------------------
            start = pc.LogicalPosition;
            pc.inMove = new Vector2(-1f, 0f); pc.inRun = true;
            yield return PGTestUtil.Fixed(60 * 2);
            pc.inMove = Vector2.zero; pc.inRun = false;
            yield return PGTestUtil.Fixed(30);
            Assert.Greater((pc.LogicalPosition - start).magnitude, pc.walkSpeed * 2.0 + 2.0, "running beats walking");

            // ---- jump ----------------------------------------------------------
            pc.inJump = true;
            double peak = 0.0; bool leftGround = false;
            for (int i = 0; i < 600; i++)
            {
                yield return new WaitForFixedUpdate();
                if (!pc.Grounded) leftGround = true;
                peak = System.Math.Max(peak, GroundGap(gm, pc));
                if (leftGround && pc.Grounded) break;
            }
            Assert.IsTrue(leftGround, "a jump leaves the ground");
            Assert.Greater(peak, 0.4, "jump height");
            Assert.IsTrue(pc.Grounded, "and gravity brings the character back");

            // ---- too far to board ----------------------------------------------
            Assert.Greater(pc.DistanceToShip, pc.boardDistance);
            Assert.IsFalse(pc.TryBoard(), "boarding needs the ship within reach");
            Assert.IsTrue(gm.OnFoot);

            // ---- turn round, walk back, board ------------------------------------
            Vector3 h0 = pc.Heading;
            pc.Turn(90f, 0f);
            Assert.Greater(Vector3.Dot(pc.Heading, Vector3.Cross(pc.Up, h0)), 0.99f, "positive yaw turns to the right");
            pc.inMove = new Vector2(0f, 1f); pc.inRun = true;
            int guard = 0;
            while (!pc.CanBoard && guard++ < 60 * 20) yield return new WaitForFixedUpdate();
            pc.inMove = Vector2.zero; pc.inRun = false;
            Assert.IsTrue(pc.CanBoard, "walked back to the ship, still " + pc.DistanceToShip.ToString("F1") + " m away");
            Assert.IsTrue(pc.TryBoard());
            Assert.IsFalse(gm.OnFoot);
            Assert.AreEqual(teleports, ShipController.TeleportCount, "nothing was relocated on foot");

            // ---- and the ship still flies ----------------------------------------
            ship.takeoffRequested = true; ship.inLift = 1f;
            yield return PGTestUtil.Fixed(180);
            PGTestUtil.ClearInputs(ship);
            Assert.IsFalse(ship.Landed, "take-off after boarding");
            Assert.Less((gm.PlayerLogical - ship.LogicalPosition).magnitude, 1e-6, "the player is the ship again");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator OnFoot_HullIsSolid_AndRelocatingTheShipBoardsThePilot()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship; var pc = gm.character;
            yield return Land(gm);
            Assert.IsTrue(pc.ExitShip());
            yield return PGTestUtil.Fixed(10);

            // Walk straight at the hull for 3 s: the character must stay outside it.
            pc.inMove = new Vector2(1f, 0f);
            double nearest = double.MaxValue;
            for (int i = 0; i < 180; i++)
            {
                yield return new WaitForFixedUpdate();
                Vector3 l = Quaternion.Inverse(ship.Orientation) * (pc.LogicalPosition - ship.LogicalPosition).ToVector3();
                nearest = System.Math.Min(nearest, System.Math.Abs(l.x));
            }
            pc.inMove = Vector2.zero;
            Assert.Greater(nearest, 2.0, "the parked hull blocks the walker");
            Assert.IsTrue(ship.Landed);

            // A debug jump moves the ship: the pilot goes with it.
            gm.DebugJumpToSurface("mirvalis", 2);
            yield return PGTestUtil.Fixed(5);
            Assert.IsFalse(gm.OnFoot, "relocating the ship puts the pilot back in the seat");
            Assert.IsFalse(gm.input.onFoot, "and hands the controls back");
        }
    }
}
