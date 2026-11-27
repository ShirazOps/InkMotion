"""Strict linked mono PCM WAV AAF interchange. Requires user-installed pyaaf2."""
import json
import pathlib
import sys
import urllib.parse
import urllib.request
import wave
from fractions import Fraction

try:
    import aaf2
    import aaf2.components
    import aaf2.essence
    import aaf2.mobs
    from aaf2.ama import create_network_locator
except ImportError as error:
    raise SystemExit(
        "pyaaf2 is missing from this Python environment. Install pyaaf2 in a trusted "
        "environment and select its python.exe. No dependency is downloaded by InkMotion."
    ) from error


def require(condition, message):
    if not condition:
        raise ValueError(message)


def integer(value, label):
    result = int(value)
    require(result == value and result >= 0, f"{label} is not a non-negative whole frame.")
    return result


def wav_info(path):
    with wave.open(str(path), "rb") as source:
        require(source.getnchannels() == 1 and source.getcomptype() == "NONE",
                "Only linked mono PCM WAV essence is supported.")
        return source.getnframes(), source.getframerate()


def export_aaf(input_path, output_path):
    data = json.loads(pathlib.Path(input_path).read_text(encoding="utf-8-sig"))
    rate = data["FrameRate"]
    require(rate in (24, 30, 60), "Unsupported AAF edit rate.")
    require(0 < len(data["Clips"]) <= 10000, "Invalid clip count.")
    with aaf2.open(output_path, "w") as file:
        composition = file.create.CompositionMob("InkMotion linked WAV edit")
        composition.usage = "Usage_TopLevel"
        file.content.mobs.append(composition)
        masters = {}
        for index, cut in enumerate(data["Clips"], 1):
            path = pathlib.Path(cut["Path"])
            require(path.is_absolute() and path.suffix.lower() == ".wav", "Expected an absolute WAV path.")
            frames, sample_rate = wav_info(path)
            start = integer(cut["Start"], "Record start")
            source_in = integer(cut["In"], "Source in")
            length = integer(cut["Length"], "Length")
            require(length > 0 and Fraction((source_in + length) * sample_rate, rate) <= frames,
                    "The source range exceeds the WAV essence.")
            require(start + length <= rate * 86400, "The edit exceeds 24 hours.")
            if str(path) not in masters:
                master = file.create.MasterMob(path.stem)
                file.content.mobs.append(master)
                master_slot = master.import_audio_essence(str(path), edit_rate=rate, offline=True)
                source_mob = master_slot.segment.mob
                source_mob.descriptor["Locator"].append(create_network_locator(file, str(path)))
                masters[str(path)] = (master, master_slot.slot_id)
            master, slot_id = masters[str(path)]
            slot = composition.create_empty_sequence_slot(rate, media_kind="sound")
            slot.name = f"Audio {index}"
            if start:
                slot.segment.components.append(file.create.Filler(media_kind="sound", length=start))
            slot.segment.components.append(master.create_source_clip(
                slot_id=slot_id, start=source_in, length=length, media_kind="sound"
            ))


def local_wav(descriptor):
    require(isinstance(descriptor, (aaf2.essence.PCMDescriptor, aaf2.essence.WAVEDescriptor)),
            "Only PCM/WAVE descriptors are supported (no compressed, multichannel or embedded essence).")
    if isinstance(descriptor, aaf2.essence.PCMDescriptor):
        require(descriptor["Channels"].value == 1, "Multichannel AAF essence is not supported.")
    else:
        # A WAVE Summary contains the RIFF/fmt header but no sample data.
        import io
        import struct
        summary = bytes(descriptor["Summary"].value)
        require(summary[:4] == b"RIFF" and summary[8:12] == b"WAVE", "Invalid WAVE Summary.")
        stream = io.BytesIO(summary)
        stream.seek(12)
        found = False
        while stream.tell() + 8 <= len(summary):
            tag = stream.read(4)
            size = struct.unpack("<I", stream.read(4))[0]
            if tag == b"fmt ":
                body = stream.read(size)
                require(len(body) >= 16, "Truncated WAVE fmt header.")
                encoding, channels = struct.unpack("<HH", body[:4])
                require(encoding == 1 and channels == 1, "Only mono PCM WAVE Summary is supported.")
                found = True
                break
            stream.seek(size + size % 2, 1)
        require(found, "WAVE Summary has no PCM format.")
    locators = list(descriptor["Locator"])
    require(len(locators) == 1 and locators[0].classdef.class_name == "NetworkLocator",
            "Expected one local WAV NetworkLocator.")
    uri = urllib.parse.urlsplit(locators[0]["URLString"].value)
    require(uri.scheme == "file" and uri.netloc in ("", "localhost") and not uri.query and not uri.fragment,
            "Only absolute local file URLs are supported; relink foreign media before importing.")
    path = pathlib.Path(urllib.request.url2pathname(uri.path))
    require(path.is_absolute() and path.suffix.lower() == ".wav", "Expected a local WAV path.")
    if path.exists():
        wav_info(path)
    return path


