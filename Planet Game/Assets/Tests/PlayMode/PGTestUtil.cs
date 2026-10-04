using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace PG.Tests
{
    /// <summary>Shared helpers. Tests run against the REAL startup scene, never a hand-built world.</summary>
    public static class PGTestUtil
    {
        public const string GameScene = "Assets/Scenes/Game.unity";

        public static IEnumerator LoadGame()
        {
            Time.timeScale = 1f;
            // Freeze the previous test's world before it is unloaded, then clear
            // the static handles so nothing from it can be picked up.
            if (GameManager.Instance != null) GameManager.Instance.enabled = false;
            if (ShipController.Instance != null) ShipController.Instance.enabled = false;
            PGSingletons.ResetAll();
            SaveSystem.PathOverride = Path.Combine(Application.temporaryCachePath, "pg_test_save.json");
#if UNITY_EDITOR
            var op = EditorSceneManager.LoadSceneAsyncInPlayMode(GameScene, new LoadSceneParameters(LoadSceneMode.Single));
            while (!op.isDone) yield return null;
#endif
            yield return null;
            yield return null;
            var gm = GameManager.Instance;
            gm.input.acceptInput = false;   // tests drive the control fields directly
            yield return new WaitForFixedUpdate();
        }

        public static IEnumerator Fixed(int steps)
        {
            for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
        }

        public static void ClearInputs(ShipController s)
        {
            s.inThrottle = 0f; s.inYaw = 0f; s.inPitch = 0f; s.inRoll = 0f; s.inLift = 0f;
            s.brake = false; s.boost = false;
        }

        /// <summary>A copy of a default-system body, without loading any scene.</summary>
        public static BodyDef Body(string id)
        {
            // Inactive, so PlanetSystem.Awake never runs and never touches the
            // running game's singleton.
            var go = new GameObject("TestSystem");
            go.SetActive(false);
            var sys = go.AddComponent<PlanetSystem>();
            sys.BuildDefaultSystem();
            var b = sys.Get(id);
            Object.DestroyImmediate(go);
            return b;
        }

        /// <summary>A planet quadtree on its own, ticked manually by the test.</summary>
        public static PlanetRuntime StandalonePlanet(BodyDef def)
        {
            var go = new GameObject("TestPlanet_" + def.id);
            var pr = go.AddComponent<PlanetRuntime>();
            pr.SurfaceMaterial = new Material(Shader.Find("PG/Terrain"));
            pr.Init(def);
            return pr;
        }
    }
}
