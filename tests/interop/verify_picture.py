"""Verify InkMotion's generated files with independent OpenTimelineIO adapters."""
import pathlib
import sys

import opentimelineio as otio

directory = pathlib.Path(sys.argv[1])
expected = [(0, 48, 72), (120, 24, 48)]
for extension, adapter in (("xml", "fcp_xml"), ("edl", "cmx_3600")):
    timeline = otio.adapters.read_from_file(
        str(directory / f"inkmotion.{extension}"), adapter_name=adapter
    )
    clips = list(timeline.find_clips())
    actual = [
        (
            int(clip.range_in_parent().start_time.value),
            int(clip.source_range.start_time.value),
            int(clip.source_range.duration.value),
        )
        for clip in clips
    ]
    assert actual == expected, (extension, actual)
    assert all(clip.source_range.duration.rate == 24 for clip in clips)
    assert all("cut" in clip.media_reference.target_url for clip in clips)
    otio.adapters.write_to_file(
        timeline, str(directory / f"external.{extension}"), adapter_name=adapter
    )
    print(f"{adapter}: verified record/source frames, gap, rate and media references")
