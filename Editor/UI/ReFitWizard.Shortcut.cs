using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        public static void OpenForAccessory(GameObject accessory)
        {
            if (accessory == null) return;
            var window = GetWindow<ReFitWizard>();
            if (window.isExecuting) return;
            window.titleContent = new GUIContent("ReFit");
            window.minSize = new Vector2(620, 560);
            window.ConfigureAccessory(accessory);
            window.Show();
        }

        internal void ConfigureAccessory(GameObject accessory)
        {
            Restart();
            var renderers = accessory.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            asset = renderers.Length == 1 ? renderers[0] : null;
            myAvatar = ReFitContextMenu.FindAvatar(accessory);
            targetAvatar = myAvatar;
            history.Push(Step.AssetLocation);
            if (myAvatar != null) current = asset != null ? Step.MyAvatarChoice : Step.AssetSelect;
            else
            {
                assetFileObject = accessory;
                current = asset != null ? Step.TargetForFile : Step.AssetFileInput;
            }
            Render();
        }
    }
}
