using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        public static void OpenForAccessory(GameObject accessory)
        {
            if (accessory == null) return;
            var window = Summon();
            if (window.isExecuting) return;
            window.ConfigureAccessory(accessory);
            window.Show();
        }

        /// <summary>Starts over with this accessory and the avatar it is worn on; several meshes are left to pick.</summary>
        internal void ConfigureAccessory(GameObject accessory)
        {
            Restart();
            UseSelection(accessory);
            Render();
        }
    }
}
