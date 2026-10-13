using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>
    /// The deep sky in the real game scene, through the real game camera and
    /// post-processing. Needs a GPU (run without -nographics); writes the frames
    /// to Artifacts/Screenshots/DeepSky/ingame for a look.
    /// </summary>
    public class DeepSkyTests
    {
        [UnityTest, Category("Visual")]
        public IEnumerator DeepSky_IsVisibleInTheGameCamera()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a GPU");
            yield return PGTestUtil.LoadGame();
            for (int i = 0; i < 10; i++) yield return null;

            var cam = Camera.main;
            var deep = Object.FindAnyObjectByType<DeepSky>();
            Assert.IsNotNull(deep);
            Assert.IsNotNull(deep.SkyCube);
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Artifacts/Screenshots/DeepSky/ingame");
            Directory.CreateDirectory(dir);

            Save(Render(cam), dir, "G01_opening_view");

            var rig = CameraRig.Instance;
            rig.enabled = false;
            try
            {
                Look(cam, GalacticFrame.Dir(-40f, -10f));
                Save(Render(cam), dir, "G02_milky_way_keel");
                Look(cam, deep.Galaxies[0].dir);
                Save(Render(cam), dir, "G03_big_spiral");
                Look(cam, GalacticFrame.Dir(150f, 22f));
                Save(Render(cam), dir, "G04_helions_veil");

                // The sky must actually add light: galactic centre with and without it.
                Look(cam, GalacticFrame.Centre);
                var on = Render(cam);
                Save(on, dir, "G05_galactic_centre");
                var renderers = deep.GetComponentsInChildren<Renderer>();
                foreach (var r in renderers) r.enabled = false;
                var off = Render(cam);
                foreach (var r in renderers) r.enabled = true;
                float lOn = MeanLuma(on), lOff = MeanLuma(off);
                Debug.Log("[PG] deep sky luma at galactic centre: with " + lOn.ToString("F4") + ", without " + lOff.ToString("F4"));
                Assert.Greater(lOn, lOff + 0.02f, "Milky Way core brightens the view");
                Object.Destroy(on); Object.Destroy(off);
            }
            finally { rig.enabled = true; }
        }

        /// <summary>The black hole is a picture on the sky and nothing else.</summary>
        [UnityTest]
        public IEnumerator BlackHole_IsSceneryOnly()
        {
            yield return PGTestUtil.LoadGame();
            var deep = Object.FindAnyObjectByType<DeepSky>();
            Assert.IsNotNull(deep.blackHoleMaterial, "black hole material assigned in the scene");
            Assert.IsNotNull(deep.BlackHoleRenderer, "black hole is drawn");
            Assert.AreEqual(0, deep.GetComponentsInChildren<Collider>(true).Length, "nothing on the sky can be hit");
            Assert.AreEqual(0, deep.GetComponentsInChildren<Rigidbody>(true).Length);
            Assert.AreEqual(0, deep.GetComponentsInChildren<Light>(true).Length, "it lights nothing");
            foreach (var b in WorldStreamer.Instance.system.bodies)
                StringAssert.DoesNotContain("hole", b.id.ToLowerInvariant(), "not a body of the system: no gravity, no navigation");

            // Fixed to the sky: flying does not move it, it only follows the view rotation.
            var mesh = deep.BlackHoleMesh;
            Vector3 c = (mesh.vertices[0] + mesh.vertices[3]) * 0.5f;
            Assert.Less(Vector3.Angle(c, deep.BlackHoleDir), 0.01f);
            Assert.AreEqual(Vector3.zero, deep.BlackHoleRenderer.transform.localPosition);
        }

        [UnityTest, Category("Visual")]
        public IEnumerator BlackHole_IsVisibleInTheGameCamera()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a GPU");
            yield return PGTestUtil.LoadGame();
            for (int i = 0; i < 10; i++) yield return null;

            var cam = Camera.main;
            var deep = Object.FindAnyObjectByType<DeepSky>();
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Artifacts/Screenshots/DeepSky/ingame");
            Directory.CreateDirectory(dir);

            var rig = CameraRig.Instance;
            rig.enabled = false;
            try
            {
                Look(cam, deep.BlackHoleDir);
                var on = Render(cam);
                Save(on, dir, "G06_black_hole");
                deep.BlackHoleRenderer.enabled = false;
                var off = Render(cam);
                deep.BlackHoleRenderer.enabled = true;

                // the shadow in the middle is black, the disk around it is the brightest thing in view
                Color32 mid = on.GetPixel(on.width / 2, on.height / 2);
                Assert.Less(mid.r + mid.g + mid.b, 30, "black shadow at the centre");
                int bright = Bright(on), brightOff = Bright(off);
                Debug.Log("[PG] black hole: bright pixels with " + bright + ", without " + brightOff);
                Assert.Greater(bright, brightOff + 20000, "accretion disk is large and bright");
                Object.Destroy(on); Object.Destroy(off);

                // from the side the rest of the sky is untouched
                Look(cam, -deep.BlackHoleDir);
                var away = Render(cam);
                deep.BlackHoleRenderer.enabled = false;
                var awayOff = Render(cam);
                deep.BlackHoleRenderer.enabled = true;
                Assert.AreEqual(MeanLuma(awayOff), MeanLuma(away), 1e-4f, "nothing changes away from the hole");
                Object.Destroy(away); Object.Destroy(awayOff);
            }
            finally { rig.enabled = true; }
        }

        static int Bright(Texture2D t)
        {
            int n = 0;
            foreach (var c in t.GetPixels32()) if (c.r > 150) n++;
            return n;
        }

        static void Look(Camera cam, Vector3 dir)
        {
            cam.transform.rotation = Quaternion.LookRotation(dir, GalacticFrame.Pole);
        }

        static Texture2D Render(Camera cam)
        {
            const int W = 1920, H = 1080;
            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        static float MeanLuma(Texture2D t)
        {
            var px = t.GetPixels32();
            double s = 0;
            foreach (var c in px) s += (c.r + c.g + c.b) / (3.0 * 255.0);
            return (float)(s / px.Length);
        }

        static void Save(Texture2D t, string dir, string name)
        {
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), t.EncodeToPNG());
        }
    }
}
