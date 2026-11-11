# LoopEdit

<img src="assets/loopedit-wordmark.png" alt="LoopEdit wordmark with copper tape around two reels" width="440">

A Windows audio editor for trimming, mixing, and looping game sounds. Originally created by djorchard. [Creator profile](https://github.com/djorchard)

## Build and run

Install the .NET 10 SDK on Windows and add the FFmpeg bin folder to PATH. You can also point the `ORCHARD_FFMPEG` environment variable at `ffmpeg.exe`. [Download .NET](https://dotnet.microsoft.com/download/dotnet/10.0) · [Download FFmpeg](https://ffmpeg.org/download.html)

```powershell
dotnet build OrchardSound.csproj -c Release
dotnet run --project OrchardSound.csproj -c Release
```

To publish a Windows x64 app folder, run the command below and open `artifacts/LoopEdit/LoopEdit.exe`. The published app uses the .NET 10 Desktop Runtime. FFmpeg is installed separately and can also sit beside the executable.

```powershell
dotnet publish OrchardSound.csproj -c Release -r win-x64 --self-contained false -o ./artifacts/LoopEdit
```

LoopEdit supports existing `.orchardsound` sessions from Orchard Sound Editor. Press F1 in the app for the keyboard and pointer guide. [Shortcut guide](assets/shortcuts.png)

## Interface theme

Dialogs use consistent spacing, evenly padded action buttons and LoopEdit's dark surfaces. Primary actions such as Record and Apply use the mint accent; secondary actions such as Cancel use a neutral outlined style. Sliders use teal thumbs and readable blue-grey channels. Keyboard focus stays visible and disabled actions remain distinct. Status messages and channel BPM labels scale with Windows DPI.

The Ink & Mint style uses ink-blue cards with crisp mint and copper icons drawn as C# vectors. Button backgrounds match their parent surface through their rounded corners, including normal, active and disabled states. The final toolbar button or F1 opens a keyboard and pointer guide drawn from one binding list in code. Click a keyboard key or press one to filter the shortcut list; All shortcuts clears the filter. Guide key presses do not trigger editor actions. Escape closes it and returns focus to the waveform. The reusable image is `assets/shortcuts.png`.

Hover a toolbar icon for a description of its action and keyboard shortcut. The tooltip uses the same dark palette as the editor.

On supported Windows versions, native title bars use matching dark caption and text colors. Effect windows remain movable, resizable modeless tools, so the waveform stays available while configuring an effect.

## Load and arrange

- Use a channel's Open icon, Ctrl+O or drop sound files onto the channel area. WAV, MP3, FLAC, OGG, Opus, M4A, AAC, AIFF and WMA inputs are decoded by FFmpeg.
- Drop onto an empty channel to load it. Additional files become more channels. Dropping onto a loaded channel adds another sound without replacing the existing one.
- Empty channels show Paste above the drop prompt and Open below it. The header Open icon replaces that channel's sound; X removes its row. A compact Add track button appears at the bottom left of the final track when there is room below its controls. The toolbar Add track icon is always available, with Ctrl+= or Ctrl+Plus.
- Paste imports copied sound files or WAV audio from the clipboard. Raw clipboard audio is retained as a numbered WAV file so sessions can reopen it. Ctrl+V pastes when the waveform has focus.
- Every loaded sound uses the same timeline and selection. Each channel and the precision view have independent Waveform/Spectrogram selectors. The left controls have separate rows with consistent spacing and fit the native dropdown height. Spectrogram grids use musical note labels on a logarithmic C0 to C10 frequency scale. Analysis runs in the background; gain changes affect spectral brightness without altering audio.
- Split channels shows separate L and R lanes inside one resizable track. The lanes use their actual channel samples and share volume, timing, selection and edits. The precision view follows the hovered track's split setting.
- In Spectrogram mode, the hovered note name appears on both edges of its highlighted row, including notes omitted from the regular grid labels. Tick **Hear hovered note** below that pane's display selector to hear its frequency band from the mix during playback or scrubbing. Precision and each track have separate checkboxes. Moving away returns to the full mix. Audition does not edit the sound or affect exports. Low notes take longer to settle, so continuous playback makes them easier to hear than brief scrubs.
- Hold the left mouse button and move horizontally on a waveform to shift that sound in time. A plain click sets the selection start. Track shifts can be negative.
- Drag the divider between panes to make adjacent channels or the precision view taller or shorter. Equal heights restores an even layout.
- Each loaded channel has a volume control from 0% to 1000%. 100% is the original gain. Loud summed audio is hard limited at full scale for playback and export, with a clipping message. Lower the volume for undistorted sound.
- Channels start enabled. Untick a channel or Ctrl+click its header to exclude it from playback, effects and export. If every channel is unticked, no channel produces audio or receives effects.

## Select, listen and fade

- Hover a channel to inspect that sound in the precision view. Move into the precision view to choose finer positions. Its numeric zoom amount, slider and mouse wheel all change the same horizontal magnification from 1x to 2048x and stay synchronized. The initial zoom is 12x. Tap Alt to fit the selection with 10% context before and after it, clamped to available audio; held Alt still bypasses snapping.
- The top precision guide follows the mouse immediately at every zoom level, including with Shift held. It never pulls the mouse toward the guide. Slow movement over lower channels becomes finer with higher zoom: 2x uses normal speed, 8x uses half speed, and 32x uses quarter speed. Fast sweeps accelerate toward normal speed, making larger moves easier. Shift adds finer movement at 10% divided by zoom, preserves subpixel positions, and hides the mouse pointer while the waveform guide remains visible. Releasing Shift, leaving the waveform or losing focus restores the pointer. Ctrl jumps directly to the physical mouse position in a lower waveform, ignoring zoom precision, and also bypasses Smart Snap.
- The top waveform is blue. A blue window on the hovered lower track marks the exact time range shown above, including when zooming or panning. Other tracks retain their colors. Spectrogram views retain their heatmap with the same blue window indicator.
- View > Follow cursor keeps the precision range centered on the white guide, including at the audio boundaries, with blank context outside the sound. View > Precision magnifier enables an additional purple 10x lens in the top third of a precision pane taller than 300 screen pixels when the main zoom is below 100x. Its purple range marker shows the extra detail being inspected.
- Moving near a precision edge starts panning immediately. It ramps up over 500 ms to at most 45% of the visible window per second, with a directional cursor, and stops at the timeline bounds. Static waveform bases are cached while cursor, selection and playhead overlays update separately.
- A vertical white guide marks the position under the cursor. Left click sets the selection start and right click sets its end. The `[` key sets the start at the cursor guide and `]` sets the end. The selection end must be later than the start.
- Click a waveform to focus it, then use Left and Right to nudge the cursor guide by one sample frame. Arrow nudges bypass smart snap for manual positioning.
- Home and End jump the cursor to the beginning or end of the shared timeline while the waveform has focus.
- The toolbar has separate Play from cursor (Space) and Play loop (Shift+Space) buttons. Each press restarts its action. Ctrl+Space stops playback. Active playback uses the mint button style and shows the Stop shortcut. A cursor before the selection plays its lead-in and then loops inside it; a cursor inside the selection loops there; a cursor after its end plays once to the timeline end. Either selection marker enables looping, with the other boundary defaulting to the timeline beginning or end. An end-only selection therefore loops from the timeline beginning. With no selection, playback runs once from the white cursor. Ctrl+A selects the whole session.
- Selection > Clear selection or Escape removes both markers and stops playback without changing the sounds. The Selection menu also groups boundary commands, Select all, Delete, Duplicate, Trim, Save section cue points and Insert silence.
- Scrub starts off. Enable it to audition short audio around the pointer while normal playback is stopped, in either movement direction. Scrubbing stops when pointer movement stops.
- Refine loop end on restart starts off. When enabled, Shift+Space uses the currently heard normal playhead, or the cursor when stopped. The first tap sets the end; later taps blend 75% of the existing end with 25% of the new tap. Each change supports Undo. Device position history accounts for queued output, loop wraps and underruns; outputs without a device clock use callback consumption as a fallback.
- Enable View > Fit waveform height to fit the source maximum to the top of the plot and its minimum to the bottom, without display headroom. Asymmetric audio can move the zero line away from the center. With this option off, +100% and -100% are the plot bounds. This changes only the display, including the precision view. It does not increase playback or export volume.
- Drag the bracket-shaped selection handles to move an existing start or end. A handle drag changes only its boundary and keeps the background track fixed. Playback continues while either marker moves: a playhead before the new start keeps playing into the loop, and a playhead beyond the new end restarts at the loop start. Very close handles separate vertically so each remains reachable. Handles keep at least one frame selected and do not cross each other. Grid labels appear on both edges: actual signed amplitude percentages in waveform mode, note names in spectrogram mode. Hovering a note recolors its energy toward white, strongest at the semitone center and fading toward its boundaries, preserving quiet and loud details. The 4096-sample FFT and 1024-sample hop trade frequency and time resolution; the note grid is a reference rather than automatic note transcription.
- Fade In ramps from silence to full selected gain across the selection. Fade Out ramps from full selected gain to silence. Enabled channel selectors choose the sounds affected by fades and effects. Audio outside the chosen time range is unchanged. Multiple fades multiply and move with their sound.
- Delete section (Del) removes the selected time range from every channel and closes the resulting gap. Trim to selection (Ctrl+T) keeps only that range and moves its beginning to the start of the timeline. Cue points follow those edits.
- Duplicate selection (Ctrl+D) immediately inserts a copy after the selected section, with cues at its original start, the join and the new end. Ctrl+R opens Reverse.
- Insert silence (Ctrl+I) accepts a duration and inserts at the white cursor or either selection boundary. All tracks, including unticked rows, remain aligned; later audio and cue points move together. The inserted range is selected and one Undo restores the previous audio and cues.
- Ctrl+Z undoes edits, including effects, levels, moves, cuts, duplication, cue points and loop-end refinement, up to 100 changes subject to the session memory budget. Ctrl+Y or Ctrl+Shift+Z redoes an undone change. A new edit clears Redo. Removing a channel leaves its source file unchanged.

Toolbar icons have accessible action names and show their shortcut hints, including left and right mouse selection and the bracket keys.

Supported editor shortcuts also work while the pointer is over an inactive LoopEdit window. Only editor bindings are intercepted; system Windows-key combinations and unassigned shortcuts remain available. The keyboard guide filters keys, and effect tools retain their transport controls. The hover keyboard hook is installed only when the app is shown and removed on disposal.

View > Mouse sensitivity offers an optional pointer speed from 1 to 20. Windows exposes this as a global setting, so LoopEdit temporarily sets it only while the pointer is over the app and restores the previous speed when it leaves, the option is disabled or the app closes. The setting starts disabled. The user's original Windows value is kept only in memory; the chosen LoopEdit preference is saved separately. An external speed change is respected. This does not replace zoom-dependent audio guide precision or Shift fine control.

Playback prepares up to about 180 ms of audio ahead. One output device uses 120 ms of buffering divided into three buffers. Volume and live effect changes replace the upcoming audio at the same play position, with a 10 ms crossfade between the old and new sound. These paths are checked through the real NAudio float-to-byte adapter and synthetic output owners; subjective listening remains a separate check.

## Waveform shading and rhythm aids

Both the channel and precision waveforms use darker colors for quieter audio amplitudes and brighter colors for louder ones, including the current gain and fades. Vertical auto magnification changes waveform height while retaining that audio-level color cue. The amplitude shading is always active and preserves each channel's color.

The optional rhythm aids start switched off:

- View > Beats / BPM (B) shows detected beat markers. The channel BPM status stays visible, showing off, analysis progress, an estimate, silence, too short, no steady tempo, an error or a timeout. Click a BPM label to retry.
- Kick hits (K) shows likely low-frequency transient markers.
- Smart snap (S) moves a nearby selection boundary onto a detected beat or saved cue point. Its tolerance is 12 displayed pixels, so the precision view gives finer control. Hold Ctrl or Alt to bypass snapping. Beat markers move with their sound's timeline offset.

Rhythm analysis runs locally in the background when an aid is enabled. The estimator searches 50 to 220 BPM, with a whitened RMS-novelty autocorrelation fallback for tracks whose attacks do not form a clean event sequence. Likely kicks use a separate low-frequency onset pass with adaptive thresholds. BPM is an estimate and can be half or double the perceived tempo. Kick markers can also identify bass notes or impacts. Very short, irregular or silent clips can have no tempo estimate. These markers are editing aids rather than a guaranteed musical grid.

The original C# analysis separates transient detection from tempo fitting, with no additional runtime dependency. Research references: [Ellis beat tracking](https://labrosa.ee.columbia.edu/projects/beattrack/), [librosa beat tracking](https://librosa.org/doc/0.11.0/generated/librosa.beat.beat_track.html) and [librosa onset strength](https://librosa.org/doc/0.11.0/generated/librosa.onset.onset_strength.html).

## Effects and recording

The Effects menu (Ctrl+E) provides Maximize, Multiband EQ, Reverb, Delay, low-pass or high-pass Filter, Reverse, Pitch Shift, Multiply, Seamless Repeat, Bitcrush and De-click. Fade In and Fade Out use the same workflow. Multiple movable modeless tools can stay open, including repeated instances of an effect. Their enabled previews run together in opening order. Apply commits only that window's effect first, with one Undo entry, then rebuilds the remaining open previews over the committed sound. Cancel removes only that instance. Numeric settings have synchronized sliders. Closing an effect tool returns focus to the waveform.

Effects target enabled channels. Unticked channels are excluded, including when all are unticked. Maximize processes each entire enabled track by default; tick Apply to selection only to limit it to the selected range. Other effects use the selected range. Maximize changes audio levels, while Fit waveform height changes only the view.

Multiband EQ has ten frequency bands labeled 31 Hz, 63 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz, 2 kHz, 4 kHz, 8 kHz and 16 kHz. Each band has a typed gain and slider from -24 to +24 dB. Zero is neutral, and Reset to flat clears every start/end gain with one preview update. Automate start and end exposes independent endpoint gains in the paired layout and varies them linearly in dB across the selected audio. The same settings process the left and right channels independently without crossfeeding, preserve clip duration, and leave unticked tracks unchanged. Boosts can increase the output level; track volume and Maximize remain available to adjust it. The fixed peaking bands use the [Audio EQ Cookbook](https://www.w3.org/TR/audio-eq-cookbook/) equations, with exact low centers of 31.25 and 62.5 Hz.

Tick Automate start and end to expose end values and linearly vary numeric settings across the processed range. The window doubles in width, placing each Start setting on the left and its End setting on the right, with a slider under each value. Turning automation off restores the single-column width. Multiply uses the same paired layout for its always-visible start/end settings. Pitch Shift supports semitones or percent while preserving duration: zero semitones and 100% leave pitch unchanged, and 25% to 400% covers 24 semitones down to 24 semitones up. Filter provides high-pass or low-pass frequency settings. Delay timing accepts milliseconds or BPM.

Reverb and Delay have Cut tail at selection end enabled by default. Untick it to let the tail blend into later audio and extend the track when necessary. Committed edits support Undo and Redo.

Record (Shift+R, with Ctrl+Alt+R retained as an alias) opens input and destination choices before capture starts. Choose an existing channel or New track, then Record. The default is the first empty channel, otherwise the first enabled one, otherwise New track. Press Record again to stop and load the result there. Capture writes a new 44.1 kHz, 16-bit mono WAV, which the editor converts to its normal stereo timeline when loaded. Recording files go beside the first loaded sound, or into `Music/LoopEdit Recordings` if no sound is loaded. Existing files are never overwritten.

The recording limit is approximately 11 minutes so the captured sound can fit the editor's decoded audio limit. Stopping flushes and closes the WAV. A device failure retains usable audio already captured, while an empty failed recording is removed. A nonresponsive input has a bounded stop. Recording starts only after an explicit Record action and input choice; no audio is captured merely by opening the app or picker.

Bitcrush provides bit depth, sample-rate reduction and wet/dry mix with optional endpoint automation. Multiply layers all copies simultaneously at the same selection position, keeping the selection length, later audio and cue points unchanged. Each voice starts with the original selected audio and gets its own stable random multiplier: 1 plus a signed variation, so 25% variation ranges from 0.75 to 1.25. Independent Pitch, Filter and Reverb checkboxes expose start/end parameters. Reroll changes the random set. Effective randomized settings stay within supported effect limits. Voices sum together, so more copies can raise the level; track volume and Maximize can adjust the resulting level.

De-click repairs detected short clicks and abrupt waveform joins inside the selected range. Sensitivity controls detection, and Repair width controls the local interpolation span. Both support start/end automation. Select a small range around a problematic join and use live preview to compare the result before applying. The effect preserves duration and stereo channel order; detection is an editing aid, so it cannot guarantee removal of every crack without affecting the surrounding sound.

Seamless Repeat overlaps each repetition with the previous one and crossfades their join. Set the total repeat count and crossfade length, then audition before applying. Loop end back to start is enabled by default: it also crossfades the final-to-first join, so the completed section can repeat as a loop. Turn it off to crossfade only between copies. Smart seam can refine the overlap length near your setting to find a closer stereo waveform match. Adaptive crossfade adjusts its curve using the similarity of the overlapping audio. Each option can be switched off.

Short selections limit the overlap automatically. Closing the loop uses one extra overlap; a single closed repetition can therefore be shorter than the original selection. Overlap shortens the interval between repetitions, so this mode can change beat timing. Only enabled channels repeat. Later audio and cue points follow the resulting length, including when a single repetition shortens the selection. Other channels receive inserted silence when time is added, or lose the same ending interval when time is removed, keeping the shared timeline aligned. Applying adds cue points at copy starts and the final end, and supports Undo.

## Cue points and sessions

Cue points replace the bookmark dropdown. Save section cue points (Ctrl+B) marks the current section boundaries on the waveform. Smart snap can use them alongside detected beats. Cue points remain attached to the timeline through cuts, trims and duplication.

Save session (Ctrl+P) writes an `.orchardsound` session. Open session (Ctrl+Shift+O) restores tracks, positions, levels, fades, cuts, processed audio and cue points. Existing sessions and their older named ranges remain compatible. Sessions reference the original sound files and store edited audio buffers. Keep those source files available; loading checks their hashes. Session saving is separate from exporting a mixed sound file.

## Export

The Export button has a same-height dropdown arrow beside its primary action. Use that arrow to choose WAV, MP3, FLAC, OGG or M4A, or Set export folder. Press E, Ctrl+S or the Export icon to save the selected mix in the chosen format. The default folder is beside the first loaded source. Set export folder (Ctrl+Shift+S) remembers the destination for later exports.

Filenames use contributing parent names and the next available number, for example `Rain + Thunder 1.mp3`, then `Rain + Thunder 2.mp3`. Existing files are never overwritten. WAV output is 48 kHz stereo, 16-bit PCM. Original source files are never edited. Save an `.orchardsound` session to preserve an arrangement for reopening; unsaved arrangements remain in memory only.

WAV exports embed cue positions and names. Exporting a selection rebases its included cues to the new file, including a cue at the exact end. Loading a WAV restores supported cue metadata and converts source-rate offsets to the editor's 48 kHz timeline.

Decoded audio is limited to 256 MiB per sound (about 11 minutes of stereo audio) and 512 MiB per session. Decoding and compressed export have three-minute deadlines and cancel when the window closes. Temporary conversions are removed after success, failure or cancellation. Waveform drawing includes current gain and fades. Export and playback use the actual summed samples.

## Tests

Run the test harness on Windows with FFmpeg available. The codec tests use libmp3lame, libvorbis, AAC, and FLAC. The harness creates its own audio fixtures and uses simulated outputs for hardware playback tests.

```powershell
dotnet run --project tests/OrchardSound.Tests.csproj -c Release
```

## Credits and license

LoopEdit was originally created by djorchard. The toolbar uses the original tape-wrap design with LoopEdit lettering. The artwork was edited with built-in ImageGen and is saved in `assets/loopedit-wordmark.png`.

<details>
<summary>Wordmark editing prompts</summary>

> Edit the supplied original wordmark into exactly LoopEdit. Preserve the entire original Loop portion, particularly the nearly touching teal reels, dark rims, three triangular spokes, orange hubs, and the copper tape band physically hugging the upper and lower reel rims with two lower guide rollers. Replace only the suffix smith with Edit, capital E and lowercase dit, in the same rounded mint lettering, shading, baseline and weight. Keep a tight horizontal layout on genuine transparency with clean alpha edges. No extra lettering, border, panel, shadows outside the existing forms, or tape paths around the outside of the whole word.

> Clean up this exact LoopEdit wordmark for use as a compact desktop toolbar logo. Preserve the exact spelling, rounded mint lettering, teal reels with triangular spokes, copper hubs, and the physical copper tape hugging the outer reel rims and running over the two lower rollers. The white and cyan edge debris between letters and near the edges is accidental: remove it completely. Every gap and letter counter must be clean, genuinely transparent alpha, with smooth antialiased edges and no white matte. Crop close to the wordmark, with small even transparent margins. No redraw of the overall shape, no box, no added details.

> Correct only the lowercase p in the attached LoopEdit logo. Use the logo's existing lowercase d as the exact shape model: rotate the d shape 180 degrees to obtain a conventional lowercase p with a circular bowl, clean single curved top, and a straight left descender below the baseline. Place this new p between the reels and the capital E, with matching mint colour and the same x-height and baseline as the d bowl. The p descender should join the bowl on the left and must not extend upwards above the bowl or create a separate hump. Keep all seven other glyphs, both reels, tape, roller placement, image dimensions, and spacing unchanged. Clean transparent alpha inside and around the replaced letter. This is a one-letter edit, not a new design.

> Clean the alpha matte of this attached LoopEdit logo while preserving the exact letter shapes, especially the now-normal lowercase p with one smooth bowl, and the tape wrapped tightly around the teal reels. Remove every speck of white from the transparent holes in the p and d, the gap before E, and between the other letters. The gaps and counters should be fully transparent, as should the surrounding canvas. Preserve the filled mint shapes, dark teal reels and copper tape, their layout, size and colours. Do not add texture, glow, outlines or a backdrop. This is a targeted cleanup of accidental edge and background pixels.

</details>

The project uses a custom source-available license. You can build, modify, and share the editor with creator credit and the LoopEdit application name preserved. [License](LICENSE)

NAudio 2.2.1 provides Windows playback and WAV output under the MIT license. [NAudio notice](licenses/NAudio.txt) · [Upstream project](https://github.com/naudio/NAudio/tree/v2.2.1)
