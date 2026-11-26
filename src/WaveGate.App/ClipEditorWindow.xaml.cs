using System.Windows;
using WaveGate.Audio;
using WaveGate.Overlay.Config;

namespace WaveGate.Overlay;

/// <summary>Edits one clip. Changes apply on Save, except the hotkey, which goes through App immediately.</summary>
public partial class ClipEditorWindow : Window
{
    private readonly ClipEntry _clip;
    private readonly Action<PlayOptions> _preview;
    private readonly Action _stopPreview;
    private readonly Action _setHotkey;
    private readonly Action _clearHotkey;
    private readonly bool _canTrim;

    public ClipEditorWindow(ClipEntry clip, IEnumerable<string> categories, TimeSpan? duration,
                            Action<PlayOptions> preview, Action stopPreview, Action setHotkey, Action clearHotkey)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _clip = clip;
        _preview = preview;
        _stopPreview = stopPreview;
        _setHotkey = setHotkey;
        _clearHotkey = clearHotkey;

        NameBox.Text = clip.Name;
        CategoryBox.ItemsSource = categories.ToList();
        CategoryBox.Text = clip.Category ?? "";
        VolumeSlider.Value = clip.Volume;
        VolumeText.Text = $"{clip.Volume:P0}";
        RetriggerBox.SelectedIndex = (int)clip.Retrigger;

        _canTrim = duration is { TotalSeconds: > 0 };
        if (_canTrim)
        {
            var length = duration!.Value.TotalSeconds;
            StartSlider.Maximum = EndSlider.Maximum = length;
            StartSlider.SmallChange = EndSlider.SmallChange = 0.05;
            StartSlider.Value = Math.Min(clip.TrimStart, length);
            EndSlider.Value = Math.Min(clip.TrimEnd ?? length, length);
            UpdateTrimText();
        }
        else
        {
            TrimPanel.Visibility = Visibility.Collapsed;
            NoTrimText.Visibility = Visibility.Visible;
        }

        UpdateHotkeyText();
        Loaded += (_, _) => NameBox.Focus();
        Closed += (_, _) => _stopPreview();
    }

    private PlayOptions CurrentOptions()
    {
        if (!_canTrim) return new PlayOptions((float)VolumeSlider.Value);
        bool trimmedEnd = EndSlider.Value < EndSlider.Maximum - 0.01;
        return new PlayOptions((float)VolumeSlider.Value, StartSlider.Value, trimmedEnd ? EndSlider.Value : null);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is not null) VolumeText.Text = $"{e.NewValue:P0}";
    }

    private void OnTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_canTrim || StartText is null) return;

        // Keep at least 0.1 s between the handles.
        if (sender == StartSlider && StartSlider.Value > EndSlider.Value - 0.1)
            StartSlider.Value = Math.Max(0, EndSlider.Value - 0.1);
        else if (sender == EndSlider && EndSlider.Value < StartSlider.Value + 0.1)
            EndSlider.Value = Math.Min(EndSlider.Maximum, StartSlider.Value + 0.1);

        UpdateTrimText();
    }

    private void UpdateTrimText()
    {
        StartText.Text = Format(StartSlider.Value);
        EndText.Text = Format(EndSlider.Value);
    }

    private static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.f");

    private void UpdateHotkeyText() => HotkeyText.Text = _clip.Hotkey ?? "None";

    private void OnPreviewClick(object sender, RoutedEventArgs e) => _preview(CurrentOptions());

    private void OnStopClick(object sender, RoutedEventArgs e) => _stopPreview();

    private void OnSetHotkeyClick(object sender, RoutedEventArgs e)
    {
        _setHotkey();
        UpdateHotkeyText();
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        _clearHotkey();
        UpdateHotkeyText();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Focus();
            return;
        }

        var options = CurrentOptions();
        _clip.Name = NameBox.Text.Trim();
        _clip.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim();
        _clip.Volume = options.Volume;
        _clip.Retrigger = (RetriggerMode)Math.Max(0, RetriggerBox.SelectedIndex);
        _clip.TrimStart = options.TrimStart;
        _clip.TrimEnd = options.TrimEnd;
        DialogResult = true;
    }
}
