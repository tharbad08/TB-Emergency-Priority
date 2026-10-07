using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.ModManagerScene;

namespace EmergencyPriorityV11Compat
{
    public sealed class ModStarter : IModStarter
    {
        public void StartMod(IModEnvironment modEnvironment)
        {
            CompatRuntime.Initialize(modEnvironment.ModPath);

            Assembly original = OriginalLocator.Find();
            if (original == null)
            {
                CompatRuntime.Log("ERROR original Emergency Priority assembly was not found inside the local mod package.");
                return;
            }

            CompatRuntime.Log("Loaded original base assembly: " + original.FullName);

            try
            {
                CompatibilityProbe.Run(original);
                EmergencyInterruptionRedirect.Install(new Harmony("tharbad08.EmergencyPriority.V11Compat"), original);
                CompatRuntime.Log("Standalone Timberborn 1.1 compatibility layer started successfully.");
            }
            catch (Exception ex)
            {
                CompatRuntime.LogException("Compatibility startup failed", ex);
            }
        }
    }

    internal static class OriginalLocator
    {
        internal static Assembly Find()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly == typeof(ModStarter).Assembly)
                        continue;

                    if (assembly.GetType("grantemsley.EmergencyPriority.EmergencyInterruptionService", false) != null &&
                        assembly.GetType("grantemsley.EmergencyPriority.EmergencyConstructionRegistry", false) != null)
                        return assembly;
                }
                catch
                {
                    // Ignore unrelated assemblies that cannot be inspected.
                }
            }

            return null;
        }
    }

    internal static class EmergencyInterruptionRedirect
    {
        private static MethodInfo _originalTryInterrupt;

        internal static void Install(Harmony harmony, Assembly original)
        {
            Type serviceType = original.GetType("grantemsley.EmergencyPriority.EmergencyInterruptionService", true);

            _originalTryInterrupt = serviceType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "TryInterrupt" && m.GetParameters().Length == 1);

            MethodInfo onJobRegistered = serviceType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "OnJobRegistered" && m.GetParameters().Length == 1);

            if (_originalTryInterrupt == null)
                throw new MissingMethodException(serviceType.FullName, "TryInterrupt");
            if (onJobRegistered == null)
                throw new MissingMethodException(serviceType.FullName, "OnJobRegistered");

            harmony.Patch(
                onJobRegistered,
                transpiler: new HarmonyMethod(typeof(EmergencyInterruptionRedirect), nameof(TranspileOnJobRegistered))
                {
                    priority = Priority.First
                });

            CompatRuntime.Log("Redirect installed: EmergencyInterruptionService.OnJobRegistered -> safe Timberborn 1.1 interruption path.");
        }

        private static IEnumerable<CodeInstruction> TranspileOnJobRegistered(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(EmergencyInterruptionRedirect), nameof(SafeTryInterrupt));
            List<CodeInstruction> rewritten = new List<CodeInstruction>();
            int replacements = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo called = instruction.operand as MethodInfo;
                if (_originalTryInterrupt != null &&
                    called != null &&
                    (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    called.Module == _originalTryInterrupt.Module &&
                    called.MetadataToken == _originalTryInterrupt.MetadataToken)
                {
                    CodeInstruction safeCall = new CodeInstruction(OpCodes.Call, replacement);
                    safeCall.labels.AddRange(instruction.labels);
                    safeCall.blocks.AddRange(instruction.blocks);
                    rewritten.Add(safeCall);
                    replacements++;
                }
                else
                {
                    rewritten.Add(instruction);
                }
            }

            CompatRuntime.Log("OnJobRegistered transpiler replaced " + replacements + " TryInterrupt call(s).");
            if (replacements != 1)
                CompatRuntime.Log("WARNING expected exactly one TryInterrupt caller; compatibility behavior will fail safe if the original mod layout changed.");

            return rewritten;
        }

        // Signature deliberately accepts object/object. The original IL leaves
        // EmergencyInterruptionService + Worker on the evaluation stack.
        private static void SafeTryInterrupt(object service, object worker)
        {
            if (service == null || worker == null)
                return;

            try
            {
                object behaviorManager = ReflectionCompat.GetComponent(worker, "Timberborn.BehaviorSystem.BehaviorManager");
                if (behaviorManager == null)
                    return;

                object runningExecutor = ReflectionCompat.GetMemberValue(behaviorManager, "RunningExecutor", "_runningExecutor");
                if (runningExecutor == null)
                    return;

                if (ReflectionCompat.IsType(runningExecutor, "Timberborn.NeedBehaviorSystem.ApplyEffectExecutor"))
                {
                    if (!ReflectionCompat.TrySetField(runningExecutor, "_finishTimestamp", 0f))
                        CompatRuntime.LogOnce("apply-effect-field", "WARNING ApplyEffectExecutor._finishTimestamp was not found; current need action was left untouched.");
                    return;
                }

                object runningBehavior = ReflectionCompat.GetMemberValue(behaviorManager, "RunningBehavior", "_runningBehavior");

                if (ReflectionCompat.IsType(runningBehavior, "Timberborn.SleepSystem.SleepNeedBehavior"))
                {
                    if (!ReflectionCompat.TrySetField(behaviorManager, "_runningExecutor", null))
                        CompatRuntime.LogOnce("sleep-executor-field", "WARNING BehaviorManager._runningExecutor was not found; sleeping worker was left untouched.");
                    return;
                }

                if (!ReflectionCompat.IsType(runningBehavior, "Timberborn.Carrying.CarryRootBehavior"))
                    return;

                if (IsHaulingToEmergencySite(service, worker))
                    return;

                if (!TryDropCargoAndReleaseReservations(service, worker))
                {
                    CompatRuntime.LogOnce(
                        "carry-failsafe",
                        "WARNING could not safely release a non-emergency carrying task. The worker was intentionally left on the current task to avoid lost goods or corrupt reservations.");
                    return;
                }

                if (!ReflectionCompat.TrySetField(behaviorManager, "_runningExecutor", null))
                    CompatRuntime.LogOnce("carry-executor-field", "WARNING BehaviorManager._runningExecutor was not found after safe cargo release.");
            }
            catch (Exception ex)
            {
                CompatRuntime.LogExceptionOnce("interrupt-exception", "Emergency interruption failed safely; worker state was left as intact as possible", ex);
            }
        }

        private static bool IsHaulingToEmergencySite(object service, object worker)
        {
            object goodReserver = ReflectionCompat.GetComponent(worker, "Timberborn.InventorySystem.GoodReserver");
            if (goodReserver == null || !ReflectionCompat.GetBool(goodReserver, "HasReservedCapacity"))
                return false;

            object reservation = ReflectionCompat.GetProperty(goodReserver, "CapacityReservation");
            object inventory = ReflectionCompat.GetProperty(reservation, "Inventory");
            if (inventory == null)
                return false;

            object constructionJob = ReflectionCompat.GetComponent(inventory, "Timberborn.ConstructionSites.ConstructionJob");
            if (constructionJob == null)
                return false;

            object registry = ReflectionCompat.GetField(service, "_registry");
            object jobsObject = ReflectionCompat.GetProperty(registry, "EmergencyJobs");
            IEnumerable jobs = jobsObject as IEnumerable;
            if (jobs == null)
                return false;

            foreach (object job in jobs)
            {
                if (ReferenceEquals(job, constructionJob))
                    return true;
            }

            return false;
        }

        private static bool TryDropCargoAndReleaseReservations(object service, object worker)
        {
            object goodCarrier = ReflectionCompat.GetComponent(worker, "Timberborn.Carrying.GoodCarrier");
            object goodReserver = ReflectionCompat.GetComponent(worker, "Timberborn.InventorySystem.GoodReserver");

            bool isCarrying = goodCarrier != null && ReflectionCompat.GetBool(goodCarrier, "IsCarrying");

            // Preflight every operation before mutating state. If Timberborn changes
            // another API, we leave the worker alone rather than half-dropping cargo.
            MethodInfo emptyHands = null;
            MethodInfo addAwaitingGoods = null;
            object spawner = null;
            object carriedGoodAmount = null;
            object gridPosition = null;

            if (isCarrying)
            {
                emptyHands = ReflectionCompat.FindInstanceMethod(goodCarrier, "EmptyHands", 0);
                carriedGoodAmount = ReflectionCompat.GetCarriedGoodAmount(goodCarrier);

                if (emptyHands == null || carriedGoodAmount == null)
                    return false;

                int amount = ReflectionCompat.GetInt(carriedGoodAmount, "Amount");
                if (amount > 0)
                {
                    spawner = ReflectionCompat.GetField(service, "_recoveredGoodStackSpawner");
                    if (spawner == null)
                        return false;

                    addAwaitingGoods = ReflectionCompat.FindInstanceMethod(spawner, "AddAwaitingGoods", 2);
                    gridPosition = ReflectionCompat.GetWorkerGridPosition(worker);
                    if (addAwaitingGoods == null || gridPosition == null)
                        return false;
                }
            }

            MethodInfo unreserveCapacity = null;
            MethodInfo unreserveStock = null;
            bool hasCapacity = goodReserver != null && ReflectionCompat.GetBool(goodReserver, "HasReservedCapacity");
            bool hasStock = goodReserver != null && ReflectionCompat.GetBool(goodReserver, "HasReservedStock");

            if (hasCapacity)
            {
                unreserveCapacity = ReflectionCompat.FindInstanceMethod(goodReserver, "UnreserveCapacity", 0);
                if (unreserveCapacity == null)
                    return false;
            }

            if (hasStock)
            {
                unreserveStock = ReflectionCompat.FindInstanceMethod(goodReserver, "UnreserveStock", 0);
                if (unreserveStock == null)
                    return false;
            }

            try
            {
                if (isCarrying)
                {
                    int amount = ReflectionCompat.GetInt(carriedGoodAmount, "Amount");
                    if (amount > 0)
                    {
                        Array goods = Array.CreateInstance(carriedGoodAmount.GetType(), 1);
                        goods.SetValue(carriedGoodAmount, 0);
                        addAwaitingGoods.Invoke(spawner, new object[] { gridPosition, goods });
                    }

                    emptyHands.Invoke(goodCarrier, null);
                }

                if (hasCapacity)
                    unreserveCapacity.Invoke(goodReserver, null);
                if (hasStock)
                    unreserveStock.Invoke(goodReserver, null);

                return true;
            }
            catch (TargetInvocationException ex)
            {
                CompatRuntime.LogExceptionOnce(
                    "cargo-release-target",
                    "A Timberborn method rejected the compatibility cargo-release operation",
                    ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                CompatRuntime.LogExceptionOnce("cargo-release", "Compatibility cargo release failed", ex);
                return false;
            }
        }
    }

    internal static class ReflectionCompat
    {
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Keep searching.
                }
            }

            return null;
        }

        internal static bool IsType(object value, string fullName)
        {
            if (value == null)
                return false;

            Type type = value.GetType();
            while (type != null)
            {
                if (type.FullName == fullName)
                    return true;
                type = type.BaseType;
            }

            return false;
        }

        internal static object GetComponent(object componentOwner, string componentTypeName)
        {
            if (componentOwner == null)
                return null;

            Type requestedType = FindType(componentTypeName);
            if (requestedType == null)
                return null;

            Type current = componentOwner.GetType();
            while (current != null)
            {
                MethodInfo generic = current
                    .GetMethods(AllInstance)
                    .FirstOrDefault(m =>
                        m.Name == "GetComponent" &&
                        m.IsGenericMethodDefinition &&
                        m.GetGenericArguments().Length == 1 &&
                        m.GetParameters().Length == 0);

                if (generic != null)
                {
                    try
                    {
                        return generic.MakeGenericMethod(requestedType).Invoke(componentOwner, null);
                    }
                    catch (TargetInvocationException ex)
                    {
                        CompatRuntime.LogExceptionOnce("get-component-" + componentTypeName, "GetComponent<" + componentTypeName + "> failed", ex.InnerException ?? ex);
                        return null;
                    }
                }

                current = current.BaseType;
            }

            return null;
        }

        internal static object GetMemberValue(object instance, string propertyName, string fieldName)
        {
            object value = GetProperty(instance, propertyName);
            if (value != null)
                return value;
            return GetField(instance, fieldName);
        }

        internal static object GetProperty(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                PropertyInfo property = FindProperty(instance.GetType(), name);
                return property == null ? null : property.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        internal static object GetField(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                FieldInfo field = FindField(instance.GetType(), name);
                return field == null ? null : field.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        internal static bool TrySetField(object instance, string name, object value)
        {
            if (instance == null)
                return false;

            try
            {
                FieldInfo field = FindField(instance.GetType(), name);
                if (field == null)
                    return false;

                field.SetValue(instance, value);
                return true;
            }
            catch (Exception ex)
            {
                CompatRuntime.LogExceptionOnce("set-field-" + name, "Failed setting field " + name, ex);
                return false;
            }
        }

        internal static bool GetBool(object instance, string propertyName)
        {
            object value = GetProperty(instance, propertyName);
            return value is bool && (bool)value;
        }

        internal static int GetInt(object instance, string propertyName)
        {
            object value = GetProperty(instance, propertyName);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        internal static MethodInfo FindInstanceMethod(object instance, string name, int parameterCount)
        {
            if (instance == null)
                return null;

            Type current = instance.GetType();
            while (current != null)
            {
                MethodInfo method = current
                    .GetMethods(AllInstance)
                    .FirstOrDefault(m => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == parameterCount);
                if (method != null)
                    return method;
                current = current.BaseType;
            }

            return null;
        }

        internal static object GetCarriedGoodAmount(object goodCarrier)
        {
            // Timberborn 1.1: GoodCarrier.CarriedGood.GoodAmount
            object carriedGood = GetProperty(goodCarrier, "CarriedGood");
            if (carriedGood != null)
            {
                if (carriedGood.GetType().FullName == "Timberborn.Goods.GoodAmount")
                    return carriedGood;

                object nestedGoodAmount = GetProperty(carriedGood, "GoodAmount");
                if (nestedGoodAmount != null)
                    return nestedGoodAmount;
            }

            // Timberborn 1.0 fallback. Kept so the compatibility logic is explicit,
            // but the standalone package targets 1.1.
            return GetProperty(goodCarrier, "CarriedGoods");
        }

        internal static object GetWorkerGridPosition(object worker)
        {
            object transform = GetProperty(worker, "Transform");
            object worldPosition = GetProperty(transform, "position");
            if (worldPosition == null)
                return null;

            Type coordinateSystem = FindType("Timberborn.Navigation.NavigationCoordinateSystem");
            if (coordinateSystem == null)
                return null;

            MethodInfo worldToGrid = coordinateSystem
                .GetMethods(AllStatic)
                .FirstOrDefault(m =>
                    m.Name == "WorldToGridInt" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(worldPosition.GetType()));

            if (worldToGrid == null)
                return null;

            try
            {
                return worldToGrid.Invoke(null, new object[] { worldPosition });
            }
            catch
            {
                return null;
            }
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            Type current = type;
            while (current != null)
            {
                PropertyInfo property = current.GetProperty(name, AllInstance);
                if (property != null)
                    return property;
                current = current.BaseType;
            }

            return null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            Type current = type;
            while (current != null)
            {
                FieldInfo field = current.GetField(name, AllInstance);
                if (field != null)
                    return field;
                current = current.BaseType;
            }

            return null;
        }
    }

    internal static class CompatibilityProbe
    {
        internal static void Run(Assembly original)
        {
            CompatRuntime.Log("=== Emergency Priority 1.1 compatibility probe ===");

            CheckOriginal(original, "grantemsley.EmergencyPriority.EmergencyInterruptionService", "OnJobRegistered", "TryInterrupt");
            CheckOriginal(original, "grantemsley.EmergencyPriority.EmergencyConstructionRegistry", "Register", "Unregister");
            CheckOriginal(original, "grantemsley.EmergencyPriority.EmergencyConstructable", "SetEmergency", "OnEnterUnfinishedState", "OnExitUnfinishedState");

            ProbeType("Timberborn.BehaviorSystem.BehaviorManager", "_runningExecutor", "_runningBehavior");
            ProbeType("Timberborn.NeedBehaviorSystem.ApplyEffectExecutor", "_finishTimestamp");
            ProbeType("Timberborn.Carrying.GoodCarrier", "IsCarrying", "CarriedGood", "EmptyHands");
            ProbeType("Timberborn.InventorySystem.GoodReserver", "HasReservedCapacity", "HasReservedStock", "CapacityReservation", "UnreserveCapacity", "UnreserveStock");
            ProbeType("Timberborn.RecoveredGoodSystem.RecoveredGoodStackSpawner", "AddAwaitingGoods");
            ProbeType("Timberborn.Navigation.NavigationCoordinateSystem", "WorldToGridInt");

            Type carrierType = ReflectionCompat.FindType("Timberborn.Carrying.GoodCarrier");
            if (carrierType != null)
            {
                bool currentShape = carrierType.GetProperty("CarriedGood", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
                bool oldShape = carrierType.GetProperty("CarriedGoods", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
                CompatRuntime.Log("GoodCarrier cargo API: CarriedGood=" + currentShape + ", CarriedGoods=" + oldShape + ".");
            }

            CompatRuntime.Log("=== compatibility probe end ===");
        }

        private static void CheckOriginal(Assembly assembly, string typeName, params string[] members)
        {
            Type type = assembly.GetType(typeName, false);
            if (type == null)
            {
                CompatRuntime.Log("PROBE MISSING original type " + typeName);
                return;
            }

            foreach (string member in members)
            {
                bool present = type.GetMember(member, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Length != 0;
                CompatRuntime.Log("PROBE original " + typeName + "." + member + "=" + present);
            }
        }

        private static void ProbeType(string typeName, params string[] members)
        {
            Type type = ReflectionCompat.FindType(typeName);
            if (type == null)
            {
                CompatRuntime.Log("PROBE MISSING runtime type " + typeName);
                return;
            }

            foreach (string member in members)
            {
                bool present =
                    type.GetMember(member, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Length != 0;
                CompatRuntime.Log("PROBE runtime " + typeName + "." + member + "=" + present);
            }
        }
    }

    internal static class CompatRuntime
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Once = new HashSet<string>();
        private static string _logPath;

        internal static void Initialize(string modPath)
        {
            _logPath = Path.Combine(modPath, "EmergencyPriority_1.1_Compat.log");
            try
            {
                File.WriteAllText(_logPath, "Emergency Priority standalone Timberborn 1.1 compatibility log" + Environment.NewLine);
            }
            catch
            {
                // Logging must never prevent the mod from loading.
            }
        }

        internal static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [EmergencyPriorityV11] " + message;

            lock (Gate)
            {
                try
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
                catch
                {
                    // Ignore file logging failures.
                }
            }

            TryUnityLog(line);
        }

        internal static void LogOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!Once.Add(key))
                    return;
            }
            Log(message);
        }

        internal static void LogException(string message, Exception ex)
        {
            Log(message + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        internal static void LogExceptionOnce(string key, string message, Exception ex)
        {
            lock (Gate)
            {
                if (!Once.Add(key))
                    return;
            }
            LogException(message, ex);
        }

        private static void TryUnityLog(string line)
        {
            try
            {
                Type debugType = ReflectionCompat.FindType("UnityEngine.Debug");
                if (debugType == null)
                    return;

                MethodInfo logMethod = debugType
                    .GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m =>
                        m.Name == "Log" &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(object));

                if (logMethod != null)
                    logMethod.Invoke(null, new object[] { line });
            }
            catch
            {
                // Ignore Unity logging failures.
            }
        }
    }
}
