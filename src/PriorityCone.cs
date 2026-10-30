using System;
using System.Collections.Generic;
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
    private const string ConePrefix = "Priority cone";

    // One stock body mesh and its split. Each kind has two: the model standing on its legs, and the one the game
    // shows instead while the machine hangs on a raised belt (MoonRaisedMachine.SetModel), the same body without
    // the legs, feet and braces. Both carry the cone at the same spot.
    private sealed class Model
    {
        private readonly string resource;
        private readonly string meshName;
        private readonly string letter;
        private readonly string coneName;
        private bool loaded;
        private bool unavailable;
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
            coneName = $"{ConePrefix} ({meshName})";
        }

        public string MeshName => meshName;
        public string Letter => letter;

        // True when the filter shows this model: the stock mesh, or the cone-less copy once swapped in.
        public bool Shows(Mesh mesh) =>
            mesh != null && (mesh.name == meshName || (bodyMesh != null && mesh.name == bodyMesh.name));

        public void Apply(Machine machine, Transform body)
        {
            if (unavailable || !Load())
                return;
            var filter = body.GetComponent<MeshFilter>();
            if (filter == null)
                return;
            var cone = body.Find(coneName);
            if (cone == null)
            {
                cone = Split(body, filter);
                if (cone == null)
                    return;
            }
            else if (filter.sharedMesh != null && filter.sharedMesh.name == meshName)
            {
                // The game put its own mesh back, baked cone included: SetModel runs again when the belts around a
                // raised machine change. The cone child is still there, so only the body needs swapping again.
                if (!StockBody(filter))
                    return;
                filter.sharedMesh = bodyMesh;
            }
            DropOtherCones(body);

            var degrees = Lean(machine, body, out var sign);
            // Unity rotates a positive angle about +z from +y toward -x, so a lean toward +x is a negative angle.
            cone.localRotation = Quaternion.AngleAxis(-sign * degrees, Vector3.forward);
            // Tipping about the base centre would lift the far edge of the base off the shell; sinking the cone
            // by that much keeps it seated, with the near edge disappearing into the shell instead.
            cone.localPosition = pivot - Vector3.up * (baseRadius * Mathf.Sin(degrees * Mathf.Deg2Rad));
        }

        // A machine that changes between standing and raised shows the other model's mesh in the same filter;
        // the cone cut for the previous model goes with it.
        private void DropOtherCones(Transform body)
        {
            for (var i = body.childCount - 1; i >= 0; i--)
            {
                var child = body.GetChild(i);
                var name = child.name;
                if (name != coneName && name.StartsWith(ConePrefix, StringComparison.Ordinal))
                    UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        // Swaps the body for the cone-less copy and adds the cone as a child, sharing the body's materials and
        // render settings. Returns null for bodies that are not the stock model.
        private Transform Split(Transform body, MeshFilter filter)
        {
            var renderer = body.GetComponent<MeshRenderer>();
            if (renderer == null || !StockBody(filter))
                return null;

            filter.sharedMesh = bodyMesh;

            var cone = new GameObject(coneName);
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
                Plugin.Logger.LogWarning($"{meshName} has {current.vertexCount} vertices but the bundled split expects {sourceVertexCount}; the {letter} will not show priorities on this model until the mod is updated for this game version");
                unavailable = true;
                return false;
            }
            return true;
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
                coneMesh = ReadMesh(reader, $"{letter} cone ({meshName})", tangents, out coneMaterials);
                Plugin.Logger.LogInfo($"{letter} cone split loaded for {meshName}: body {bodyMesh.vertexCount} vertices, cone {coneMesh.vertexCount} vertices, pivot {pivot}");
                return true;
            }
            catch (Exception e)
            {
                unavailable = true;
                Plugin.Logger.LogWarning($"Could not load the {letter} cone split for {meshName}, the {letter} will not show priorities on that model: {e}");
                return false;
            }
        }
    }

    private static readonly Model[] splitterModels =
    {
        new("splitter-cone.bin", "SM_splitter.001", "S"),
        new("splitter-raised-cone.bin", "splitter_raised", "S"),
    };
    private static readonly Model[] mergerModels =
    {
        new("merger-cone.bin", "SM_merger.001", "M"),
        new("merger-raised-cone.bin", "merger_raised", "M"),
    };
    private static readonly HashSet<string> layoutsLogged = new();
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
                Apply(machine is Merger ? mergerModels : splitterModels, machine, view.transform);
        }
    }

    // The body is found by the mesh it shows, never by path: the splitter prefab keeps it on a child named after
    // the mesh, the merger prefab on the "Model" object itself, and a raised machine shows the raised mesh in
    // whichever filter the standing one used.
    private static void Apply(Model[] models, Machine machine, Transform view)
    {
        var filters = view.GetComponentsInChildren<MeshFilter>(true);
        for (var i = 0; filters is not null && i < filters.Length; i++)
        {
            var mesh = filters[i].sharedMesh;
            foreach (var model in models)
            {
                if (model.Shows(mesh))
                {
                    model.Apply(machine, filters[i].transform);
                    return;
                }
            }
        }
        LogLayout(models, view, filters);
    }

    // Any other layout is logged once per kind, so a game update that renames the meshes shows up in the log.
    private static void LogLayout(Model[] models, Transform view, MeshFilter[] filters)
    {
        if (!layoutsLogged.Add(models[0].Letter))
            return;
        var meshes = "";
        for (var i = 0; filters is not null && i < filters.Length; i++)
        {
            var mesh = filters[i].sharedMesh;
            meshes += $"{filters[i].name}: {(mesh != null ? mesh.name : "none")}; ";
        }
        var expected = new List<string>();
        foreach (var model in models)
            expected.Add($"'{model.MeshName}'");
        Plugin.Logger.LogWarning($"No mesh named {string.Join(" or ", expected)} under the {models[0].Letter} machine view '{view.name}'; meshes found: {meshes}");
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
