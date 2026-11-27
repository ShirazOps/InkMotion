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
