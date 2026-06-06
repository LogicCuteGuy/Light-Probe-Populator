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

    void OnGUI()
    {
        GUILayout.Label("Light Probe Populator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        groupName = EditorGUILayout.TextField("Group Name", groupName);
        deleteExisting = EditorGUILayout.Toggle("Delete Existing Group", deleteExisting);
        resolution = (Resolution)EditorGUILayout.EnumPopup("Resolution", resolution);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Low      – bounds.max only\n" +
            "Medium   – bounds.max + bounds.min\n" +
            "High     – bounds + 2× lerped random probes\n" +
            "Very High – bounds + 4× lerped random probes",
            MessageType.Info);

        EditorGUILayout.Space();

        if (GUILayout.Button("Generate", GUILayout.Height(30)))
            Generate();
    }

    static void Generate(Resolution resolution, string groupName, bool deleteExisting)
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

            probeLocations.Add(renderer.bounds.max);

            if (resolution != Resolution.Low)
                probeLocations.Add(renderer.bounds.min);
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

        // Create the group
        var go = new GameObject(groupName);
        var lpg = go.AddComponent<LightProbeGroup>();
        lpg.probePositions = probeLocations.ToArray();

        Debug.Log($"[Light Probe Populator] Created \"{groupName}\" with {probeLocations.Count} probes ({resolution})");
    }

    // Convenience wrapper used by the Generate button
    void Generate()
    {
        Generate(resolution, groupName, deleteExisting);
    }
}
