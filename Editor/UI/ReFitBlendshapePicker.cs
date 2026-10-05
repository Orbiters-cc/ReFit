using System;
using System.Collections.Generic;
namespace Orbiters.ReFit.Editor
{
    internal sealed class ReFitBlendshapePicker : Orbiters.Toolkit.Editor.BlendshapePicker
    {
        internal ReFitBlendshapePicker(IEnumerable<string> names, List<string> selection, Action changed)
            : base(names, selection, changed, ReFitBlendshapeHistory.Read()) { }
    }
}
