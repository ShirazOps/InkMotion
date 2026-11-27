"""Inspect binary AAF independently of the bridge and construct external fixtures."""
import pathlib
import shutil
import sys

import aaf2
import opentimelineio as otio
from aaf2.ama import create_network_locator, get_wave_fmt

directory = pathlib.Path(sys.argv[1])
exported = directory / "inkmotion-audio.aaf"
assert exported.read_bytes()[:8] == bytes.fromhex("D0CF11E0A1B11AE1")
with aaf2.open(str(exported), "r") as file:
    composition, = file.content.compositionmobs()
    assert not list(file.content.essencedata)
    actual = []
    for slot in composition.slots:
        filler, clip = slot.segment.components
        assert filler.media_kind == clip.media_kind == "Sound"
        master = clip.mob
        source = master.slot_at(clip.slot_id).segment.mob
        descriptor = source.descriptor
        assert descriptor["Channels"].value == 1
        assert descriptor["AudioSamplingRate"].value == 48000
        assert descriptor["Locator"][0]["URLString"].value.endswith("linked%20mono.wav")
        actual.append((filler.length, clip.start, clip.length))
    assert actual == [(48, 24, 48), (144, 48, 24)], actual

timeline = otio.adapters.read_from_file(str(exported), adapter_name="AAF")
clips = list(timeline.find_clips())
assert [
    (int(c.range_in_parent().start_time.value), int(c.source_range.start_time.value),
     int(c.source_range.duration.value))
    for c in clips
] == [(48, 24, 48), (144, 48, 24)]
assert all(c.media_reference.target_url.endswith("linked%20mono.wav") for c in clips)

# Independent hierarchy: a WAVEDescriptor (the bridge writer uses PCMDescriptor),
# and sample-rate Master/Source slots rather than the writer's frame-rate slots.
with aaf2.open(str(directory / "external-audio.aaf"), "w") as file:
    source = file.create.SourceMob("External WAV")
    file.content.mobs.append(source)
    source_slot = source.create_empty_slot(edit_rate=48000, media_kind="sound")
    source_slot.segment.length = 480000
    descriptor = file.create.WAVEDescriptor()
    descriptor["SampleRate"].value = 48000
    descriptor["Length"].value = 480000
    descriptor["Summary"].value = get_wave_fmt(str(directory / "linked mono.wav"))
    descriptor["Locator"].append(create_network_locator(file, str(directory / "linked mono.wav")))
    source.descriptor = descriptor
    master = file.create.MasterMob("External master")
    file.content.mobs.append(master)
    master_slot = master.create_timeline_slot(48000)
    master_slot.segment = source.create_source_clip(
        slot_id=source_slot.slot_id, start=0, length=480000, media_kind="sound"
    )
    composition = file.create.CompositionMob("External composition")
    file.content.mobs.append(composition)
    slot = composition.create_empty_sequence_slot(24, media_kind="sound")
    slot.segment.components.append(file.create.Filler(media_kind="sound", length=96))
    slot.segment.components.append(master.create_source_clip(
        slot_id=master_slot.slot_id, start=72, length=24, media_kind="sound"
    ))

external = otio.adapters.read_from_file(str(directory / "external-audio.aaf"), adapter_name="AAF")
external_clip, = external.find_clips()
assert external_clip.source_range.start_time.to_seconds() == 3
assert external_clip.source_range.duration.to_seconds() == 1
assert external_clip.range_in_parent().start_time.to_seconds() == 4

for name in ("unsupported-picture", "unsupported-stereo", "unsupported-cycle"):
    target = directory / f"{name}.aaf"
    shutil.copyfile(exported, target)
    with aaf2.open(str(target), "rw") as file:
        composition, = file.content.compositionmobs()
        if name == "unsupported-picture":
            slot = composition.create_empty_sequence_slot(24, media_kind="picture")
            slot.segment.components.append(file.create.Filler(media_kind="picture", length=24))
        elif name == "unsupported-stereo":
            source, = file.content.sourcemobs()
            source.descriptor["Channels"].value = 2
        else:
            master, = file.content.mastermobs()
            master.slots[0].segment = master.create_source_clip(
                slot_id=master.slots[0].slot_id, start=0, length=240, media_kind="sound"
            )
print(f"pyaaf2 {aaf2.__version__}: verified binary header, linked essence, descriptors, source/record frames; external and rejection fixtures written")
