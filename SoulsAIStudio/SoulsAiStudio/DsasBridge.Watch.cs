#nullable enable
using System;
using DSAnimStudio;

namespace SoulsAiStudio.App
{
    /// <summary>DSAS calls used when files change on disk (see <see cref="SasApp"/> file watch).</summary>
    public sealed partial class DsasBridge
    {
        /// <summary>The loaded character's animations (TAE) have unsaved changes in DSAS.</summary>
        public bool TaeModified
        {
            get
            {
                try { return zzz_DocumentManager.CurrentDocument?.EditorScreen?.IsModified == true; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>A character is open, so DSAS has params loaded that can be reloaded.</summary>
        public bool CanReloadParams
        {
            get
            {
                try { return zzz_DocumentManager.CurrentDocument?.EditorScreen?.IsFileOpen == true && zzz_DocumentManager.CurrentDocument.GameRoot != null; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>DSAS's own File > "Auto Reload Params When File Is Written To" is on (it reloads by itself).</summary>
        public bool DsasAutoReloadsParams => zzz_ParamManagerIns.AutoParamReloadEnabled;

        /// <summary>Same as DSAS's File > Reload GameParam and FMGs (runs as a DSAS loading task).</summary>
        public void ReloadParams()
        {
            var doc = zzz_DocumentManager.CurrentDocument ?? throw new InvalidOperationException("No character is loaded.");
            doc.LoadingTaskMan.DoLoadingTask("FileReloadGameParam", "Reloading GameParam and FMGs...", _ =>
            {
                doc.GameRoot.ReloadParams();
                doc.GameRoot.ReloadFmgs();
                doc.EditorScreen?.Graph?.ViewportInteractor?.CurrentModel?.RescanNpcParams();
                doc.EditorScreen?.Graph?.ViewportInteractor?.NewScrub();
                DSAnimStudio.ImguiOSD.Window.Equipment.RequestIndexRefresh = true;
            }, disableProgressBarByDefault: true);
        }
    }
}
