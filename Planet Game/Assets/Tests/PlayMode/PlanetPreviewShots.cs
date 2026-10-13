using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>
    /// Visual check of Pelagia and Vharos through the real game camera. Explicit:
    /// it only runs when selected, and needs a graphics device (no -nographics).
    /// Writes PNGs to Artifacts/Screenshots/Planets.
    /// </summary>
    public class PlanetPreviewShots
    {
        [TearDown] public void TearDown() { Time.timeScale = 1f; }

        static readonly string Dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Artifacts", "Screenshots", "Planets"));

        [UnityTest, Explicit, Category("Preview"), Timeout(600000)]
        public IEnumerator Preview_NewPlanets()
        {
            yield return PGTestUtil.LoadGame();
            Directory.CreateDirectory(Dir);
            var gm = GameManager.Instance; var ship = gm.ship;
            var gas = gm.system.Get("vharos");
            var sea = gm.system.Get("pelagia");
            gm.hud.enabled = false;

            // Vharos from 28 km, lit side, slightly off-centre.
            DVec3 toSun = (gm.system.Star.LogicalPos - gas.LogicalPos).normalized;
            DVec3 side = DVec3.Cross(toSun, new DVec3(0, 1, 0)).normalized;
            yield return Shot(ship, gas.LogicalPos + (toSun * 0.85 + side * 0.5).normalized * 28000.0, gas.LogicalPos, "vharos_orbit");
            // Skimming the haze top toward the limb.
            DVec3 a = (toSun + side * 0.2).normalized;
            yield return Shot(ship, gas.LogicalPos + a * (gas.radius + 900.0), gas.LogicalPos + side * (gas.radius * 1.2), "vharos_haze");
            // In the cloud decks, 1 km under the top.
            yield return Shot(ship, gas.LogicalPos + toSun * (gas.radius - 1000.0), gas.LogicalPos + side * gas.radius, "vharos_deck");
            // Near the quiet core.
            yield return Shot(ship, gas.LogicalPos + toSun * 1500.0, gas.LogicalPos - side * 3000.0, "vharos_core");

            // Pelagia from orbit, and low over the islands.
            DVec3 sSun = (gm.system.Star.LogicalPos - sea.LogicalPos).normalized;
            DVec3 sSide = DVec3.Cross(sSun, new DVec3(0, 1, 0)).normalized;
            yield return Shot(ship, sea.LogicalPos + (sSun * 0.8 + sSide * 0.45).normalized * (sea.radius + 7000.0), sea.LogicalPos, "pelagia_orbit");
            gm.DebugJumpToSurface("pelagia", 0);
            ship.inThrottle = 0f;
            yield return Settle(4f);
            yield return Capture("pelagia_low");
            ship.inPitch = -0.6f;
            yield return new WaitForSeconds(0.6f);
            ship.inPitch = 0f;
            yield return Settle(2f);
            yield return Capture("pelagia_low_down");

            // The island under that landing spot: straight down from 450 m, then
            // across it from 60 m.
            DVec3 spot = (ship.LogicalPosition - sea.LogicalPos).normalized;
            double ground = sea.radius + TerrainFunc.Elevation(sea, gm.streamer.RuntimeOf(sea).Perm, spot);
            DVec3 east = DVec3.Cross(spot, new DVec3(0, 1, 0)).normalized;
            yield return Shot(ship, sea.LogicalPos + spot * (ground + 450.0), sea.LogicalPos, "pelagia_island_top", 4f);
            yield return Shot(ship, sea.LogicalPos + (spot * ground + east * 220.0).normalized * (ground + 60.0), sea.LogicalPos + spot * (ground + 15.0), "pelagia_island_side", 4f);
        }

        static IEnumerator Shot(ShipController ship, DVec3 pos, DVec3 lookAt, string name, float settle = 1.5f)
        {
            Vector3 fwd = (lookAt - pos).normalized.ToVector3();
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, fwd);
            if (up.sqrMagnitude < 0.01f) up = Vector3.ProjectOnPlane(Vector3.right, fwd);
            ship.Orientation = Quaternion.LookRotation(fwd, up.normalized);
            ship.TeleportLogical(pos, DVec3.zero);
            PGTestUtil.ClearInputs(ship);
            CameraRig.Instance.SnapImmediate();
            yield return Settle(settle);
            yield return Capture(name);
        }

        static IEnumerator Settle(float seconds)
        {
            float t = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t < seconds) yield return null;
        }

        static IEnumerator Capture(string name)
        {
            yield return null;   // not WaitForEndOfFrame: it never fires in batch mode
            var cam = CameraRig.Instance.cam;
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Object.Destroy(rt);
            Object.Destroy(tex);
            Debug.Log("[PREVIEW] " + name + " -> " + Path.Combine(Dir, name + ".png"));
        }
    }
}
