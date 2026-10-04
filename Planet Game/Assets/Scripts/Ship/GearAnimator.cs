using UnityEngine;

namespace PG
{
    /// <summary>
    /// Landing-gear retraction. Each leg is a pivot transform whose imported
    /// pose is the deployed pose. <see cref="stowEuler"/> is the stowing
    /// rotation expressed in the SHIP's frame (+X right, +Y up, +Z forward), so
    /// it does not depend on how the model's internal nodes happen to be
    /// oriented after import. Driven by ShipController.GearDown.
    /// </summary>
    public class GearAnimator : MonoBehaviour
    {
        public Transform[] legs;
        public Vector3[] stowEuler;
        public float duration = 1.6f;

        Quaternion[] deployed;      // local rotation, deployed
        Quaternion[] parentInShip;  // parent frame relative to this (ship-aligned) transform
        float t = 1f, target = 1f;

        public float Deployment => t;

        void Awake() { Cache(); }

        void Cache()
        {
            if (legs == null) return;
            deployed = new Quaternion[legs.Length];
            parentInShip = new Quaternion[legs.Length];
            for (int i = 0; i < legs.Length; i++)
            {
                if (legs[i] == null) continue;
                deployed[i] = legs[i].localRotation;
                Quaternion parentRot = legs[i].parent != null ? legs[i].parent.rotation : Quaternion.identity;
                parentInShip[i] = Quaternion.Inverse(transform.rotation) * parentRot;
            }
        }

        public void SetDeployed(bool down) { target = down ? 1f : 0f; }

        void Update()
        {
            if (legs == null || deployed == null || legs.Length != deployed.Length) return;
            if (Mathf.Approximately(t, target)) return;
            t = Mathf.MoveTowards(t, target, Time.deltaTime / Mathf.Max(duration, 0.05f));
            float s = t * t * (3f - 2f * t);
            for (int i = 0; i < legs.Length; i++)
            {
                if (legs[i] == null) continue;
                Vector3 e = stowEuler != null && i < stowEuler.Length ? stowEuler[i] : new Vector3(-90f, 0f, 0f);
                // Rotation E in ship space, conjugated into the leg's parent space.
                Quaternion p = parentInShip[i];
                Quaternion eLocal = Quaternion.Inverse(p) * Quaternion.Euler(e * (1f - s)) * p;
                legs[i].localRotation = eLocal * deployed[i];
            }
        }
    }
}
