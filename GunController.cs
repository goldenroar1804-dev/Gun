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

        private sealed class PlatformSnapshot
        {
            public object Socket;
            public object Plug;
            public int PlugId;
            public MemberInfo SocketPlatformMember;
            public object SocketPlatformValue;
            public object DefaultMagazine;
            public MemberInfo DefaultPlatformMember;
            public object DefaultPlatformValue;
        }

        private sealed class RecoilSnapshot
        {
            public object Rigidbody;
            public MemberInfo VelocityMember;
            public MemberInfo AngularVelocityMember;
            public float[] Velocity;
            public float[] AngularVelocity;
        }

        private static bool _initialized;
        private static bool _damageHookInstalled;
        private static bool _anyMagHooksInstalled;
        private static EventInfo _preFireEvent;
        private static Delegate _preFireHandler;
        private static MethodInfo _setRpmMethod;
        private static MethodInfo _createHookMethod;
        private static readonly Dictionary<string, float> OriginalValues = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> AppliedMagazineCapacity = new Dictionary<string, int>();
        private static readonly Dictionary<int, PlatformSnapshot> PlatformSnapshots = new Dictionary<int, PlatformSnapshot>();
        private static readonly Dictionary<int, RecoilSnapshot> RecoilSnapshots = new Dictionary<int, RecoilSnapshot>();
        private static readonly HashSet<string> LoggedMembers = new HashSet<string>();
        private static bool _infiniteAmmoLogWritten;

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

                _createHookMethod = hookingType.GetMethod("CreateHook", StaticFlags, null,
                    new[] { typeof(MethodInfo), typeof(MethodInfo), typeof(bool) }, null);
                if (_createHookMethod == null)
                    throw new Exception("BoneLib Hooking.CreateHook was not found.");

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
                InstallAnyMagHooks();
                InstallRecoilHooks();

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

                _createHookMethod.Invoke(null, new object[] { target, prefix, true });
                _damageHookInstalled = true;
                MelonLogger.Msg("Gun Modifier: NPC damage hook installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: damage hook unavailable: " + ex.Message);
            }
        }

        private static void InstallRecoilHooks()
        {
            try
            {
                Type gunType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Il2CppSLZ.Marrow.Gun"))
                    .FirstOrDefault(t => t != null);
                if (gunType == null)
                {
                    MelonLogger.Warning("Gun Modifier: Gun type not found; recoil hook unavailable.");
                    return;
                }

                MethodInfo target = gunType.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "OnFire");
                MethodInfo prefix = typeof(GunController).GetMethod(nameof(RecoilPrefix), StaticFlags);
                MethodInfo postfix = typeof(GunController).GetMethod(nameof(RecoilPostfix), StaticFlags);
                if (target == null || prefix == null || postfix == null)
                    throw new Exception("Gun.OnFire or recoil callbacks were not found.");

                _createHookMethod.Invoke(null, new object[] { target, prefix, true });
                _createHookMethod.Invoke(null, new object[] { target, postfix, false });
                MelonLogger.Msg("Gun Modifier: physical recoil hooks installed on Gun.OnFire.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: recoil hook unavailable: " + ex.Message);
            }
        }

        private static void RecoilPrefix(object __instance)
        {
            if (__instance == null || Math.Abs(GunSettings.RecoilMultiplier - 1f) < 0.001f)
                return;

            try
            {
                Type rigidbodyType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.Rigidbody"))
                    .FirstOrDefault(t => t != null);
                if (rigidbodyType == null)
                    return;

                // Search the socket, gun object, their transforms, and the gun's
                // host/entity roots. Unity components can expose lookup methods on
                // their GameObject or Transform rather than on the IL2CPP wrapper.
                object socket = GetMemberValue(__instance, "ammoSocket");
                object body = FindGunRigidbody(__instance, socket, rigidbodyType);
                if (body == null)
                {
                    LogOnce("recoil-no-rigidbody", "Gun Modifier: recoil could not locate a Rigidbody on the gun, its parents, or its children.");
                    return;
                }
                LogOnce("recoil-rigidbody-found", "Gun Modifier: recoil found a Rigidbody on the gun hierarchy.");

                MemberInfo velocity = FindMember(body.GetType(), "velocity");
                MemberInfo angularVelocity = FindMember(body.GetType(), "angularVelocity");
                if (velocity == null || angularVelocity == null)
                    return;

                int id = GetInstanceId(__instance, __instance.GetType());
                RecoilSnapshots[id] = new RecoilSnapshot
                {
                    Rigidbody = body,
                    VelocityMember = velocity,
                    AngularVelocityMember = angularVelocity,
                    Velocity = ReadVector(ReadMember(body, velocity)),
                    AngularVelocity = ReadVector(ReadMember(body, angularVelocity))
                };
            }
            catch (Exception ex)
            {
                LogOnce("recoil-prefix-error", "Gun Modifier: recoil capture failed: " + ex.Message);
            }
        }

        private static void RecoilPostfix(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                int id = GetInstanceId(__instance, __instance.GetType());
                if (!RecoilSnapshots.TryGetValue(id, out RecoilSnapshot before))
                    return;
                RecoilSnapshots.Remove(id);

                float multiplier = GunSettings.RecoilMultiplier;
                if (Math.Abs(multiplier - 1f) < 0.001f || before.Rigidbody == null)
                    return;

                float[] velocityAfter = ReadVector(ReadMember(before.Rigidbody, before.VelocityMember));
                float[] angularAfter = ReadVector(ReadMember(before.Rigidbody, before.AngularVelocityMember));
                float[] velocityTarget = ScaleDelta(before.Velocity, velocityAfter, multiplier);
                float[] angularTarget = ScaleDelta(before.AngularVelocity, angularAfter, multiplier);

                WriteMemberValue(before.Rigidbody, before.VelocityMember, CreateVector(before.VelocityMember, velocityTarget));
                WriteMemberValue(before.Rigidbody, before.AngularVelocityMember, CreateVector(before.AngularVelocityMember, angularTarget));
            }
            catch (Exception ex)
            {
                LogOnce("recoil-postfix-error", "Gun Modifier: recoil adjustment failed: " + ex.Message);
            }
        }

        private static object FindGunRigidbody(object gun, object socket, Type rigidbodyType)
        {
            object[] candidates =
            {
                socket,
                GetMemberValue(socket, "gameObject"),
                GetMemberValue(socket, "transform"),
                gun,
                GetMemberValue(gun, "gameObject"),
                GetMemberValue(gun, "transform"),
                GetMemberValue(gun, "host"),
                GetMemberValue(GetMemberValue(gun, "host"), "gameObject"),
                GetMemberValue(gun, "entity"),
                GetMemberValue(GetMemberValue(gun, "entity"), "gameObject")
            };

            foreach (object candidate in candidates)
            {
                if (candidate == null) continue;
                object body = InvokeComponentLookup(candidate, "GetComponent", rigidbodyType);
                if (body == null) body = InvokeComponentLookup(candidate, "GetComponentInParent", rigidbodyType);
                if (body == null) body = InvokeComponentLookup(candidate, "GetComponentInChildren", rigidbodyType);
                if (body != null) return body;
            }
            return null;
        }

        private static object InvokeComponentLookup(object target, string methodName, Type componentType)
        {
            if (target == null) return null;
            try
            {
                MethodInfo method = target.GetType().GetMethods(InstanceFlags)
                    .FirstOrDefault(m => m.Name == methodName &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(Type));
                return method == null ? null : method.Invoke(target, new object[] { componentType });
            }
            catch { return null; }
        }

        private static float[] ReadVector(object vector)
        {
            if (vector == null) return new[] { 0f, 0f, 0f };
            return new[]
            {
                Convert.ToSingle(GetMemberValue(vector, "x")),
                Convert.ToSingle(GetMemberValue(vector, "y")),
                Convert.ToSingle(GetMemberValue(vector, "z"))
            };
        }

        private static float[] ScaleDelta(float[] before, float[] after, float multiplier)
        {
            return new[]
            {
                before[0] + (after[0] - before[0]) * multiplier,
                before[1] + (after[1] - before[1]) * multiplier,
                before[2] + (after[2] - before[2]) * multiplier
            };
        }

        private static object CreateVector(MemberInfo vectorMember, float[] values)
        {
            Type vectorType = vectorMember is FieldInfo
                ? ((FieldInfo)vectorMember).FieldType
                : ((PropertyInfo)vectorMember).PropertyType;
            ConstructorInfo constructor = vectorType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
            if (constructor != null)
                return constructor.Invoke(new object[] { values[0], values[1], values[2] });

            object vector = Activator.CreateInstance(vectorType);
            WriteMemberValue(vector, FindMember(vectorType, "x"), values[0]);
            WriteMemberValue(vector, FindMember(vectorType, "y"), values[1]);
            WriteMemberValue(vector, FindMember(vectorType, "z"), values[2]);
            return vector;
        }

        private static void InstallAnyMagHooks()
        {
            try
            {
                Type socketType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Il2CppSLZ.Marrow.AmmoSocket"))
                    .FirstOrDefault(t => t != null);

                if (socketType == null)
                {
                    MelonLogger.Warning("Gun Modifier: AmmoSocket type not found; Any Mag unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(GunController).GetMethod(nameof(AnyMagPrefix), StaticFlags);
                MethodInfo applyPostfix = typeof(GunController).GetMethod(nameof(AnyMagPostfix), StaticFlags);
                MethodInfo restorePostfix = typeof(GunController).GetMethod(nameof(AnyMagRestorePostfix), StaticFlags);
                int installed = 0;

                // Try the incoming plug before the socket validates it, then retry
                // after the socket has assigned its internal magazine plug.
                installed += HookAnyMagMethod(socketType, "OnPlugEnter", 1, prefix, true);
                installed += HookAnyMagMethod(socketType, "OnPlugEnter", 1, applyPostfix, false);
                installed += HookAnyMagMethod(socketType, "OnPlugLocked", 1, prefix, true);
                installed += HookAnyMagMethod(socketType, "OnPlugLocked", 1, applyPostfix, false);
                installed += HookAnyMagMethod(socketType, "OnPlugExit", 1, restorePostfix, false);
                installed += HookAnyMagMethod(socketType, "OnPlugUnlocked", 0, restorePostfix, false);

                _anyMagHooksInstalled = installed >= 2;
                MelonLogger.Msg("Gun Modifier: Any Mag lifecycle hooks installed: " + installed + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Gun Modifier: Any Mag hook setup failed: " + ex);
            }
        }

        private static int HookAnyMagMethod(Type socketType, string methodName, int parameterCount, MethodInfo hook, bool isPrefix)
        {
            MethodInfo target = socketType.GetMethods(InstanceFlags)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == parameterCount);
            if (target == null)
            {
                MelonLogger.Warning("Gun Modifier: AmmoSocket." + methodName + " was not found.");
                return 0;
            }

            _createHookMethod.Invoke(null, new object[] { target, hook, isPrefix });
            MelonLogger.Msg("Gun Modifier: Any Mag hooked AmmoSocket." + methodName + (isPrefix ? " (prefix)." : " (postfix)."));
            return 1;
        }

        private static void AnyMagPrefix(object __instance, object[] __args)
        {
            object plug = __args != null && __args.Length > 0 ? __args[0] : null;
            TryApplyAnyMag(__instance, plug, "prefix");
        }

        private static void AnyMagPostfix(object __instance, object[] __args)
        {
            object plug = __args != null && __args.Length > 0 ? __args[0] : null;
            TryApplyAnyMag(__instance, plug, "postfix");
        }

        private static void TryApplyAnyMag(object socket, object incomingPlug, string phase)
        {
            if (socket == null) return;

            int socketId = GetInstanceId(socket, socket.GetType());
            if (!GunSettings.AnyMag)
            {
                RestoreAnyMagPlatforms(socketId);
                return;
            }

            try
            {
                // OnPlugEnter's argument may not yet contain its Magazine. After
                // the socket assigns _magazinePlug, that object is the reliable path.
                object plug = incomingPlug ??
                    GetMemberValue(socket, "_magazinePlug") ??
                    GetMemberValue(socket, "magazinePlug");

                object magazine = GetMemberValue(plug, "magazine") ??
                    GetMemberValue(plug, "_magazine");
                object magazineState = GetMemberValue(magazine, "magazineState") ??
                    GetMemberValue(magazine, "state");
                object magazineData = GetMemberValue(magazineState, "magazineData") ??
                    GetMemberValue(magazine, "magazineData") ??
                    GetMemberValue(plug, "magazineData") ??
                    GetMemberValue(plug, "data");

                string incomingPlatform =
                    SafeString(GetMemberValue(magazineData, "platform"));
                if (incomingPlatform == "null" || string.IsNullOrEmpty(incomingPlatform))
                    incomingPlatform = SafeString(GetMemberValue(magazine, "platform"));
                if (incomingPlatform == "null" || string.IsNullOrEmpty(incomingPlatform))
                    incomingPlatform = SafeString(GetMemberValue(plug, "platform"));

                if (incomingPlatform == "null" || string.IsNullOrEmpty(incomingPlatform))
                {
                    LogOnce("anymag-no-platform-" + phase,
                        "Gun Modifier: Any Mag " + phase + " could not read a platform. plug=" +
                        DescribeObject(plug) + "; magazine=" + DescribeObject(magazine) +
                        "; magazineState=" + DescribeObject(magazineState) +
                        "; magazineData=" + DescribeObject(magazineData) +
                        "; plug members=" + DescribeMembers(plug));
                    return;
                }

                object gun = GetMemberValue(socket, "gun");
                object defaultMagazine = GetMemberValue(gun, "defaultMagazine");
                if (gun == null || defaultMagazine == null)
                {
                    LogOnce("anymag-no-gun-default", "Gun Modifier: Any Mag could not find the gun's default magazine data.");
                    return;
                }

                int plugId = GetInstanceId(plug ?? magazine, (plug ?? magazine).GetType());
                if (PlatformSnapshots.TryGetValue(socketId, out PlatformSnapshot existing) && existing.PlugId != plugId)
                    RestoreAnyMagPlatforms(socketId);

                if (!PlatformSnapshots.TryGetValue(socketId, out PlatformSnapshot snapshot))
                {
                    snapshot = new PlatformSnapshot
                    {
                        Socket = socket,
                        Plug = plug,
                        PlugId = plugId,
                        SocketPlatformMember = FindMember(socket.GetType(), "platform"),
                        DefaultMagazine = defaultMagazine,
                        DefaultPlatformMember = FindMember(defaultMagazine.GetType(), "platform")
                    };

                    if (snapshot.SocketPlatformMember != null)
                        snapshot.SocketPlatformValue = ReadMember(socket, snapshot.SocketPlatformMember);
                    if (snapshot.DefaultPlatformMember != null)
                        snapshot.DefaultPlatformValue = ReadMember(defaultMagazine, snapshot.DefaultPlatformMember);

                    PlatformSnapshots[socketId] = snapshot;
                }

                if (snapshot.SocketPlatformMember != null)
                    WriteMemberValue(socket, snapshot.SocketPlatformMember, incomingPlatform);
                if (snapshot.DefaultMagazine != null && snapshot.DefaultPlatformMember != null)
                    WriteMemberValue(snapshot.DefaultMagazine, snapshot.DefaultPlatformMember, incomingPlatform);

                LogOnce("anymag-platform:" + incomingPlatform,
                    "Gun Modifier: Any Mag matched socket and gun magazine platform to incoming magazine platform '" +
                    incomingPlatform + "'.");
            }
            catch (Exception ex)
            {
                LogOnce("anymag-prefix-error", "Gun Modifier: Any Mag compatibility match failed: " + ex.Message);
            }
        }

        private static void AnyMagRestorePostfix(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                int socketId = GetInstanceId(__instance, __instance.GetType());
                RestoreAnyMagPlatforms(socketId);
            }
            catch (Exception ex)
            {
                LogOnce("anymag-restore-error", "Gun Modifier: Any Mag platform restore failed: " + ex.Message);
            }
        }

        private static void RestoreAnyMagPlatforms(int socketId)
        {
            if (!PlatformSnapshots.TryGetValue(socketId, out PlatformSnapshot snapshot))
                return;

            PlatformSnapshots.Remove(socketId);

            if (snapshot.SocketPlatformMember != null && snapshot.Socket != null)
                WriteMemberValue(snapshot.Socket, snapshot.SocketPlatformMember, snapshot.SocketPlatformValue);
            if (snapshot.DefaultMagazine != null && snapshot.DefaultPlatformMember != null)
                WriteMemberValue(snapshot.DefaultMagazine, snapshot.DefaultPlatformMember, snapshot.DefaultPlatformValue);
        }

        private static void DamagePrefix(object[] __args)
        {
            float multiplier = GunSettings.DamageMultiplier;
            if (Math.Abs(multiplier - 1f) < 0.001f || __args == null)
                return;

            for (int i = 0; i < __args.Length; i++)
            {
                object value = __args[i];
                if (value == null) continue;

                MemberInfo damageMember = FindMember(value.GetType(), "damage");
                if (damageMember == null) continue;

                try
                {
                    float damage = Convert.ToSingle(ReadMember(value, damageMember));
                    WriteMemberValue(value, damageMember, damage * multiplier);
                    __args[i] = value;
                    return;
                }
                catch { }
            }
        }

        private static void OnPreFireGun(object gun)
        {
            if (gun == null) return;

            try
            {
                Type gunType = gun.GetType();
                ApplyFireRate(gun, gunType);
                ApplyMagazineCapacity(gun, gunType);
                ApplyInfiniteAmmo(gun, gunType);
                LogGunMembersOnce(gunType);
                InspectMagazineLayout(gun, gunType);
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

        private static void ApplyMagazineCapacity(object gun, Type gunType)
        {
            float multiplier = GunSettings.MagazineMultiplier;
            object defaultMagazine = GetMemberValue(gun, "defaultMagazine");
            object internalMagazine = GetMemberValue(gun, "internalMagazine");

            ApplyMagazineData(defaultMagazine, multiplier, "default-magazine");
            ApplyMagazineData(internalMagazine, multiplier, "internal-magazine");

            object socket = GetMemberValue(gun, "ammoSocket");
            object plug = GetMemberValue(socket, "_magazinePlug");
            object magazine = GetMemberValue(plug, "magazine");
            object state = GetMemberValue(magazine, "magazineState");
            object loadedData = GetMemberValue(state, "magazineData");

            ApplyMagazineData(loadedData, multiplier, "loaded-magazine");

            if (state != null && loadedData != null)
            {
                int targetCapacity = ReadIntMember(loadedData, "rounds");
                int stateId = GetInstanceId(state, state.GetType());
                string key = stateId + ":capacity";
                if (targetCapacity > 0 && (!AppliedMagazineCapacity.TryGetValue(key, out int previous) || previous != targetCapacity))
                {
                    AppliedMagazineCapacity[key] = targetCapacity;
                    if (GunSettings.MagazineMultiplier > 1.001f || ReadAmmoCount(state) > targetCapacity)
                    {
                        InvokeIntMethod(state, "SetCartridge", targetCapacity);
                        LogOnce("mag-capacity-set:" + key,
                            "Gun Modifier: adjusted loaded magazine capacity to " + targetCapacity + " rounds.");
                    }
                }
            }
        }

        private static void ApplyMagazineData(object magazineData, float multiplier, string category)
        {
            if (magazineData == null) return;

            MemberInfo roundsMember = FindMember(magazineData.GetType(), "rounds");
            if (roundsMember == null) return;

            try
            {
                string key = GetInstanceId(magazineData, magazineData.GetType()) + ":rounds";
                if (!OriginalValues.TryGetValue(key, out float original))
                {
                    original = Convert.ToSingle(ReadMember(magazineData, roundsMember));
                    OriginalValues[key] = original;
                }

                int target = Math.Max(1, (int)Math.Round(original * multiplier));
                WriteMemberValue(magazineData, roundsMember, target);
                if (Math.Abs(multiplier - 1f) > 0.001f)
                    LogOnce(category + ":" + key, "Gun Modifier: " + category + " capacity set to " + target + " rounds.");
            }
            catch (Exception ex)
            {
                LogOnce("mag-data-error:" + category, "Gun Modifier: could not adjust " + category + ": " + ex.Message);
            }
        }

        private static void ApplyInfiniteAmmo(object gun, Type gunType)
        {
            if (!GunSettings.InfiniteAmmo)
                return;

            object magazineState = GetMemberValue(gun, "MagazineState");
            if (magazineState == null)
            {
                object socket = GetMemberValue(gun, "ammoSocket");
                object plug = GetMemberValue(socket, "_magazinePlug");
                object magazine = GetMemberValue(plug, "magazine");
                magazineState = GetMemberValue(magazine, "magazineState");
            }

            if (magazineState != null && InvokeNoArgMethod(magazineState, "Refill"))
            {
                if (!_infiniteAmmoLogWritten)
                {
                    _infiniteAmmoLogWritten = true;
                    MelonLogger.Msg("Gun Modifier: infinite ammo is refilling the active MagazineState.");
                }
            }
            else
            {
                LogOnce("infinite-ammo-no-magstate",
                    "Gun Modifier: infinite ammo is enabled, but this gun has no active MagazineState. Internal-magazine handling still needs inspection.");
            }
        }

        private static void InspectMagazineLayout(object gun, Type gunType)
        {
            string gunKey = "mag-inspect:" + gunType.FullName;
            if (!LoggedMembers.Add(gunKey)) return;

            object socket = GetMemberValue(gun, "ammoSocket");
            object plug = GetMemberValue(socket, "_magazinePlug");
            object magazine = GetMemberValue(plug, "magazine");
            object state = GetMemberValue(magazine, "magazineState");
            object data = GetMemberValue(state, "magazineData");

            MelonLogger.Msg("Gun Modifier: ammoSocket=" + DescribeObject(socket) +
                "; socket platform=" + SafeString(GetMemberValue(socket, "platform")) +
                "; plug=" + DescribeObject(plug) +
                "; magazine=" + DescribeObject(magazine) +
                "; inserted=" + SafeString(GetMemberValue(magazine, "isMagazineInserted")) +
                "; magazineData=" + DescribeObject(data) +
                "; mag platform=" + SafeString(GetMemberValue(data, "platform")) +
                "; mag rounds=" + SafeString(GetMemberValue(data, "rounds")) +
                "; loaded ammo=" + SafeString(GetMemberValue(state, "AmmoCount")) +
                "; default platform=" + SafeString(GetMemberValue(GetMemberValue(gun, "defaultMagazine"), "platform")) +
                "; default rounds=" + SafeString(GetMemberValue(GetMemberValue(gun, "defaultMagazine"), "rounds")) +
                "; internal rounds=" + SafeString(GetMemberValue(GetMemberValue(gun, "internalMagazine"), "rounds")));
        }

        private static string DescribeObject(object value)
        {
            return value == null ? "null" : value.GetType().FullName;
        }

        private static string DescribeMembers(object value)
        {
            if (value == null) return "null";
            try
            {
                return string.Join(", ", value.GetType().GetFields(InstanceFlags).Select(f => f.Name)
                    .Concat(value.GetType().GetProperties(InstanceFlags).Select(p => p.Name))
                    .Distinct().Take(60));
            }
            catch { return value.GetType().FullName; }
        }

        private static string SafeString(object value)
        {
            return value == null ? "null" : Convert.ToString(value);
        }

        private static int ReadAmmoCount(object state)
        {
            try { return Convert.ToInt32(GetMemberValue(state, "AmmoCount")); }
            catch { return -1; }
        }

        private static int ReadIntMember(object target, string name)
        {
            try { return Convert.ToInt32(GetMemberValue(target, name)); }
            catch { return 0; }
        }

        private static void InvokeIntMethod(object target, string name, int value)
        {
            try
            {
                MethodInfo method = target.GetType().GetMethods(InstanceFlags)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 1 &&
                        (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(uint)));
                if (method != null)
                    method.Invoke(target, new object[] { Convert.ChangeType(value, method.GetParameters()[0].ParameterType) });
            }
            catch (Exception ex)
            {
                LogOnce("invoke-" + name, "Gun Modifier: " + name + " failed: " + ex.Message);
            }
        }

        private static bool InvokeNoArgMethod(object target, string name)
        {
            try
            {
                MethodInfo method = target.GetType().GetMethods(InstanceFlags)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
                if (method == null) return false;
                method.Invoke(target, null);
                return true;
            }
            catch (Exception ex)
            {
                LogOnce("invoke-" + name, "Gun Modifier: " + name + " failed: " + ex.Message);
                return false;
            }
        }

        private static void LogGunMembersOnce(Type type)
        {
            string key = "gun-layout:" + type.FullName;
            if (!LoggedMembers.Add(key)) return;

            string members = string.Join(", ", type.GetFields(InstanceFlags).Select(f => f.Name)
                .Concat(type.GetProperties(InstanceFlags).Select(p => p.Name)).Distinct().Take(100));
            MelonLogger.Msg("Gun Modifier: gun layout " + type.FullName + " => " + members);
        }

        private static void LogOnce(string key, string message)
        {
            if (LoggedMembers.Add(key))
                MelonLogger.Msg(message);
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            if (type == null) return null;
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (field != null) return field;
            PropertyInfo property = type.GetProperty(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (property != null && property.CanRead) return property;
            return null;
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null) return null;
            try
            {
                MemberInfo member = FindMember(target.GetType(), name);
                return member == null ? null : ReadMember(target, member);
            }
            catch { return null; }
        }

        private static object ReadMember(object target, MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.GetValue(target);
            return ((PropertyInfo)member).GetValue(target, null);
        }

        private static void WriteMemberValue(object target, MemberInfo member, object value)
        {
            Type memberType = member is FieldInfo ? ((FieldInfo)member).FieldType : ((PropertyInfo)member).PropertyType;
            object converted = value == null ? null :
                (memberType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, Nullable.GetUnderlyingType(memberType) ?? memberType));
            FieldInfo field = member as FieldInfo;
            if (field != null)
                field.SetValue(target, converted);
            else
            {
                PropertyInfo property = (PropertyInfo)member;
                if (property.CanWrite)
                    property.SetValue(target, converted, null);
            }
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