def resolve(clip, visited, offset_seconds, duration_seconds, parent_rate):
    require(isinstance(clip, aaf2.components.SourceClip), "Only SourceClip references are supported.")
    require(not clip["FadeInLength"].value and not clip["FadeOutLength"].value,
            "SourceClip fades are not supported.")
    require("ChannelIDs" not in clip or not clip["ChannelIDs"].value,
            "Explicit source-channel selection is not supported.")
    mob = clip.mob
    require(mob is not None and not isinstance(mob, aaf2.mobs.CompositionMob),
            "Missing or nested composition reference.")
    key = (str(mob.mob_id), clip.slot_id)
    require(key not in visited and len(visited) < 16, "Cyclic or excessively deep AAF reference.")
    visited = visited | {key}
    slot = mob.slot_at(clip.slot_id)
    require(slot.classdef.class_name == "TimelineMobSlot" and slot.origin == 0, "Only zero-origin timeline slots are supported.")
    require(slot.media_kind == "Sound", "Non-audio source slot.")
    source_rate = Fraction(slot.edit_rate)
    require(source_rate > 0 and clip.start >= 0, "Invalid source edit rate or position.")
    offset = Fraction(clip.start, 1) / parent_rate + offset_seconds
    require(offset + duration_seconds <= Fraction(slot.segment.length, 1) / source_rate,
            "A source reference exceeds its referenced slot.")
    if isinstance(mob, aaf2.mobs.SourceMob):
        require(mob.essence is None, "Embedded AAF essence is not supported.")
        path = local_wav(mob.descriptor)
        essence_rate = Fraction(mob.descriptor["SampleRate"].value)
        essence_length = mob.descriptor["Length"].value
        require(essence_rate > 0 and essence_length is not None and essence_length > 0,
                "Invalid essence descriptor duration.")
        require(offset + duration_seconds <= Fraction(essence_length, 1) / essence_rate,
                "A source reference exceeds the descriptor's essence duration.")
        return path, offset
    segment = slot.segment
    if isinstance(segment, aaf2.components.Sequence):
        parts = list(segment.components)
        require(len(parts) == 1, "Source sequences with edits/effects are not supported.")
        segment = parts[0]
    return resolve(segment, visited, offset, duration_seconds, source_rate)


def import_aaf(input_path, output_path):
    cuts = []
    with aaf2.open(input_path, "r") as file:
        compositions = list(file.content.compositionmobs())
        require(len(compositions) == 1, "AAF must contain exactly one composition.")
        composition = compositions[0]
        rate = None
        for slot in composition.slots:
            require(slot.classdef.class_name == "TimelineMobSlot" and slot.origin == 0,
                    "Only zero-origin timeline slots are supported.")
            slot_rate = Fraction(slot.edit_rate)
            require(slot_rate in (24, 30, 60), "Only 24/30/60 fps composition slots are supported, not sample-based timelines.")
            require(rate is None or rate == slot_rate, "Mixed composition edit rates are not supported.")
            rate = slot_rate
            if isinstance(slot.segment, aaf2.components.Timecode):
                timecode = slot.segment
                require(timecode.start == 0 and not timecode.drop and timecode.fps == rate,
                        "Only zero-origin NDF timecode is supported.")
                continue
            require(slot.media_kind == "Sound", "Picture, metadata and non-audio slots are not supported.")
            segment = slot.segment
            parts = list(segment.components) if isinstance(segment, aaf2.components.Sequence) else [segment]
            require(parts and not isinstance(parts[-1], aaf2.components.Filler),
                    "Empty slots and trailing fillers cannot be preserved.")
            position = 0
            for part in parts:
                require(part.length > 0, "Invalid AAF component length.")
                if isinstance(part, aaf2.components.Filler):
                    position += part.length
                    continue
                path, offset = resolve(part, set(), Fraction(0), Fraction(part.length, 1) / rate, rate)
                source_in = integer(offset * rate, "Source in")
                length = integer(part.length, "Length")
                require(position + length <= rate * 86400, "The edit exceeds 24 hours.")
                if path.exists():
                    samples, sample_rate = wav_info(path)
                    require(Fraction((source_in + length) * sample_rate, rate) <= samples,
                            "An AAF source range exceeds the linked WAV.")
                cuts.append({"Path": str(path), "Start": integer(position, "Record start"),
                             "In": source_in, "Length": length})
                require(len(cuts) <= 10000, "AAF contains more than 10,000 clips.")
                position += length
        require(rate is not None and cuts, "AAF contains no supported audio clips.")
    pathlib.Path(output_path).write_text(
        json.dumps({"FrameRate": int(rate), "Clips": cuts}), encoding="utf-8"
    )


if __name__ == "__main__":
    try:
        require(len(sys.argv) == 4 and sys.argv[1] in ("import", "export"), "Invalid bridge arguments.")
        if sys.argv[1] == "export":
            export_aaf(sys.argv[2], sys.argv[3])
        else:
            import_aaf(sys.argv[2], sys.argv[3])
    except Exception as error:
        print(f"AAF interchange failed: {error}", file=sys.stderr)
        sys.exit(1)
