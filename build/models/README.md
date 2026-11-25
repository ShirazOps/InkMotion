# On-device AI models

InkMotion does not bundle or automatically download AI models. AI pose limb tracking and automatic occlusion mattes need the ONNX files below, imported from **Ink workspace → AI MODELS → Import…**. They run locally with Windows ML on x64 and ARM64 builds only.

Check each project's licence and the licence of the specific checkpoint before exporting, using or redistributing it. Both projects below publish their code under Apache-2.0, but weights trained on other datasets can carry extra terms. For pose, use weights trained only on COCO; avoid checkpoints that also include datasets with non-commercial terms.

| Model | File name | Inputs | Outputs |
| --- | --- | --- | --- |
| RTMPose-m, COCO 17 keypoints, 256 × 192 | `rtmpose-m-coco-256x192.onnx` | `input` float32 `[1, 3, 256, 192]`, RGB, ImageNet mean/std in 0–255 | `simcc_x` `[1, 17, 384]`, `simcc_y` `[1, 17, 512]` (split ratio 2) |
| MobileSAM image encoder | `mobilesam-encoder.onnx` | `image` float32 `[1, 3, 1024, 1024]`, RGB, longest side resized to 1024, ImageNet mean/std, zero padding at the right and bottom | `image_embeddings` `[1, 256, 64, 64]` |
| MobileSAM mask decoder | `mobilesam-decoder.onnx` | `image_embeddings`, `point_coords` `[1, N, 2]`, `point_labels` `[1, N]`, `mask_input` `[1, 1, 256, 256]`, `has_mask_input` `[1]`, `orig_im_size` `[2]` | `masks`, `iou_predictions`, `low_res_masks` (single mask) |

InkMotion checks these names when loading a model and refuses a file that does not match.

## RTMPose

1. Install [MMPose](https://github.com/open-mmlab/mmpose) and [MMDeploy](https://github.com/open-mmlab/mmdeploy) in a Python environment.
2. Download a COCO-only RTMPose-m 256 × 192 checkpoint from the [RTMPose project page](https://github.com/open-mmlab/mmpose/tree/main/projects/rtmpose) and confirm its licence.
3. Export with MMDeploy's static ONNX SimCC configuration, which names the input `input` and the outputs `simcc_x` and `simcc_y`:

   ```text
   python mmdeploy/tools/deploy.py \
     mmdeploy/configs/mmpose/pose-detection_simcc_onnxruntime_static.py \
     <rtmpose-m_coco_256x192 config> <checkpoint.pth> <sample.jpg> \
     --work-dir out --device cpu
   ```

4. Rename `out/end2end.onnx` to `rtmpose-m-coco-256x192.onnx`.

## MobileSAM

1. Clone [MobileSAM](https://github.com/ChaoningZhang/MobileSAM), install it, and confirm the licence of `mobile_sam.pt`.
2. Export the decoder with the repository's ONNX script, returning a single mask. This produces the decoder input and output names listed above:

   ```text
   python scripts/export_onnx_model.py --checkpoint weights/mobile_sam.pt \
     --model-type vit_t --output mobilesam-decoder.onnx --return-single-mask
   ```

3. Export the encoder (`model.image_encoder`) with `torch.onnx.export`, using a `[1, 3, 1024, 1024]` input named `image` and an output named `image_embeddings`. InkMotion normalises and pads the image itself, so export the bare encoder without preprocessing.

After importing, InkMotion shows each file's SHA-256 so you can record which build you are using. Exported files are not covered by InkMotion's licence.

## Speech recognition and speaker labels

Speech models are optional, are not bundled, and are never downloaded by InkMotion. If you choose to use them, obtain the files yourself from the sources below, extract them, and import the required files from the speech subtitle dialogue. Put each model set's files in one folder before opening the picker. InkMotion copies them under `%LOCALAPPDATA%\InkMotion\Models\Speech` and displays their SHA-256 hashes after import. Import checks file sizes and the ONNX header; it does not establish a model's provenance, licence, or compatibility.

The `org.k2fsa.sherpa.onnx` NuGet package supplies the local inference runtime (Apache-2.0); it does not include speech weights. The model weights have separate terms:

| Use | Required files | Model source and terms |
| --- | --- | --- |
| Multilingual transcription | A matching `tiny-encoder.int8.onnx` and `tiny-decoder.int8.onnx` pair, plus `tiny-tokens.txt` | The [sherpa-onnx Whisper guide](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/whisper/index.html) links to its exported files. The underlying OpenAI Whisper model is MIT-licensed; check the terms accompanying the exact export you obtain. |
| Speaker segmentation | `model.int8.onnx` or `model.onnx` | sherpa-onnx's [speaker diarisation model guide](https://github.com/k2-fsa/sherpa-onnx/blob/master/docs/source/onnx/speaker-diarization/models.rst) identifies this as an export of [pyannote segmentation 3.0](https://huggingface.co/pyannote/segmentation-3.0), which is MIT-licensed. Hugging Face requires users to accept the model conditions and share account information before accessing its files. Do not bypass that access gate; use the model only if you accept those conditions. |
| Speaker embeddings | `3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx` | The [3D-Speaker ERes2Net model](https://www.modelscope.cn/models/iic/speech_eres2net_sv_en_voxceleb_16k/) is listed under Apache-2.0. sherpa-onnx's diarisation guide links to the compatible ONNX export. This embedding model is trained for English speech, so speaker labels may be less reliable for other languages. |

Whisper expects the multilingual tiny files above; this import flow does not accept other Whisper sizes or English-only `tiny.en` files. Speaker labelling is optional and requires both diarisation files. Speaker numbers are anonymous turns, not identities, and should be checked. Each model run is limited to one hour of audio. Processing is local; speech audio is not uploaded and InkMotion does not download the model files.
