using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MelonLoader;

namespace GunModifier
{
    public static class GunController
    {
        private static bool _initialized;
        private static EventInfo _preFireEvent;
        private static Delegate _preFireHandler;
        private static MethodInfo _setRpmMethod;
        private static readonly Dictionary<int, float> OriginalRpm = new Dictionary<int, float>();

        public static void Initialize(Assembly boneLib)
        {
            if (_initialized)
                return;

            try
            {
                Type hookingType = boneLib.GetType("BoneLib.Hooking");
                Type extensionsType = boneLib.GetType("BoneLib.Extensions");

                if (hookingType == null || extensionsType == null)
                    throw new Exception("BoneLib Hooking or Extensions type was not found.");

                _preFireEvent = hookingType.GetEvent(
                    "OnPreFireGun",
                    BindingFlags.Public | BindingFlags.Static);

                if (_preFireEvent == null)
                    throw new Exception("BoneLib OnPreFireGun event was not found.");

                _setRpmMethod = extensionsType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "SetRpm")
                            return false;

                        ParameterInfo[] parameters = m.GetParameters();
                        return parameters.Length == 2 && parameters[1].ParameterType == typeof(float);
                    });

                if (_setRpmMethod == null)
                    throw new Exception("BoneLib Extensions.SetRpm method was not found.");

                MethodInfo callback = typeof(GunController).GetMethod(
                    nameof(OnPreFireGun),
                    BindingFlags.NonPublic | BindingFlags.Static);

                _preFireHandler = Delegate.CreateDelegate(_preFireEvent.EventHandlerType, callback);
                _preFireEvent.AddEventHandler(null, _preFireHandler);

                _initialized = true;
                MelonLogger.Msg("Gun Modifier: fire-rate hook installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Gun Modifier: could not install fire-rate hook: " + ex);
            }
        }

        private static void OnPreFireGun(object gun)
        {
            if (gun == null || _setRpmMethod == null)
                return;

            try
            {
                Type gunType = gun.GetType();
                ParameterInfo gunParameter = _setRpmMethod.GetParameters()[0];
                if (!gunParameter.ParameterType.IsInstanceOfType(gun))
                    return;

                int id = GetInstanceId(gun, gunType);
                float originalRpm;

                if (!OriginalRpm.TryGetValue(id, out originalRpm))
                {
                    originalRpm = ReadRpm(gun, gunType);
                    if (originalRpm <= 0f)
                        return;

                    OriginalRpm[id] = originalRpm;
                }

                float multiplier = GunSettings.FireRateMultiplier;
                float targetRpm = Math.Max(1f, originalRpm * multiplier);
                _setRpmMethod.Invoke(null, new object[] { gun, targetRpm });
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Gun Modifier: fire-rate update failed: " + ex.Message);
            }
        }

        private static float ReadRpm(object gun, Type gunType)
        {
            FieldInfo field = gunType.GetField(
                "roundsPerMinute",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
                return Convert.ToSingle(field.GetValue(gun));

            PropertyInfo property = gunType.GetProperty(
                "roundsPerMinute",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property != null)
                return Convert.ToSingle(property.GetValue(gun));

            throw new Exception("Gun roundsPerMinute field/property was not found.");
        }

        private static int GetInstanceId(object gun, Type gunType)
        {
            MethodInfo method = gunType.GetMethod(
                "GetInstanceID",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (method != null)
                return Convert.ToInt32(method.Invoke(gun, null));

            return RuntimeHelpers.GetHashCode(gun);
        }
    }
}