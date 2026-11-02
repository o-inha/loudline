using System.Windows;
using System.Windows.Input;

namespace Multiloud.App.Controls;

public sealed partial class TimelineView
{
    // Closing a project abandons editor drafts instead of committing them to a
    // removed source. This also releases drag captures and source references.
    internal void ResetProjectInteractions()
    {
        CompleteLoopInteraction(cancel: true, allowSeek: false);
        _valueDragChanged = false;
        _valueDragTrack = null;
        var valueElement = _valueDragElement; _valueDragElement = null;
        if (valueElement?.IsMouseCaptured == true) valueElement.ReleaseMouseCapture();
        bool syncing = _syncingEditors; _syncingEditors = true;
        try
        {
            _inputTextDirty = _targetTextDirty = false;
            CloseNativeTextEdit(false); CloseNativeTextEdit(true);
            _editorTrack = _hoveredTrack = _hoverNodeTrack = null;
            _hoverNodeIndex = -1;
            _hoverEditor.Visibility = Visibility.Collapsed;
            _removeTrackButton.Visibility = Visibility.Collapsed;
        }
        finally { _syncingEditors = syncing; }
        _dragBoundaryIndex = -1;
        _boundaryMoved = _boundaryWasAdded = _boundaryEditStarted = false;
        _dragEdited = false;
        CompletePointerInteraction();
        _trackDragInProgress = false;
        ClearTrackDrop(); StopNavigation();
        SetCrosshairPointer(null); SetTrackHoverPointer(null);
        LivePreviewActive = false;
        Cursor = Cursors.Arrow;
        InvalidateCustomBoundaries();
        InvalidateTimeline();
    }
}
