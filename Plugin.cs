 using System;
using System.Linq;
using System.Reflection;
using MelonLoader;

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

            try
            {
                Assembly boneLib = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "BoneLib");

                if (boneLib == null)
                {
                    boneLib = Assembly.Load("BoneLib");
                }

                Type pageType = boneLib.GetType("BoneLib.BoneMenu.Page");

                if (pageType == null)
                {
                    throw new Exception("BoneMenu Page type not found.");
                }

                Assembly unityAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "UnityEngine.CoreModule");

                if (unityAssembly == null)
                {
                    unityAssembly = Assembly.Load("UnityEngine.CoreModule");
                }

                Type colorType = unityAssembly.GetType("UnityEngine.Color");

                if (colorType == null)
                {
                    throw new Exception("Unity Color type not found.");
                }

                object white = Activator.CreateInstance(colorType);

                colorType.GetField("r").SetValue(white, 1f);
                colorType.GetField("g").SetValue(white, 1f);
                colorType.GetField("b").SetValue(white, 1f);
                colorType.GetField("a").SetValue(white, 1f);

                PropertyInfo rootProperty = pageType.GetProperty(
                    "Root",
                    BindingFlags.Public | BindingFlags.Static
                );

                object root = rootProperty.GetValue(null);

                MethodInfo createPage = pageType.GetMethod(
                    "CreatePage",
                    new Type[] { typeof(string), colorType }
                );

                object page = createPage.Invoke(
                    root,
                    new object[] { "Gun Modifier", white }
                );

                MethodInfo createInt = pageType.GetMethod(
                    "CreateInt",
                    new Type[]
                    {
                        typeof(string),
                        colorType,
                        typeof(int),
                        typeof(int),
                        typeof(int),
                        typeof(int),
                        typeof(Action<int>)
                    }
                );

                createInt.Invoke(page, new object[]
                {
                    "Damage (%)", white, 100, 10, 0, 500,
                    new Action<int>(SetDamage)
                });

                createInt.Invoke(page, new object[]
                {
                    "Fire Rate (%)", white, 100, 10, 10, 300,
                    new Action<int>(SetFireRate)
                });

                createInt.Invoke(page, new object[]
                {
                    "Recoil (%)", white, 100, 10, 0, 300,
                    new Action<int>(SetRecoil)
                });

                createInt.Invoke(page, new object[]
                {
                    "Magazine (%)", white, 100, 25, 100, 500,
                    new Action<int>(SetMagazine)
                });

                MethodInfo createBool = pageType.GetMethod(
                    "CreateBool",
                    new Type[]
                    {
                        typeof(string),
                        colorType,
                        typeof(bool),
                        typeof(Action<bool>)
                    }
                );

                createBool.Invoke(page, new object[]
                {
                    "Infinite Ammo", white, false,
                    new Action<bool>(SetInfiniteAmmo)
                });

                MelonLogger.Msg("Gun Modifier menu created!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Gun Modifier menu failed: " + ex);
            }
        }

        private static void SetDamage(int value)
        {
            GunSettings.DamageMultiplier = value / 100f;
        }

        private static void SetFireRate(int value)
        {
            GunSettings.FireRateMultiplier = value / 100f;
        }

        private static void SetRecoil(int value)
        {
            GunSettings.RecoilMultiplier = value / 100f;
        }

        private static void SetMagazine(int value)
        {
            GunSettings.MagazineMultiplier = value / 100f;
        }

        private static void SetInfiniteAmmo(bool value)
        {
            GunSettings.InfiniteAmmo = value;
        }
    }
}           
