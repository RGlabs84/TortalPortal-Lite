using System;
using System.IO;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// The one atomic-write helper every ops output-surface engine in this wave shares (#67's command
    /// response file, #74's JSON/CSV export, #77's SVG map, #78's snapshots, #65's first-seen timestamp
    /// side file) - write to "&lt;path&gt;.tmp", then swap it into place, so a reader (an external script, a
    /// tail -f, an HTTP GET landing mid-write) never observes a half-written document. Ports the pattern
    /// the catalog cites verbatim from this mod family's own prior art: TortalPortal/PortalExport.Write
    /// (PortalExport.cs:75) and Wonderland's SupplySwitch.Save (SupplySwitch.cs:100-165), both of which
    /// use write-temp-then-File.Replace/Move rather than writing the live path directly.
    /// </summary>
    public static class OpsOutputAtomicFile
    {
        public static bool TryWriteAllText(string path, string content, out string? error)
        {
            error = null;
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir!);
                }

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content);

                if (File.Exists(path))
                {
                    // File.Replace is the true atomic swap on the same volume; File.Move is the fallback
                    // for the (rare, first-write) case where the destination doesn't exist yet.
                    try
                    {
                        File.Replace(tmp, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                PortalDebug.LogWarning($"[OpsOutputAtomicFile] atomic write to '{path}' failed: {error}");
                return false;
            }
        }
    }
}
