# Third-party software and model notices

The original LiveCaptions Translator license and copyright remain in [LICENSE](LICENSE). Modifications in this fork add a continuous bilingual reader and optional local speaker diarization. No endorsement by the upstream authors or model providers is implied.

## New runtime dependencies

| Component | Pinned version | License / source |
| --- | --- | --- |
| NAudio | 2.2.1 | MIT, copyright Mark Heath; [license](licenses/NAudio.txt), [source](https://github.com/naudio/NAudio/tree/v2.2.1) |
| sherpa-onnx | org.k2fsa.sherpa.onnx 2.0.1, commit af2d8d81dbfa61c4cea4c568da78f3982edca005 | Apache-2.0; copyright Xiaomi Corporation / the Next-gen Kaldi team; [license](licenses/Apache-2.0.txt), [source](https://github.com/k2-fsa/sherpa-onnx/tree/af2d8d81dbfa61c4cea4c568da78f3982edca005) |
| ONNX Runtime | 1.28.2, shipped inside the sherpa x64 runtime package | MIT, copyright Microsoft; [license](licenses/ONNXRuntime.txt), [third-party notices](licenses/ONNXRuntime-ThirdPartyNotices.txt) |

## Models

Model files are obtained from the public sherpa-onnx release URLs in [the checksum manifest](src/speech/models/manifest.json). They are not committed into this repository. The installer selects only the listed inference files.

- **Zipformer English 2023-06-21, INT8**: model README declares Apache-2.0. Original training checkpoint by marcoyang / icefall contributors: [model card](https://huggingface.co/marcoyang/icefall-libri-giga-pruned-transducer-stateless7-streaming-2023-04-04), [training recipe](https://github.com/k2-fsa/icefall/pull/984). See [Apache-2.0](licenses/Apache-2.0.txt).
- **pyannote segmentation-3.0**: MIT, copyright (c) 2022 CNRS, from the license included in the public sherpa model archive. See [model license](licenses/Pyannote-Segmentation.txt) and [model card](https://huggingface.co/pyannote/segmentation-3.0). Authors and citations: Alexis Plaquet and Hervé Bredin, “Powerset multi-class cross entropy loss for neural speaker diarization”, INTERSPEECH 2023. The application uses the publicly distributed ONNX conversion, not the gated Hugging Face download endpoint.
- **NVIDIA NeMo TitaNet Small**: the official [NGC model card](https://catalog.ngc.nvidia.com/orgs/nvidia/nemo/models/titanet_small) states that the model is covered by the NeMo Toolkit license; the corresponding [NeMo v1.19.0 license](https://github.com/NVIDIA/NeMo/blob/v1.19.0/LICENSE) is Apache-2.0. See [Apache-2.0](licenses/Apache-2.0.txt). NVIDIA and NeMo contributors retain their rights.
- **English online punctuation 2024-08-06, INT8**: Edge-Punct-Casing, copyright its contributors, Apache-2.0; [source and license](https://github.com/frankyoujian/Edge-Punct-Casing), [Apache-2.0](licenses/Apache-2.0.txt).

## Existing dependencies and self-contained runtime

The distribution retains WPF-UI 4.0.1 ([MIT license](licenses/WPF-UI.txt)); CsvHelper 33.0.1 by Josh Close (Apache-2.0 option); Microsoft.Data.Sqlite 9.0.1 / .NET libraries (MIT); SQLitePCLRaw 2.1.10 by SourceGear (Apache-2.0); and SQLite (public domain). The self-contained .NET runtime license and notices are included in [licenses/DotNet.txt](licenses/DotNet.txt) and [licenses/DotNet-ThirdPartyNotices.txt](licenses/DotNet-ThirdPartyNotices.txt). Interop.UIAutomationClient and other original dependency declarations remain in the project file.

## Test corpus

The reproducible audio tests use six utterances from **LibriSpeech test-clean**, by Vassil Panayotov, Guoguo Chen, Daniel Povey and Sanjeev Khudanpur. Source: [OpenSLR 12](https://www.openslr.org/12), license [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Test code concatenates clips, inserts silence, crops short turns, and mixes clips for overlap tests. Generated references retain file names, original text, speaker labels, and audio SHA-256. Corpus audio is downloaded separately and is not bundled with the application.
