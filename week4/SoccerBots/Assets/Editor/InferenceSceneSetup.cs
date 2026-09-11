using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Creates a clean, single-field scene for recording the trained 3v3 policies.
// The source Soccer scene stays a multi-field training scene.
public static class InferenceSceneSetup
{
    const string SourceScene = "Assets/Scenes/Soccer.unity";
    const string OutputScene = "Assets/Scenes/Inference3v3.unity";

    [MenuItem("PCUBE/Week 4/Create Inference 3v3 Scene")]
    public static void Create()
    {
        EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

        var fields = Object.FindObjectsByType<SoccerEnvController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (fields.Length == 0)
        {
            Debug.LogError("No SoccerEnvController was found in Soccer.unity.");
            return;
        }

        // The field nearest the world origin is the presentation field. Disable every other
        // copy so the scene runs one readable 3v3 match instead of the parallel training grid.
        var presentationField = fields
            .OrderBy(field => field.transform.position.sqrMagnitude)
            .First();
        foreach (var field in fields)
            field.gameObject.SetActive(field == presentationField);

        var camera = Camera.main;
        if (camera == null)
        {
            Debug.LogError("The source scene has no Main Camera.");
            return;
        }

        var target = presentationField.transform.position;
        camera.transform.SetPositionAndRotation(
            target + new Vector3(0f, 25f, -27f),
            Quaternion.LookRotation(target - (target + new Vector3(0f, 25f, -27f))));
        camera.fieldOfView = 48f;

        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), OutputScene);
        AssetDatabase.Refresh();
        Debug.Log($"Inference scene created: {OutputScene}");
    }
}
