using UnityEngine;

namespace PG
{
    /// <summary>
    /// Clears the static singleton handles. Called by automated tests between
    /// cases so that a torn-down world never leaves a dangling reference behind
    /// for the next test to pick up.
    /// </summary>
    public static class PGSingletons
    {
        public static void ResetAll()
        {
            FloatingOrigin.ResetInstance();
            WorldStreamer.ResetInstance();
            ShipController.ResetInstance();
            CameraRig.ResetInstance();
            GameManager.ResetInstance();
            BeaconRegistry.ResetInstance();
            SaveSystem.ResetInstance();
            PGGlobalLighting.ResetInstance();
            PlanetSystem.ResetInstance();
        }
    }
}
