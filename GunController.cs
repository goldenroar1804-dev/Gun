using UnityEngine;

namespace GunModifier
{
    public static class GunController
    {
        public static void ApplySettings(GameObject gun)
        {
            if (gun == null)
                return;

            ApplyDamage(gun);
            ApplyFireRate(gun);
            ApplyRecoil(gun);
            ApplyMagazine(gun);
        }

        private static void ApplyDamage(GameObject gun)
        {
            // Weapon damage implementation goes here.
        }

        private static void ApplyFireRate(GameObject gun)
        {
            // Fire-rate implementation goes here.
        }

        private static void ApplyRecoil(GameObject gun)
        {
            // Recoil implementation goes here.
        }

        private static void ApplyMagazine(GameObject gun)
        {
            // Magazine implementation goes here.
        }
    }
}
