# Loudline

> [!IMPORTANT]  
> This is a 100% vibe-coded adhoc app prompted over the course of a of couple days. No human has written or reviewed a single line of code. No human has probably even read this README.

Loudline is a Windows app for basic loudness control of an entire music release. It exists mainly for convenient user interface for this purpose; the audio processing itself contains nothing miraculous or innovate.

It is a portable, single .exe file, and you can download it from here:

[![Btn Download]][Link Download] [![Btn Support]][Link Support]

---

### Some of Loudline's features:
- Is not an audio editor beyond loudness control.
- Everything is immediately processed: you see/hear exactly what you will export at all times.
  - Move playhead to control which audio file (song) is processed first/next, when all tracks are being processed. 
- Display all audio files (song) horizontally stacked, as if a single waveform.
  - Re-order them freely by dragging around.
- Dynamic leveling, as in one big volume envelope.
  - Toggle on/off.
  - App automatically suggests a gentle volume envelope to smooth out loudness variation
  - ...which can be freely edited
  - ...and adjusted by the "Strength" slider
  - ...and a few other settings.
- Track level matching to -N LUFS.
  - Toggle on/off.
  - Manual controls per audio file (song) to overwrite global LUFS target.
- Peak protection.
  - Toggle on/off. 
  - Level-match with original audio level to hear the difference.
  - Delta to hear what is removed.
  - Has a number of settings.
  - Also works as a "volume maximizer" (haven't tried, but in theory, anyway).
- Toggle waveform view between Original, Result, or Comparison.
- Include a secondary (background) audio track. 
  - Has fixed volume across all audio files (songs) in the main track.
  - Is not processed in any way.
  - Its volume is still accounted for in all the audio files (songs) and their processing.
- Slice or merge audio files (songs).
  - Each audio slice is treated as their own audio file (song) and everything is processed accordingly. 
- Insert silence.
- Seamless single segment (A) or dual segment (A/B) looping.
- Lightweight and smooth waveform zooming and scrolling.
- Custom track (song) boundaries.
  - As if we concatenate all tracks (songs) into one long audio file, and then re-slice it by these custom bounadry markers when we export.
  - All processing still happens according to original audio file (song) boundaries (which we can also slice/merge).
- My personal favourite: dropdown for selecting output device right there on the main UI, without need to dive into app settings.
  
---

> [!NOTE]  
> From this point onward, it's the AI agent's original README. You can be the first person to read it through.

A native **Windows 11 x64** desktop app for balancing the loudness of music across a release. Drop in tracks, review the suggested changes on one continuous timeline, listen, fine-tune, and export separate processed WAV files.

## Music and optional background

Use Loudline with a music lane alone, or add a separate background that keeps its fixed level while the music is loudness-controlled. The **Background track** toolbar switch hides or shows that optional lane without changing its audio. Audition loops do not repeat or alter background source files.

The music lane has one continuous project Dynamic Leveling envelope, plus individual track targets and gains. The smaller **Background (unprocessed)** lane accepts one or more audio files. Drop them at a time position, drag a clip to move it, and drag its upper edge handles to create linear amplitude fade-in/out. Multiple clips can overlap and add together. The first file drop snaps to 0:00 within 96 screen pixels of the lane start; hold Shift to bypass snapping. A sole existing clip also snaps when moved near the start. The layer has one fixed gain from −60 to +12 dB. Drag its green Gain value vertically in 1 dB steps (Shift: 0.1 dB), or double-click to type a value; Enter or clicking away commits, and Escape cancels. Exact processing starts only after a changed value is committed. Only explicit placement, gain and fades affect the layer. Drag the divider to change the lane heights. **Solo** auditions only the background at the current playhead position and does no analysis or rendering.

With an enabled, nonempty background layer, all Result audio and exported files use one **48 kHz stereo** timeline. This keeps a stereo background consistent beneath mono/stereo and mixed-rate music. Background segments follow the absolute album sample clock; they never restart at music track boundaries. Longer files are cropped at the album end, and uncovered positions contain silence. An empty or disabled layer retains ordinary source-format processing.

01 analyzes and levels only music. 02 measures each final music-plus-background mixdown and changes only music gain toward that track's target. 03 reserves time-local true-peak headroom for the fixed background and limits/attenuates only music. A brief background peak restricts the music around that event instead of reserving the same worst-case space throughout the song. Finished mixes are measured again, with local music-only repairs and a conservative safety fallback when needed. An unattainable target is reported rather than changing the background. With 03 enabled, a background that alone exceeds the selected ceiling blocks the affected Result until its gain is reduced. With 03 off, there is no ceiling enforcement or hidden static attenuation.

Result playback, Comparison and both Export all / Export this file use the final mixdown. Each music track is still exported as its own WAV with the corresponding continuous background segment. Original playback includes the same bed with unprocessed music, mixed on demand without waiting for a rendered Result. Level-match A/B remains monitoring-only. Background changes reuse cached music loudness analysis and authored envelopes; changes to album order can change which background portion accompanies a song and therefore require fresh final matching.

Projects use format 9 and save custom export markers/options, the continuous project envelope, source ranges for slices, and all background sources, gain, positions and fades with relative and absolute paths. Missing background sources use the relocation dialog and per-clip Relink action. Project formats 1–8 remain readable. Projects without saved background audio open with an empty background lane; saving a legacy project may suggest a separate copy to preserve its original file. Undo/Redo includes background changes and slicing. Place the playhead inside a music file and press **Slice** or **S** to divide it immediately at that sample boundary. The new boundary and source waveforms appear immediately, before any audio preparation or analysis. With an active module, each slice starts its own analysis and processing automatically. With all three modules off, music and background are mixed only in small playback/export buffers. No loudness analysis, full-file mix render, limiting or source PCM copying is performed for a cut. Slices share immutable source ranges and waveform summaries; fixed gains and background fades are applied by the streaming reader. The music waveform uses its native source data, while the background has its own lane. Cuts preserve the absolute source and background clocks, including resampling phase. Enabling a module later starts the needed analysis and processing automatically. With Dynamic leveling enabled, all pending music analyses finish before the final shared envelope is designed and clips are rendered. This prevents each newly analyzed slice from invalidating previously finished clips. Rendering chooses its next pending clip from the current playhead after each completed file, without restarting work already in progress. If that clip is ready, pending work continues forward in album order. Each completed clip becomes playable before the rest finish. With Dynamic leveling off, independent matching/protection retains per-clip analysis and rendering progress. The continuous project envelope stays in place. Reopening or relinking a slice uses its saved region of the original source; adjacent slices share the decoded root and waveform summaries.

## Editing and audition loops

**Slice (S)** cuts at the current playhead without activating a pointer tool. Clicking or dragging the music waveform continues to seek or scrub. **Merge (M)** joins the two music items at the nearest internal boundary into one processing/export item. Adjacent source ranges share their original PCM when possible; different sources retain a saved composition recipe. The continuous envelope and absolute project markers remain intact.

Undo and Redo restore compatible existing Results immediately, including after Merge when the previous variants have left the preview cache. History remembers exact preview keys without retaining additional audio buffers. If a previous variant has no remaining owner and has been released, processing rebuilds it as needed.

**Insert silence** opens Start / Nearest boundary / All boundaries / End choices and a decimal duration, default 2 seconds (0.001–600 seconds). All boundaries inserts only between existing items. Generated silence is project-owned metadata with shared zero samples; it never depends on a temporary WAV. It can be moved, sliced, removed and exported like a track. Insertion retimes the music envelope with its music and holds the curve through the gap. Background audio keeps its absolute clock and remains audible through a music gap.

The transport's **LOOP: A / B** buttons are independent. **A (Z)** is blue and **B (X)** amber. With exactly one enabled, drag the time ruler to paint or replace that range. With both enabled, ruler drags seek; the existing colored end handles remain editable. Track-body drags always scrub normally. Disabling a loop hides it without losing the saved range. Both ranges and their switches are stored in the project and have independent, audio-free Undo/Redo.

One enabled valid range repeats itself; two alternate **A → B → A**, regardless of their chronological order. Repeats remain inside the current audio endpoint and use bounded reusable readers, with short monitoring-only edge ramps to reduce clicks. Scrubbing auditions the exact cursor position. The loops are listening tools and never trim or repeat an export. Missing/unready Results retain the normal wait-and-resume behavior.

## Custom track boundaries and export

**Custom Boundaries** shows a compact marker bar between the overview and time ruler. Click empty space to add a track boundary, click a marker to remove it, or drag a marker to move it. The project start and end are implicit boundaries. Hiding the bar retains its markers. Markers are absolute project positions; positions beyond a temporarily shorter album remain saved but are ignored until in range again. They never alter source tracks, envelopes, matching, limiting, playback or waveform audio caches. Marker and export-option undo is independent of audio processing, including a render completing while the edit is in history.

The connected **gear | Export all** group keeps a normal-color settings gear beside the primary export action, with its settings dropdown aligned to the right edge of the full group. Its dropdown uses connected choices for WAV format (24-bit PCM / 32-bit float) and Individual files / One file. Individual files reveals File boundaries / Custom boundaries and the boundary fade; One file hides these controls and shrinks the panel. Custom is the default preference when markers exist. Without valid markers the UI selects Original and disables Custom. Original exports retain each source track's current Result and native output format. Custom exports stream the entire finished mix as one continuous timeline, split solely at the authored markers, even when a new file spans several original songs. One-file export ignores all markers.

**Boundary fade defaults to 0 ms**, preserving gapless reconstruction. Optional linear fades affect only internal custom export joins, leave the album beginning/end intact, and do not insert silence or change duration. They create a small dip at the join, so leave the value at zero for an album intended to play continuously. At zero fade, concatenated custom exports are sample-identical to the one-file export, including deterministic 24-bit dither. This describes the exported audio; player buffering and gapless support remain player-dependent.

Uniform output formats are preserved. A mixed-format album uses one 48 kHz stereo clock for Custom or One file; resampling never restarts at a custom boundary. Writing uses bounded chunks rather than a full-album PCM allocation. Completed files remain when cancellation occurs; partial files are removed, and sources/existing outputs are protected.

Small module settings float below their toolbar section, with the right edges aligned. Export options begin directly with the format buttons. Menus omit the unused left icon gutter; selected menu options show their checkmark on the right. The gear, Cancel, Escape or an outside click closes the area without applying its draft. Apply commits once. Solo is amber to distinguish its monitoring-only routing. Ordinary buttons retain keyboard operation without a persistent focus highlight; Slice has its supplied icon and label. Display switches, fitting actions and monitoring tools are ordered in groups with subtle separators and audio icons.

## Run

Download **Loudline-win-x64.zip** from [Releases](https://github.com/o-inha/loudline/releases), extract the ZIP, and run **Loudline.exe**. The app is a single self-contained executable with the .NET runtime included; no loose DLLs or localization folders are needed. The archive also includes documentation and licence notices. You can move Loudline.exe by itself to run it; keep the licence and third-party notices when redistributing it. No installer, account, internet connection, or audio upload is required. The executable is unsigned.

The real app window first paints a Loading view, then initializes the editor inside that same window. Runtime initialization still happens before the first window appears, and bundled native libraries may need extracting to a temporary cache. This startup sequence does not guarantee an instantaneous first frame or placement on the intended virtual desktop if you switch before the window appears.

Toolbar controls adapt through four states as the window widens: two toolbars with icons, two toolbars with icons and text, one toolbar with icons, then one toolbar with icons and text. Consolidating to one toolbar takes priority over retaining labels. The processing stages stay centered in the remaining space between the view actions and output selector. Controls switch to icons before the view actions would wrap; very narrow rows can scroll horizontally. **View → File list** (F) hides or restores the file column and gives its width to the waveform editor. **View → Button style → Default / Icons only** chooses adaptive labels or permanent icons, saved as an app preference. Buttons with dedicated shortcuts show their label and key in a tooltip in both presentation modes. Other icon-only buttons show their label; Delta remains icon-only with its own tooltip. Toolbar buttons, view segments and display switches share borderless backgrounds and a uniform height. The folder button beside Add audio files opens a Loudline project. Add audio files uses the primary accent while the release is empty and returns to its normal appearance when it contains a loaded, importing or missing track. Each processing section has a green power button: click it to bypass that stage; a gray power icon and dimmed section show the bypassed state. Original / Result / Comparison form a connected button group. The transport is centered and uses icons. The waveform fills the main editor; separate gain and envelope lanes are omitted. Persistent instructions and the comparison legend are omitted; numeric feedback appears when inspecting or dragging envelope points. The title bar, status bar, and About dialog show the version. Active analysis/rendering shows a prominent centered spinner over the dimmed waveform, plus a spinner in the track list. Neither has an adjacent processing label. Queued tracks are marked separately.

The executable uses the supplied symbol with a white outline so it remains visible against light and dark Explorer backgrounds. The window/taskbar symbol follows the app theme; the full Loudline artwork is retained as the logo asset. The interface follows the Windows app theme. Choose **View → Theme** to override it. Scrollbars use a slim 4-pixel thickness at 100% display scaling and appear on hover, keyboard focus or capture. Designed for a Windows 11 desktop with 32 GB RAM. **Help → About** shows the version, designer attribution, source link and licence information in the app's themed dialog style.

The empty project shows **Auto-process immediately**, which switches all three processing modules together without creating an unsaved edit. It starts enabled. Playhead dragging in the main waveform or time ruler auditions short faded audio grains in every view, including both lanes; normal playback resumes at the released position if it was running before scrubbing.

**Help → Keyboard shortcuts** lists all main and context-specific bindings in a scrollable table. Main bindings include **1 / 2 / 3** Original / Result / Comparison; **E** Envelope; **G** Gains; **B** Background visibility; **C** Custom boundaries; **A / Home** Fit all; **S** Slice at playhead; **M** Merge nearest boundary; **Z / X** Loop A / B; **Shift+B** background Enabled; **Shift+S** background Solo; **F** file list; **Ctrl+W** Close project. Text and number editors retain normal typing.

## Workflow

1. **Add or drop audio files.** Mono/stereo WAV and AIFF are decoded directly. FLAC, MP3, M4A, AAC, and WMA use installed Windows Media Foundation codecs. Unsupported encodings produce an error instead of a silent conversion. New import batches are sorted naturally by filename (001, 002, 010, 012, a, c, k), ignoring case. Dragging over the waveform or file list highlights the nearest insertion boundary. Drop there to insert the naturally sorted batch together at that position; dropping elsewhere or using Add audio files appends. Files are analyzed and previewed automatically; saved projects retain their chosen order.
2. **Dynamic Leveling** suggests slow, bounded gain changes across sustained sections. The default maximum suggested change is ±6 dB, an 8-second transition length, and 65% Strength. A short-term loudness profile, sustained-change detection, a deadband, and silence/fade protection keep the envelope from chasing individual notes or transients. Sustained, abrupt section changes are localized with cached 100 ms analysis and may receive a shorter continuous gain ramp; isolated hits, brief breaks, gradual fades and bass-only spectral changes do not automatically qualify. The section gear exposes **Bass sensitivity**: new sessions use 25% (Reduced), and 100% (Normal) retains the previous detector. A fixed 90 Hz detector high-pass branch reduces deep bass’s influence, blended with the original K-weighted energy so bass is never completely ignored. It filters the analysis only; it does not EQ the audio. Section levels, their reference and track-boundary corrections share the same detector. Bass-dominated passages receive conservative positive-gain suggestions, while fades, silence and sustained musical dynamics remain protected. Changing the slider drafts a setting; **Apply** updates the suggested curve once and creates one undo step. Existing manual adjustments are retained as offsets from the previous suggestion, with new knots where the revised curve needs them. The gear remains available during import, analysis and preview processing. Apply cancels superseded previews, keeps decoding/analysis running, and lets each newly analyzed source finish with the latest settings. Cached detector energies avoid rescanning audio for sensitivity changes.
3. **Track Matching** measures each track *after* its leveling envelope and matches the requested LUFS target. The default is −14 LUFS. Its own **Strength** blends automatic gain correction in dB: 0% leaves the measured loudness unchanged, 50% moves halfway toward the target, and 100% applies the full correction. After you change Track Matching Strength, the LUFS field shows the selected track’s estimated result in amber, updating to its measured result once ready. The stored target stays separate: focusing the field reveals that target, and typing a target or choosing a preset returns the normal goal display. An untouched focus/blur restores the amber result. Partial Strength can produce different resulting LUFS for different tracks, including their target overrides and manual trim. Peak protection still enforces the ceiling. Presets are starting points, not a requirement to master all music to the same target.
4. **Peak protection** uses adaptive stereo-linked peak control. The gear contains connected **Transparent / Punchy** style buttons, **Response** (Fast ↔ Slow), **Transient preservation**, **Bass control** (Broadband ↔ Selective), and an optional **Dynamics safeguard** with a compact **Maximum sustained reduction · dB** field. Response controls anticipation and recovery speed; Transient preservation shortens ducking around detected attacks. Neither is an amount-of-protection control. Bass control selectively reduces low-frequency peaks before the final broadband limiter, helping a deep bass event affect higher instruments less; 0% retains the ordinary broadband response. Its complementary bass/residual split does not change below-ceiling audio. New projects use 50% Bass control; older projects without that saved setting retain 0% until explicitly changed. Dynamics safeguard starts off, with a 3 dB sustained-reduction limit when enabled. It can lower music drive and accept a quieter result to avoid sustained heavy limiting. Reconstructed peaks are checked after processing, with local corrective attenuation before a last-resort safety trim. The default ceiling is −1 dBTP. Loudline uses its own limiter implementation. Output loudness and peaks are measured again; the requested target can remain constrained. The meter shows 100 ms peak reduction during playback and the selected track's maximum when stopped. Track feedback includes average reduction and time spent above 0.1 dB attenuation. The module's ear button auditions **Delta**, described below.
5. **Audition and edit.** Original plays the source. Result and Comparison play the exact rendered processing. The section power buttons independently bypass each processing stage. Compare keeps a gray original contour above the green processed waveform, with blue bands for boosts and coral bands for cuts. Adjustments repaint immediately using an approximation while the exact preview rebuilds automatically; matching gain estimates respond to the current controls before that render finishes. The status bar offers a highlighted **Process** action whenever work needs resuming. Canceling a processing change restores its previous controls and envelope. Canceling file loading retains found-but-unprocessed slots and can be resumed; these are distinct from missing files. A 500 ms settling period coalesces committed control changes, quick toggles and undo. Returning to active settings does no DSP work and keeps playback running. Previously rendered variants are reused from a 2 GiB session cache; only changed tracks with uncached settings are rebuilt. Source analysis is retained, and envelope edits reuse intermediate processing where possible. Playback can start on any ready track while other tracks are analyzed or rendered. It continues into the next track if that preview is ready when reached; otherwise it waits at that boundary. Playback interrupted by a preview update resumes when that track is ready, unless a manual transport action cancels the pending resume. Editing the currently playing track retains its position while its preview updates; rendering other tracks does not interrupt playback.
6. **Export all.** Use the large toolbar action or Ctrl+E and choose an output folder. A centered Exporting overlay shows file count, weighted overall percentage, a progress bar and Cancel; it clears on completion, cancellation or failure. Each track or slice becomes a separate numbered WAV with unchanged duration. Music-only output retains the source format; an enabled nonempty background layer uses 48 kHz stereo. Default output is 24-bit PCM with TPDF dither; File → WAV format offers 32-bit float. Source files and existing output files are preserved. Completed files are kept if export is cancelled. Right-click a track in the list or waveform and choose **Export this file…** to save only that track’s current processed result. Other tracks may still be processing or missing; the chosen track must be ready. Monitoring level-match does not affect exported audio.

With all three processing modules off, the Result uses the decoded source ranges directly. Fixed music gains and any enabled background are combined only in bounded playback/export buffers; no full mix is rendered and no loudness scan, limiter or hidden ceiling attenuation runs. Source waveforms remain available immediately. Unmeasured clips omit loudness figures until an enabled module needs those measurements. Result and Comparison remain available. Turning a module on automatically analyzes and processes any unmeasured clips; loading or reopening an all-off project also skips loudness analysis.

**File → Close project** confirms unsaved changes, cancels active work and returns to the empty startup session. Both lanes, source/render caches, history, markers and project settings are cleared; app preferences such as output device, theme and button style remain.

Background audio can be added while Result rendering is running. A source-loading operation (decoding and analysis) retains exclusive ownership of the import/cancel transaction, so another import must wait for that operation to finish.

## Per-track controls

Hover over a track in the waveform to reveal **Input dB** and **Target LUFS**, stacked vertically with every line aligned right. Drag a value up or down, or double-click it for direct entry in a compact three-character-width field. Input gain moves in 1 dB steps; hold Shift for 0.1 dB steps. Target loudness moves in 1 LUFS steps. Input gain (−30 to +30 dB) comes before leveling, matching and peak protection; full-strength matching can compensate for a constant input-gain change. Dragging the neutral white/gray horizontal track-gain line on the waveform adjusts a final manual trim after matching. A dark contrast outline and compact value label keep it visible over the waveform and envelope. **Gains** shows or hides that line without changing the processing.

Target LUFS initially follows the global target. Editing it creates a track override (−36 to −6 LUFS), which ignores global target changes; the reset button restores inheritance. A custom target above the global target has a faint blue waveform background, below it a faint red background, and an equal-valued override a neutral tint. Matching Strength still applies to overridden tracks.

Both the file list and waveform have an upper-right trash icon on hover. It removes the track from the release and keeps its source file. Undo restores it. The project has one continuous gold volume envelope; file boundaries do not reset it or create independent automation endpoints. Cached detector summaries are merged on a common timeline, so sustained quiet/loud sections are recognized across song edges. Importing, removing or reordering updates the suggestion while retaining manual offsets with their audio. Local point edits project only the neighboring knot interval into affected tracks; matching and limiting may still change a track's overall gain. Slicing preserves the exact project curve and changes only the independent matching/export partitions.

## Audio output

Use the sound-device selector in the toolbar to choose an output at any time. Switching during playback keeps the current position and continues on the chosen output; switching while paused keeps it paused. The device choice is remembered. **Windows default** follows changes to the system default output. Available outputs refresh automatically and whenever the dropdown opens;  If the selected output disappears, Loudline falls back to Windows default when available.

## Timeline controls

| Action | Control |
| --- | --- |
| Zoom around pointer | Mouse wheel (animated) |
| Pan horizontally | Shift + wheel (animated) |
| Pan with a touchpad or horizontal wheel | Horizontal scroll over the waveform; small movements retain their precision |
| Scroll a compact toolbar | Horizontal touchpad gesture or horizontal wheel over the overflowing row |
| Move or resize visible region | Drag overview viewport or its edges |
| Fit one track | Double-click its waveform, overview segment, or list item |
| Fit release | Home / Fit all |
| Reorder tracks | Hover and drag the horizontal six-dot waveform grip (shown with at least three interior joins visible), or use the always-visible vertical grip on a file-list item; drop at a highlighted boundary |
| Seek | Click or drag the waveform/ruler, including the filename strip and playhead triangle |
| Inspect cursor position | Hover the signal for green position markers in the time ruler and dB scales, plus live time, waveform dBFS and envelope-gain dB in the transport bar |
| Play / pause | ▶ / Ⅱ or Space |
| Per-track input / target | Hover a waveform track; drag Input dB / Target LUFS |
| Fine input gain | Hold Shift while dragging Input dB |
| Fine leveling / matching strength | Hold Shift; wheel/key increments become 0.1% instead of 1% |
| Remove track | Hover its waveform or file-list item; click the trash icon |
| Adjust overall track gain | Enable **Gains** and drag the horizontal gain line on the waveform |
| Edit leveling | Enable **Envelope** and drag gold nodes over the waveform |
| Inspect envelope values | Hover a point or drag it for numeric feedback |
| Add envelope point | Click an empty part of the envelope line; keep the mouse held to drag the new point |
| Remove envelope point | Right-click a point (endpoints retained) |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Add files | Ctrl+O |
| Save project | Ctrl+S |
| Open project | Ctrl+Shift+O |
| Export all | Ctrl+E / Export all |
| Export one processed track | Right-click its file-list item or waveform → Export this file… |

Dragging either Strength slider updates a draft waveform preview live. Dynamic Leveling also moves its displayed envelope toward or away from zero, with point labels showing the applied dB gain on the fixed left-hand scale; a full-strength +6 dB point at 50% Strength is displayed as +3 dB. Stored authored points are preserved. At zero Strength or while bypassed, the envelope is flat and clicking it seeks rather than attempting an undefined gain edit. Exact audio processing waits until mouse release and runs only if the final Strength percentage changed. Returning the slider to its starting percentage leaves the audio and preview cache intact. Shift fine adjustment remains available. Envelope and Strength drags keep a consistent preview detail level throughout the gesture, then refine it once on release.

When audio needs rebuilding, the queue checks the playhead again after each file finishes. Its current track is next if it needs work; otherwise the queue continues forward and then wraps to earlier tracks. Seeking never cancels the file already being processed or changes the release order. Playback interrupted by an update resumes at the same track position as soon as its current result is ready, while the remaining tracks continue processing. Manual transport actions take precedence over a pending automatic resume.

The leveling envelope is edited directly on the waveform. Inserting a point on the existing line preserves its shape and reuses the current preview without DSP work. Dragging that same point changes the curve. Double-clicking away from the curve still fits the track. The **Envelope** and **Gains** switches affect display only.

Hovering a waveform track adds a very faint background highlight. Hovering the signal shows a grey vertical cursor line across the music lane and compact green position markers within the time ruler and both dB scales. These update independently of waveform tiles; the grey line hides during dragging. The envelope marker follows the envelope scale's visibility. The transport keeps track statistics on the left, playback centered, and live cursor time, amplitude dBFS and envelope gain on the right. Cursor level measures the axis position, rather than sampling audio at that time. Single-clicking a filename seeks; double-clicking still fits its track.

The waveform is centered below the filename strip; hover controls overlay the signal without reserving extra height, and both scales align with the plot. The waveform uses a linear amplitude display. Its right-hand scale shows dBFS relative to digital full scale, so those logarithmic labels have nonlinear spacing. The gold scale on the left shows envelope gain in dB: 0 dB means no gain change, positive values boost, and negative values attenuate. These preview and scale updates leave the audio algorithms unchanged.

The overview always mirrors the playhead with a passive gold marker, even when playback is outside the visible main region. Wheel navigation uses short 220 ms easing transitions, retargeted from the displayed camera and paced by the display's rendering cycle. Dense envelopes draw only visible knots and screen-space detail during motion; full detail returns when navigation settles. Original, Result and Comparison all draw cached image tiles that move with the camera. Background workers prepare the requested detail; matching cached tiles from coarser or finer detail levels stay visible when available during that work, using the visible time span rather than requiring offscreen coverage. Reuse requires the same audio source, processing state and palette, so navigation stays responsive as resolution changes. Playhead, processing indicators and hover controls update separately, so their animation does not rebuild the waveform. Multi-resolution peak summaries keep queries bounded at both release and sample zoom levels. Seeking during playback changes the stream at an audio callback boundary and keeps the output device open; a small amount of audio already queued to the device can still play first. The waveform combines channel peaks into one display. Playback and processing preserve stereo. The track-gain line shows input gain, automatic matching gain, and your manual trim together. The project envelope shows the **applied** gain after Leveling Strength. Editable nodes retain their full-strength authored values internally; dragging the displayed curve converts back through Strength and removes the derived edge correction, so edits do not stack corrections. Gain is interpolated linearly in dB. Manual gain trims are applied after automatic matching, so a deliberate offset remains deliberate.

The **Level-match A/B** display toggle attenuates whichever listening mode is louder to a common per-track loudness, including when Result is quieter than Original. Its amber active state distinguishes the listening adjustment from exported processing. It affects monitoring only: processing, displayed waveform, project edits and exported PCM are unchanged. The **File → WAV format** submenu selects 24-bit PCM or 32-bit float export.

The ear button in **03 Peak protection** is always icon-only, with the tooltip **Delta**. Its amber active state auditions the signal removed by that module, including any drive reduction made by Dynamics safeguard. The fixed background cancels from this difference. Delta sounds the same in Original, Result and Comparison; it waits for the current Result when that track needs processing. It reconstructs the difference in small playback buffers and never renders or retains another full-length audio file. A/B level matching is temporarily disabled while Delta is active, preserving its checked state for afterward. Delta and Background Solo are mutually exclusive; enabling either turns the other off. These monitoring switches do not change processing, undo history, project edits or exports.

With selective bass protection active, the reduction meter and its peak/average statistics report the more attenuated band together with broadband attenuation. They do not imply that every frequency was reduced by that same amount. The dynamics safeguard evaluates the actual music's energy reduction independently of that band meter.

The file list and waveform always share the playback/export order. File-list six-dot grips remain visible. Waveform grips appear on hover only when at least three interior track boundaries are visible; album start and end do not count. Drag from either available grip; the highlighted boundary shows where the track will land. Tracks can be dragged between the two views. The envelope, manual gain, measurements and rendered audio move intact with their track. A move recalculates only newly joined automatic edge envelopes from cached measurements. Manual envelopes and unaffected preview variants remain intact; tracks with changed processing rebuild automatically. Playback retains the same point within its track after a move and continues if that preview is ready. **Edit → Reset selected track** restores suggested leveling and clears its manual gain, input gain and target override. **View → Rebuild preview** resumes a cancelled preview. The status bar shows ongoing work and offers a highlighted Resume processing action after cancellation; automatic updates need no manual action. Processing spinners contain no text banners. Persistent help text is omitted.

The processing options use compact floating dropdown areas. Numeric options have their description and a three-character-width field on one row. Closing a changed project uses a themed **Save / Don’t save / Cancel** confirmation; Cancel, Escape or the window close button keep the project open, and choosing Save proceeds only if saving succeeds. Missing-source dialogs use the same app colors and controls.

## Projects

Save a `.loudline` project to retain track order, source paths, saved track durations, processing settings, manual gain, input gain, per-track target overrides, and both authored and automatic edge envelopes. Original audio is referenced, not embedded. Each source retains both a path relative to the project and an absolute path. Opening tries the relative path first, then the absolute path, so projects can follow a moved folder or find audio kept in its original location. Legacy `.multiloud` files and project formats 1–8 remain supported; newly saved projects use format 9. Older projects without a Bass sensitivity setting open at 100% (Normal), preserving their previous behavior and stored envelope edits. To use reduced bass influence in an existing project, choose a lower sensitivity in section 01’s gear and apply the updated suggestions; manual offsets are preserved, and Undo restores the previous curves. Saved curves are not silently redesigned when a project opens; **Suggest** generates a fresh curve using the current section-change detector.

If sources are missing, a relink dialog offers an exact file selection or a folder search that includes subfolders. Folder search matches the exact filename and leaves ambiguous same-name matches unresolved for you to choose. Click **Done** to keep any unresolved tracks as muted red missing slots with their saved duration, order and settings. Each missing track also offers **Relink file** and **Relink folder** afterward. A replacement is decoded, analyzed and previewed in the same slot. Playback stops at a missing-track boundary. Export all is blocked until all sources are resolved; ready individual tracks can still be exported from their context menus. Undo history is session-only and holds up to 12 edits.

Right-click any music track or slice and choose **Relink…** to select an updated complete source render. Missing tracks offer **Choose file…** or **Locate folder…**. All music clips referencing that source path update together, including nonadjacent slices. Their source-time cuts, album order, input gains, target overrides, manual gains and exact authored project envelope remain in place; custom export boundaries and background placement are retained. The replacement is freshly decoded once, even when its path is unchanged, and slices share views of that decoded source. Relevant analysis and processing run for the replacement clips, while all-modules-off projects keep their live bypass. This does not invoke Suggest. The selected full render must retain the same timeline origin and cover every saved slice; a source that is too short is rejected before any clip changes. Inserting or removing time in the DAW requires adjusting the cuts yourself. Source replacement is one undoable edit; failed or cancelled loading leaves the previous sources intact.

## Processing details and limits

- Mono/stereo K-weighted, absolute/relative-gated integrated loudness using BS.1770 principles: 400 ms blocks and 100 ms hop. Three-second centered windows evaluated each second guide section leveling. A separate cached bass-sensitive profile guides envelopes and joins; standard LUFS, LRA and peak metering continue to use their existing full-signal measurement paths. Bass sensitivity changes do not redefine LUFS or alter the peak-protection detector. The LRA display is an estimate, especially on short clips.
- Peak metering uses a 4×, 24-tap windowed-sinc reconstruction estimate. This is **not certified loudness/compliance metering**.
- Limiting can audibly alter highly dynamic music at aggressive targets. No limiter can guarantee inaudible changes for arbitrary input. Lower the target if the track reports heavy reduction. With Peak protection off, the selected ceiling is inactive and no automatic attenuation is applied.
- Equal LUFS is a useful starting point, not a guarantee that differently arranged music feels identical in loudness. Audition quiet/loud transitions and refine the envelope. Intro/outro fades and silence are protected conservatively rather than normalized upward.
- Mono is monitored with equal-power duplication to stereo so its channel-summed metered energy is preserved. Exports retain native mono; third-party players may use a different mono playback gain.
- Inputs are decoded to float32 in RAM. Limit: just under 2 GiB of decoded samples per source (enough for a one-hour 48 kHz stereo background), with a 12 GiB release working-audio guard. Original and processed buffers, decoder buffers, analysis, multi-resolution waveform summaries, incremental-processing state, undo history, and the bounded 2 GiB preview-history cache require additional RAM. The cache budget includes retained processing and waveform-summary state. The current per-track preview remains available independently of history-cache eviction. Drawing tiles have their own bounded cache. 8–384 kHz, one or two channels only. Standard RIFF WAV export has a 4 GiB per-file limit.
- Loudline does not copy metadata/artwork or provide lossy export, spectral matching, VST hosting, or automatic listening-model training. Result audio and export use the same rendered samples.

## Build and verify

Requires the .NET 8 SDK on Windows (Visual Studio 2022 with desktop .NET support is optional).

```powershell
./build.ps1
```

This runs the dependency-free DSP tests and hardware-free playback regression tests, publishes a self-contained x64 executable, and writes `artifacts/Loudline-win-x64.zip`.

The Windows workflow also compares process launch to the early `MainWindow.ContentRendered` event for single-EXE and folder builds. It alternates three launches with fresh isolated bundle extraction directories and five repeated launches, and uploads `Loudline-startup-performance.json` with raw runs and medians. These are CI-machine event timings, not physical display timing, a cold operating-system cache measurement, or a virtual desktop placement guarantee. Only the single-EXE package is distributed.

```powershell
dotnet run --project tests/Multiloud.Tests/Multiloud.Tests.csproj -c Release
dotnet run --project tests/Multiloud.Playback.Tests/Multiloud.Playback.Tests.csproj -c Release
dotnet build Multiloud.sln -c Release
```

An optional CPU-only benchmark compares exact hierarchical waveform queries with direct PCM scans and the previous peak-bin/detail loop, using 20 synthetic stereo tracks, 3840 Comparison columns and four viewport fractions. It verifies queried extrema against PCM before timing. The default fixture uses about 440 MiB of PCM; arguments set seconds per track and timed repetitions. These measurements exclude WPF drawing, layout and GPU composition.

```powershell
dotnet run --project tests/Multiloud.Performance/Multiloud.Performance.csproj -c Release -- 30 5
```

The GitHub Windows workflow also launches the published WPF app, exercises WAV import/render/export with mono/stereo synthetic tracks, verifies the live processing queue, output selector, no-op toggles, cached restoration, animated ruler/overview navigation, readiness publishing, exact per-track/project/undo state, target-override cache isolation, and reordering without unnecessary DSP work. It captures ready/processing states, responsive layouts, and themed menus before publishing a release tied to the source commit. DSP checks cover reference sine loudness, matching, strength/trim, sustained sections, fades, silence, peak protection, stereo linkage, and cancellation. Fresh and incremental renders are compared bit for bit across envelope edits and matching/limiter modes; checks also cover filter checkpoints, local dependency ranges, immutable previous buffers, and no-op point insertion. Waveform checks compare arbitrary mono/stereo queries with exact samples, preserve boundary impulses, bound query work and allocations, and verify immutable local updates. Playback tests reproduce NAudio’s byte-backed float callback buffer and verify mixed-rate/channel playback, partial reads, original/preview buffers, repeated persistent seeking, queued-audio clock mapping, pending boundaries and concurrent callbacks without requiring a physical audio device. Cache tests verify canonical effective settings, no-op flat/bypassed envelopes, collinear point edits, reference reuse, LRU eviction, and memory limits including retained processing and waveform state. An independent FFmpeg `ebur128` check on a multitone/level-change/spike fixture agreed at −14.1 LUFS and −1.1 dBTP.

Architecture: `Multiloud.Core` contains platform-independent analysis/DSP; `Multiloud.App` is a Windows-only WPF editor with NAudio decoding and WASAPI playback. Two background workers build waveform images; the UI draws cached images and camera transforms. All three views share the bounded 128 MiB drawing cache, including conservative bitmap CPU/GPU storage accounting. Everything processes locally.

## Licence

Loudline is available under the [MIT License](LICENSE), copyright © 2026 O. Inha. Anyone may use, fork, modify and redistribute it, including commercially, provided that the copyright and licence notice are retained. The software is supplied without warranty.

Third-party components keep their own licence terms. Preserve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the packaged `licenses` directory when redistributing the executable. See those notices for Lucide icons, NAudio, the WDL resampler, and the included .NET runtimes.

[Btn Download]: https://img.shields.io/badge/⤓_Download_(ZIP,_~66MB)-177a5d?style=for-the-badge
[Link Download]: https://github.com/o-inha/loudline/releases/download/v1.0.0/Loudline-win-x64.zip
[Btn Support]: https://img.shields.io/badge/☕️_Buy_Me_a_Coffee-325b70?style=for-the-badge
[Link Support]: https://buymeacoffee.com/inha
