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
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _initialized;
        private static bool _damageHookInstalled;
        private static bool _magHooksScanned;
        private static EventInfo _preFireEvent;
        private static Delegate _preFireHandler;
        private static MethodInfo _setRpmMethod;
        private static readonly Dictionary<string, float> OriginalValues = new Dictionary<string, float>();
        private static readonly HashSet<string> LoggedMembers = new HashSet<string>();

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

                _preFireEvent = hookingType.GetEvent("OnPreFireGun", StaticFlags);
                if (_preFireEvent == null)
                    throw new Exception("BoneLib OnPreFireGun event was not found.");

                _setRpmMethod = extensionsType.GetMethods(StaticFlags).FirstOrDefault(m =>
                {
                    if (m.Name != "SetRpm") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 2 && p[1].ParameterType == typeof(float);
                });
                if (_setRpmMethod == null)
                    throw new Exception("BoneLib Extensions.SetRpm method was not found.");

                MethodInfo callback = typeof(GunController).GetMethod(nameof(OnPreFireGun), StaticFlags);
                _preFireHandler = Delegate.CreateDelegate(_preFireEvent.EventHandlerType, callback);
                _preFireEvent.AddEventHandler(null, _preFireHandler);

                InstallDamageHook(hookingType);
                ScanMagazineCompatibilityHooks(hookingType);

                _initialized = true;
                MelonLogger.Msg("Gun Modifier: fire-rate hook installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Gun Modifier: controller initialization failed: " + ex);
            }
        }

        private static void InstallDamageHook(Type hookingType)
        {
            try
            {
                Type healthType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Il2CppSLZ.Marrow.PuppetMasta.SubBehaviourHealth"))
                    .FirstOrDefault(t => t != null);

                if (healthType == null)
                {
                    MelonLogger.Warning("Gun Modifier: NPC damage type not found; damage hook unavailable.");
                    return;
                }

                MethodInfo target = healthType.GetMethods(InstanceFlags)
                    .FirstOrDefault(m => m.Name == "TakeDamage");
                MethodInfo prefix = typeof(GunController).GetMethod(nameof(DamagePrefix), StaticFlags);
                if (target == null || prefix == null)
                    throw new Exception("TakeDamage or damage prefix method not found.");

                MethodInfo createHook = hookingType.GetMethod("CreateHook", StaticFlags);
                createHook.Invoke(null, new object[] { target, prefix, true });
                _damageHookInstalled = true;
                MelonLogger.Msg("Gun Modifier: NPC damage hook installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: damage hook unavailable: " + ex.Message);
            }
        }

        // Harmony supplies the original arguments as an object array. The Attack value
        // is changed only when its own damage member is found.
        private static void DamagePrefix(object[] __args)
        {
            float multiplier = GunSettings.DamageMultiplier;
            if (Math.Abs(multiplier - 1f) < 0.001f || __args == null)
                return;

            for (int i = 0; i < __args.Length; i++)
            {
                object value = __args[i];
                if (value == null) continue;

                Type type = value.GetType();
                MemberInfo damageMember = FindMember(type, "damage");
                if (damageMember == null) continue;

                try
                {
                    object oldValue = ReadMember(value, damageMember);
                    float damage = Convert.ToSingle(oldValue);
                    WriteMember(value, damageMember, damage * multiplier);
                    __args[i] = value;
                    return;
                }
                catch { }
            }
        }

        private static void ScanMagazineCompatibilityHooks(Type hookingType)
        {
            if (_magHooksScanned) return;
            _magHooksScanned = true;

            try
            {
                MethodInfo createHook = hookingType.GetMethod("CreateHook", StaticFlags);
                MethodInfo prefix = typeof(GunController).GetMethod(nameof(AnyMagPrefix), StaticFlags);
                int installed = 0;

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string assemblyName = assembly.GetName().Name ?? "";
                    if (!assemblyName.Contains("Il2CppSLZ") && !assemblyName.Contains("Assembly-CSharp"))
                        continue;

                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (Type type in types)
                    {
                        string typeName = type.Name;
                        if (!typeName.Contains("Magazine") && !typeName.Contains("Mag"))
                            continue;

                        MethodInfo[] methods;
                        try { methods = type.GetMethods(InstanceFlags); }
                        catch { continue; }

                        foreach (MethodInfo method in methods)
                        {
                            string name = method.Name.ToLowerInvariant();
                            bool looksLikeCompatibility =
                                name.Contains("compatib") || name.Contains("caninsert") ||
                                name.Contains("canplug") || name.Contains("canattach") ||
                                name.Contains("acceptmag") || name.Contains("validmag");
                            if (!looksLikeCompatibility || method.ReturnType != typeof(bool))
                                continue;

                            try
                            {
                                createHook.Invoke(null, new object[] { method, prefix, true });
                                installed++;
                            }
                            catch { }
                        }
                    }
                }

                MelonLogger.Msg("Gun Modifier: Any Mag compatibility hooks found: " + installed + ".");
                if (installed == 0)
                    MelonLogger.Warning("Gun Modifier: no known magazine compatibility methods found; Any Mag needs runtime inspection.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: magazine compatibility scan failed: " + ex.Message);
            }
        }

        private static bool AnyMagPrefix(ref bool __result)
        {
            if (!GunSettings.AnyMag) return true;
            __result = true;
            return false;
        }

        private static void OnPreFireGun(object gun)
        {
            if (gun == null) return;

            try
            {
                Type gunType = gun.GetType();
                ApplyFireRate(gun, gunType);
                ApplyGunValues(gun, gunType);
                LogGunMembersOnce(gun, gunType);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: gun update failed: " + ex.Message);
            }
        }

        private static void ApplyFireRate(object gun, Type gunType)
        {
            ParameterInfo gunParameter = _setRpmMethod.GetParameters()[0];
            if (!gunParameter.ParameterType.IsInstanceOfType(gun)) return;

            int id = GetInstanceId(gun, gunType);
            string key = id + ":roundsPerMinute";
            float original;
            if (!OriginalValues.TryGetValue(key, out original))
            {
                original = ReadRpm(gun, gunType);
                if (original <= 0f) return;
                OriginalValues[key] = original;
            }

            float target = Math.Max(1f, original * GunSettings.FireRateMultiplier);
            _setRpmMethod.Invoke(null, new object[] { gun, target });
        }

        private static void ApplyGunValues(object gun, Type gunType)
        {
            ApplyNamedMultiplier(gun, gunType,
                new[] { "recoil", "recoilForce", "recoilAmount", "recoilStrength" },
                GunSettings.RecoilMultiplier, "recoil");

            ApplyNamedMultiplier(gun, gunType,
                new[] { "magazineCapacity", "maxMagazine", "maxAmmo", "maxRounds", "capacity" },
                GunSettings.MagazineMultiplier, "magazine");

            if (GunSettings.InfiniteAmmo)
            {
                float maximum = FindNumericMember(gun, gunType,
                    new[] { "magazineCapacity", "maxMagazine", "maxAmmo", "maxRounds", "capacity" });
                if (maximum > 0f)
                    SetNamedNumericMembers(gun, gunType,
                        new[] { "ammo", "currentAmmo", "ammoCount", "rounds", "currentRounds", "loadedAmmo" },
                        maximum);
            }
        }

        private static void ApplyNamedMultiplier(object target, Type type, string[] names, float multiplier, string category)
        {
            if (Math.Abs(multiplier - 1f) < 0.001f) return;

            foreach (string name in names)
            {
                MemberInfo member = FindMember(type, name);
                if (member == null) continue;

                try
                {
                    string key = GetInstanceId(target, type) + ":" + member.Name;
                    float original;
                    if (!OriginalValues.TryGetValue(key, out original))
                    {
                        original = Convert.ToSingle(ReadMember(target, member));
                        OriginalValues[key] = original;
                    }
                    WriteMember(target, member, original * multiplier);
                    LogMember(category, type, member.Name);
                }
                catch { }
            }
        }

        private static float FindNumericMember(object target, Type type, string[] names)
        {
            foreach (string name in names)
            {
                MemberInfo member = FindMember(type, name);
                if (member == null) continue;
                try { return Convert.ToSingle(ReadMember(target, member)); }
                catch { }
            }
            return 0f;
        }

        private static void SetNamedNumericMembers(object target, Type type, string[] names, float value)
        {
            foreach (string name in names)
            {
                MemberInfo member = FindMember(type, name);
                if (member == null) continue;
                try
                {
                    WriteMember(target, member, value);
                    LogMember("infinite-ammo", type, member.Name);
                }
                catch { }
            }
        }

        private static void LogGunMembersOnce(object gun, Type type)
        {
            string key = "gun-layout:" + type.FullName;
            if (!LoggedMembers.Add(key)) return;

            string members = string.Join(", ", type.GetFields(InstanceFlags).Select(f => f.Name)
                .Concat(type.GetProperties(InstanceFlags).Select(p => p.Name)).Distinct().Take(80));
            MelonLogger.Msg("Gun Modifier: gun layout " + type.FullName + " => " + members);
        }

        private static void LogMember(string category, Type type, string member)
        {
            string key = category + ":" + type.FullName + ":" + member;
            if (LoggedMembers.Add(key))
                MelonLogger.Msg("Gun Modifier: detected " + category + " member " + type.FullName + "." + member);
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags flags = InstanceFlags;
            FieldInfo field = type.GetField(name, flags | BindingFlags.IgnoreCase);
            if (field != null) return field;
            PropertyInfo property = type.GetProperty(name, flags | BindingFlags.IgnoreCase);
            if (property != null && property.CanRead && property.CanWrite) return property;
            return null;
        }

        private static object ReadMember(object target, MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.GetValue(target);
            return ((PropertyInfo)member).GetValue(target);
        }

        private static void WriteMember(object target, MemberInfo member, float value)
        {
            Type memberType = member is FieldInfo ? ((FieldInfo)member).FieldType : ((PropertyInfo)member).PropertyType;
            object converted = Convert.ChangeType(value, memberType);
            FieldInfo field = member as FieldInfo;
            if (field != null) field.SetValue(target, converted);
            else ((PropertyInfo)member).SetValue(target, converted);
        }

        private static float ReadRpm(object gun, Type type)
        {
            MemberInfo member = FindMember(type, "roundsPerMinute");
            if (member == null) throw new Exception("Gun roundsPerMinute member was not found.");
            return Convert.ToSingle(ReadMember(gun, member));
        }

        private static int GetInstanceId(object target, Type type)
        {
            MethodInfo method = type.GetMethod("GetInstanceID", InstanceFlags);
            if (method != null)
            {
                try { return Convert.ToInt32(method.Invoke(target, null)); }
                catch { }
            }
            return RuntimeHelpers.GetHashCode(target);
        }
    }
}