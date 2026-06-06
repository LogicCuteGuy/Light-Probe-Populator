using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class GenerateLightProbes : EditorWindow
{
    enum Resolution { Low, Medium, High, VeryHigh }

    [MenuItem("Tools/Light Probe Populator")]
    static void OpenWindow()
    {
        var window = GetWindow<GenerateLightProbes>("Light Probe Populator");
        window.minSize = new Vector2(320, 200);
        window.Show();
    }

    Resolution resolution = Resolution.Medium;
    string groupName = "Light Probe Group";
    bool deleteExisting = true;
    float offset = 0.5f;
    float probeRadius = 0.2f;

    // Reference-based exclusion
    enum ExcludeMode { Lower, Upper }

    [System.Serializable]
    class ExcludeEntry
    {
        public GameObject target;
        public ExcludeMode mode = ExcludeMode.Lower;
    }

    List<ExcludeEntry> excludeEntries = new List<ExcludeEntry>();

    void OnGUI()
    {
        GUILayout.Label("Light Probe Populator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        groupName = EditorGUILayout.TextField("Group Name", groupName);
        deleteExisting = EditorGUILayout.Toggle("Delete Existing Group", deleteExisting);
        resolution = (Resolution)EditorGUILayout.EnumPopup("Resolution", resolution);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Reference Exclusions", EditorStyles.boldLabel);

        for (int i = 0; i < excludeEntries.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            excludeEntries[i].target = (GameObject)EditorGUILayout.ObjectField(excludeEntries[i].target, typeof(GameObject), true);
            excludeEntries[i].mode = (ExcludeMode)EditorGUILayout.EnumPopup(excludeEntries[i].mode, GUILayout.Width(70));
            if (GUILayout.Button("X", GUILayout.Width(22)))
            {
                excludeEntries.RemoveAt(i);
                i--;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("+ Add Exclusion"))
            excludeEntries.Add(new ExcludeEntry());

        EditorGUILayout.HelpBox(
            "Lower = exclude probes inside X/Z footprint that are BELOW the object.\n" +
            "Upper = exclude probes inside X/Z footprint that are ABOVE the object.",
            MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Probe Settings", EditorStyles.boldLabel);
        offset = EditorGUILayout.FloatField("Offset from Bounds", offset);
        probeRadius = EditorGUILayout.FloatField("Collider Check Radius", probeRadius);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Low      – bounds.max only\n" +
            "Medium   – bounds.max + bounds.min\n" +
            "High     – bounds + 2× lerped random probes\n" +
            "Very High – bounds + 4× lerped random probes\n\n" +
            "Offset pushes probes outward from bounds.\n" +
            "Probes inside colliders are removed automatically.",
            MessageType.Info);

        EditorGUILayout.Space();

        if (GUILayout.Button("Generate", GUILayout.Height(30)))
            Generate();
    }

    static void Generate(Resolution resolution, string groupName, bool deleteExisting,
        float offset, float probeRadius,
        List<ExcludeEntry> excludeEntries)
    {
        var probeLocations = new List<Vector3>();

        // Remove existing group if requested
        if (deleteExisting)
        {
            var existing = GameObject.Find(groupName);
            if (existing != null)
                DestroyImmediate(existing);
        }

        // Collect probes from static renderers
        foreach (var obj in UnityEngine.Object.FindObjectsOfType<GameObject>())
        {
            if (!obj.isStatic) continue;

            var renderer = obj.GetComponent<Renderer>();
            if (renderer == null) continue;

            Bounds b = renderer.bounds;

            // Reference-based exclusions: iterate the list
            bool excluded = false;
            foreach (var entry in excludeEntries)
            {
                if (entry.target == null) continue;
                Bounds rb = entry.target.GetComponent<Renderer>() != null
                    ? entry.target.GetComponent<Renderer>().bounds
                    : new Bounds(entry.target.transform.position, Vector3.one);

                bool insideXZ = b.center.x >= rb.min.x && b.center.x <= rb.max.x &&
                                b.center.z >= rb.min.z && b.center.z <= rb.max.z;

                if (!insideXZ) continue;

                if (entry.mode == ExcludeMode.Lower && b.max.y < rb.max.y)
                    { excluded = true; break; }

                if (entry.mode == ExcludeMode.Upper && b.min.y > rb.min.y)
                    { excluded = true; break; }
            }
            if (excluded) continue;

            Vector3 center = b.center;
            Vector3 extents = b.extents + Vector3.one * offset;

            // Add offset-pushed corners: max and min along all axes
            probeLocations.Add(center + new Vector3( extents.x,  extents.y,  extents.z));
            probeLocations.Add(center + new Vector3(-extents.x, -extents.y, -extents.z));

            if (resolution != Resolution.Low)
            {
                probeLocations.Add(center + new Vector3(-extents.x,  extents.y,  extents.z));
                probeLocations.Add(center + new Vector3( extents.x, -extents.y, -extents.z));
            }
        }

        // Add interpolated probes for high / very high
        int multiplier = 0;
        switch (resolution)
        {
            case Resolution.High:      multiplier = 2; break;
            case Resolution.VeryHigh:  multiplier = 4; break;
        }

        if (multiplier > 0)
        {
            int baseCount = probeLocations.Count;
            int extraCount = baseCount * multiplier;
            for (int i = 0; i < extraCount; i++)
            {
                probeLocations.Add(Vector3.Lerp(
                    probeLocations[Random.Range(0, baseCount)],
                    probeLocations[Random.Range(0, baseCount)],
                    0.5f));
            }
        }

        // Gather all terrains in the scene for underground detection
        var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
        int removedByCollider = 0;
        int removedByTerrain = 0;

        // Remove any probes that land inside colliders or underneath terrain
        for (int i = probeLocations.Count - 1; i >= 0; i--)
        {
            Vector3 pos = probeLocations[i];
            bool remove = false;

            // Check colliders (including triggers — catches mesh colliders too)
            if (Physics.CheckSphere(pos, probeRadius, ~0, QueryTriggerInteraction.Collide))
            {
                remove = true;
                removedByCollider++;
            }

            // Check if probe is below any terrain surface
            if (!remove)
            {
                foreach (var terrain in terrains)
                {
                    TerrainCollider tc = terrain.GetComponent<TerrainCollider>();
                    if (tc == null) continue;

                    // Raycast upward from probe — if it hits terrain, probe is underground
                    if (Physics.Raycast(pos, Vector3.up, out RaycastHit hit, 1000f))
                    {
                        if (hit.collider == tc)
                        {
                            remove = true;
                            removedByTerrain++;
                            break;
                        }
                    }
                }
            }

            if (remove)
                probeLocations.RemoveAt(i);
        }

        Debug.Log($"[Light Probe Populator] Removed {removedByCollider} probes inside colliders, {removedByTerrain} probes under terrain");

        // Create the group
        var go = new GameObject(groupName);
        var lpg = go.AddComponent<LightProbeGroup>();
        lpg.probePositions = probeLocations.ToArray();

        Debug.Log($"[Light Probe Populator] Created \"{groupName}\" with {probeLocations.Count} probes ({resolution})");
    }

    // Convenience wrapper used by the Generate button
    void Generate()
    {
        Generate(resolution, groupName, deleteExisting, offset, probeRadius,
            excludeEntries);
    }
}
