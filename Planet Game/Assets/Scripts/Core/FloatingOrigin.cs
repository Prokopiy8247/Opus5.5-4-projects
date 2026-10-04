using System;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Converts between logical (double precision, system-scale) coordinates and
    /// Unity render coordinates.
    ///
    /// Render space is rebuilt only when the player has drifted more than
    /// OriginShiftThreshold metres from the render origin. A shift is a pure
    /// translation: every tracked object's *logical* transform is untouched, so
    /// relative positions, velocities, beacons and terrain signatures survive
    /// exactly. That is what makes this a rendering trick rather than the
    /// teleport the task forbids.
    ///
    /// The new origin is snapped to OriginGrid so the delta applied to meshes is
    /// an exact power-of-two fraction - this kills the low-order jitter that
    /// otherwise shows up as camera shake far from the origin.
    /// </summary>
    public class FloatingOrigin : MonoBehaviour
    {
        public static FloatingOrigin Instance { get; private set; }

        /// <summary>Clears the static handle. Used only by automated tests.</summary>
        public static void ResetInstance() { Instance = null; }

        /// <summary>Current logical position of render-space (0,0,0).</summary>
        public DVec3 Origin { get; private set; } = DVec3.zero;

        public int ShiftCount { get; private set; }
        public double LastShiftDistance { get; private set; }

        /// <summary>Objects rebased on every shift (ships, props, beacons...).</summary>
        readonly System.Collections.Generic.List<ITracked> tracked = new System.Collections.Generic.List<ITracked>();

        public interface ITracked
        {
            /// <summary>Called after the origin moved; re-derive render transform from logical state.</summary>
            void OnOriginShift(DVec3 delta);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public void Register(ITracked t) { if (t != null && !tracked.Contains(t)) tracked.Add(t); }
        public void Unregister(ITracked t) { tracked.Remove(t); }

        public Vector3 ToRender(DVec3 logical) => logical.ToRender(Origin);
        public DVec3 ToLogical(Vector3 render) => DVec3.FromRender(render, Origin);

        /// <summary>
        /// A direction or a velocity is a translation-free quantity, so it does
        /// not change when the origin moves - this exists purely so the intent
        /// is readable at every call site.
        /// </summary>
        public Vector3 ToRenderV(DVec3 v) => v.ToVector3();
        public DVec3 ToLogicalV(Vector3 v) => DVec3.From(v);

        /// <summary>
        /// Call once per frame with the player's logical position. Returns true
        /// if a shift happened this frame.
        /// </summary>
        public bool Tick(DVec3 playerLogical)
        {
            DVec3 delta = playerLogical - Origin;
            double dist = delta.magnitude;
            if (dist < PGConst.OriginShiftThreshold) return false;
            Shift(playerLogical);
            return true;
        }

        public void Shift(DVec3 playerLogical)
        {
            // Snap so the shift vector is exactly representable in float.
            double g = PGConst.OriginGrid;
            DVec3 target = new DVec3(
                Math.Round(playerLogical.x / g) * g,
                Math.Round(playerLogical.y / g) * g,
                Math.Round(playerLogical.z / g) * g);

            DVec3 delta = target - Origin;
            if (delta.sqrMagnitude < 1e-9) return;

            Origin = target;
            ShiftCount++;
            LastShiftDistance = delta.magnitude;

            // Physics state is expressed in render space, so move the whole
            // simulation back by the same delta in one go.
            Physics.SyncTransforms();

            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                if (tracked[i] == null) { tracked.RemoveAt(i); continue; }
                try { tracked[i].OnOriginShift(delta); }
                catch (Exception e) { Debug.LogWarning("[FloatingOrigin] tracked shift failed: " + e.Message); }
            }
        }

        /// <summary>
        /// Force the origin onto a known logical point (used when loading a save
        /// or teleporting a debug camera). Unlike Shift() this does not require
        /// the target to be within the drift threshold, but it notifies every
        /// tracked object exactly the same way - so a teleport moves the origin
        /// and leaves the universe where it was.
        /// </summary>
        public void SetOriginHard(DVec3 logical)
        {
            DVec3 delta = logical - Origin;
            Origin = logical;
            if (delta.sqrMagnitude < 1e-9) return;

            ShiftCount++;
            LastShiftDistance = delta.magnitude;

            Physics.SyncTransforms();

            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                if (tracked[i] == null) { tracked.RemoveAt(i); continue; }
                try { tracked[i].OnOriginShift(delta); }
                catch (Exception e) { Debug.LogWarning("[FloatingOrigin] tracked shift failed: " + e.Message); }
            }
        }
    }
}
