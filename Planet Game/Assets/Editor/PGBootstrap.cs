using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace PG.EditorTools
{
    /// <summary>
    /// Builds the whole runnable game from code: materials, the post-processing
    /// profile, the ship prefab (from the Blender export) and the ONE startup
    /// scene. Nothing has to be assembled by hand.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod PG.EditorTools.PGBootstrap.BuildAll
    /// </summary>
    public static class PGBootstrap
    {
        public const string ScenePath      = "Assets/Scenes/Game.unity";
        const string MatDir         = "Assets/Materials";
        const string PrefabDir      = "Assets/Prefabs";
        public const string ShipFbx = "Assets/Art/Ship/Ship_Explorer.fbx";
        public const string PropsFbx = "Assets/Art/Props/PG_Props.fbx";
        public static readonly string[] PoiFbx =
        {
            "Assets/Art/POI/POI_TarnVeth_Arch.fbx",
            "Assets/Art/POI/POI_Mirvalis_Spires.fbx",
            "Assets/Art/POI/POI_Kryosyne_Rift.fbx",
        };
        public const string ShipPrefabPath = "Assets/Prefabs/PlayerShip.prefab";
        const string PostFxPath     = "Assets/Settings/PG_PostFX.asset";

        // ------------------------------------------------------------- entry

        [MenuItem("Tools/PG/Build Game Scene", priority = 20)]
        public static void BuildAll()
        {
            try
            {
                EnsureFolders();
                var mats = BuildMaterials();
                ImportShipModel(mats);
                ImportPropModels(mats);
                var shipPrefab = BuildShipPrefab(mats);
                BuildScene(mats, shipPrefab);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[PG] BUILD OK - scene written to " + ScenePath);
                VerifyScene();
            }
            catch (Exception e)
            {
                Debug.LogError("[PG] BUILD FAILED: " + e);
                throw;
            }
        }

        static void EnsureFolders()
        {
            foreach (var d in new[] { "Assets/Art", "Assets/Art/Ship", "Assets/Scenes", MatDir, PrefabDir, "Assets/Settings" })
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), d));
            AssetDatabase.Refresh();
        }

        // --------------------------------------------------------- materials

        public class Mats
        {
            public Material terrain, water, atmo, clouds, sun, corona, stars, exhaust, plasma;
            public Material hull, accent, dark, glass, glow;
            public Material prop, crystal, landmark, beacon, discovery;
            public Material screen, flora, poiSandstone, poiBasalt, poiObsidian, poiEmber;
        }

        static Material MakeMat(string name, string shaderName, Action<Material> setup)
        {
            string path = MatDir + "/" + name + ".mat";
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new Exception("shader not found: " + shaderName);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            else if (m.shader != shader) m.shader = shader;
            setup?.Invoke(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void C(Material m, string p, Color c) { if (m.HasProperty(p)) m.SetColor(p, c); }
        static void F(Material m, string p, float f) { if (m.HasProperty(p)) m.SetFloat(p, f); }

        static Mats BuildMaterials()
        {
            var m = new Mats();
            m.terrain = MakeMat("PG_Terrain", "PG/Terrain", mt => { C(mt, "_Tint", Color.white); F(mt, "_SpecAmount", 0.04f); });
            m.water   = MakeMat("PG_Water", "PG/Water", null);
            m.atmo    = MakeMat("PG_Atmosphere", "PG/Atmosphere", mt => F(mt, "_Intensity", 1.25f));
            m.clouds  = MakeMat("PG_Clouds", "PG/Clouds", null);
            m.sun     = MakeMat("PG_Sun", "PG/Sun", mt => { C(mt, "_Color", new Color(1f, 0.84f, 0.60f)); F(mt, "_Intensity", 7f); });
            m.corona  = MakeMat("PG_Corona", "PG/Glow", mt => { C(mt, "_Color", new Color(1f, 0.72f, 0.42f)); F(mt, "_Intensity", 1.3f); F(mt, "_Falloff", 3.4f); });
            m.stars   = MakeMat("PG_Stars", "PG/Stars", mt => F(mt, "_Brightness", 1.7f));
            m.exhaust = MakeMat("PG_Exhaust", "PG/Glow", mt => { C(mt, "_Color", new Color(0.55f, 0.80f, 1f)); F(mt, "_Intensity", 1.6f); F(mt, "_Falloff", 1.7f); });
            m.plasma  = MakeMat("PG_Plasma", "PG/Glow", mt => { C(mt, "_Color", new Color(1f, 0.62f, 0.32f)); F(mt, "_Intensity", 1.5f); F(mt, "_Falloff", 1.9f); });

            m.hull = MakeMat("PG_ShipHull", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.80f, 0.82f, 0.84f)); F(mt, "_Metallic", 0.35f); F(mt, "_Smoothness", 0.62f);
                F(mt, "_RimBoost", 0.12f); F(mt, "_PanelLines", 1f); F(mt, "_PanelSize", 1.25f); F(mt, "_LineWidth", 0.012f);
            });
            m.accent = MakeMat("PG_ShipAccent", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.86f, 0.36f, 0.12f)); F(mt, "_Metallic", 0.25f); F(mt, "_Smoothness", 0.58f);
                F(mt, "_RimBoost", 0.10f); F(mt, "_PanelLines", 0.6f); F(mt, "_PanelSize", 0.9f);
            });
            m.dark = MakeMat("PG_ShipDark", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.11f, 0.12f, 0.13f)); F(mt, "_Metallic", 0.85f); F(mt, "_Smoothness", 0.48f);
                F(mt, "_RimBoost", 0.06f); F(mt, "_PanelLines", 0f);
            });
            m.glass = MakeMat("PG_ShipGlass", "PG/ShipGlass", mt => { C(mt, "_Tint", new Color(0.12f, 0.24f, 0.32f)); F(mt, "_Opacity", 0.5f); });
            m.glow = MakeMat("PG_ShipGlow", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.08f, 0.10f, 0.14f)); F(mt, "_Metallic", 0f); F(mt, "_Smoothness", 0.3f);
                C(mt, "_Emissive", new Color(0.45f, 0.78f, 1f)); F(mt, "_EmissiveBoost", 3f); F(mt, "_PanelLines", 0f);
            });

            m.prop = MakeMat("PG_Prop", "PG/Ship", mt => { C(mt, "_BaseColor", new Color(0.40f, 0.35f, 0.30f)); F(mt, "_Metallic", 0.05f); F(mt, "_Smoothness", 0.15f); F(mt, "_RimBoost", 0.05f); });
            m.crystal = MakeMat("PG_Crystal", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.35f, 0.62f, 0.72f)); F(mt, "_Metallic", 0.25f); F(mt, "_Smoothness", 0.86f);
                C(mt, "_Emissive", new Color(0.16f, 0.42f, 0.52f)); F(mt, "_EmissiveBoost", 0.8f); F(mt, "_RimBoost", 0.4f);
            });
            m.landmark = MakeMat("PG_Landmark", "PG/Ship", mt => { C(mt, "_BaseColor", new Color(0.56f, 0.46f, 0.36f)); F(mt, "_Metallic", 0.1f); F(mt, "_Smoothness", 0.3f); F(mt, "_RimBoost", 0.15f); });
            m.beacon = MakeMat("PG_Beacon", "PG/Ship", mt => { C(mt, "_BaseColor", new Color(0.25f, 0.30f, 0.34f)); C(mt, "_Emissive", new Color(0.25f, 1.0f, 0.45f)); F(mt, "_EmissiveBoost", 3f); });
            m.discovery = MakeMat("PG_Discovery", "PG/Ship", mt => { C(mt, "_BaseColor", new Color(0.34f, 0.28f, 0.20f)); C(mt, "_Emissive", new Color(1.0f, 0.78f, 0.22f)); F(mt, "_EmissiveBoost", 3f); });

            m.screen = MakeMat("PG_ShipScreen", "PG/Ship", mt =>
            {
                // Instrument displays: a dim teal glow with a fine grid, so they
                // read as screens rather than flat light panels.
                C(mt, "_BaseColor", new Color(0.02f, 0.05f, 0.06f)); F(mt, "_Metallic", 0f); F(mt, "_Smoothness", 0.85f);
                C(mt, "_Emissive", new Color(0.12f, 0.52f, 0.62f)); F(mt, "_EmissiveBoost", 0.9f);
                F(mt, "_PanelLines", 1f); F(mt, "_PanelSize", 0.03f); F(mt, "_LineWidth", 0.07f); F(mt, "_RimBoost", 0f);
            });
            m.flora = MakeMat("PG_Flora", "PG/Prop", mt => { C(mt, "_Tint", Color.white); F(mt, "_Wrap", 0.3f); mt.enableInstancing = true; });
            m.poiSandstone = MakeMat("PG_PoiSandstone", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.70f, 0.42f, 0.27f)); F(mt, "_Metallic", 0.02f); F(mt, "_Smoothness", 0.18f);
                F(mt, "_RimBoost", 0.08f); F(mt, "_PanelLines", 0.35f); F(mt, "_PanelSize", 6.0f); F(mt, "_LineWidth", 0.02f);
            });
            m.poiBasalt = MakeMat("PG_PoiBasalt", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.17f, 0.17f, 0.19f)); F(mt, "_Metallic", 0.1f); F(mt, "_Smoothness", 0.42f);
                F(mt, "_RimBoost", 0.12f); F(mt, "_PanelLines", 0f);
            });
            m.poiObsidian = MakeMat("PG_PoiObsidian", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.06f, 0.05f, 0.08f)); F(mt, "_Metallic", 0.35f); F(mt, "_Smoothness", 0.86f);
                F(mt, "_RimBoost", 0.3f); F(mt, "_PanelLines", 0f);
            });
            m.poiEmber = MakeMat("PG_PoiEmber", "PG/Ship", mt =>
            {
                C(mt, "_BaseColor", new Color(0.20f, 0.06f, 0.02f)); F(mt, "_Metallic", 0f); F(mt, "_Smoothness", 0.3f);
                C(mt, "_Emissive", new Color(1.0f, 0.42f, 0.12f)); F(mt, "_EmissiveBoost", 3.5f); F(mt, "_PanelLines", 0f);
            });

            // Old material assets that no longer exist in the design.
            foreach (var stale in new[] { "PG_FarPlanet", "PG_ShipCanopy", "PG_ShipGear" })
                AssetDatabase.DeleteAsset(MatDir + "/" + stale + ".mat");
            return m;
        }

        // -------------------------------------------------------- ship model

        /// <summary>Blender material name -> PG material, for both the old and the new export.</summary>
        static Dictionary<string, Material> MaterialRemap(Mats m) => new Dictionary<string, Material>
        {
            { "SHIP_Hull", m.hull }, { "PG_HullMetal", m.hull }, { "Material", m.hull },
            { "SHIP_Accent", m.accent }, { "PG_HullAccent", m.accent },
            { "SHIP_Dark", m.dark }, { "PG_GearMetal", m.dark },
            { "SHIP_Glass", m.glass }, { "PG_CanopyGlass", m.glass },
            { "SHIP_Glow", m.glow }, { "PG_EngineGlow", m.glow },
            { "SHIP_Screen", m.screen },
            { "POI_Sandstone", m.poiSandstone }, { "POI_Basalt", m.poiBasalt },
            { "POI_Obsidian", m.poiObsidian }, { "POI_Ember", m.poiEmber },
        };

        /// <summary>
        /// The Blender prop kit (meshes only, vertex-coloured, drawn instanced by
        /// FloraField) and the three landmark models (materials remapped by name).
        /// </summary>
        public static void ImportPropModels(Mats mats)
        {
            var pi = AssetImporter.GetAtPath(PropsFbx) as ModelImporter;
            if (pi == null) throw new Exception("prop kit missing at " + PropsFbx + " - export PROPS_Flora from Blender first");
            pi.globalScale = 1f;
            pi.useFileScale = true;
            pi.bakeAxisConversion = true;
            pi.importNormals = ModelImporterNormals.Import;
            pi.importTangents = ModelImporterTangents.None;
            pi.materialImportMode = ModelImporterMaterialImportMode.None;
            pi.importAnimation = false;
            pi.importCameras = false;
            pi.importLights = false;
            pi.isReadable = false;
            pi.SaveAndReimport();

            foreach (var path in PoiFbx)
            {
                var im = AssetImporter.GetAtPath(path) as ModelImporter;
                if (im == null) throw new Exception("landmark model missing at " + path);
                im.globalScale = 1f;
                im.useFileScale = true;
                im.bakeAxisConversion = true;
                im.importNormals = ModelImporterNormals.Import;
                im.importTangents = ModelImporterTangents.None;
                im.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                im.materialLocation = ModelImporterMaterialLocation.InPrefab;
                im.importAnimation = false;
                im.importCameras = false;
                im.importLights = false;
                im.isReadable = false;
                foreach (var kv in MaterialRemap(mats))
                    im.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
                im.SaveAndReimport();
            }
            Debug.Log("[PG] prop kit + landmarks imported");
        }

        /// <summary>
        /// Each prop object's transform inside the imported FBX prefab (root axis
        /// conversion included), so instanced drawing places the raw mesh exactly
        /// as the model hierarchy would: base on the ground, up along +Y.
        /// </summary>
        static Matrix4x4[] PropMeshMatrices(params string[] names)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(PropsFbx);
            var result = new Matrix4x4[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var t = FindDeep(root.transform, names[i]);
                if (t == null) throw new Exception("prop object '" + names[i] + "' not found in " + PropsFbx);
                result[i] = root.transform.worldToLocalMatrix * t.localToWorldMatrix;
                result[i] = Matrix4x4.TRS(Vector3.zero, root.transform.localRotation, Vector3.one) * result[i];
            }
            return result;
        }

        static Mesh PropMesh(string name)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(PropsFbx))
                if (o is Mesh m && m.name == name) return m;
            throw new Exception("prop mesh '" + name + "' not found in " + PropsFbx);
        }

        public static void ImportShipModel(Mats mats)
        {
            var importer = AssetImporter.GetAtPath(ShipFbx) as ModelImporter;
            if (importer == null) throw new Exception("ship model missing at " + ShipFbx + " - export it from Blender first");
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.isReadable = true;
            foreach (var kv in MaterialRemap(mats))
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            importer.SaveAndReimport();
            Debug.Log("[PG] ship model imported: " + ShipFbx);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindDeep(root.GetChild(i), name);
                if (t != null) return t;
            }
            return null;
        }

        static Transform Anchor(Transform parent, string name, Vector3 localPos)
        {
            var t = FindDeep(parent, name);
            if (t != null) return t;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        static GameObject BuildShipPrefab(Mats mats)
        {
            var root = new GameObject("PlayerShip");
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 14000f;
            rb.useGravity = false;
            rb.isKinematic = true;

            var sc = root.AddComponent<ShipController>();
            var audio = root.AddComponent<ShipAudio>();
            audio.ship = sc;

            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            sc.shipVisual = visual.transform;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ShipFbx);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.name = "ShipModel";
            // Keep the importer's own root transform: it carries the FBX axis
            // conversion (Blender Z-up -> Unity Y-up). The Blender source has the
            // nose on +Y, which this conversion turns into Unity +Z; resetting it
            // here is what laid the ship on its back.
            inst.transform.SetParent(visual.transform, false);

            var renderers = inst.GetComponentsInChildren<MeshRenderer>(true);
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            var glow = new List<Renderer>();
            foreach (var mr in renderers)
            {
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var lb = mf.sharedMesh.bounds;
                    foreach (var c in Corners(lb))
                    {
                        Vector3 p = root.transform.InverseTransformPoint(mr.transform.TransformPoint(c));
                        if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                    }
                }
                if (mr.sharedMaterials.Any(x => x == mats.glow)) glow.Add(mr);
            }
            sc.engineGlowRenderers = glow.ToArray();

            // Named anchors come from the Blender export; the fallbacks below are
            // only for a model exported without them.
            var t = visual.transform;
            sc.noseAnchor = Anchor(t, "Nose", new Vector3(0f, b.center.y, b.max.z));
            sc.cockpitAnchor = Anchor(t, "Cockpit", new Vector3(0f, b.max.y - 0.9f, b.max.z * 0.45f));
            var engL = Anchor(t, "Engine_L", new Vector3(-1.2f, b.center.y, b.min.z));
            var engR = Anchor(t, "Engine_R", new Vector3(1.2f, b.center.y, b.min.z));
            sc.engineAnchors = new[] { engL, engR };
            var gF = Anchor(t, "Gear_F", new Vector3(0f, b.min.y, b.max.z * 0.5f));
            var gL = Anchor(t, "Gear_L", new Vector3(-2.5f, b.min.y, b.min.z * 0.4f));
            var gR = Anchor(t, "Gear_R", new Vector3(2.5f, b.min.y, b.min.z * 0.4f));
            sc.gearFeet = new[] { gF, gL, gR };

            var legs = new[] { "GearPivot_F", "GearPivot_L", "GearPivot_R" }.Select(n => FindDeep(t, n)).Where(x => x != null).ToArray();
            if (legs.Length == 3)
            {
                var ga = visual.AddComponent<GearAnimator>();
                ga.legs = legs;
                ga.stowEuler = new[] { new Vector3(-100f, 0f, 0f), new Vector3(0f, 0f, 100f), new Vector3(0f, 0f, -100f) };
                sc.gearAnimator = ga;
            }

            var col = root.AddComponent<CapsuleCollider>();
            col.direction = 2;
            col.center = b.center;
            col.radius = Mathf.Max(1f, Mathf.Min(b.size.x, b.size.y) * 0.35f);
            col.height = b.size.z;

            // Engine exhaust: one soft additive emitter per engine, local space,
            // pointing backwards (-Z of the ship).
            var ps = new List<ParticleSystem>();
            foreach (var eng in sc.engineAnchors)
            {
                var ex = new GameObject("Exhaust");
                ex.transform.SetParent(eng, false);
                ex.transform.localRotation = Quaternion.LookRotation(eng.InverseTransformDirection(-root.transform.forward));
                ps.Add(MakeExhaust(ex, mats.exhaust));
            }
            sc.thrusterParticles = ps.ToArray();

            var plasmaGo = new GameObject("EntryPlasma");
            plasmaGo.transform.SetParent(root.transform, false);
            MakePlasma(plasmaGo, mats.plasma);
            plasmaGo.SetActive(false);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, ShipPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log("[PG] ship prefab written: " + ShipPrefabPath + " bounds " + b);
            return prefab;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
        }

        static ParticleSystem MakeExhaust(GameObject go, Material mat)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.30f);
            main.startSpeed = 28f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.4f);
            main.startColor = new Color(0.65f, 0.85f, 1f, 0.9f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 400;
            main.playOnAwake = true;
            var em = ps.emission;
            em.rateOverTime = 40f;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = 4f;
            sh.radius = 0.3f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(0.85f, 0.95f, 1f), 0f), new GradientColorKey(new Color(0.35f, 0.55f, 1f), 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.35f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        static void MakePlasma(GameObject go, Material mat)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            main.startSpeed = 25f;
            main.startSize = new ParticleSystem.MinMaxCurve(2.2f, 4.2f);
            main.startColor = new Color(1f, 0.6f, 0.3f, 0.6f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 700;
            main.playOnAwake = true;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = 38f;
            sh.radius = 3.2f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.92f, 0.75f), 0f), new GradientColorKey(new Color(1f, 0.35f, 0.12f), 1f) },
                      new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ------------------------------------------------------------- scene

        static T GetOrAdd<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (p.TryGet<T>(out var c)) return c;
            c = p.Add<T>(true);
            c.name = typeof(T).Name;
            c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(c, p);
            return c;
        }

        static VolumeProfile BuildPostFx()
        {
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFxPath);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(p, PostFxPath);
            }
            var bloom = GetOrAdd<Bloom>(p);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.75f);
            bloom.scatter.Override(0.68f);
            var tm = GetOrAdd<Tonemapping>(p);
            tm.mode.Override(TonemappingMode.ACES);
            var ca = GetOrAdd<ColorAdjustments>(p);
            ca.postExposure.Override(0.25f);
            ca.contrast.Override(8f);
            ca.saturation.Override(10f);
            var vig = GetOrAdd<Vignette>(p);
            vig.intensity.Override(0.2f);
            vig.smoothness.Override(0.45f);
            EditorUtility.SetDirty(p);
            return p;
        }

        static void BuildScene(Mats mats, GameObject shipPrefab)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGo = new GameObject("PG_GlobalLighting");
            var gl = lightGo.AddComponent<PGGlobalLighting>();
            var dirGo = new GameObject("Sun_Directional");
            dirGo.transform.SetParent(lightGo.transform, false);
            var dl = dirGo.AddComponent<Light>();
            dl.type = LightType.Directional;
            dl.color = new Color(1f, 0.95f, 0.88f);
            dl.intensity = 1.2f;
            dl.shadows = LightShadows.None;
            gl.directional = dl;

            var root = new GameObject("PG_Game");
            var system = root.AddComponent<PlanetSystem>();
            root.AddComponent<FloatingOrigin>();
            var streamer = root.AddComponent<WorldStreamer>();
            streamer.system = system;
            streamer.TerrainMaterial = mats.terrain;
            streamer.WaterMaterial = mats.water;
            streamer.AtmosphereMaterial = mats.atmo;
            streamer.CloudMaterial = mats.clouds;

            var beacons = root.AddComponent<BeaconRegistry>();
            beacons.BeaconMaterial = mats.beacon;
            beacons.DiscoveryMaterial = mats.discovery;

            var props = root.AddComponent<PropScatter>();
            props.streamer = streamer;
            props.PropMaterial = mats.prop;
            props.CrystalMaterial = mats.crystal;
            props.AwesomeMaterial = mats.landmark;
            props.PoiArch = AssetDatabase.LoadAssetAtPath<GameObject>(PoiFbx[0]);
            props.PoiSpires = AssetDatabase.LoadAssetAtPath<GameObject>(PoiFbx[1]);
            props.PoiRift = AssetDatabase.LoadAssetAtPath<GameObject>(PoiFbx[2]);

            var flora = root.AddComponent<FloraField>();
            flora.streamer = streamer;
            flora.material = mats.flora;
            flora.conifer = PropMesh("FLORA_Conifer");
            flora.broadleaf = PropMesh("FLORA_Broadleaf");
            flora.bush = PropMesh("FLORA_Bush");
            flora.dryShrub = PropMesh("FLORA_DryShrub");
            flora.boulder = PropMesh("ROCK_Boulder");
            flora.hoodoo = PropMesh("ROCK_Hoodoo");
            flora.iceSpikes = PropMesh("ICE_Spikes");
            flora.basalt = PropMesh("ROCK_Basalt");
            flora.meshMatrices = PropMeshMatrices("FLORA_Conifer", "FLORA_Broadleaf", "FLORA_Bush", "FLORA_DryShrub",
                                                  "ROCK_Boulder", "ROCK_Hoodoo", "ICE_Spikes", "ROCK_Basalt");

            var save = root.AddComponent<SaveSystem>();
            save.streamer = streamer;

            var atm = root.AddComponent<AtmosphereSystem>();
            atm.streamer = streamer;
            atm.lighting = gl;

            var game = root.AddComponent<GameManager>();
            game.system = system;
            game.streamer = streamer;
            game.save = save;
            game.beacons = beacons;
            game.props = props;
            game.atmosphere = atm;
            game.lighting = gl;

            var star = new GameObject("Helion").AddComponent<StarBody>();
            star.system = system;
            star.SunMaterial = mats.sun;
            star.CoronaMaterial = mats.corona;

            var sf = new GameObject("Starfield").AddComponent<Starfield>();
            sf.StarMaterial = mats.stars;

            var shipGo = (GameObject)PrefabUtility.InstantiatePrefab(shipPrefab);
            shipGo.name = "PlayerShip";
            var ship = shipGo.GetComponent<ShipController>();
            ship.streamer = streamer;
            flora.ship = ship;
            var shipAudio = shipGo.GetComponent<ShipAudio>();
            shipAudio.atmosphere = atm;
            atm.ship = ship;
            var plasma = shipGo.transform.Find("EntryPlasma");
            atm.entryParticles = plasma != null ? plasma.GetComponent<ParticleSystem>() : null;

            var camGo = new GameObject("MainCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 400000f;
            cam.fieldOfView = 62f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.004f, 0.005f, 0.010f);
            cam.allowHDR = true;
            cam.allowMSAA = true;
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.None;
            camData.dithering = true;
            camGo.AddComponent<AudioListener>();
            var rig = camGo.AddComponent<CameraRig>();
            rig.cam = cam;
            rig.ship = ship;
            rig.streamer = streamer;
            rig.atmosphere = atm;
            atm.rig = rig;

            var volGo = new GameObject("PG_PostFX");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.sharedProfile = BuildPostFx();

            var input = root.AddComponent<ShipInput>();
            input.ship = ship;
            input.rig = rig;

            var hud = root.AddComponent<HUD>();
            hud.game = game;
            hud.streamer = streamer;
            hud.ship = ship;
            hud.rig = rig;
            hud.save = save;
            hud.beacons = beacons;
            hud.system = system;
            hud.input = input;
            hud.shipAudio = shipAudio;

            game.ship = ship;
            game.rig = rig;
            game.input = input;
            game.hud = hud;
            save.ship = ship;
            save.rig = rig;
            save.beacons = beacons;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[PG] scene built and saved: " + ScenePath);
        }

        // ------------------------------------------------------- verification

        /// <summary>
        /// Batch check of the generated content. Prints PG_VERIFY lines (also
        /// asserted by the PlayMode scene-integrity test).
        /// </summary>
        [MenuItem("Tools/PG/Verify Scene", priority = 30)]
        public static void VerifyScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sb = new System.Text.StringBuilder();
            void R(string k, object v) { sb.AppendLine("PG_VERIFY " + k + " = " + v); }

            R("build_scenes", string.Join(",", EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path)));
            var ff = UnityEngine.Object.FindAnyObjectByType<FloraField>();
            if (ff != null)
            {
                foreach (var m in new[] { ff.conifer, ff.broadleaf, ff.bush, ff.dryShrub, ff.boulder, ff.hoodoo, ff.iceSpikes, ff.basalt })
                    R("flora_mesh", m == null ? "MISSING" : m.name + " bounds min " + m.bounds.min.ToString("F2") + " size " + m.bounds.size.ToString("F2") + " colors " + m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color) + " tris " + (m.GetIndexCount(0) / 3));
                if (ff.meshMatrices != null && ff.meshMatrices.Length == 8 && ff.conifer != null)
                {
                    var bb = ff.conifer.bounds;
                    Vector3 top = ff.meshMatrices[0].MultiplyPoint3x4(new Vector3(bb.center.x, bb.center.y, bb.min.z));
                    Vector3 bottom = ff.meshMatrices[0].MultiplyPoint3x4(new Vector3(bb.center.x, bb.center.y, bb.max.z));
                    R("flora_conifer_axis", "base " + bottom.ToString("F2") + " top " + top.ToString("F2") + " (expect top ~ +11 on Y)");
                }
                R("flora_material_instancing", ff.material != null && ff.material.enableInstancing);
                R("flora_ship_ref", ff.ship != null);
            }
            var ps = UnityEngine.Object.FindAnyObjectByType<PropScatter>();
            if (ps != null) R("poi_models", (ps.PoiArch != null) + "," + (ps.PoiSpires != null) + "," + (ps.PoiRift != null));
            R("game_managers", UnityEngine.Object.FindObjectsByType<GameManager>(FindObjectsSortMode.None).Length);
            R("ships", UnityEngine.Object.FindObjectsByType<ShipController>(FindObjectsSortMode.None).Length);
            R("cameras", UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length);
            R("huds", UnityEngine.Object.FindObjectsByType<HUD>(FindObjectsSortMode.None).Length);
            R("flora_fields", UnityEngine.Object.FindObjectsByType<FloraField>(FindObjectsSortMode.None).Length);
            R("audio_listeners", UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length);

            var ship = UnityEngine.Object.FindAnyObjectByType<ShipController>();
            if (ship != null)
            {
                Vector3 L(Transform x) => x != null ? ship.transform.InverseTransformPoint(x.position) : new Vector3(float.NaN, float.NaN, float.NaN);
                R("nose_local", L(ship.noseAnchor).ToString("F2"));
                R("cockpit_local", L(ship.cockpitAnchor).ToString("F2"));
                R("engine_L_local", L(ship.engineAnchors[0]).ToString("F2"));
                R("engine_R_local", L(ship.engineAnchors[1]).ToString("F2"));
                R("gear_feet_local", string.Join(" ", ship.gearFeet.Select(g => L(g).ToString("F2"))));
                R("gear_animator", ship.gearAnimator != null);
                if (ship.gearAnimator != null)
                    R("gear_pivot_rot_in_ship", string.Join(" ", ship.gearAnimator.legs.Select(l => (Quaternion.Inverse(ship.transform.rotation) * l.rotation).eulerAngles.ToString("F1"))));
                var model = ship.transform.Find("Visual/ShipModel");
                if (model != null) R("model_root_rot", model.localRotation.eulerAngles.ToString("F1") + " children " + string.Join(",", Enumerable.Range(0, model.childCount).Select(i => model.GetChild(i).name + model.GetChild(i).localEulerAngles.ToString("F0"))));
                R("glow_renderers", ship.engineGlowRenderers != null ? ship.engineGlowRenderers.Length : 0);
                int missing = 0, tris = 0;
                var matNames = new HashSet<string>();
                foreach (var mr in ship.GetComponentsInChildren<MeshRenderer>(true))
                {
                    foreach (var m in mr.sharedMaterials) { if (m == null) missing++; else matNames.Add(m.name); }
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;
                }
                R("ship_missing_materials", missing);
                R("ship_materials", string.Join(",", matNames.OrderBy(x => x)));
                R("ship_triangles", tris);
            }
            var ws = UnityEngine.Object.FindAnyObjectByType<WorldStreamer>();
            R("streamer_materials", ws != null && ws.TerrainMaterial && ws.WaterMaterial && ws.AtmosphereMaterial && ws.CloudMaterial);
            Debug.Log(sb.ToString());
        }
    }
}
