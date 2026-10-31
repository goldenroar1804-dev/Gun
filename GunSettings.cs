namespace GunModifier
{
    public static class GunSettings
    {
        public static float DamageMultiplier = 1.0f;
        public static float FireRateMultiplier = 1.0f;
        public static float RecoilMultiplier = 1.0f;
        public static float MagazineMultiplier = 1.0f;

        public static bool InfiniteAmmo = false;
        public static bool ExtendedMagazine = false;

        public static void Reset()
        {
            DamageMultiplier = 1.0f;
            FireRateMultiplier = 1.0f;
            RecoilMultiplier = 1.0f;
            MagazineMultiplier = 1.0f;

            InfiniteAmmo = false;
            ExtendedMagazine = false;
        }
    }
}
