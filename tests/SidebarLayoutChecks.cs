using System.Reflection;
using OrchardSound;

public static class SidebarLayoutChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    static CheckBox Audition(Panel panel) => panel.Controls.OfType<CheckBox>().Single(box => box.Name == "HearHoveredNote");
    static bool LocallyVisible(Control control) => (bool)typeof(Control).GetProperty("DesiredVisibility", Private)!.GetValue(control)!;

    public static void Run()
    {
        int checks = 0; Exception? failure = null;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAILED SIDEBAR: " + name); checks++; Console.WriteLine("PASS SIDEBAR " + name); }
        var thread = new Thread(() =>
        {
            try
            {
                var layout = typeof(EditorForm).GetMethod("LayoutSidebarPanel", BindingFlags.Static | BindingFlags.NonPublic)!;
                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    using var form = new EditorForm([]); _ = form.Handle;
                    object? Field(string name) => typeof(EditorForm).GetField(name, Private)!.GetValue(form);
                    void Call(string name) => typeof(EditorForm).GetMethod(name, Private)!.Invoke(form, null);
                    var canvas = (WaveCanvas)Field("canvas")!;
                    var samples = new float[96];
                    var track = new AudioTrack { Path = "Sidebar fixture.wav", Samples = samples, Peaks = new PeakPyramid(samples), Rhythm = new(120, [], []) };
                    canvas.Slots[0].Track = track; canvas.Slots.Add(new()); Call("RebuildTrackControls");
                    canvas.SetViewportHeight((int)((3 * WaveCanvas.MinPaneHeight + 2 * WaveCanvas.SplitterHeight + 16) * canvas.DpiScale));
                    canvas.FitHeight(true); Call("LayoutTrackControls");
                    var precision = (Panel)Field("zoomControls")!;
                    var tracks = ((List<Control>)Field("trackControls")!).Cast<Panel>().ToArray();
                    if (dpi == 96)
                    {
                        var enabled = typeof(EditorForm).GetMethod("IsNoteAuditionEnabled", Private)!;
                        bool IsEnabled(int pane) => (bool)enabled.Invoke(form, [pane])!;
                        var precisionMode = precision.Controls.OfType<ComboBox>().Single();
                        var trackMode = tracks[0].Controls.OfType<ComboBox>().Single();
                        precisionMode.SelectedIndex = trackMode.SelectedIndex = (int)AudioViewMode.Spectrogram;
                        Check(!Audition(precision).Checked && !Audition(tracks[0]).Checked && !IsEnabled(0) && !IsEnabled(1),
                            "the spectrogram audition controls start unchecked and bypassed");
                        Audition(tracks[0]).Checked = true;
                        Check(!IsEnabled(0) && IsEnabled(1) && !IsEnabled(2), "audition settings are independent for each track and the precision pane");
                        trackMode.SelectedIndex = (int)AudioViewMode.Waveform;
                        Check(!IsEnabled(1) && Audition(tracks[0]).Checked, "the waveform bypasses audition while retaining its spectrogram preference");
                        trackMode.SelectedIndex = (int)AudioViewMode.Spectrogram;
                        Call("RebuildTrackControls");
                        tracks = ((List<Control>)Field("trackControls")!).Cast<Panel>().ToArray();
                        Check(IsEnabled(1) && Audition(tracks[0]).Checked, "a rebuilt track sidebar preserves the note audition preference");
                        precisionMode.SelectedIndex = tracks[0].Controls.OfType<ComboBox>().Single().SelectedIndex = (int)AudioViewMode.Waveform;
                        canvas.FitHeight(true);
                    }
                    var panels = new[] { precision }.Concat(tracks).ToArray();
                    float scale = dpi / 96f, ratio = dpi / (float)form.DeviceDpi;
                    var fonts = new List<(Control Control, Font Original, Font Scaled)>();
                    try
                    {
                        for (int index = 0; index < panels.Length; index++)
                        {
                            var panel = panels[index];
                            var originalFonts = All(panel).ToDictionary(control => control, control => control.Font);
                            panel.Scale(new SizeF(ratio, ratio));
                            foreach (var control in All(panel))
                            {
                                var original = originalFonts[control];
                                var scaled = new Font(original.FontFamily, original.SizeInPoints * ratio, original.Style, GraphicsUnit.Point);
                                fonts.Add((control, original, scaled)); control.Font = scaled;
                            }
                            layout.Invoke(null, [panel, index == 0, scale]);
                            Rectangle PositionPanel()
                            {
                                var pane = canvas.GetPaneBounds(index);
                                var paneDevice = new Rectangle((int)(pane.Left * scale), (int)(pane.Top * scale), (int)(pane.Width * scale), (int)(pane.Height * scale));
                                panel.Location = new((int)(18 * scale), paneDevice.Top + (int)(12 * scale));
                                return paneDevice;
                            }
                            var paneDevice = PositionPanel();
                            string context = (index == 0 ? "precision" : index == 1 ? "loaded channel" : "empty channel") + " at simulated " + dpi + " DPI";
                            var children = panel.Controls.Cast<Control>().Where(LocallyVisible).ToArray();
                            Check(children.All(control => panel.ClientRectangle.Contains(control.Bounds)) && paneDevice.Contains(panel.Bounds),
                                context + " keeps every control inside its sidebar and minimum-height pane");
                            var combo = children.OfType<ComboBox>().Single();
                            var metrics = TextRenderer.MeasureText(combo.Text, combo.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                            bool fits = combo.Height >= combo.PreferredHeight && combo.ItemHeight >= combo.Font.Height && combo.ClientSize.Height >= metrics.Height;
                            if (!fits) Console.WriteLine($"Sidebar combo metrics {context}: bounds {combo.Bounds}, preferred {combo.PreferredHeight}, font {combo.Font.Height}, item {combo.ItemHeight}, text {metrics}");
                            Check(fits, context + " reserves the real native dropdown preferred height and full font/item height");
                            Check(children.OfType<NumericUpDown>().All(number => number.Height >= number.PreferredHeight)
                                && children.OfType<Label>().All(label => label.ClientSize.Height >= TextRenderer.MeasureText(label.Text, label.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height),
                                context + " keeps numeric fields and label text vertically readable");
                            var rows = Rows(panel, index == 0).Select(row => row.Select(control => control.Bounds).Aggregate(Rectangle.Union)).OrderBy(row => row.Top).ToArray();
                            int minimumGap = (int)Math.Round(6 * scale);
                            bool gaps = rows.Zip(rows.Skip(1)).All(pair => pair.Second.Top - pair.First.Bottom >= minimumGap);
                            if (!gaps) Console.WriteLine($"Sidebar row gaps {context}: " + string.Join("; ", rows.Select(row => row.ToString())));
                            Check(gaps, context + " separates title mode numeric slider and BPM rows by at least six logical pixels");
                            var audition = Audition(panel);
                            int waveformHeight = panel.Height;
                            Check(!LocallyVisible(audition), context + " hides the audition control in waveform mode");
                            combo.SelectedIndex = (int)AudioViewMode.Spectrogram;
                            layout.Invoke(null, [panel, index == 0, scale]);
                            paneDevice = PositionPanel();
                            var spectrumRows = Rows(panel, index == 0).Select(row => row.Select(control => control.Bounds).Aggregate(Rectangle.Union)).OrderBy(row => row.Top).ToArray();
                            Check(LocallyVisible(audition) && audition.Top >= combo.Bottom + minimumGap
                                && panel.ClientRectangle.Contains(audition.Bounds) && paneDevice.Contains(panel.Bounds)
                                && spectrumRows.Zip(spectrumRows.Skip(1)).All(pair => pair.Second.Top - pair.First.Bottom >= minimumGap),
                                context + " fits the spectrogram audition row below the selector with readable spacing");
                            combo.SelectedIndex = (int)AudioViewMode.Waveform;
                            layout.Invoke(null, [panel, index == 0, scale]);
                            Check(!LocallyVisible(audition) && panel.Height == waveformHeight, context + " restores waveform sidebar spacing when leaving spectrogram mode");
                        }
                        Check(tracks[0].Controls.OfType<Label>().Single(label => label.AccessibleName == "Estimated BPM").Text == "BPM: off (B)"
                            && tracks[1].Controls.OfType<NumericUpDown>().Count() == 0,
                            "sidebar spacing preserves loaded BPM guidance and the empty channel controls at simulated " + dpi + " DPI");
                    }
                    finally
                    {
                        foreach (var font in fonts) { font.Control.Font = font.Original; font.Scaled.Dispose(); }
                    }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Sidebar layout fixture deadline exceeded.");
        if (failure != null) throw new Exception("Sidebar layout fixture failed.", failure);
        Console.WriteLine($"{checks} sidebar layout checks passed. DPI geometry was simulated with real font and native control metrics; no window was shown.");
    }

    static Control[][] Rows(Panel panel, bool precision)
    {
        var children = panel.Controls.Cast<Control>().ToArray();
        var mode = children.OfType<ComboBox>().Single();
        var audition = Audition(panel);
        if (precision)
        {
            Control[][] precisionRows = [[children.OfType<Label>().Single(label => label.Text == "PRECISION")],
                children.Where(control => control is NumericUpDown || control is Label label && label.Text is "Zoom" or "x").ToArray(),
                [children.OfType<TrackBar>().Single()], [mode]];
            return LocallyVisible(audition) ? [.. precisionRows, [audition]] : precisionRows;
        }
        var rows = new List<Control[]>
        {
            children.Where(control => control is CheckBox && control != audition && control.AccessibleName?.StartsWith("Split channels in Track ") != true || control is Label label && label.AccessibleName != "Estimated BPM" && label.AutoEllipsis
                || control is IconButton && control.AccessibleName == "Remove track").ToArray(),
            children.Where(control => control == mode || control is IconButton && control.AccessibleName?.StartsWith("Open or replace audio in Track ") == true).ToArray(),
            new Control[] { children.OfType<CheckBox>().Single(control => control.AccessibleName?.StartsWith("Split channels in Track ") == true) }
        };
        if (LocallyVisible(audition)) rows.Insert(2, [audition]);
        if (children.OfType<NumericUpDown>().Any())
        {
            rows.Add(children.Where(control => control is NumericUpDown || control is Label label && label.Text is "Volume" or "%").ToArray());
            rows.Add([children.OfType<TrackBar>().Single()]); rows.Add([children.OfType<Label>().Single(label => label.AccessibleName == "Estimated BPM")]);
        }
        return rows.ToArray();
    }
}
