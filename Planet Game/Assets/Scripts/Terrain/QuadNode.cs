using System;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Lifecycle of one quadtree node.
    ///
    /// The handoff between a parent patch and its four children is the most
    /// visible failure mode of a quadtree planet: retire the parent too early and
    /// the ground opens onto black space for as long as the children take to
    /// build. The rule enforced here is
    ///
    ///   Collapsed (parent drawn) -> Splitting (parent drawn, children building,
    ///   children hidden) -> Split (atomic swap: all 4 children shown, parent hidden)
    ///
    /// and merging is the exact reverse: the parent's mesh is still alive while it
    /// is Split (only hidden), so it is shown again in the same frame the children
    /// are released. A node is only allowed to split once its own mesh exists, so
    /// at every rendered frame each region of the sphere is drawn by exactly one
    /// live mesh.
    /// </summary>
    public enum NodeState
    {
        /// <summary>No mesh yet. Never visible.</summary>
        Empty,
        /// <summary>Leaf with a live mesh, drawn.</summary>
        Collapsed,
        /// <summary>Own mesh drawn; four children exist and are building, hidden.</summary>
        Splitting,
        /// <summary>Own mesh kept but hidden; the children draw the region.</summary>
        Split,
        /// <summary>Released. Any job still referring to this node is stale.</summary>
        Retired
    }

    /// <summary>
    /// One node of a planet's quadtree. Root nodes are the 6 cube faces split into
    /// RootSplit x RootSplit children.
    /// </summary>
    public class QuadNode
    {
        public PlanetRuntime planet;
        public int face;          // 0..5
        public int level;
        public int ix, iy;        // grid coordinates at this level within the face
        public int gridN;         // nodes per face edge at this level
        public QuadNode[] children;

        public GameObject go;
        public MeshFilter mf;
        public MeshRenderer mr;
        public MeshCollider mc;

        public NodeState state = NodeState.Empty;

        /// <summary>
        /// Bumped whenever the node is retired. A build job captures the value at
        /// queue time; PlanetRuntime discards the result if it no longer matches,
        /// so a job finishing late can never re-light a node that no longer owns a
        /// region of the sphere.
        /// </summary>
        public int generation;

        public bool buildQueued;              // a generation job is in flight
        public PatchBuildJob built;           // arrays of the live mesh (collider source)
        public Mesh colliderMesh;             // owned collider mesh, pooled on retire

        // Cached geometry for distance tests.
        readonly DVec3 centreLocal;           // planet-centre relative, on the datum sphere
        readonly double sizeMetres;

        public bool IsLeaf => children == null;
        public bool MeshReady => go != null && mf != null && mf.sharedMesh != null;
        public bool Drawn => go != null && go.activeSelf;

        public QuadNode(PlanetRuntime p, int face, int level, int ix, int iy, int gridN)
        {
            planet = p; this.face = face; this.level = level;
            this.ix = ix; this.iy = iy; this.gridN = gridN;
            generation = p.NextGeneration();

            double u = (ix + 0.5) / gridN;
            double v = (iy + 0.5) / gridN;
            centreLocal = CubeSphere.FacePoint(face, u, v) * p.Def.radius;
            sizeMetres = (Math.PI * 0.5) / gridN * p.Def.radius;
        }

        /// <summary>Patch centre in logical coordinates.</summary>
        public DVec3 CenterLogical() => centreLocal + planet.Def.LogicalPos;

        /// <summary>Approximate edge length of the patch on the surface, metres.</summary>
        public double ApproxSizeMetres() => sizeMetres;

        public double DistanceTo(DVec3 logicalPoint)
        {
            return (CenterLogical() - logicalPoint).magnitude - sizeMetres * 0.5;
        }

        /// <summary>
        /// Create the four children. The parent keeps drawing; the children stay
        /// hidden until all four have meshes (see <see cref="TryCompleteSplit"/>).
        /// Callers must only split a node whose own mesh is ready.
        /// </summary>
        public void Split()
        {
            if (!IsLeaf || !MeshReady) return;
            children = new QuadNode[4];
            int n = gridN * 2;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    children[j * 2 + i] = new QuadNode(planet, face, level + 1, ix * 2 + i, iy * 2 + j, n);
            state = NodeState.Splitting;
        }

        /// <summary>All four children have live meshes.</summary>
        public bool ChildrenReady
        {
            get
            {
                if (children == null) return false;
                for (int i = 0; i < 4; i++)
                    if (!children[i].MeshReady) return false;
                return true;
            }
        }

        /// <summary>
        /// The atomic swap. Shows all four children and hides the parent within
        /// the same frame, and only once every child can draw.
        /// </summary>
        public bool TryCompleteSplit()
        {
            if (state != NodeState.Splitting || !ChildrenReady) return false;
            for (int i = 0; i < 4; i++)
            {
                children[i].state = NodeState.Collapsed;
                children[i].go.SetActive(true);
            }
            go.SetActive(false);
            state = NodeState.Split;
            return true;
        }

        /// <summary>
        /// Collapse back into this node. The parent mesh is shown before any child
        /// is released, so the region is covered across the transition.
        /// </summary>
        public bool Merge()
        {
            if (IsLeaf) return true;
            if (!MeshReady) return false;
            go.SetActive(true);
            for (int i = 0; i < 4; i++) children[i].Release();
            children = null;
            state = NodeState.Collapsed;
            return true;
        }

        /// <summary>
        /// Give up this node and its subtree. Returns GameObjects and meshes to the
        /// planet's pools and bumps the generation so any in-flight job is stale.
        /// </summary>
        public void Release()
        {
            if (children != null)
            {
                for (int i = 0; i < 4; i++) children[i].Release();
                children = null;
            }
            generation = planet.NextGeneration();
            buildQueued = false;
            built = null;
            if (go != null)
            {
                planet.ReturnPatch(this);
                go = null; mf = null; mr = null; mc = null;
            }
            colliderMesh = null;
            state = NodeState.Retired;
        }
    }

    /// <summary>
    /// Cube-face parameterisation shared by the mesh builder and the quadtree.
    ///
    /// The mapping is computed from the 3D cube point, never from per-face
    /// (u, v) alone. That is what makes it seamless: two faces sharing an edge
    /// produce the same 3D cube point there, so they produce the same sphere
    /// point. The previous per-face formula warped u and v independently of the
    /// face's third coordinate and left gaps of hundreds of metres between faces.
    /// </summary>
    public static class CubeSphere
    {
        /// <summary>Point on the unit cube for a face and a, b in [-1, 1].</summary>
        public static DVec3 CubePoint(int face, double a, double b)
        {
            switch (face)
            {
                case 0: return new DVec3(1.0, a, b);      // +X
                case 1: return new DVec3(-a, 1.0, b);     // +Y
                case 2: return new DVec3(-1.0, -a, b);    // -X
                case 3: return new DVec3(a, -1.0, b);     // -Y
                case 4: return new DVec3(a, b, 1.0);      // +Z
                default: return new DVec3(a, -b, -1.0);  // -Z
            }
        }

        /// <summary>
        /// Unit direction for u, v in [0, 1] on a face. Uses the area-preserving
        /// cube-to-sphere map (Nowell), which keeps cells close to uniform.
        /// With this orientation table, triangles (i,j)-(i+1,j)-(i,j+1) face
        /// outward on every face.
        /// </summary>
        public static DVec3 FacePoint(int face, double u, double v)
        {
            DVec3 c = CubePoint(face, u * 2.0 - 1.0, v * 2.0 - 1.0);
            double x2 = c.x * c.x, y2 = c.y * c.y, z2 = c.z * c.z;
            double sx = c.x * Math.Sqrt(Math.Max(0.0, 1.0 - y2 * 0.5 - z2 * 0.5 + y2 * z2 / 3.0));
            double sy = c.y * Math.Sqrt(Math.Max(0.0, 1.0 - z2 * 0.5 - x2 * 0.5 + z2 * x2 / 3.0));
            double sz = c.z * Math.Sqrt(Math.Max(0.0, 1.0 - x2 * 0.5 - y2 * 0.5 + x2 * y2 / 3.0));
            return new DVec3(sx, sy, sz).normalized;
        }
    }
}
