using System.IO;
using UnityEditor.AssetImporters;

namespace RpmSynth.Editor
{
    [ScriptedImporter(1, "egrain")]
    internal sealed class EngineGrainImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<EngineGrainProfile>();
            profile.SetRecording(File.ReadAllBytes(context.assetPath));
            _ = profile.Data; // Validate the file during import.
            context.AddObjectToAsset("profile", profile);
            context.SetMainObject(profile);
        }
    }
}
