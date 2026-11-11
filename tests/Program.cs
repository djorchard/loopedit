using OrchardSound;
using NAudio.Wave;

try
{
int passed = 0;
void Check(bool condition, string name)
{ if (!condition) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("PASS " + name); }
bool Near(float a, float b, float tolerance = .0001f) => Math.Abs(a - b) <= tolerance;
AudioTrack Track(string name, params float[] frames)
{
    var samples = frames.SelectMany(v => new[] { v, -v }).ToArray();
    return new AudioTrack { Path = name, Samples = samples, Peaks = new PeakPyramid(samples) };
}
float[] Render(AudioTrack[] tracks, long a, long b, int count, bool loop = false)
{
    var result = new float[count]; var mix = new MixProvider(tracks.Select(t => t.Snapshot()), a, b, loop);
    int read = mix.Read(result, 0, count);
    if (!loop && read != Math.Min(count, (b - a) * 2)) throw new Exception("Incorrect read length");
    return result;
}

var a = Track("A.wav", .1f, .2f, .3f, .4f);
Check(Render([a], 1, 3, 4).SequenceEqual(new[] { .2f, -.2f, .3f, -.3f }), "selection uses exact stereo frame boundaries");
Check(Render([a], 1, 3, 12, true).SequenceEqual(new[] { .2f, -.2f, .3f, -.3f, .2f, -.2f, .3f, -.3f, .2f, -.2f, .3f, -.3f }), "loop wraps without leaking outside selection");
var cursorMix = new MixProvider([a.Snapshot()], 0, 4, true, initialFrame: 2);
var cursorSamples = new float[8]; cursorMix.Read(cursorSamples, 0, cursorSamples.Length);
Check(cursorSamples.SequenceEqual(new[] { .3f, -.3f, .4f, -.4f, .1f, -.1f, .2f, -.2f }), "cursor playback begins partway through the loop then wraps to its start");
var oneShot = new MixProvider([a.Snapshot()], 0, 4, false, initialFrame: 2);
var oneShotSamples = new float[8];
int oneShotRead = oneShot.Read(oneShotSamples, 0, oneShotSamples.Length);
Check(oneShotRead == 4 && oneShotSamples.Take(4).SequenceEqual(new[] { .3f, -.3f, .4f, -.4f }) && oneShot.Read(oneShotSamples, 0, oneShotSamples.Length) == 0, "unselected cursor playback reaches the end once without wrapping");
var b = Track("B.mp3", .2f, .1f); b.Offset = 2;
var combined = Render([a, b], 0, 5, 10);
Check(Near(combined[4], .5f) && Near(combined[6], .5f) && Near(combined[8], 0), "delayed track mixes in sync with silence after end");
b.Offset = -1;
Check(Near(Render([b], -1, 2, 6)[0], .2f) && Near(Render([b], -1, 2, 6)[4], 0), "negative offsets and silence are supported");
a.Gain = 10;
var loud = new MixProvider([a.Snapshot()], 0, 4, false); var loudBuffer = new float[8]; loud.Read(loudBuffer, 0, 8);
Check(loudBuffer[6] == 1 && loudBuffer[7] == -1 && loud.ClippedSamples == 6, "1000 percent gain clamps and counts clipped samples");
a.Gain = 0;
Check(Render([a], 0, 4, 8).All(v => v == 0), "zero percent volume mutes");
a.Gain = 1;
var fade = Track("Fade.wav", .5f, .5f, .5f, .5f, .5f, .5f);
Editing.ApplyFade([fade], 1, 5, true);
var faded = Render([fade], 0, 6, 12);
Check(Near(faded[0], .5f) && faded[2] == 0 && Near(faded[4], 1f / 6) && Near(faded[8], .5f) && Near(faded[10], .5f), "fade in affects selected samples only, reaching unity at end");
fade.Fades.Clear(); Editing.ApplyFade([fade], 1, 5, false);
faded = Render([fade], 0, 6, 12);
Check(Near(faded[2], .5f) && faded[8] == 0 && Near(faded[10], .5f), "fade out reaches zero and preserves outside selection");
fade.Offset = 2; fade.Fades.Clear(); Editing.ApplyFade([fade], 0, 5, true);
Check(Near(Render([fade], 2, 3, 2)[0], .25f), "fade spans the global range on offset tracks");
var snapshot = fade.Snapshot(); fade.Fades.Clear(); fade.Gain = 0;
Check(snapshot.Fades.Length == 1 && Near(snapshot.Gain, 1), "playback and export snapshots isolate subsequent edits");
bool invalid = false; try { _ = new MixProvider([], 2, 2, true); } catch (ArgumentException) { invalid = true; }
Check(invalid, "zero length selection is rejected");
var random = new Random(42); var samples = Enumerable.Range(0, 80000).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
var pyramid = new PeakPyramid(samples); bool peaksCorrect = true;
for (int i = 0; i < 200; i++)
{
    int start = random.Next(0, 39999), end = random.Next(start + 1, 40001);
    var expected = samples.Skip(start * 2).Take((end - start) * 2).ToArray(); var actual = pyramid.Query(start, end);
    peaksCorrect &= Near(actual.Min, Math.Min(0, expected.Min())) && Near(actual.Max, Math.Max(0, expected.Max()));
}
Check(peaksCorrect, "200 waveform peak ranges match actual sample extrema");
Check(Editing.ExportStem([a, b], 0, 5) == "A + B", "merged filename uses both parent names");
b.Gain = 0; Check(Editing.ExportStem([a, b], 0, 5) == "A", "muted track is omitted from export name");
Check(Editing.ExportStem([], 0, 5) == "Clip", "empty contribution has safe filename");

string root = Path.Combine(Path.GetTempPath(), "OrchardSound-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    float[] tone = new float[48000 * 2];
    for (int i = 0; i < 48000; i++) { tone[i * 2] = .2f * (float)Math.Sin(i * Math.PI * 2 * 440 / 48000); tone[i * 2 + 1] = -.1f; }
    var source = new AudioTrack { Path = Path.Combine(root, "Tone.wav"), Samples = tone, Peaks = new PeakPyramid(tone) };
    var first = await AudioFiles.ExportAsync([source.Snapshot()], 12000, 36000, root, "Tone", ".wav");
    using (var reader = new WaveFileReader(first.Path))
        Check(reader.WaveFormat.SampleRate == 48000 && reader.WaveFormat.Channels == 2 && reader.Length == 24000 * 4, "WAV export has exact selected duration and stereo PCM format");
    var second = await AudioFiles.ExportAsync([source.Snapshot()], 0, 48000, root, "Tone", ".wav");
    Check(Path.GetFileName(first.Path) == "Tone 1.wav" && Path.GetFileName(second.Path) == "Tone 2.wav", "numbered export never overwrites earlier output");
    var loaded = await AudioFiles.LoadAsync(first.Path);
    Check(loaded.Frames == 24000 && Near(loaded.Samples[1], -.1f, .0001f), "WAV loads with real channel samples preserved");
    foreach (string ext in new[] { ".mp3", ".flac", ".ogg", ".m4a" })
    {
        var file = await AudioFiles.ExportAsync([source.Snapshot()], 0, 48000, root, "Tone", ext);
        var decoded = await AudioFiles.LoadAsync(file.Path);
        Check(decoded.Frames >= 47000 && decoded.Frames <= 50000 && decoded.Samples.Any(s => Math.Abs(s) > .05), ext + " export and decode round trip");
    }
    var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    bool didCancel = false;
    try { await AudioFiles.ExportAsync([source.Snapshot()], 0, 48000, root, "Cancelled", ".wav", cancelled.Token); }
    catch (OperationCanceledException) { didCancel = true; }
    Check(didCancel && !Directory.GetFiles(root, ".orchard-*").Any(), "cancelled export cleans temporary files");
    await File.WriteAllTextAsync(Path.Combine(root, "bad.wav"), "invalid audio");
    bool badRejected = false;
    try { await AudioFiles.LoadAsync(Path.Combine(root, "bad.wav")); } catch (IOException) { badRejected = true; }
    Check(badRejected, "invalid file returns a decode error");
    var simultaneous = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AudioFiles.ExportAsync([source.Snapshot()], 0, 400, root, "Concurrent", ".wav")));
    Check(simultaneous.Select(x => x.Path).Distinct().Count() == 3, "concurrent exports choose distinct filenames atomically");
    Check(!Directory.GetFiles(root, ".orchard-*").Any(), "successful conversions leave no temporary files");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{passed} checks passed.");
