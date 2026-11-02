using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private void SyncCeilingControl()
    {
        // Round-trip formatting preserves values entered in Advanced or loaded
        // from a project; merely focusing and leaving the field is a no-op.
        string text = _settings.CeilingDb.ToString("R", CultureInfo.InvariantCulture);
        CeilingBox.Text = text.Contains('.') ? text : text + ".0";
    }

    private void CeilingCommit(object sender, RoutedEventArgs e) => CommitCeiling();

    private void CeilingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitCeiling();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitCeiling()
    {
        if (!_initialized || _syncing || _exporting) return;
        if (!double.TryParse(CeilingBox.Text.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double ceiling) || !double.IsFinite(ceiling) || ceiling is < -6 or > -.1)
        {
            SyncCeilingControl();
            StatusText.Text = "Enter a ceiling from −6 to −0.1 dBTP.";
            return;
        }
        if (ceiling == _settings.CeilingDb)
        {
            SyncCeilingControl();
            return;
        }
        PushUndo();
        _settings = _settings with { CeilingDb = ceiling };
        SyncCeilingControl();
        _dirty = true;
        QueueRender();
    }
}
