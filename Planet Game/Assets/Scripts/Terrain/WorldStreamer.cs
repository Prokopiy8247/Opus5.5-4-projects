using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Owns the planet streams and answers every question the rest of the game
    /// asks about ground:
    ///   * what is the surface height under this point?
    ///   * which body's sphere of influence am I in (with hysteresis)?
    ///   * where is the water, if any?
    ///
    /// Every planet's quadtree is ticked every frame. A distant planet simply
    /// stays at its always-built root level, which is what makes it visible
    /// from anywhere in the system with the same silhouette, colours and
    /// continents it has up close - there is no separate far-away mesh to swap.
    /// </summary>
    public class WorldStreamer : MonoBehaviour
    {
        public static WorldStreamer Instance { get; private set; }

        /// <summary>Clears the static handle. Used only by automated tests.</summary>
        public static void ResetInstance() { Instance = null; }

        public PlanetSystem system;
        public Material TerrainMaterial;
        public Material WaterMaterial;
        public Material AtmosphereMaterial;
        public Material CloudMaterial;

        readonly Dictionary<string, PlanetRuntime> runtimes = new Dictionary<string, PlanetRuntime>();
        readonly Dictionary<string, PlanetShells> shells = new Dictionary<string, PlanetShells>();

        /// <summary>Body whose sphere of influence currently owns the player.</summary>
        public BodyDef CurrentBody { get; private set; }
        public BodyDef TargetBody { get; private set; }

        public int TotalActivePatches { get; private set; }
        public int TotalPendingBuilds { get; private set; }
        public int TotalLiveNodes { get; private set; }
        public int TotalMeshesAllocated { get; private set; }
        public int TotalObjectsAllocated { get; private set; }
        public int TotalColliders { get; private set; }
        public int TotalStaleRejected { get; private set; }

        /// <summary>Bodies whose point of interest the player has scanned.</summary>
        public readonly HashSet<string> Discovered = new HashSet<string>();

        public IEnumerable<PlanetRuntime> Runtimes => runtimes.Values;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (system == null) system = GetComponent<PlanetSystem>();
        }

        public void Init()
        {
            foreach (var b in system.bodies)
            {
                if (b.kind == BodyKind.Star) continue;   // the star is drawn by StarBody
                BuildRuntime(b);
            }
            if (FloatingOrigin.Instance != null) UpdateTransforms(FloatingOrigin.Instance.Origin);
        }

        /// <summary>
        /// Re-create every planet runtime at the current seeds. Only called when a
        /// save is loaded, because the save carries each body's seed and the
        /// terrain must agree with the file for the restored position to be the
        /// same place.
        /// </summary>
        public void RebuildAfterSaveLoad()
        {
            foreach (var kv in runtimes) if (kv.Value != null) Destroy(kv.Value.gameObject);
            runtimes.Clear();
            shells.Clear();
            Init();
        }

        public PlanetRuntime RuntimeOf(BodyDef b)
        {
            if (b == null) return null;
            return runtimes.TryGetValue(b.id, out var r) ? r : null;
        }

        public PlanetRuntime RuntimeOf(string id) => runtimes.TryGetValue(id, out var r) ? r : null;

        public PlanetShells ShellsOf(BodyDef b)
        {
            if (b == null) return null;
            return shells.TryGetValue(b.id, out var s) ? s : null;
        }

        void BuildRuntime(BodyDef b)
        {
            var go = new GameObject("Planet_" + b.displayName);
            go.transform.SetParent(transform, false);

            var sh = go.AddComponent<PlanetShells>();
            sh.Build(b, TerrainMaterial, WaterMaterial, AtmosphereMaterial, CloudMaterial);

            var pr = go.AddComponent<PlanetRuntime>();
            pr.SurfaceMaterial = sh.TerrainMaterial;
            pr.Init(b);

            runtimes[b.id] = pr;
            shells[b.id] = sh;
        }

        // ------------------------------------------------------------ queries

        /// <summary>Surface height in metres above the datum, for a logical world point.</summary>
        public double ElevationAt(BodyDef b, DVec3 logicalWorldPos)
        {
            var pr = RuntimeOf(b);
            if (pr == null) return 0.0;
            DVec3 dir = (logicalWorldPos - b.LogicalPos).normalized;
            return TerrainFunc.Elevation(b, pr.Perm, dir);
        }

        /// <summary>Height of the *solid* ground (the water surface counts for water bodies).</summary>
        public double GroundHeightAt(BodyDef b, DVec3 logicalWorldPos)
        {
            double h = ElevationAt(b, logicalWorldPos);
            if (b.seaLevel > -1e8 && h < b.seaLevel) return b.seaLevel;
            return h;
        }

        public double AltitudeAboveGround(BodyDef b, DVec3 logicalWorldPos)
        {
            if (b == null) return 1e9;
            double r = (logicalWorldPos - b.LogicalPos).magnitude;
            return r - (b.radius + GroundHeightAt(b, logicalWorldPos));
        }

        public double AltitudeAboveDatum(BodyDef b, DVec3 logicalWorldPos)
        {
            return (logicalWorldPos - b.LogicalPos).magnitude - b.radius;
        }

        public DVec3 SurfaceNormalAt(BodyDef b, DVec3 logicalWorldPos)
        {
            var pr = RuntimeOf(b);
            DVec3 dir = (logicalWorldPos - b.LogicalPos).normalized;
            if (pr == null) return dir;
            return TerrainFunc.SurfaceNormal(b, pr.Perm, dir, 4.0);
        }

        /// <summary>Water depth, negative if the point is over dry land.</summary>
        public double WaterDepthAt(BodyDef b, DVec3 logicalWorldPos)
        {
            if (b.seaLevel < -1e8) return -1.0;
            return b.seaLevel - ElevationAt(b, logicalWorldPos);
        }

        // -------------------------------------------------------------- ticks

        /// <summary>
        /// Resolve the body that owns the player (with hysteresis), then advance
        /// every planet's quadtree, colliders near the player, and the shells.
        /// </summary>
        public void Tick(DVec3 playerLogical, DVec3 travelDir, float speed, float dt)
        {
            CurrentBody = ResolveBody(playerLogical);

            int patches = 0, pending = 0, nodes = 0, meshes = 0, objects = 0, colliders = 0, stale = 0;
            foreach (var kv in runtimes)
            {
                var pr = kv.Value;
                var b = pr.Def;
                pr.BeginFrame();
                pr.Tick(playerLogical, travelDir, speed, b == CurrentBody);
                if (b == CurrentBody) FillColliders(pr, playerLogical);

                patches += pr.ActivePatches;
                pending += pr.PendingBuilds;
                nodes += pr.LiveNodes;
                meshes += pr.MeshesAllocated;
                objects += pr.ObjectsAllocated;
                colliders += pr.LiveColliders;
                stale += pr.StaleJobsRejected;
            }
            UpdateTransforms(FloatingOrigin.Instance.Origin);

            TotalActivePatches = patches;
            TotalPendingBuilds = pending;
            TotalLiveNodes = nodes;
            TotalMeshesAllocated = meshes;
            TotalObjectsAllocated = objects;
            TotalColliders = colliders;
            TotalStaleRejected = stale;
        }

        void UpdateTransforms(DVec3 origin)
        {
            foreach (var kv in runtimes)
            {
                kv.Value.UpdateRenderTransform(origin);
                if (shells.TryGetValue(kv.Key, out var sh)) sh.Tick(origin);
            }
        }

        void FillColliders(PlanetRuntime pr, DVec3 playerLogical)
        {
            // Colliders only inside the local physics ring - props and the
            // on-foot test stand on them. The ship's own contact is analytic.
            const double ring = 1500.0;
            foreach (var leaf in pr.Leaves)
            {
                if (pr.colliderBudgetUsed >= PGConst.ColliderBuildsPerFrame) break;
                if (leaf.colliderMesh != null) continue;
                if (leaf.DistanceTo(playerLogical) < ring) pr.EnsureCollider(leaf);
            }
        }

        BodyDef ResolveBody(DVec3 p)
        {
            // Nearest landable body by surface clearance. The star never owns
            // the player: it has no ground and no air.
            BodyDef best = null;
            double bestClearance = double.MaxValue;
            foreach (var b in system.bodies)
            {
                if (!b.landable) continue;
                double clearance = (b.LogicalPos - p).magnitude - b.radius;
                if (clearance < bestClearance) { bestClearance = clearance; best = b; }
            }
            if (best == null) return system.Star;

            // Hysteresis: the current owner keeps the ship until another body is
            // clearly closer, so a boundary cannot flip back and forth.
            if (CurrentBody != null && CurrentBody != best && CurrentBody.landable)
            {
                double cur = (CurrentBody.LogicalPos - p).magnitude - CurrentBody.radius;
                if (cur < bestClearance + 1500.0) return CurrentBody;
            }
            return best;
        }

        /// <summary>Navigation target (a planet).</summary>
        public void SetTarget(BodyDef b) { TargetBody = b; }

        public double DistanceToTarget(DVec3 p)
        {
            return TargetBody == null ? 0.0 : (TargetBody.LogicalPos - p).magnitude;
        }
    }
}
