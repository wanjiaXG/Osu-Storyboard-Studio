namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    [System.Runtime.InteropServices.DllImport("cimgui", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igClosePopupsExceptModals();

    private System.Numerics.Vector2 dockPosition, dockSize;
    public FullscreenWindow? FullscreenWindow { get; init; }
    internal bool FullscreenControlsHidden => viewer.FullscreenControlsHidden;
    internal string FullscreenState => viewer.FullscreenState;

    public void SetFullscreen(bool value)
    {
        if (value)
        {
            if (app.Project == null || app.IsLoading || app.Exporting) return;
            FullscreenWindow?.Enter(viewer.Open ? viewer.VisiblePreviewBounds : null);
            viewer.EnterFullscreen();
        }
        else
        {
            viewer.ExitFullscreen();
            FullscreenWindow?.Exit();
        }
    }
}
