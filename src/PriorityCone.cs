using System;
using System.IO;
using System.IO.Compression;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;
using UnityEngine.Rendering;

namespace PrioritySplitters;

// The S cone on a splitter and the M cone on a merger are baked into the body meshes, which ship non-readable,
// so they cannot be separated in the running game. tools/extract_cone.py splits each mesh offline into a
// cone-less body and the cone. Each machine view gets the body swapped and the cone re-added as its own child
// transform, so showing the priority is a plain rotation of that child. Round-robin machines stand upright,
// exactly as vanilla.
internal static class PriorityCone
{
    private const string ConeName = "Priority cone";

    // One stock body mesh and its split: the splitter's and the merger's.
    private sealed class Model
    {
        private readonly string resource;
        private readonly string meshName;
        private readonly string letter;
        private bool loaded;
        private bool unavailable;
        private bool hierarchyLogged;
        private int sourceVertexCount;
        private Vector3 pivot;
        private float baseRadius;
        private Mesh bodyMesh;
        private Mesh coneMesh;
        private int[] coneMaterials;

        public Model(string resource, string meshName, string letter)
        {
            this.resource = resource;
            this.meshName = meshName;
            this.letter = letter;
        }

        public void Apply(Machine machine, Transform view)
        {
            if (unavailable || !Load())
                return;
            var body = FindBody(view);
            if (body == null)
            {
                LogHierarchy(view);
                return;
            }
            var cone = body.Find(ConeName);
            if (cone == null)
            {
                cone = Split(body);
                if (cone == null)
                    return;
            }

            var degrees = Lean(machine, body, out var sign);
            // Unity rotates a positive angle about +z from +y toward -x, so a lean toward +x is a negative angle.
            cone.localRotation = Quaternion.AngleAxis(-sign * degrees, Vector3.forward);
            // Tipping about the base centre would lift the far edge of the base off the shell; sinking the cone
            // by that much keeps it seated, with the near edge disappearing into the shell instead.
            cone.localPosition = pivot - Vector3.up * (baseRadius * Mathf.Sin(degrees * Mathf.Deg2Rad));
        }

        // The renderer holding the stock body mesh, or the cone-less copy once swapped. The splitter prefab keeps
        // it on a child named after the mesh, the merger prefab on the "Model" object itself, so it is found by
        // mesh name rather than by path.
        private Transform FindBody(Transform view)
        {
            var filters = view.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; filters is not null && i < filters.Length; i++)
            {
                var mesh = filters[i].sharedMesh;
                if (mesh != null && (mesh.name == meshName || (bodyMesh != null && mesh.name == bodyMesh.name)))
                    return filters[i].transform;
            }
            return null;
        }

        // Swaps the body for the cone-less copy and adds the cone as a child, sharing the body's materials and
        // render settings. Returns null for bodies that are not the stock model, such as raised machines.
        private Transform Split(Transform body)
        {
            var filter = body.GetComponent<MeshFilter>();
            var renderer = body.GetComponent<MeshRenderer>();
            if (filter == null || renderer == null || !StockBody(filter))
                return null;

            filter.sharedMesh = bodyMesh;

            var cone = new GameObject(ConeName);
            cone.layer = body.gameObject.layer;
            var transform = cone.transform;
            transform.SetParent(body, false);
            transform.localPosition = pivot;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            cone.AddComponent<MeshFilter>().sharedMesh = coneMesh;

            var coneRenderer = cone.AddComponent<MeshRenderer>();
            var sources = renderer.sharedMaterials;
            var materials = new Il2CppReferenceArray<Material>(coneMaterials.Length);
            for (var i = 0; i < coneMaterials.Length; i++)
                materials[i] = coneMaterials[i] < sources.Length ? sources[coneMaterials[i]] : null;
            coneRenderer.sharedMaterials = materials;
            coneRenderer.shadowCastingMode = renderer.shadowCastingMode;
            coneRenderer.receiveShadows = renderer.receiveShadows;
            coneRenderer.lightProbeUsage = renderer.lightProbeUsage;
            coneRenderer.reflectionProbeUsage = renderer.reflectionProbeUsage;
            coneRenderer.renderingLayerMask = renderer.renderingLayerMask;
            return transform;
        }

        // True when the filter holds the stock body the bundled split was made from.
        private bool StockBody(MeshFilter filter)
        {
            var current = filter.sharedMesh;
            if (current == null || current.name != meshName)
                return false;
            if (current.vertexCount != sourceVertexCount)
            {
                Plugin.Logger.LogWarning($"{meshName} has {current.vertexCount} vertices but the bundled split expects {sourceVertexCount}; the {letter} will not show priorities until the mod is updated for this game version");
                unavailable = true;
                return false;
            }
            return true;
        }

