using System;
using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Starts the solver's container (or a <see cref="FluidTank"/>'s interior, when one is set)
    /// filled with horizontal layers of liquid, bottom first, each a given thickness of one
    /// <see cref="FluidMixing"/> liquid.
    /// </summary>
    [RequireComponent(typeof(FluidSolver))]
    public class LayeredFill : FluidInitialFill
    {
        [Serializable]
        public struct Layer
        {
            [Range(0, FluidMixing.MaxLiquids - 1)] public int liquid;
            [Min(0f)] public float thickness;
        }

        [SerializeField] Layer[] layers = { new Layer { liquid = 0, thickness = 0.2f } };
        [SerializeField] FluidTank tank;

        public void Configure(Layer[] newLayers, FluidTank fillTank = null)
        {
            layers = newLayers;
            tank = fillTank;
        }

        Bounds Region => tank != null ? tank.Interior : GetComponent<FluidSolver>().Container;

        public override void Generate(float spacing, List<Vector3> positions, List<Vector3> velocities)
        {
            Bounds box = Region;
            float half = spacing * 0.5f;
            float y = box.min.y + half;
            foreach (Layer layer in layers)
            {
                float top = y - half + layer.thickness;
                for (; y < top; y += spacing)
                for (float z = box.min.z + half; z < box.max.z; z += spacing)
                for (float x = box.min.x + half; x < box.max.x; x += spacing)
                    positions.Add(new Vector3(x, y, z));
            }
        }

        public override void AssignLiquids(List<Vector3> positions, List<int> liquids)
        {
            Bounds box = Region;
            foreach (Vector3 p in positions)
            {
                float height = p.y - box.min.y;
                int liquid = layers.Length > 0 ? layers[layers.Length - 1].liquid : 0;
                foreach (Layer layer in layers)
                {
                    if (height < layer.thickness)
                    {
                        liquid = layer.liquid;
                        break;
                    }
                    height -= layer.thickness;
                }
                liquids.Add(liquid);
            }
        }
    }
}