UiChecks.Run(args.FirstOrDefault());
TransportChecks.Run();
CursorPlaybackChecks.Run();
RhythmChecks.Run();
await RecordingChecks.Run();
await SelectionChecks.Run();
EffectChecks.Run();
TimelineChecks.Run();
await PreviewChecks.Run();
BufferedChecks.Run();
ThemeChecks.Run();
ShortcutHelpChecks.Run();
ClipboardChecks.Run();
LoopRefinementChecks.Run();
ScrubChecks.Run();
RhythmUiChecks.Run();
RenderingChecks.Run();
await AutomationChecks.Run();
await CueChecks.Run();
await DuplicationChecks.Run();
await MultiEffectChecks.Run();
await MultiplyChecks.Run();
MultiplyDialogChecks.Run();
BitcrushChecks.Run();
SpectrogramChecks.Run();
await SeamlessRepeatChecks.Run();
SeamlessRepeatDialogChecks.Run();
await SeamlessRepeatIntegrationChecks.Run();
await ClosedRepeatChecks.Run();
LiveMarkerIntegrationChecks.Run();
EffectColumnChecks.Run();
await LiveLoopChecks.Run();
PointerPrecisionChecks.Run();
await DeClickChecks.Run();
await EqChecks.Run();
EqDialogChecks.Run();
await EqIntegrationChecks.Run();
SidebarLayoutChecks.Run();
PointerAccelerationChecks.Run();
RawMarkerChecks.Run();
ZoomOverlayChecks.Run();
ToolbarChecks.Run();
HoverHotkeyChecks.Run();
await SilenceChecks.Run();
SilenceIntegrationChecks.Run();
await RhythmRobustnessChecks.Run();
MouseSpeedChecks.Run();
MouseSensitivityUiChecks.Run();
SelectionZoomChecks.Run();
StereoLaneChecks.Run();
NestedPrecisionChecks.Run();
SpectralHoverChecks.Run();
NoteBandAuditionChecks.Run();
NoteAuditionIntegrationChecks.Run();
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
