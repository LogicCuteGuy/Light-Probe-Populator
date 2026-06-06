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
    enum ExcludeMode { Below, Above }

    [System.Serializable]
    class ExcludeEntry
    {
        public GameObject target;
        public ExcludeMode mode = ExcludeMode.Below;
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
            "Below = exclude objects UNDER the target.\n" +
            "Above = exclude objects ON TOP of the target.",
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
            "Probes inside colliders/terrain are moved to floor height.",
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

                // Check if object overlaps target on X/Z axis
                bool overlapXZ = b.min.x < rb.max.x && b.max.x > rb.min.x &&
                                 b.min.z < rb.max.z && b.max.z > rb.min.z;

                if (!overlapXZ) continue;

                if (entry.mode == ExcludeMode.Below && b.max.y < rb.min.y)
                    { excluded = true; break; }

                if (entry.mode == ExcludeMode.Above && b.min.y > rb.max.y)
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
        int repositioned = 0;
        float surfaceOffset = 0.1f; // small offset above detected surface

        // Push probes upward until they are above all colliders/terrain
        for (int i = 0; i < probeLocations.Count; i++)
        {
            Vector3 pos = probeLocations[i];
            bool inside = true;
            int safety = 0;

            // Keep moving probe up until it's clear of all colliders and terrain
            while (inside && safety < 100)
            {
                inside = false;

                // Check non-trigger colliders
                if (Physics.CheckSphere(pos, probeRadius, ~0, QueryTriggerInteraction.Ignore))
                {
                    pos += Vector3.up * surfaceOffset;
                    inside = true;
                }

                // Check terrain — raycast up, if terrain is above the probe, push up
                if (!inside)
                {
                    foreach (var terrain in terrains)
                    {
                        TerrainCollider tc = terrain.GetComponent<TerrainCollider>();
                        if (tc == null) continue;

                        if (Physics.Raycast(pos, Vector3.up, out RaycastHit tHit, 1000f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            if (tHit.collider == tc)
                            {
                                pos = tHit.point + Vector3.up * surfaceOffset;
                                inside = true;
                                break;
                            }
                        }
                    }
                }
                safety++;
            }

            Vector3 original = probeLocations[i];
            probeLocations[i] = pos;
            if (pos.y > original.y) repositioned++;
        }

        Debug.Log($"[Light Probe Populator] Repositioned {repositioned} probes upward above colliders/terrain");

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
