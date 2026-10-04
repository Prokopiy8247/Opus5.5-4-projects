using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PG.EditorTools
{
    /// <summary>
    /// Windows release build of the single startup scene.
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod PG.EditorTools.PGPlayerBuild.BuildWindows
    /// </summary>
    public static class PGPlayerBuild
    {
        const string OutDir  = "Build/Windows";
        const string ExeName = "SableReach.exe";

        [MenuItem("Tools/PG/Build Windows Player (Release)", priority = 40)]
        public static void BuildWindows()
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), OutDir);
            Directory.CreateDirectory(outDir);

            // Exactly one startup scene, always the generated game scene.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(PGBootstrap.ScenePath, true) };

            PlayerSettings.productName = "Sable Reach";
            PlayerSettings.companyName = "PG";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { PGBootstrap.ScenePath },
                locationPathName = Path.Combine(outDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            string msg = "[PG] PLAYER BUILD " + s.result + "\n    output   : " + s.outputPath +
                         "\n    size     : " + (s.totalSize / (1024 * 1024)) + " MB" +
                         "\n    duration : " + s.totalTime +
                         "\n    errors   : " + s.totalErrors + "\n    warnings : " + s.totalWarnings;
            if (s.result != BuildResult.Succeeded)
            {
                Debug.LogError(msg);
                throw new Exception("Player build failed: " + s.result);
            }
            Debug.Log(msg);
            File.WriteAllText(Path.Combine(outDir, "HOW_TO_RUN.txt"),
                "Sable Reach - seamless planetary exploration (Windows x64)\r\n\r\n" +
                "Run " + ExeName + ". The mouse is captured; Esc releases it, a click captures it again.\r\n" +
                "Press F1 in game for the full key list. See README_RUN.md in the project folder.\r\n");
        }
    }
}
