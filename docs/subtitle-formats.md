# Subtitle formats

InkMotion imports and exports subtitle cues from **File > Import subtitles…** and **File > Export subtitles…**. Only subtitle tracks are exported; text (title) tracks are not. Cue times are aligned to the project frame rate on import, and overlapping cues are placed on separate subtitle tracks.

Whenever a file contains something InkMotion cannot represent exactly, or an export cannot write a clip property, the action finishes and then lists each issue in a dialog. Constructs that would make the result wrong (invalid timings, unterminated tags, out-of-order karaoke timestamps, text that would break the format) are rejected with an error instead.

## Word timing and styling

Text and subtitle clips can hold optional word runs (**Edit word timing and style…** in the clip context menu). Each run may be bold, italic, underlined or coloured, and may have a reveal time relative to the clip start. Before its reveal time a run is drawn in the clip's upcoming colour. Preview and export render runs identically.

## SubRip (`.srt`)

Plain text with timings. Formatting tags are kept as literal text. Export writes plain text only and reports when word runs, font, colour, position or fades were not written.

## WebVTT (`.vtt`) subset

Supported on import and export:

- The `WEBVTT` header (header text and metadata lines are ignored), optional cue identifiers and `NOTE` blocks.
- Timings in `hh:mm:ss.ttt` or `mm:ss.ttt` form.
- `<b>`, `<i>`, `<u>` and `<c>` tags, including the default colour classes (`white`, `lime`, `cyan`, `red`, `yellow`, `magenta`, `blue`, `black`).
- `STYLE` blocks containing `::cue(.class) { color: … }` and `::cue(:future) { color: … }` rules. Colours may be `#rgb`, `#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()` or a default class name. `:future` sets the upcoming-word colour.
- Timestamp tags (`<00:00:01.500>`), which become word reveal times.
- The entities `&amp;`, `&lt;`, `&gt;`, `&nbsp;`, `&lrm;`, `&rlm;`, `&quot;`, `&apos;` and numeric character references.
- Cue settings `line:P%`, `position:P%` and `align:center`/`middle`. Percentages place the caption centre, so a `line` without the `,center` alignment is approximate.

Imported with a warning:

- `<v Name>` voice spans become a `Name: ` prefix; `<lang>` spans keep their text but not the language; `<ruby>`/`<rt>` annotations are placed in brackets after the base text.
- Unknown or background (`bg_…`) classes, other CSS properties or selectors, `REGION` blocks and region settings, line numbers, `size`, `vertical` and other alignments are ignored.
- Unknown entities are kept as literal text.

Export writes a `STYLE` block for non-default colours and the upcoming-word colour, timestamp tags for revealed words, and `position`/`line` settings when a caption is not in the default position. WebVTT has no per-cue font, size, background or fade, so those are reported. Text containing a blank line is rejected because it would end the cue. Only one upcoming colour is written per file.

## Advanced SubStation Alpha (`.ass`) subset

Only ASS scripts (`ScriptType: v4.00+`, `[V4+ Styles]`) are supported. SubStation Alpha (`.ssa`, `[V4 Styles]`) files are rejected with an error.

Supported on import:

- `[Script Info]` `PlayResX`/`PlayResY` (missing values follow the libass defaults, 384 × 288) and `WrapStyle`.
- `[V4+ Styles]` and `[Events]` read through their `Format` lines, so field order may vary and the final `Text` field may contain commas. `Comment` events and `;` comment lines are skipped.
- Style font name and size (scaled from `PlayResY` to InkMotion's 1080-line reference), primary colour and alpha, secondary colour (the upcoming-word colour), bold, italic, underline, alignment and margins. `BorderStyle` 3 uses the outline colour as the caption box and `BorderStyle` 4 uses the back colour.
- Timings in `H:MM:SS.CC` form.
- The escapes `\N` (line break), `\n` (space, or a line break when `WrapStyle` is 2) and `\h` (non-breaking space).
- Override tags `\b`, `\i`, `\u`, `\c`/`\1c`, `\1a`, `\2c`, `\k`, `\r`, `\pos`, `\an`/`\a` and `\fad`, plus `\fn` and `\fs` before any text. Braces without a backslash are comments. `\k` durations become word reveal times.
- `\pos` and the alignment's vertical anchor are converted to InkMotion's caption-centre position using an estimated line height of 1.2 × the font size, so vertical placement is approximate.

Imported with a warning:

- `\kf`, `\K` and `\ko` sweeps become an instant colour change at the start of each syllable; `\alpha` applies only to the text colour.
- Left- and right-aligned events are centred; outlines (`BorderStyle` 1) are drawn with InkMotion's drop shadow and no box.
- Strikeout, scaling, spacing, angle, other override tags (such as `\blur`, `\bord`, `\move`, `\t`, `\clip`, `\frz`), mid-line font or karaoke-colour changes, event effects, `Picture`/`Sound`/`Movie`/`Command` events, `[Fonts]`/`[Graphics]` and other sections are ignored. The actor `Name` field is ignored.
- Events using vector drawings (`\p1` and above) are skipped. Out-of-range font sizes and positions are clamped.

Export writes `PlayResX: 1920` and `PlayResY: 1080`, one style per distinct font, size, text colour, upcoming colour and background colour (`BorderStyle` 3), `\pos` and `\fad` for every event, and `\r`-separated runs with `\b1`, `\i1`, `\u1`, `\c`/`\1a` and `\k` karaoke durations rounded to centiseconds. Clip rotation is reported. Words without a reveal time that follow timed words take the preceding time, with a warning. Text containing `{`, `}`, `\N`, `\n` or `\h` is rejected because ASS would reinterpret it.
