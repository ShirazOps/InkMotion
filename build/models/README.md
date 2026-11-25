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
