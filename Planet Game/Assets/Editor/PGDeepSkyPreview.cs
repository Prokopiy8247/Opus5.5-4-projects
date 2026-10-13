using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PG.EditorTools
{
    /// <summary>
    /// Renders the deep sky (DeepSky + Starfield) in a throw-away scene through
    /// the game's URP camera settings and post-processing, without starting the
    /// game. Also verifies the cubemap face orientation: with the bake in
    /// direction-debug mode, every pixel must show its own view direction.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod PG.EditorTools.PGDeepSkyPreview.RenderAll
    /// Output: Artifacts/Screenshots/DeepSky/*.png
    /// </summary>
    public static class PGDeepSkyPreview
    {
        const string OutDir = "Artifacts/Screenshots/DeepSky";

        [MenuItem("Tools/PG/Render Deep Sky Preview", priority = 60)]
        public static void RenderAll()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), OutDir);
            Directory.CreateDirectory(outDir);
            Shader.SetGlobalFloat("_PG_StarFade", 1f);

            var deep = new GameObject("DeepSky").AddComponent<DeepSky>();
            deep.bakeShader = Shader.Find("Hidden/PG/DeepSkyBake");
            deep.skyMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PG_DeepSky.mat");
            deep.galaxyMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PG_Galaxy.mat");
            deep.blackHoleMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PG_BlackHole.mat");

            var camGo = new GameObject("PreviewCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 400000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.004f, 0.005f, 0.010f);
            cam.allowHDR = true;
            var camData = cam.GetUniversalAdditionalCameraData();

            try
            {
                // ---- 1. orientation check
                deep.Build(debugDirections: true);
                deep.transform.Find("DeepSky_Galaxies").gameObject.SetActive(false);
                if (deep.BlackHoleRenderer != null) deep.BlackHoleRenderer.gameObject.SetActive(false);
                camData.renderPostProcessing = false;
                bool ok = CheckOrientation(cam);
                Debug.Log("[PG] DEEPSKY ORIENTATION " + (ok ? "PASS" : "FAIL"));

                // ---- 2. pretty views with the game's post-processing and stars
                deep.Build();
                var stars = new GameObject("Starfield");
                stars.AddComponent<MeshFilter>().sharedMesh = Starfield.Build(7000, 20261003);
                stars.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PG_Stars.mat");
                var vol = new GameObject("PostFX").AddComponent<Volume>();
                vol.isGlobal = true;
                vol.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/PG_PostFX.asset");
                camData.renderPostProcessing = true;
                camData.dithering = true;

                var big = deep.Galaxies[0].dir;
                Shot(cam, outDir, "01_galactic_centre", GalacticFrame.Dir(0f, 0f), 62f);
                Shot(cam, outDir, "02_band_keel_coalsack", GalacticFrame.Dir(-65f, 0f), 62f);
                Shot(cam, outDir, "03_magellanic_clouds", GalacticFrame.Dir(-70f, -35f), 62f);
                Shot(cam, outDir, "04_swan_veil", GalacticFrame.Dir(78f, -2f), 62f);
                Shot(cam, outDir, "05_big_spiral", big, 62f);
                Shot(cam, outDir, "06_helions_veil", GalacticFrame.Dir(150f, 22f), 62f);
                Shot(cam, outDir, "07_anticentre", GalacticFrame.Dir(180f, 0f), 62f);
                Shot(cam, outDir, "08_north_pole_galaxies", GalacticFrame.Pole, 62f);
                Shot(cam, outDir, "09_ring_close", GalacticFrame.Dir(-150f, 36f), 12f);
                Shot(cam, outDir, "10_big_spiral_close", big, 20f);
                Shot(cam, outDir, "11_edge_on_close", deep.Galaxies[4].dir, 14f);
                Shot(cam, outDir, "12_lagoon_close", GalacticFrame.Dir(6f, -1.5f), 20f);
                Shot(cam, outDir, "13_world_forward", Vector3.forward, 62f);
                Shot(cam, outDir, "14_world_right", Vector3.right, 62f);
                Shot(cam, outDir, "15_world_back", Vector3.back, 62f);
                Shot(cam, outDir, "16_world_left", Vector3.left, 62f);
                Shot(cam, outDir, "17_black_hole", deep.BlackHoleDir, 62f);
                Shot(cam, outDir, "18_black_hole_close", deep.BlackHoleDir, 24f);
                Shot(cam, outDir, "19_black_hole_wide", deep.BlackHoleDir, 90f);
                Debug.Log("[PG] DEEPSKY PREVIEW OK - " + outDir);
            }
            catch (Exception e)
            {
                Debug.LogError("[PG] DEEPSKY PREVIEW FAILED: " + e);
                throw;
            }
        }

        static void Aim(Camera cam, Vector3 dir, float fov)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, GalacticFrame.Pole)) < 0.98f ? GalacticFrame.Pole : Vector3.forward;
            cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(dir, up));
            cam.fieldOfView = fov;
        }

        static bool CheckOrientation(Camera cam)
        {
            const int S = 128;
            var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var tex = new Texture2D(S, S, TextureFormat.RGBAFloat, false, true);
            bool ok = true;
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                               new Vector3(1, 1, 1), new Vector3(-1, 0.3f, 0.6f) };
            Vector2[] probes = { new Vector2(0.5f, 0.5f), new Vector2(0.85f, 0.5f), new Vector2(0.5f, 0.85f), new Vector2(0.15f, 0.2f) };
            foreach (var d in dirs)
            {
                Aim(cam, d.normalized, 70f);
                cam.aspect = 1f;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, S, S), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                foreach (var p in probes)
                {
                    Color c = tex.GetPixel((int)(p.x * S), (int)(p.y * S));
                    Vector3 got = new Vector3(c.r, c.g, c.b) * 2f - Vector3.one;
                    Vector3 want = cam.ViewportPointToRay(new Vector3(p.x, p.y, 0f)).direction;
                    float dot = Vector3.Dot(got.normalized, want.normalized);
                    if (dot < 0.995f)
                    {
                        ok = false;
                        Debug.LogWarning("[PG] deepsky orientation: look " + d + " probe " + p + " got " + got.ToString("F3") + " want " + want.ToString("F3"));
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            return ok;
        }

        static void Shot(Camera cam, string dir, string name, Vector3 look, float fov)
        {
            const int W = 1920, H = 1080;
            Aim(cam, look, fov);
            cam.aspect = (float)W / H;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
