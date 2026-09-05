using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// Optional reflection bridge to MCB. ReFit intentionally has no package dependency on MCB.
    /// </summary>
    internal static class MCBIntegrationService
    {
        internal sealed class RefittedAsset
        {
            public UnityEngine.Object owner;
            public string rendererPath;
            public SkinnedMeshRenderer renderer;

            public string DisplayName
            {
                get
                {
                    if (renderer == null) return rendererPath ?? "Re-fitted asset";
                    var root = renderer.transform.root;
                    return root != null ? $"{renderer.name}  ({root.name})" : renderer.name;
                }
            }
        }

        private const string EnabledPreference = "Orbiters.ReFit.MCBIntegration.Enabled";
        private const string BridgeTypeName = "MCBReFitIntegration";
        private const string RegisterMethodName = "RegisterStandaloneRefit";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPreference, true);
            set => EditorPrefs.SetBool(EnabledPreference, value);
        }

        public static bool IsAvailable => FindRegisterMethod() != null;

        public static IReadOnlyList<RefittedAsset> GetRefittedAssets()
        {
            var assets = new List<RefittedAsset>();
            var restoreMethod = FindRestoreMethod();
            if (restoreMethod == null) return assets;

            var parameters = restoreMethod.GetParameters();
            if (parameters.Length != 2) return assets;

            var seenRendererIds = new HashSet<int>();
            foreach (var owner in Resources.FindObjectsOfTypeAll(parameters[0].ParameterType))
            {
                var ownerComponent = owner as Component;
                if (ownerComponent == null || !ownerComponent.gameObject.scene.IsValid() ||
                    !ownerComponent.gameObject.scene.isLoaded) continue;

                if (!(GetMemberValue(owner, "appliedRefits") is IEnumerable entries)) continue;
                foreach (var entry in entries)
                {
                    string rendererPath = GetMemberValue(entry, "rendererPath") as string;
                    var refitMesh = GetMemberValue(entry, "refitMesh") as Mesh;
                    if (string.IsNullOrEmpty(rendererPath) || refitMesh == null) continue;

                    var rendererTransform = ownerComponent.transform.root.Find(rendererPath);
                    var renderer = rendererTransform != null
                        ? rendererTransform.GetComponent<SkinnedMeshRenderer>()
                        : null;
                    if (renderer == null || renderer.sharedMesh != refitMesh ||
                        !seenRendererIds.Add(renderer.GetInstanceID())) continue;

                    assets.Add(new RefittedAsset
                    {
                        owner = owner,
                        rendererPath = rendererPath,
                        renderer = renderer
                    });
                }
            }

            return assets;
        }

        public static bool TryResetRefittedAsset(RefittedAsset asset)
        {
            if (asset == null || asset.owner == null || string.IsNullOrEmpty(asset.rendererPath)) return false;
            var method = FindRestoreMethod();
            if (method == null) return false;

            try
            {
                method.Invoke(null, new object[] { asset.owner, asset.rendererPath });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogWarning($"[ReFit] MCB integration could not reset the asset: {(ex.InnerException ?? ex).Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReFit] MCB integration could not reset the asset: {ex.Message}");
                return false;
            }
        }

        public static bool TryRegisterStandaloneRefit(ReFitRequest request, ReFitResult result)
        {
            if (!Enabled || request == null || result == null || !result.success || result.sceneRenderer == null)
                return false;

            var method = FindRegisterMethod();
            if (method == null) return false;

            try
            {
                var registered = method.Invoke(null, new object[]
                {
                    request.targetAvatar,
                    result.sceneRenderer,
                    result.originalRendererState,
                    result.mesh,
                    result.meshAssetPath,
                    result.secondarySourceShapeNames,
                    result.secondaryShapeNames
                });
                return registered is bool success && success;
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogWarning($"[ReFit] MCB integration could not register the result: {(ex.InnerException ?? ex).Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReFit] MCB integration could not register the result: {ex.Message}");
                return false;
            }
        }

        private static MethodInfo FindRegisterMethod()
        {
            var type = FindBridgeType();
            return type?.GetMethod(RegisterMethodName, BindingFlags.Public | BindingFlags.Static);
        }

        private static MethodInfo FindRestoreMethod()
        {
            var type = FindBridgeType();
            if (type == null) return null;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "RestoreAsset" && method.GetParameters().Length == 2) return method;
            }

            return null;
        }

        private static Type FindBridgeType() => FindType(BridgeTypeName);

        internal static string TryGetWizardToken()
        {
            var authType = FindType("AuthenticationService");
            var method = authType?.GetMethod("GetAuth", BindingFlags.Public | BindingFlags.Static);
            object auth = null;
            try { auth = method?.Invoke(null, null); }
            catch { }
            if (auth == null) return null;
            var tokenField = auth.GetType().GetField("token", BindingFlags.Public | BindingFlags.Instance);
            return tokenField?.GetValue(auth) as string;
        }

        internal static string TryInvokeStaticString(string typeName, string methodName, params object[] arguments)
        {
            var type = FindType(typeName);
            if (type == null) return null;
            try
            {
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                return method?.Invoke(null, arguments) as string;
            }
            catch { return null; }
        }

        internal static bool? TryGetStaticBool(string typeName, string propertyName)
        {
            var type = FindType(typeName);
            if (type == null) return null;
            try
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                return property != null ? (bool?)property.GetValue(null) : null;
            }
            catch { return null; }
        }

        internal static void TrySetStaticBool(string typeName, string propertyName, bool value)
        {
            var type = FindType(typeName);
            if (type == null) return;
            try
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property != null && property.CanWrite) property.SetValue(null, value);
            }
            catch { }
        }

        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

        internal static Type FindType(string name)
        {
            if (Types.TryGetValue(name, out var cached)) return cached;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(name); }
                catch { }
                if (type != null) { Types[name] = type; return type; }
            }
            return null;
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            var type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property != null && property.CanRead ? property.GetValue(target) : null;
        }
    }
}
