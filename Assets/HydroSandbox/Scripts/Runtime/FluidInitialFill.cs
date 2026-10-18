using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Replaces a <see cref="FluidSolver"/>'s default block of initial particles (on the same
    /// GameObject) with scene-specific positions, generated at every reset.
    /// </summary>
    public abstract class FluidInitialFill : MonoBehaviour
    {
        /// <summary>Appends initial particle positions (and optionally velocities) on a lattice of the given spacing.</summary>
        public abstract void Generate(float spacing, List<Vector3> positions, List<Vector3> velocities);

        /// <summary>Optionally appends the liquid index (see <see cref="FluidMixing"/>) of each generated position; unassigned particles are liquid 0.</summary>
        public virtual void AssignLiquids(List<Vector3> positions, List<int> liquids) { }
    }
}
