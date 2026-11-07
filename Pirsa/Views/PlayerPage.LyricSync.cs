using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pirsa.Helpers;
using Windows.System;

namespace Pirsa.Views;

/// <summary>
/// Lyric-style subtitle sync. The lines around the playhead are shown like a
/// lyrics view — current line large, three either side, empty cues skipped —
/// and clicking the line that is being spoken aligns it to the moment of the
/// click. The video keeps playing throughout; there is no number to guess.
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>Lines shown either side of the current one.</summary>
    private const int LyricSyncRadius = 3;

    private const long LyricSyncFineStepMs = 50;

    private static readonly TimeSpan LyricSyncToastDuration = TimeSpan.FromSeconds(3.2);

    /// <summary>Cue times and text for the active subtitle file.</summary>
    private IReadOnlyList<SubtitleCue> _subtitleCues = [];

    private string? _subtitleCuesPath;

    private bool _lyricSyncOpen;

    /// <summary>Offsets to return to, newest last; the toast's Undo pops one.</summary>
    private readonly List<long> _lyricSyncUndo = [];

    private int _renderedLyricIndex = int.MinValue;

    private DispatcherTimer? _lyricSyncToastTimer;

    // ------------------------------------------------------------------ opening

    private async void LyricSync_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricSyncOpen || !_engine.IsReady)
        {
            return;
        }

        if (!HasActiveSubtitle || string.IsNullOrWhiteSpace(_selectedSubtitlePath))
        {
            return;
        }

        var path = _selectedSubtitlePath;
        if (!string.Equals(_subtitleCuesPath, path, StringComparison.OrdinalIgnoreCase))
        {
            var cues = await Task.Run(() => SubtitleCueFile.Load(path));
            if (_disposed ||
                _lyricSyncOpen ||
                !HasActiveSubtitle ||
                !string.Equals(_selectedSubtitlePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _subtitleCuesPath = path;
            _subtitleCues = cues ?? [];
            UpdateSubtitleTimingUi();
        }

        if (_subtitleCues.Count == 0)
        {
            ViewModel.SubtitleStatus =
                "Sync by ear needs a text subtitle (SRT, VTT or ASS).";
            RefreshSubtitleFlyout();
            return;
        }

        OpenLyricSync();
    }

    private void OpenLyricSync()
    {
        _lyricSyncOpen = true;
        _lyricSyncUndo.Clear();
        _renderedLyricIndex = int.MinValue;

        _resumeHintTimer?.Stop();
        ResumePanel.Visibility = Visibility.Collapsed;
        _subtitleHudTimer?.Stop();
        SubtitleHudPanel.Visibility = Visibility.Collapsed;
        SubtitlesFlyout.Hide();
        HideChromeForLyricSync();

        LyricSyncPanel.Visibility = Visibility.Visible;
        UpdateLyricSyncHeader();
        HideLyricSyncToast();
        RenderLyricLines(_engine.PositionMs);

        // The point is to hear the line. Opening the panel from a paused frame
        // should start the scene again.
        if (!_engine.IsPlaying)
        {
            _engine.SetPaused(false);
        }

        _engine.FocusSurface(FocusState.Programmatic);
    }

    // ----------------------------------------------------------------- rendering

    /// <summary>Rebuilds the seven-ish rows around the line currently displayed.</summary>
    private void RenderLyricLines(long positionMs)
    {
        LyricLinesPanel.Children.Clear();

        var cues = _subtitleCues;
        if (cues.Count == 0)
        {
            _renderedLyricIndex = -1;
            return;
        }

        var current = SubtitleCueFile.FindActiveIndex(cues, positionMs - _subtitleDelayMs);
        _renderedLyricIndex = current;

        var first = current < 0 ? 0 : Math.Max(0, current - LyricSyncRadius);
        var last = current < 0
            ? Math.Min(cues.Count - 1, (LyricSyncRadius * 2) + 1)
            : Math.Min(cues.Count - 1, current + LyricSyncRadius);

        var currentLineStyle = (Style)Resources["LyricCurrentTextStyle"];
        var lineStyle = (Style)Resources["LyricLineTextStyle"];
        var hintStyle = (Style)Resources["LyricAlignHintStyle"];

        for (var i = first; i <= last; i++)
        {
            var cue = cues[i];

            if (i == current)
            {
                var text = new TextBlock
                {
                    Text = cue.Text,
                    Style = currentLineStyle,
                };
                AutomationProperties.SetName(text, cue.Text);

                LyricLinesPanel.Children.Add(new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 2, 0, 2),
                    Child = text,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
                continue;
            }

            LyricLinesPanel.Children.Add(BuildLyricLineButton(i, cue, current, lineStyle, hintStyle));
        }
    }

    private Button BuildLyricLineButton(
        int index,
        SubtitleCue cue,
        int current,
        Style lineStyle,
        Style hintStyle)
    {
        var distance = Math.Abs(index - current);

        var text = new TextBlock
        {
            Text = cue.Text,
            Style = lineStyle,
            FontSize = distance switch { 1 => 15.5, 2 => 14.5, _ => 13.5 },
        };

        var align = new TextBlock
        {
            Text = "Align",
            Style = hintStyle,
        };

        var layout = new Grid { ColumnSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(text);
        Grid.SetColumn(align, 1);
        layout.Children.Add(align);

        var opacity = distance switch { 1 => 0.78, 2 => 0.56, _ => 0.4 };

        var button = new Button
        {
            Content = layout,
            Tag = index,
            Style = (Style)Resources["LyricLineButtonStyle"],
            Opacity = opacity,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += LyricLine_Click;

        // The affordance only appears when the row is actually under the
        // pointer or focus, like a lyrics view that reveals its controls.
        void Highlight(bool on)
        {
            align.Opacity = on ? 1 : 0;
            button.Opacity = on ? 1 : opacity;
        }

        button.PointerEntered += (_, _) => Highlight(true);
        button.PointerExited += (_, _) => Highlight(false);
        button.GotFocus += (_, _) => Highlight(true);
        button.LostFocus += (_, _) => Highlight(false);

        AutomationProperties.SetName(button, cue.Text);
        AutomationProperties.SetHelpText(button, "Align this line to the current moment");
        return button;
    }

    /// <summary>Only the highlighting moves while the scene plays; the rows are rebuilt on a line change.</summary>
    private void UpdateLyricSyncPosition(long positionMs)
    {
        if (!_lyricSyncOpen || _subtitleCues.Count == 0)
        {
            return;
        }

        var current = SubtitleCueFile.FindActiveIndex(_subtitleCues, positionMs - _subtitleDelayMs);
        if (current == _renderedLyricIndex)
        {
            return;
        }

        RenderLyricLines(positionMs);
    }

    // ------------------------------------------------------------------ actions

    private void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (!_lyricSyncOpen || sender is not Button { Tag: int index })
        {
            return;
        }

        if (index < 0 || index >= _subtitleCues.Count)
        {
            return;
        }

        var position = _engine.PositionMs;
        var offset = position - _subtitleCues[index].StartMs;
        offset = (long)Math.Round(offset / 10.0) * 10;

        PushLyricSyncUndo();
        SetSubtitleDelay(offset, announce: false);
        RenderLyricLines(position);
        UpdateLyricSyncHeader();
        ShowLyricSyncToast();
    }

    private void LyricSyncEarlier_Click(object sender, RoutedEventArgs e) =>
        NudgeLyricSync(-LyricSyncFineStepMs);

    private void LyricSyncLater_Click(object sender, RoutedEventArgs e) =>
        NudgeLyricSync(LyricSyncFineStepMs);

    private void NudgeLyricSync(long deltaMs)
    {
        if (!_lyricSyncOpen)
        {
            return;
        }

        PushLyricSyncUndo();
        SetSubtitleDelay(_subtitleDelayMs + deltaMs, announce: false);
        RenderLyricLines(_engine.PositionMs);
        UpdateLyricSyncHeader();
        ShowLyricSyncToast();
    }

    private void PushLyricSyncUndo()
    {
        _lyricSyncUndo.Add(_subtitleDelayMs);
        if (_lyricSyncUndo.Count > 20)
        {
            _lyricSyncUndo.RemoveAt(0);
        }
    }

    private void LyricSyncUndo_Click(object sender, RoutedEventArgs e)
    {
        if (!_lyricSyncOpen || _lyricSyncUndo.Count == 0)
        {
            return;
        }

        var previous = _lyricSyncUndo[^1];
        _lyricSyncUndo.RemoveAt(_lyricSyncUndo.Count - 1);

        SetSubtitleDelay(previous, announce: false);
        RenderLyricLines(_engine.PositionMs);
        UpdateLyricSyncHeader();
        HideLyricSyncToast();
    }

    private void LyricSyncClose_Click(object sender, RoutedEventArgs e) => CloseLyricSync();

    private void UpdateLyricSyncHeader()
    {
        LyricSyncValueText.Text = FormatSubtitleDelayValue(_subtitleDelayMs);
        LyricSyncUndoButton.Visibility = _lyricSyncUndo.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // -------------------------------------------------------------------- toast

    private void ShowLyricSyncToast()
    {
        LyricSyncToastValue.Text = FormatSubtitleDelayValue(_subtitleDelayMs);
        LyricSyncToast.Visibility = Visibility.Visible;

        if (_lyricSyncToastTimer is null)
        {
            _lyricSyncToastTimer = new DispatcherTimer { Interval = LyricSyncToastDuration };
            _lyricSyncToastTimer.Tick += (_, _) => HideLyricSyncToast();
        }

        _lyricSyncToastTimer.Stop();
        _lyricSyncToastTimer.Start();
    }

    private void HideLyricSyncToast()
    {
        _lyricSyncToastTimer?.Stop();
        LyricSyncToast.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------- open / close

    private void CloseLyricSync()
    {
        if (!_lyricSyncOpen)
        {
            return;
        }

        _lyricSyncOpen = false;
        _lyricSyncToastTimer?.Stop();
        LyricSyncPanel.Visibility = Visibility.Collapsed;
        LyricSyncToast.Visibility = Visibility.Collapsed;
        _lyricSyncUndo.Clear();

        ShowChrome();
        StartChromeTimer();
        ShowSubtitleTimingHud();
        _engine.FocusSurface(FocusState.Programmatic);
    }

    /// <summary>
    /// A source or track change invalidates the cue list the panel was built
    /// from. The offset stays; it belongs to the title.
    /// </summary>
    private void AbortLyricSync()
    {
        if (!_lyricSyncOpen)
        {
            return;
        }

        _lyricSyncOpen = false;
        _lyricSyncToastTimer?.Stop();
        LyricSyncPanel.Visibility = Visibility.Collapsed;
        LyricSyncToast.Visibility = Visibility.Collapsed;
        _lyricSyncUndo.Clear();
    }

    private void HideChromeForLyricSync()
    {
        _chromeTimer.Stop();
        _chromeHidden = true;
        Chrome.Opacity = 0;
        Chrome.IsHitTestVisible = false;
        RefreshSubtitleLift();
    }

    // --------------------------------------------------------------------- keys

    /// <summary>Space plays or pauses, the arrows nudge the offset, Esc finishes.</summary>
    private void HandleLyricSyncKey(KeyRoutedEventArgs e)
    {
        if (!_lyricSyncOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseLyricSync();
                e.Handled = true;
                break;

            case VirtualKey.Space:
                if (e.KeyStatus.WasKeyDown)
                {
                    e.Handled = true;
                    break;
                }

                TogglePlayPause();
                e.Handled = true;
                break;

            case VirtualKey.Left:
                NudgeLyricSync(-LyricSyncFineStepMs);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                NudgeLyricSync(LyricSyncFineStepMs);
                e.Handled = true;
                break;
        }
    }
}
