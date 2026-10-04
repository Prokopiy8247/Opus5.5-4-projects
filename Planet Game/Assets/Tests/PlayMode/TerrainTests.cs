using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PG.Tests
{
    /// <summary>
    /// Terrain correctness: true cube-face borders, the parent/child handoff,
    /// stale-job rejection, full coverage every frame of a descent and ascent,
    /// the ocean shell radius, and pool stability.
    /// </summary>
    public class TerrainTests
    {
        readonly List<GameObject> spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var g in spawned) if (g != null) UnityEngine.Object.Destroy(g);
            spawned.Clear();
        }

        PlanetRuntime Planet(string id, out BodyDef def)
        {
            def = PGTestUtil.Body(id);
            var pr = PGTestUtil.StandalonePlanet(def);
            spawned.Add(pr.gameObject);
            return pr;
        }

        // ------------------------------------------------------- cube-face borders

        /// <summary>(face, a, b) for a cube point lying on that face's plane.</summary>
        static bool Invert(int face, DVec3 c, out double u, out double v)
        {
            double a, b; u = v = 0;
            switch (face)
            {
                case 0: if (Math.Abs(c.x - 1) > 1e-12) return false; a = c.y; b = c.z; break;
                case 1: if (Math.Abs(c.y - 1) > 1e-12) return false; a = -c.x; b = c.z; break;
                case 2: if (Math.Abs(c.x + 1) > 1e-12) return false; a = -c.y; b = c.z; break;
                case 3: if (Math.Abs(c.y + 1) > 1e-12) return false; a = c.x; b = c.z; break;
                case 4: if (Math.Abs(c.z - 1) > 1e-12) return false; a = c.x; b = c.y; break;
                default: if (Math.Abs(c.z + 1) > 1e-12) return false; a = c.x; b = -c.y; break;
            }
            u = (a + 1) * 0.5; v = (b + 1) * 0.5;
            return u >= -1e-12 && u <= 1 + 1e-12 && v >= -1e-12 && v <= 1 + 1e-12;
        }

        /// <summary>The previous, per-face warp - kept only to prove the test detects its gaps.</summary>
        static DVec3 OldFacePoint(int face, double u, double v)
        {
            double a = u * 2.0 - 1.0, b = v * 2.0 - 1.0, aa = a * a, bb = b * b;
            double x = a * Math.Sqrt(1.0 - bb * 0.5 - aa * bb / 3.0 + aa * bb * bb / 6.0);
            double y = b * Math.Sqrt(1.0 - aa * 0.5 - aa * bb / 3.0 + aa * aa * bb / 6.0);
            switch (face)
            {
                case 0: return new DVec3(1, x, y).normalized;
                case 1: return new DVec3(-x, 1, y).normalized;
                case 2: return new DVec3(-1, -x, y).normalized;
                case 3: return new DVec3(x, -1, y).normalized;
                case 4: return new DVec3(x, y, 1).normalized;
                default: return new DVec3(x, -y, -1).normalized;
            }
        }

        [Test]
        public void TrueCubeFaceBorders_AllTwelveEdges_MatchToNumericalPrecision()
        {
            const double R = 3000.0;
            double maxNew = 0, maxOld = 0;
            int pairs = 0;
            for (int fa = 0; fa < 6; fa++)
            for (int edge = 0; edge < 4; edge++)
            for (int s = 0; s <= 64; s++)
            {
                double t = s / 64.0;
                double ua = edge == 0 ? 0 : edge == 1 ? 1 : t;
                double va = edge == 2 ? 0 : edge == 3 ? 1 : t;
                DVec3 c = CubeSphere.CubePoint(fa, ua * 2 - 1, va * 2 - 1);
                for (int fb = 0; fb < 6; fb++)
                {
                    if (fb == fa || !Invert(fb, c, out double ub, out double vb)) continue;
                    pairs++;
                    maxNew = Math.Max(maxNew, (CubeSphere.FacePoint(fa, ua, va) - CubeSphere.FacePoint(fb, ub, vb)).magnitude * R);
                    maxOld = Math.Max(maxOld, (OldFacePoint(fa, ua, va) - OldFacePoint(fb, ub, vb)).magnitude * R);
                }
            }
            Debug.Log($"[TEST] cube-face borders: {pairs} shared samples, max gap now {maxNew:E2} m, previous mapping {maxOld:F1} m");
            Assert.Greater(pairs, 700, "every edge sample must be found on its neighbouring face");
            Assert.Less(maxNew, 1e-6, "shared edges must coincide to numerical precision");
            Assert.Greater(maxOld, 100.0, "sanity: the test must detect the old mapping's gaps");
        }

        [Test]
        public void PatchesOnNeighbouringFaces_ShareTheirBorderVertices()
        {
            var def = PGTestUtil.Body("mirvalis");
            var perm = PGNoise.BuildPerm(def.seed);
            int res = PGConst.PatchRes, n = PGConst.RootSplit;
            // Face 0 (+X) right edge (u = 1) meets face 1 (+Y) left edge (u = 0), v -> Z on both.
            var a = new PatchBuildJob(); PlanetRuntime.BuildArrays(a, def, perm, 0, 0, n - 1, 0, n, res);
            var b = new PatchBuildJob(); PlanetRuntime.BuildArrays(b, def, perm, 1, 0, 0, 0, n, res);
            int side = res + 1;
            double worst = 0;
            for (int j = 0; j <= res; j++)
                worst = Math.Max(worst, (a.verts[j * side + res] - b.verts[j * side + 0]).magnitude);
            Debug.Log($"[TEST] border vertices of neighbouring faces: worst gap {worst:E2} m");
            Assert.Less(worst, 1e-3);
        }

        // ---------------------------------------------------------------- handoff

        static DVec3 Above(BodyDef d, QuadNode n, double alt)
        {
            DVec3 dir = (n.CenterLogical() - d.LogicalPos).normalized;
            return d.LogicalPos + dir * (d.radius + 400.0 + alt);
        }

        [UnityTest]
        public IEnumerator Handoff_ParentDrawsUntilAllChildrenReady_ThenAtomicSwap_AndMergeRestoresParent()
        {
            var pr = Planet("mirvalis", out var def);
            DVec3 far = def.LogicalPos + new DVec3(0, 0, 1) * 1e6;
            pr.Tick(far, new DVec3(0, 0, 1), 0f, false);
            Assert.IsTrue(pr.ValidateTree(out string e0), e0);
            Assert.AreEqual(24, pr.ActivePatches, "24 root patches always drawn");

            var root = pr.roots[0];
            DVec3 near = Above(def, root, 300.0);
            pr.Tick(near, new DVec3(0, 0, 1), 0f, false);
            Assert.AreEqual(NodeState.Splitting, root.state);
            Assert.IsTrue(root.Drawn, "parent keeps drawing while its children build");
            foreach (var c in root.children) Assert.IsFalse(c.Drawn, "children hidden until all four are ready");

            int frames = 0;
            while (root.state == NodeState.Splitting && frames++ < 600)
            {
                yield return null;
                pr.Tick(near, new DVec3(0, 0, 1), 0f, false);
                Assert.IsTrue(pr.ValidateTree(out string e), "frame " + frames + ": " + e);
            }
            Assert.AreEqual(NodeState.Split, root.state);
            Assert.IsFalse(root.Drawn);
            foreach (var c in root.children) Assert.IsTrue(c.Drawn || c.state == NodeState.Split || c.state == NodeState.Splitting);

            // Merge: parent is drawable immediately, children released in the same frame.
            frames = 0;
            while (pr.LiveNodes > 24 && frames++ < 600)
            {
                yield return null;
                pr.Tick(far, new DVec3(0, 0, 1), 0f, false);
                Assert.IsTrue(pr.ValidateTree(out string e), "merge frame " + frames + ": " + e);
            }
            Assert.AreEqual(24, pr.LiveNodes);
            Assert.IsTrue(root.Drawn);
        }

        [UnityTest]
        public IEnumerator StaleJob_ForARetiredNode_IsRejected_AndCreatesNoPatch()
        {
            var pr = Planet("tarnveth", out var def);
            var root = pr.roots[0];
            DVec3 near = Above(def, root, 300.0);
            DVec3 far = def.LogicalPos + new DVec3(0, 1, 0) * 1e6;
            pr.Tick(near, new DVec3(0, 0, 1), 0f, false);
            Assert.AreEqual(NodeState.Splitting, root.state);
            var child = root.children[0];

            // A finished job for the child, captured with its current generation...
            var job = new PatchBuildJob { node = child, generation = child.generation };
            PlanetRuntime.BuildArrays(job, def, pr.Perm, child.face, child.level, child.ix, child.iy, child.gridN, PGConst.PatchRes);

            // ...then the child is retired before the job is applied.
            pr.Tick(far, new DVec3(0, 0, 1), 0f, false);
            Assert.AreEqual(NodeState.Retired, child.state);
            int before = pr.StaleJobsRejected;
            pr.InjectFinishedJobForTest(job);
            pr.Tick(far, new DVec3(0, 0, 1), 0f, false);
            yield return null;
            Assert.Greater(pr.StaleJobsRejected, before, "stale result must be rejected");
            Assert.IsNull(child.go, "a retired node must never get a patch");
            Assert.IsTrue(pr.ValidateTree(out string e), e);
        }

        [UnityTest]
        public IEnumerator FullCoverage_EveryFrame_DuringDescentToTheSurfaceAndBack()
        {
            var pr = Planet("mirvalis", out var def);
            DVec3 dir = new DVec3(0.3, 0.8, -0.52).normalized;
            double ground = TerrainFunc.Elevation(def, pr.Perm, dir);
            int failures = 0; string first = null; int maxNodes = 0, frames = 0;

            // 9 km down to 25 m above the ground, then back up to 9 km, sliding sideways.
            for (int i = 0; i <= 700; i++)
            {
                double t = i <= 400 ? i / 400.0 : 1.0 - (i - 400) / 300.0;
                double alt = 25.0 + (9000.0 - 25.0) * Math.Pow(1.0 - t, 3.0);
                DVec3 side = DVec3.Cross(dir, new DVec3(0, 1, 0)).normalized;
                DVec3 d = (dir + side * (t * 0.05)).normalized;
                DVec3 viewer = def.LogicalPos + d * (def.radius + ground + alt);
                pr.BeginFrame();
                pr.Tick(viewer, -d, 300f, true);
                frames++;
                maxNodes = Math.Max(maxNodes, pr.LiveNodes);
                if (!pr.ValidateTree(out string e)) { failures++; if (first == null) first = "frame " + i + ": " + e; }
                yield return null;
            }
            Debug.Log($"[TEST] coverage: {frames} frames, failures {failures}, peak live nodes {maxNodes}, stale rejected {pr.StaleJobsRejected}");
            Assert.AreEqual(0, failures, first);
            Assert.Greater(maxNodes, 100, "the descent must actually refine the tree");
        }

        [UnityTest]
        public IEnumerator MemoryAndPools_Plateau_OverRepeatedDescents()
        {
            var pr = Planet("kryosyne", out var def);
            var objs = new List<int>(); var meshes = new List<int>();
            DVec3 far = def.LogicalPos + new DVec3(1, 0, 0) * 1e6;
            for (int cycle = 0; cycle < 5; cycle++)
            {
                DVec3 dir = new DVec3(Math.Cos(cycle * 1.3), 0.4, Math.Sin(cycle * 1.3)).normalized;
                double g = TerrainFunc.Elevation(def, pr.Perm, dir);
                for (int i = 0; i <= 200; i++)
                {
                    double alt = 30.0 + 8000.0 * Math.Pow(1.0 - i / 200.0, 3.0);
                    pr.BeginFrame();
                    pr.Tick(def.LogicalPos + dir * (def.radius + g + alt), -dir, 200f, true);
                    yield return null;
                }
                for (int i = 0; i < 300 && (pr.LiveNodes > 24 || pr.PendingBuilds > 0); i++)
                {
                    pr.Tick(far, new DVec3(1, 0, 0), 0f, false);
                    yield return null;
                }
                objs.Add(pr.ObjectsAllocated); meshes.Add(pr.MeshesAllocated);
                Assert.AreEqual(24, pr.LiveNodes, "back to the root level after leaving");
            }
            Debug.Log("[TEST] pool allocations per cycle: objects " + string.Join(",", objs) + " meshes " + string.Join(",", meshes));
            Assert.LessOrEqual(objs[4], objs[1] + 40, "GameObjects must be recycled, not leaked");
            Assert.LessOrEqual(meshes[4], meshes[1] + 80, "meshes must be recycled, not leaked");
            Assert.LessOrEqual(pr.ObjectsPooled + pr.ActivePatches, pr.ObjectsAllocated);
        }

        // ------------------------------------------------------------------- water

        [Test]
        public void WaterShell_LiesOnRadiusPlusSeaLevel_ForNegativeAndPositiveSeaLevel()
        {
            foreach (double sea in new[] { -40.0, 25.0 })
            {
                var def = PGTestUtil.Body("mirvalis");
                def.seaLevel = sea;
                var go = new GameObject("Shell");
                spawned.Add(go);
                var sh = go.AddComponent<PlanetShells>();
                sh.Build(def, null, null, null, null);
                Assert.IsNotNull(sh.Water, "water shell for sea level " + sea);
                var mesh = sh.Water.GetComponent<MeshFilter>().sharedMesh;
                var verts = mesh.vertices;
                double want = def.radius + sea, worst = 0;
                var perm = PGNoise.BuildPerm(def.seed);
                double hMin = double.MaxValue, hMax = double.MinValue;
                for (int i = 0; i < verts.Length; i += 7)
                {
                    Vector3 w = sh.Water.transform.TransformPoint(verts[i]) - go.transform.position;
                    worst = Math.Max(worst, Math.Abs(w.magnitude - want));
                    double h = TerrainFunc.Elevation(def, perm, DVec3.From(verts[i]).normalized);
                    hMin = Math.Min(hMin, h); hMax = Math.Max(hMax, h);
                }
                Debug.Log($"[TEST] water seaLevel {sea}: max radius error {worst:F4} m; terrain under it spans {hMin:F0}..{hMax:F0} m");
                Assert.Less(worst, 0.05, "water vertices must lie on radius + seaLevel");
                Assert.Greater(hMax - hMin, 100.0, "...while the terrain under them varies: water is not a copy of the terrain");
            }
        }
    }
}
