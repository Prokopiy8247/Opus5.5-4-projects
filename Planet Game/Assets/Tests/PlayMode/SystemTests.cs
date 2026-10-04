using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>Save/load, scene integrity and the long interplanetary route.</summary>
    public class SystemTests
    {
        [TearDown] public void TearDown() { Time.timeScale = 1f; }

        [UnityTest]
        public IEnumerator SaveLoad_InSpace_RestoresPoseVelocityTargetAndBeacons()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            PGTestUtil.ClearInputs(ship);
            ship.inThrottle = 0.4f; ship.inYaw = 0.3f;
            yield return PGTestUtil.Fixed(120);
            PGTestUtil.ClearInputs(ship);
            gm.DropBeacon();
            gm.SetTarget(gm.system.Get("kryosyne"));
            DVec3 p = ship.LogicalPosition, v = ship.LogicalVelocity;
            Quaternion q = ship.Orientation;
            Assert.IsTrue(gm.save.Save());

            ship.inThrottle = 1f; ship.inPitch = 0.5f;
            yield return PGTestUtil.Fixed(120);
            PGTestUtil.ClearInputs(ship);
            gm.DropBeacon();
            gm.SetTarget(gm.system.Get("tarnveth"));

            Assert.IsTrue(gm.save.Load());
            Assert.Less((ship.LogicalPosition - p).magnitude, 1e-6, "position");
            Assert.Less((ship.LogicalVelocity - v).magnitude, 1e-6, "velocity");
            Assert.Less(Quaternion.Angle(ship.Orientation, q), 0.01f, "orientation");
            Assert.AreEqual("kryosyne", gm.streamer.TargetBody.id, "target");
            Assert.AreEqual(1, gm.beacons.Count, "beacons");
            Assert.IsFalse(ship.Landed);
        }

        [UnityTest]
        public IEnumerator SaveLoad_OnSurface_AfterDebugPlacement_RestoresLandedStateAndSeeds()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            PGTestUtil.ClearInputs(ship);
            gm.DebugJumpToSurface("mirvalis", 1);       // narrow debug placement for this test only
            yield return PGTestUtil.Fixed(10);
            Time.timeScale = 4f;
            ship.landingRequested = true;
            float t = Time.time;
            while (!ship.Landed && Time.time - t < 90f) yield return new WaitForFixedUpdate();
            Time.timeScale = 1f;
            Assert.IsTrue(ship.Landed, "assisted landing must complete: " + ship.LandStatus);
            yield return PGTestUtil.Fixed(30);
            DVec3 p = ship.LogicalPosition; Quaternion q = ship.Orientation;
            int seed = gm.system.Get("mirvalis").seed;
            Assert.IsTrue(gm.save.Save());

            ship.takeoffRequested = true; ship.inLift = 1f;
            yield return PGTestUtil.Fixed(180);
            PGTestUtil.ClearInputs(ship);
            Assert.IsFalse(ship.Landed);

            Assert.IsTrue(gm.save.Load());
            yield return PGTestUtil.Fixed(10);
            Assert.IsTrue(ship.Landed, "landed state restored");
            Assert.Less((ship.LogicalPosition - p).magnitude, 0.05, "same spot on the same ground");
            Assert.Less(Quaternion.Angle(ship.Orientation, q), 1f);
            Assert.AreEqual(seed, gm.system.Get("mirvalis").seed);
            double gap = ship.AltitudeAGL - ship.GroundClearance;
            Assert.Less(System.Math.Abs(gap), 0.05, "gear rests on the ground");
        }

        [UnityTest]
        public IEnumerator SceneIntegrity_OneOfEverything_AllReferencesAssigned()
        {
            yield return PGTestUtil.LoadGame();
            Assert.AreEqual(1, Object.FindObjectsByType<GameManager>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<ShipController>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Count(c => c.isActiveAndEnabled));
            Assert.AreEqual(1, Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(a => a.isActiveAndEnabled));
            Assert.AreEqual(1, Object.FindObjectsByType<StarBody>(FindObjectsSortMode.None).Length, "Helion is drawn");
            Assert.AreEqual(1, Object.FindObjectsByType<Starfield>(FindObjectsSortMode.None).Length);
            var ws = WorldStreamer.Instance;
            Assert.IsNotNull(ws.TerrainMaterial); Assert.IsNotNull(ws.WaterMaterial);
            Assert.IsNotNull(ws.AtmosphereMaterial); Assert.IsNotNull(ws.CloudMaterial);
            var ship = ShipController.Instance;
            foreach (var r in ship.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials) Assert.IsNotNull(m, "missing material on " + r.name);
            Assert.Greater(ship.GetComponentsInChildren<MeshFilter>().Length, 10, "the imported Blender ship, not a placeholder");
            Assert.IsNotNull(ship.gearAnimator);
            foreach (var b in ws.system.Planets) Assert.IsNotNull(ws.RuntimeOf(b), b.id + " runtime");
#if UNITY_EDITOR
            var scenes = UnityEditor.EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            CollectionAssert.AreEqual(new[] { PGTestUtil.GameScene }, scenes, "exactly one startup scene in the build");
#endif
        }

        /// <summary>
        /// The real thing: from the fresh start near Mirvalis, the input-level
        /// pilot selects Tarn-Veth, turns, engages the pulse drive, crosses ~52 km
        /// of space, enters the atmosphere and lands - with continuous
        /// integration and no relocation of any kind.
        /// </summary>
        [UnityTest, Category("Long"), Timeout(1200000)]
        public IEnumerator RealInterplanetaryRoute_MirvalisToTarnVeth_IsContinuousFlightAndLands()
        {
            yield return PGTestUtil.LoadGame();
            var gm = GameManager.Instance; var ship = gm.ship;
            int tp0 = ShipController.TeleportCount;
            ship.ResetStepStats();
            Time.timeScale = 3f;
            var rp = gm.StartRoutePilot(null, null, false);
            rp.fullRoute = false;
            rp.targets = new[] { "tarnveth" };
            float start = Time.realtimeSinceStartup;
            while (!rp.Done && Time.realtimeSinceStartup - start < 1100f) yield return null;
            Time.timeScale = 1f;
            foreach (var line in rp.Report) Debug.Log(line);
            Assert.IsTrue(rp.Done, "route did not finish in time");
            Assert.IsFalse(rp.Failed, rp.FailReason);
            Assert.AreEqual(tp0, ShipController.TeleportCount, "no teleport");
            Assert.AreEqual(0, GameManager.SceneLoadsSinceStart, "no scene load");
            Assert.LessOrEqual(ship.MaxStepRatio, 1.0, "continuous, physically bounded motion");
            Assert.AreEqual("tarnveth", gm.streamer.CurrentBody.id);
            Assert.IsTrue(ship.Landed);
            Assert.That(rp.HopSeconds["tarnveth"], Is.InRange(25f, 120f), "interplanetary leg duration");
            Assert.AreEqual(0, rp.CoverageFailures, rp.FirstCoverageFailure);
        }
    }
}