        // Raised machines use their own body mesh (splitter_raised, merger_raised) with no split, so they are
        // skipped quietly; any other layout is logged once so a game update shows up in the log.
        private void LogHierarchy(Transform view)
        {
            if (hierarchyLogged)
                return;
            var meshes = "";
            var filters = view.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; filters is not null && i < filters.Length; i++)
            {
                var mesh = filters[i].sharedMesh;
                if (mesh != null && mesh.name.EndsWith("_raised"))
                    return;
                meshes += $"{filters[i].name}: {(mesh != null ? mesh.name : "none")}; ";
            }
            hierarchyLogged = true;
            Plugin.Logger.LogWarning($"No mesh named '{meshName}' under the {letter} machine view '{view.name}'; meshes found: {meshes}");
        }

        private bool Load()
        {
            if (loaded)
                return bodyMesh != null && coneMesh != null;
            loaded = true;
            try
            {
                using var stream = typeof(PriorityCone).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidDataException($"embedded resource {resource} is missing");
                using var inflated = new MemoryStream();
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                    deflate.CopyTo(inflated);
                inflated.Position = 0;
                var reader = new BinaryReader(inflated);

                var magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != (byte)'P' || magic[1] != (byte)'S' || magic[2] != (byte)'C' || magic[3] != (byte)'1')
                    throw new InvalidDataException("unexpected header");
                sourceVertexCount = reader.ReadInt32();
                var tangents = (reader.ReadInt32() & 1) != 0;
                pivot = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                baseRadius = reader.ReadSingle();

                bodyMesh = ReadMesh(reader, meshName + " (no cone)", tangents, out _);
                coneMesh = ReadMesh(reader, letter + " cone", tangents, out coneMaterials);
                Plugin.Logger.LogInfo($"{letter} cone split loaded: body {bodyMesh.vertexCount} vertices, cone {coneMesh.vertexCount} vertices, pivot {pivot}");
                return true;
            }
            catch (Exception e)
            {
                unavailable = true;
                Plugin.Logger.LogWarning($"Could not load the {letter} cone split, the {letter} will not show priorities: {e}");
                return false;
            }
        }
    }

    private static readonly Model splitter = new("splitter-cone.bin", "SM_splitter.001", "S");
    private static readonly Model merger = new("merger-cone.bin", "SM_merger.001", "M");
    private static MoonDepotConstruction construction;

    public static void Reset() => construction = null;

    // Brings every tracked machine's cone in line with its priority. Cheap enough to run a couple of times a
    // second, which also catches views that spawn as the player walks around.
    public static void Sync()
    {
        if (Plugin.LeanDegrees.Value == 0f)
            return;
        if (construction == null)
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonDepotConstruction>(), false);
            if (found is null || found.Length == 0)
                return;
            construction = found[0].TryCast<MoonDepotConstruction>();
            if (construction == null)
                return;
        }
        foreach (var machine in MachineLayout.All)
        {
            var view = construction.ViewOf(machine.Id);
            if (view != null)
                (machine is Merger ? merger : splitter).Apply(machine, view.transform);
        }
    }

    // Degrees to lean and which way: sign +1 leans toward the body's +x side. 0 when the machine is
    // round-robin, the priority port is straight ahead, or the body is not oriented as expected.
    private static float Lean(Machine machine, Transform body, out float sign)
    {
        sign = 0f;
        var priority = machine.Priority;
        if (priority is null || Mathf.Abs(priority.Lateral) < 0.05f)
            return 0f;
        var worldRight = machine.Rotation * Vector3.Cross(Vector3.up, machine.Flow);
        var localRight = body.InverseTransformDirection(worldRight);
        if (Mathf.Abs(localRight.x) < 0.5f)
            return 0f;
        sign = (priority.Lateral > 0f) == (localRight.x > 0f) ? 1f : -1f;
        return Plugin.LeanDegrees.Value;
    }

    private static Mesh ReadMesh(BinaryReader reader, string name, bool tangents, out int[] materials)
    {
        var vertexCount = reader.ReadInt32();
        var subMeshCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        materials = new int[subMeshCount];
        var starts = new int[subMeshCount];
        var counts = new int[subMeshCount];
        for (var i = 0; i < subMeshCount; i++)
        {
            materials[i] = reader.ReadInt32();
            starts[i] = reader.ReadInt32();
            counts[i] = reader.ReadInt32();
        }

        var vertices = new Il2CppStructArray<Vector3>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var normals = new Il2CppStructArray<Vector3>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            float x = reader.ReadSByte(), y = reader.ReadSByte(), z = reader.ReadSByte();
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length < 1f)
                length = 1f;
            normals[i] = new Vector3(x / length, y / length, z / length);
        }
        var uv = new Il2CppStructArray<Vector2>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
            uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        Il2CppStructArray<Vector4> tangentArray = null;
        if (tangents)
        {
            tangentArray = new Il2CppStructArray<Vector4>(vertexCount);
            for (var i = 0; i < vertexCount; i++)
                tangentArray[i] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        var indices = new int[indexCount];
        for (var i = 0; i < indexCount; i++)
            indices[i] = reader.ReadInt32();

        var mesh = new Mesh();
        mesh.name = name;
        mesh.hideFlags = HideFlags.HideAndDontSave;
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uv;
        if (tangentArray is not null)
            mesh.tangents = tangentArray;
        mesh.subMeshCount = subMeshCount;
        for (var i = 0; i < subMeshCount; i++)
        {
            var triangles = new Il2CppStructArray<int>(counts[i]);
            for (var j = 0; j < counts[i]; j++)
                triangles[j] = indices[starts[i] + j];
            mesh.SetTriangles(triangles, i, false, 0);
        }
        mesh.RecalculateBounds();
        return mesh;
    }
}
