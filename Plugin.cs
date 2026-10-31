using MelonLoader;
using BoneLib.BoneMenu;
using UnityEngine;

[assembly: MelonInfo(
    typeof(GunModifier.Plugin),
    "Gun Modifier",
    "1.0.0",
    "GunModifier")]

[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace GunModifier
{
    public class Plugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Gun Modifier loaded!");

            Page page = Page.Root.CreatePage(
                "Gun Modifier",
                Color.white
            );

            page.CreateInt(
                "Damage (%)",
                Color.white,
                100, 10, 0, 500,
                value => GunSettings.DamageMultiplier = value / 100f
            );

            page.CreateInt(
                "Fire Rate (%)",
                Color.white,
                100, 10, 10, 300,
                value => GunSettings.FireRateMultiplier = value / 100f
            );

            page.CreateInt(
                "Recoil (%)",
                Color.white,
                100, 10, 0, 300,
                value => GunSettings.RecoilMultiplier = value / 100f
            );

            page.CreateInt(
                "Magazine (%)",
                Color.white,
                100, 25, 100, 500,
                value => GunSettings.MagazineMultiplier = value / 100f
            );

            page.CreateBool(
                "Infinite Ammo",
                Color.white,
                false,
                value => GunSettings.InfiniteAmmo = value
            );

            page.CreateBool(
                "Extended Magazine",
                Color.white,
                false,
                value => GunSettings.ExtendedMagazine = value
            );

            MelonLogger.Msg("Gun Modifier menu created!");
        }
    }
}
