using System;
using System.Linq;
using System.Reflection;
using MelonLoader;

[assembly: MelonInfo(
    typeof(GunModifier.Plugin),
    "Gun Modifier",
    "1.2.7",
    "GunModifier")]

[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace GunModifier
{
    public class Plugin : MelonMod
    {
        private bool _menuCreated;
        private bool _menuAttempted;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Gun Modifier loaded!");
            MelonLogger.Msg("Waiting for BoneLib BoneMenu to initialize...");
        }

        public override void OnUpdate()
        {
            GunController.Update();

            if (_menuCreated || _menuAttempted)
                return;

            try
            {
                Assembly boneLib = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "BoneLib");

                if (boneLib == null)
                    return;

                Type pageType = boneLib.GetType("BoneLib.BoneMenu.Page");
                if (pageType == null)
                    throw new Exception("BoneLib.BoneMenu.Page type was not found.");

                PropertyInfo rootProperty = pageType.GetProperty(
                    "Root",
                    BindingFlags.Public | BindingFlags.Static);

                if (rootProperty == null)
                    throw new Exception("BoneMenu Page.Root property was not found.");

                // BoneLib creates Page.Root during its own initialization.
                // Wait until it exists instead of trying to build our menu too early.
                object root = rootProperty.GetValue(null);
                if (root == null)
                    return;

                // Install the gun-fire hook once BoneLib is fully available.
                GunController.Initialize(boneLib);

                Assembly unityAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "UnityEngine.CoreModule");

                if (unityAssembly == null)
                    unityAssembly = Assembly.Load("UnityEngine.CoreModule");

                Type colorType = unityAssembly.GetType("UnityEngine.Color");
                if (colorType == null)
                    throw new Exception("UnityEngine.Color type was not found.");

                object white = Activator.CreateInstance(colorType);
                colorType.GetField("r").SetValue(white, 1f);
                colorType.GetField("g").SetValue(white, 1f);
                colorType.GetField("b").SetValue(white, 1f);
                colorType.GetField("a").SetValue(white, 1f);

                // BoneLib 3.2.2 CreatePage has four parameters:
                // name, color, maxElements, createLink.
                MethodInfo createPage = pageType.GetMethod(
                    "CreatePage",
                    new Type[] { typeof(string), colorType, typeof(int), typeof(bool) });

                if (createPage == null)
                    throw new Exception("BoneMenu CreatePage(string, Color, int, bool) was not found.");

                object page = createPage.Invoke(root, new object[] { "Gun Modifier", white, 0, true });

                MethodInfo createInt = pageType.GetMethod(
                    "CreateInt",
                    new Type[]
                    {
                        typeof(string), colorType, typeof(int), typeof(int),
                        typeof(int), typeof(int), typeof(Action<int>)
                    });

                if (createInt == null)
                    throw new Exception("BoneMenu CreateInt method was not found.");

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
                        typeof(string), colorType, typeof(bool), typeof(Action<bool>)
                    });

                if (createBool == null)
                    throw new Exception("BoneMenu CreateBool method was not found.");

                createBool.Invoke(page, new object[]
                {
                    "Infinite Ammo", white, false,
                    new Action<bool>(SetInfiniteAmmo)
                });

                createBool.Invoke(page, new object[]
                {
                    "Any Mag", white, false,
                    new Action<bool>(SetAnyMag)
                });

                _menuCreated = true;
                MelonLogger.Msg("Gun Modifier menu created successfully!");
            }
            catch (Exception ex)
            {
                _menuAttempted = true;
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

        private static void SetAnyMag(bool value)
        {
            GunSettings.AnyMag = value;
            MelonLogger.Msg("Gun Modifier: Any Mag set to " + value + ". Magazine compatibility hook status will be reported by GunController.");
        }
    }
}