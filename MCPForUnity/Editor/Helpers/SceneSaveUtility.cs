using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Helpers
{
    internal static class SceneSaveUtility
    {
        public static bool SaveDirtyOpenScenes(string logPrefix, string unsavedSceneMessage)
        {
            bool allSavesSucceeded = true;
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty)
                    continue;

                if (string.IsNullOrEmpty(scene.path))
                {
                    McpLog.Warn($"{logPrefix} Skipping unsaved scene '{scene.name}': {unsavedSceneMessage}");
                    continue;
                }

                try
                {
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        allSavesSucceeded = false;
                        McpLog.Warn($"{logPrefix} Failed to save dirty scene '{scene.name}': Unity declined the save.");
                    }
                }
                catch (Exception ex)
                {
                    allSavesSucceeded = false;
                    McpLog.Warn($"{logPrefix} Failed to save dirty scene '{scene.name}': {ex.Message}");
                }
            }

            return allSavesSucceeded;
        }
    }
}
