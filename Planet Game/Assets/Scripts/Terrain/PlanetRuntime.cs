using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PG
{
    /// <summary>
    /// Mesh generation job data. The heavy math runs on a worker thread and
    /// touches no UnityEngine objects - only arrays and the pure terrain
    /// function. The resulting arrays are handed back to the main thread, which
    /// is the only place a Mesh is created or a collider is assigned.
    /// </summary>
    public class PatchBuildJob
    {
        public QuadNode node;
        /// <summary>Node generation when the job was queued; a mismatch means the result is stale.</summary>
        public int generation;
        public Vector3[] verts;
        public Vector3[] normals;
        public Color32[] colors;
        public int[] tris;
        public Vector3[] colliderVerts;
        public int[] colliderTris;
        public volatile bool done;
        public volatile bool failed;
        public string error;

        public int VertCount => verts != null ? verts.Length : 0;
    }

    /// <summary>
    /// Per-planet streaming state: quadtree, job pipeline, pools and budgets.
    ///
    /// The tree is the planet's only terrain representation at every distance.
    /// The 24 root patches are built synchronously at start-up and never retired,
    /// so the coarsest level is always complete; finer levels replace it through
    /// the atomic split described on <see cref="NodeState"/>. There is no separate
    /// far-away sphere to hand over from, which removes a whole class of
    /// far/near transition holes.
    /// </summary>
    public class PlanetRuntime : MonoBehaviour, FloatingOrigin.ITracked
    {
        public BodyDef Def;
        public int[] Perm;

        public QuadNode[] roots;

        readonly List<QuadNode> drawn = new List<QuadNode>();
        readonly Queue<PatchBuildJob> readyJobs = new Queue<PatchBuildJob>();
        readonly List<PatchBuildJob> inFlight = new List<PatchBuildJob>();
        readonly List<QuadNode> scratchChildren = new List<QuadNode>(4);

        Transform patchRoot;
        int generationCounter;

        // ---- pools. A retired patch gives its GameObject AND its meshes back;
        //      a rented mesh is always detached from every renderer/collider. ---
        readonly Stack<GameObject> goPool = new Stack<GameObject>();
        readonly Stack<Mesh> meshPool = new Stack<Mesh>();
        int allocatedMeshes, allocatedObjects;

        public Material SurfaceMaterial;

        // ---- counters surfaced in telemetry and asserted by tests ------------
        public int ActivePatches { get; private set; }
        public int PendingBuilds { get; private set; }
        public int LiveNodes { get; private set; }
        public int MeshesAllocated => allocatedMeshes;
        public int MeshesPooled => meshPool.Count;
        public int ObjectsAllocated => allocatedObjects;
        public int ObjectsPooled => goPool.Count;
        public int StaleJobsRejected { get; private set; }
        public int LiveColliders { get; private set; }
        public int colliderBudgetUsed;

        /// <summary>Patches drawn this frame, valid after Tick().</summary>
        public List<QuadNode> Leaves => drawn;

        public double MaxElevation;
        public double MinElevation;

        public int NextGeneration() => ++generationCounter;

        void Awake()
        {
            patchRoot = new GameObject("Patches").transform;
            patchRoot.SetParent(transform, false);
        }

        void OnEnable() { FloatingOrigin.Instance?.Register(this); }
        void OnDisable() { FloatingOrigin.Instance?.Unregister(this); }

        public void Init(BodyDef def)
        {
            Def = def;
            Perm = PGNoise.BuildPerm(def.seed);
            if (patchRoot == null) Awake();

            double mn = double.MaxValue, mx = double.MinValue;
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < 8; j++)
                    for (int i = 0; i < 8; i++)
                    {
                        var d = CubeSphere.FacePoint(f, (i + 0.5) / 8.0, (j + 0.5) / 8.0);
                        double h = TerrainFunc.Elevation(Def, Perm, d);
                        if (h < mn) mn = h;
                        if (h > mx) mx = h;
                    }
            MinElevation = mn;
            MaxElevation = mx;

            roots = new QuadNode[6 * PGConst.RootSplit * PGConst.RootSplit];
            int idx = 0;
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < PGConst.RootSplit; j++)
                    for (int i = 0; i < PGConst.RootSplit; i++)
                        roots[idx++] = new QuadNode(this, f, 0, i, j, PGConst.RootSplit);

            // The coarsest level is built synchronously: the planet is complete
            // from the very first rendered frame and never has to "appear".
            foreach (var r in roots)
            {
                var job = new PatchBuildJob { node = r, generation = r.generation };
                BuildArrays(job, Def, Perm, r.face, r.level, r.ix, r.iy, r.gridN, PGConst.PatchRes);
                ApplyJob(job);
                r.state = NodeState.Collapsed;
                r.go.SetActive(true);
            }
            LiveNodes = roots.Length;
        }

        void OnDestroy()
        {
            foreach (var m in meshPool) if (m != null) Destroy(m);
            meshPool.Clear();
        }

        // ------------------------------------------------------------ streaming

        public void Tick(DVec3 viewerLogical, DVec3 travelDir, float travelSpeed, bool allowColliders)
        {
            int splitsLeft = PGConst.MaxSplitsPerFrame;
            for (int i = 0; i < roots.Length; i++)
                Refine(roots[i], viewerLogical, travelDir, travelSpeed, ref splitsLeft);

            PumpJobs();

            for (int i = 0; i < roots.Length; i++) CompleteSplits(roots[i]);

            drawn.Clear();
            for (int i = 0; i < roots.Length; i++) CollectDrawn(roots[i]);

            ActivePatches = drawn.Count;
            PendingBuilds = inFlight.Count + readyJobs.Count;
            TrimPools();
        }

        // Pools keep a reserve for the next split burst; anything beyond it
        // (e.g. after leaving this planet, whose tree collapses to a few
        // levels) is released a few items per frame, so allocations track the
        // live tree instead of the historical peak of every visited planet.
        const int GoPoolReserve = 160, MeshPoolReserve = 320;
        void TrimPools()
        {
            for (int i = 0; i < 8 && goPool.Count > GoPoolReserve; i++)
            {
                var go = goPool.Pop();
                if (go != null) Destroy(go);
                allocatedObjects--;
            }
            for (int i = 0; i < 16 && meshPool.Count > MeshPoolReserve; i++)
            {
                var m = meshPool.Pop();
                if (m != null) Destroy(m);
                allocatedMeshes--;
            }
        }

        double EffectiveDistance(QuadNode n, DVec3 viewer, DVec3 travelDir, float speed)
        {
            double dist = n.DistanceTo(viewer);
            // Look ahead along the direction of travel so the ground in front is
            // refined before the ship arrives over it.
            double lookAhead = Math.Min(speed * 2.0, 6000.0);
            if (lookAhead > 1.0)
            {
                double d2 = n.DistanceTo(viewer + travelDir * lookAhead);
                if (d2 < dist) dist = d2;
            }
            return dist;
        }

        void Refine(QuadNode n, DVec3 viewer, DVec3 travelDir, float speed, ref int splitsLeft)
        {
            double dist = EffectiveDistance(n, viewer, travelDir, speed);
            double size = n.ApproxSizeMetres();
            bool wantSplit = dist < size * PGConst.SplitFactor && n.level < PGConst.MaxLevel;
            bool wantMerge = dist > size * PGConst.MergeFactor;

            switch (n.state)
            {
                case NodeState.Collapsed:
                    if (wantSplit && splitsLeft > 0 && LiveNodes + 4 <= PGConst.MaxActivePatches && n.MeshReady)
                    {
                        splitsLeft--;
                        n.Split();
                        LiveNodes += 4;
                        for (int i = 0; i < 4; i++) RequestBuild(n.children[i]);
                    }
                    break;

                case NodeState.Splitting:
                    if (wantMerge)
                    {
                        // Turned away before the children finished: cancel. The
                        // parent never stopped drawing.
                        LiveNodes -= CountSubtree(n) - 1;
                        n.Merge();
                    }
                    else
                    {
                        for (int i = 0; i < 4; i++)
                            if (!n.children[i].MeshReady) RequestBuild(n.children[i]);
                    }
                    break;

                case NodeState.Split:
                    if (wantMerge)
                    {
                        LiveNodes -= CountSubtree(n) - 1;
                        n.Merge();
                    }
                    else
                    {
                        // Nearest child first, so a capped split budget goes to the
                        // ground under the ship.
                        scratchChildren.Clear();
                        scratchChildren.AddRange(n.children);
                        var kids = scratchChildren.ToArray();
                        Array.Sort(kids, (a, b) => a.DistanceTo(viewer).CompareTo(b.DistanceTo(viewer)));
                        for (int i = 0; i < kids.Length; i++)
                            Refine(kids[i], viewer, travelDir, speed, ref splitsLeft);
                    }
                    break;

                case NodeState.Empty:
                    RequestBuild(n);
                    break;
            }
        }

        static int CountSubtree(QuadNode n)
        {
            int c = 1;
            if (n.children != null)
                for (int i = 0; i < 4; i++) c += CountSubtree(n.children[i]);
            return c;
        }

        void CompleteSplits(QuadNode n)
        {
            if (n.children == null) return;
            if (n.state == NodeState.Splitting) { n.TryCompleteSplit(); return; }
            for (int i = 0; i < 4; i++) CompleteSplits(n.children[i]);
        }

        void CollectDrawn(QuadNode n)
        {
            if (n.state == NodeState.Split)
            {
                for (int i = 0; i < 4; i++) CollectDrawn(n.children[i]);
                return;
            }
            if (n.Drawn) drawn.Add(n);
        }

        // -------------------------------------------------------------- builds

        void RequestBuild(QuadNode n)
        {
            if (n.buildQueued || n.MeshReady || n.state == NodeState.Retired) return;
            n.buildQueued = true;

            var job = new PatchBuildJob { node = n, generation = n.generation };
            inFlight.Add(job);

            var def = Def; var perm = Perm;
            int face = n.face, level = n.level, ix = n.ix, iy = n.iy, gridN = n.gridN;
            int res = PGConst.PatchRes;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    BuildArrays(job, def, perm, face, level, ix, iy, gridN, res);
                }
                catch (Exception e)
                {
                    job.error = e.Message;
                    job.failed = true;
                }
                job.done = true;
            });
        }

        void PumpJobs()
        {
            for (int i = inFlight.Count - 1; i >= 0; i--)
                if (inFlight[i].done) { readyJobs.Enqueue(inFlight[i]); inFlight.RemoveAt(i); }

            int applied = 0;
            while (readyJobs.Count > 0 && applied < PGConst.MeshBuildsPerFrame)
            {
                var j = readyJobs.Dequeue();
                if (!AcceptJob(j)) continue;
                ApplyJob(j);
                applied++;
            }
        }

        /// <summary>
        /// The stale-job gate. A finished job is applied only if its node is still
        /// the same generation, is still in the tree and has no mesh yet.
        /// </summary>
        public bool AcceptJob(PatchBuildJob j)
        {
            if (j == null || j.node == null) return false;
            var n = j.node;
            if (n.generation != j.generation || n.state == NodeState.Retired)
            {
                StaleJobsRejected++;
                return false;
            }
            n.buildQueued = false;
            if (j.failed)
            {
                Debug.LogWarning("[Planet] patch build failed: " + j.error);
                return false;
            }
            return !n.MeshReady;
        }

        /// <summary>Upload a finished job into a pooled GameObject. The patch starts hidden.</summary>
        public void ApplyJob(PatchBuildJob j)
        {
            var n = j.node;
            GameObject go = RentGameObject(n);
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            var mc = go.GetComponent<MeshCollider>();

            Mesh m = RentMesh();
            m.indexFormat = j.VertCount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(j.verts);
            m.SetNormals(j.normals);
            m.SetColors(j.colors);
            m.SetTriangles(j.tris, 0, true);
            m.UploadMeshData(false);

            mf.sharedMesh = m;
            mr.sharedMaterial = SurfaceMaterial;
            mc.sharedMesh = null;

            n.go = go; n.mf = mf; n.mr = mr; n.mc = mc;
            n.built = j;
            go.SetActive(false);
        }

        /// <summary>
        /// Collider for a drawn patch inside the local physics ring. Ground
        /// contact for the ship itself is analytic and never waits for this.
        /// </summary>
        public void EnsureCollider(QuadNode n)
        {
            if (n == null || n.mc == null || n.colliderMesh != null || n.built == null) return;
            if (colliderBudgetUsed >= PGConst.ColliderBuildsPerFrame) return;
            var j = n.built;
            if (j.colliderVerts == null || j.colliderVerts.Length < 4) return;

            var cm = RentMesh();
            cm.indexFormat = IndexFormat.UInt16;
            cm.SetVertices(j.colliderVerts);
            cm.SetTriangles(j.colliderTris, 0, true);
            n.mc.sharedMesh = cm;
            n.colliderMesh = cm;
            colliderBudgetUsed++;
            LiveColliders++;
        }

        public void BeginFrame() { colliderBudgetUsed = 0; }

        // ------------------------------------------------------------- pooling

        GameObject RentGameObject(QuadNode n)
        {
            GameObject go;
            if (goPool.Count > 0)
            {
                go = goPool.Pop();
            }
            else
            {
                go = new GameObject("Patch", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
                go.transform.SetParent(patchRoot, false);
                var r = go.GetComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                allocatedObjects++;
            }
            go.name = $"Patch_{n.face}_{n.level}_{n.ix}_{n.iy}";
            go.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            return go;
        }

        Mesh RentMesh()
        {
            if (meshPool.Count > 0) return meshPool.Pop();
            allocatedMeshes++;
            var m = new Mesh { name = "PGPatch" };
            m.MarkDynamic();
            return m;
        }

        /// <summary>Called by a retiring node: detach and pool its mesh(es) and its GameObject.</summary>
        public void ReturnPatch(QuadNode n)
        {
            var go = n.go;
            if (go == null) return;
            go.SetActive(false);
            if (n.mf != null && n.mf.sharedMesh != null)
            {
                var m = n.mf.sharedMesh;
                n.mf.sharedMesh = null;
                m.Clear();
                meshPool.Push(m);
            }
            if (n.mc != null && n.mc.sharedMesh != null)
            {
                var m = n.mc.sharedMesh;
                n.mc.sharedMesh = null;
                m.Clear();
                meshPool.Push(m);
                LiveColliders--;
            }
            goPool.Push(go);
        }

        // Patch vertices are planet-centre relative; an origin shift is handled
        // entirely by moving the planet container.
        void FloatingOrigin.ITracked.OnOriginShift(DVec3 delta) { }

        public void UpdateRenderTransform(DVec3 origin)
        {
            transform.SetPositionAndRotation((Def.LogicalPos - origin).ToRender(DVec3.zero), Quaternion.identity);
        }

        // ============================================================ VALIDATION

        /// <summary>
        /// Checks the invariants the renderer depends on. Used by the automated
        /// coverage tests every frame of a split/merge sequence:
        ///   1. every root region is drawn by exactly one level of the tree;
        ///   2. no drawn patch has a drawn ancestor (no overlapping LODs);
        ///   3. every active patch GameObject belongs to a node of the tree
        ///      (no orphaned, floating patches).
        /// </summary>
        public bool ValidateTree(out string error)
        {
            error = null;
            int drawnInTree = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                if (!Covered(roots[i], false, ref drawnInTree, ref error)) return false;
            }
            int activeObjects = 0;
            for (int i = 0; i < patchRoot.childCount; i++)
                if (patchRoot.GetChild(i).gameObject.activeSelf) activeObjects++;
            if (activeObjects != drawnInTree)
            {
                error = "active patch objects " + activeObjects + " != drawn tree nodes " + drawnInTree;
                return false;
            }
            return true;
        }

        static bool Covered(QuadNode n, bool ancestorDrawn, ref int drawnCount, ref string error)
        {
            if (n.Drawn)
            {
                if (ancestorDrawn) { error = "overlap at " + n.go.name; return false; }
                drawnCount++;
                if (n.state == NodeState.Split) { error = "split node still drawn: " + n.go.name; return false; }
                // A Splitting node's hidden children must stay hidden.
                if (n.children != null)
                    for (int i = 0; i < 4; i++)
                        if (n.children[i].Drawn) { error = "child drawn under drawing parent: " + n.children[i].go.name; return false; }
                return true;
            }
            if (n.state == NodeState.Split && n.children != null)
            {
                for (int i = 0; i < 4; i++)
                    if (!Covered(n.children[i], false, ref drawnCount, ref error)) return false;
                return true;
            }
            error = "uncovered region: face " + n.face + " level " + n.level + " (" + n.ix + "," + n.iy + ") state " + n.state;
            return false;
        }

        /// <summary>Test hook: queue a hand-built job as though a worker had finished it.</summary>
        public void InjectFinishedJobForTest(PatchBuildJob j)
        {
            j.done = true;
            readyJobs.Enqueue(j);
        }

        // ============================================================== MESHING
        // Static, no UnityEngine object access: safe on worker threads.

        public static void BuildArrays(PatchBuildJob job, BodyDef def, int[] perm,
                                       int face, int level, int ix, int iy, int gridN, int res)
        {
            int side = res + 1;
            int count = side * side;
            var verts = new Vector3[count];
            var norms = new Vector3[count];
            var cols = new Color32[count];

            double denom = (double)gridN * res;
            double quadMetres = (Math.PI * 0.5) / denom * def.radius;
            // Normals are taken at a scale tied to the patch resolution, so a
            // coarse patch is not shaded with sub-triangle detail it cannot show.
            double normalStep = Math.Max(2.0, Math.Min(80.0, quadMetres * 0.6));

            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    double u = (ix * res + i) / denom;
                    double v = (iy * res + j) / denom;

                    DVec3 dir = CubeSphere.FacePoint(face, u, v);
                    double h = TerrainFunc.Elevation(def, perm, dir);
                    double r = def.radius + h;

                    int k = j * side + i;
                    verts[k] = new Vector3((float)(dir.x * r), (float)(dir.y * r), (float)(dir.z * r));

                    DVec3 nrm = TerrainFunc.SurfaceNormal(def, perm, dir, h, normalStep);
                    norms[k] = new Vector3((float)nrm.x, (float)nrm.y, (float)nrm.z);

                    Color c = TerrainFunc.Albedo(def, perm, h, nrm, dir);
                    cols[k] = new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f),
                                          (byte)(Mathf.Clamp01(c.b) * 255f), 255);
                }
            }

            // ---- surface triangles, outward facing (see CubeSphere) ----------
            var tris = new List<int>(res * res * 6 + res * 4 * 12);
            for (int j = 0; j < res; j++)
            {
                for (int i = 0; i < res; i++)
                {
                    int a = j * side + i;
                    int b = a + 1;
                    int c = a + side;
                    int d = c + 1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }

            // ---- skirts --------------------------------------------------------
            // A curtain hanging toward the planet centre along each border hides
            // T-junction cracks where a neighbour is one LOD coarser. Depth scales
            // with the patch so coarse patches get deep enough skirts. Double-
            // sided, so it covers the crack from whichever side it is seen.
            double skirtDepth = Math.Max(3.0, Math.Min(240.0, quadMetres * 1.6));
            int[] border = new int[4 * res];
            int p = 0;
            for (int i = 0; i < res; i++) border[p++] = i;                              // bottom, left->right
            for (int j = 0; j < res; j++) border[p++] = j * side + res;                 // right, bottom->top
            for (int i = res; i > 0; i--) border[p++] = res * side + i;                 // top, right->left
            for (int j = res; j > 0; j--) border[p++] = j * side;                       // left, top->bottom
            int loop = p;

            var allVerts = new Vector3[count + loop];
            var allNorms = new Vector3[count + loop];
            var allCols = new Color32[count + loop];
            Array.Copy(verts, allVerts, count);
            Array.Copy(norms, allNorms, count);
            Array.Copy(cols, allCols, count);
            for (int s = 0; s < loop; s++)
            {
                Vector3 vv = verts[border[s]];
                float len = vv.magnitude;
                float k = len > 1f ? (float)((len - skirtDepth) / len) : 1f;
                allVerts[count + s] = vv * k;
                allNorms[count + s] = norms[border[s]];
                allCols[count + s] = cols[border[s]];
            }
            for (int s = 0; s < loop; s++)
            {
                int a = border[s];
                int b = border[(s + 1) % loop];
                int sa = count + s;
                int sb = count + (s + 1) % loop;
                tris.Add(a); tris.Add(sb); tris.Add(b);
                tris.Add(a); tris.Add(sa); tris.Add(sb);
                tris.Add(a); tris.Add(b); tris.Add(sb);
                tris.Add(a); tris.Add(sb); tris.Add(sa);
            }

            job.verts = allVerts;
            job.normals = allNorms;
            job.colors = allCols;
            job.tris = tris.ToArray();

            // ---- collider: every second vertex, same outward winding ----------
            int cres = Math.Max(2, res / 2);
            int cs = cres + 1;
            var cv = new Vector3[cs * cs];
            for (int j = 0; j < cs; j++)
                for (int i = 0; i < cs; i++)
                {
                    int si = Math.Min(res, i * res / cres);
                    int sj = Math.Min(res, j * res / cres);
                    cv[j * cs + i] = verts[sj * side + si];
                }
            var ct = new int[cres * cres * 6];
            int t = 0;
            for (int j = 0; j < cres; j++)
                for (int i = 0; i < cres; i++)
                {
                    int a = j * cs + i, b = a + 1, c = a + cs, d = c + 1;
                    ct[t++] = a; ct[t++] = b; ct[t++] = c;
                    ct[t++] = b; ct[t++] = d; ct[t++] = c;
                }
            job.colliderVerts = cv;
            job.colliderTris = ct;
        }
    }
}
