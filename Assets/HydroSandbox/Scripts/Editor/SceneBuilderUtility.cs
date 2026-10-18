using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HydroSandbox.Editor
{
    /// <summary>Shared pieces of the scene builders: materials, world-scaled boxes, build settings.</summary>
    static class SceneBuilderUtility
    {
        const string Root = "Assets/HydroSandbox";
        const string MaterialFolder = Root + "/Materials";

        public static Material LoadOrCreateMaterial(string name, string shaderName)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder(Root, "Materials");
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(material, path);
            }
            return material;
        }

        /// <summary>
        /// A box whose UVs are in world meters divided by <paramref name="textureMeters"/>, so stone
        /// keeps a consistent scale however the box is sized (Unity's cube stretches 0..1 per face).
        /// </summary>
        public static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material material, float textureMeters)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(name, size, textureMeters);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        public static Mesh BoxMesh(string name, Vector3 size, float textureMeters)
        {
            Vector3 e = size * 0.5f;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            void Face(Vector3 normal, Vector3 u, Vector3 v, float uSize, float vSize)
            {
                int start = vertices.Count;
                Vector3 c = Vector3.Scale(normal, e);
                Vector3 du = u * (uSize * 0.5f);
                Vector3 dv = v * (vSize * 0.5f);
                vertices.Add(c - du - dv);
                vertices.Add(c + du - dv);
                vertices.Add(c + du + dv);
                vertices.Add(c - du + dv);
                for (int i = 0; i < 4; i++)
                    normals.Add(normal);
                float su = uSize / textureMeters;
                float sv = vSize / textureMeters;
                uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(su, 0));
                uvs.Add(new Vector2(su, sv));
                uvs.Add(new Vector2(0, sv));
                triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            Face(Vector3.forward, Vector3.left, Vector3.up, size.x, size.y);
            Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y);
            Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y);
            Face(Vector3.left, Vector3.back, Vector3.up, size.z, size.y);
            Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z);
            Face(Vector3.down, Vector3.right, Vector3.back, size.x, size.z);

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static void AddSceneToBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == path))
                return;
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
