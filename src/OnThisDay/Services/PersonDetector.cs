using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace OnThisDay.Services;

public readonly record struct SubjectScore(float Score, float Area);

public readonly record struct PersonDetection(SubjectScore Person, SubjectScore Cat, FocusBox? Focus);

/// <summary>
/// YOLOX-s person and cat detector (COCO classes 0 and 15), both read from the same pass. Pre- and post-processing follow YOLOX
/// demo/ONNXRuntime/onnx_inference.py: top-left letterbox padded with 114, BGR, raw 0-255, CHW,
/// and the release model's output still needs the grid decode from demo_postprocess.
/// </summary>
public sealed class PersonDetector : IDisposable
{
    private const int InputSize = 640;
    private const float AreaMinScore = 0.3f;
    private const float FocusMinScore = 0.5f;
    private static readonly int[] Strides = [8, 16, 32];

    // Row layout is [cx, cy, w, h, obj, cls0..cls79]
    private const int PersonColumn = 5 + 0;
    private const int CatColumn = 5 + 15;

    private readonly InferenceSession? _session;
    private readonly string _inputName = "";
    private readonly string _outputName = "";
    private readonly int _channels;

    public bool IsEnabled => _session != null;

    public PersonDetector(ILogger<PersonDetector> logger)
    {
        // /app/models/yolox_s.onnx in the image; locally copied from the repo's models/ folder by the csproj
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "yolox_s.onnx");
        if (!File.Exists(modelPath))
        {
            logger.LogWarning("Person model not found at {Path}; person and cat detection is off and /api/day returns photos unfiltered", modelPath);
            return;
        }

        // Two threads keep the NAS responsive while detection runs
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        var session = new InferenceSession(modelPath, options);

        var (inputName, input) = session.InputMetadata.First();
        var (outputName, output) = session.OutputMetadata.First();
        logger.LogInformation("Person model {Path}: input {Input} [{InputShape}], output {Output} [{OutputShape}]",
            modelPath, inputName, string.Join(", ", input.Dimensions), outputName, string.Join(", ", output.Dimensions));

        var anchors = Strides.Sum(s => (InputSize / s) * (InputSize / s));
        if (!input.Dimensions.SequenceEqual([1, 3, InputSize, InputSize])
            || output.Dimensions.Length != 3 || output.Dimensions[1] != anchors || output.Dimensions[2] <= CatColumn)
        {
            logger.LogError("Unexpected person model shape; person detection is off");
            session.Dispose();
            return;
        }

        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _channels = output.Dimensions[2];
    }

    public PersonDetection Detect(string filePath)
    {
        if (_session == null)
            throw new InvalidOperationException("Person detection is off");

        // Letterbox: resize to fit 640 and keep it in the top-left corner
        using var image = ThumbnailService.LoadOriented(filePath);
        var imgW = (int)image.Width;
        var imgH = (int)image.Height;
        var ratio = Math.Min((float)InputSize / imgW, (float)InputSize / imgH);
        var resizedW = Math.Max(1, (int)(imgW * ratio));
        var resizedH = Math.Max(1, (int)(imgH * ratio));

        image.FilterType = FilterType.Triangle; // bilinear, like cv2.INTER_LINEAR
        image.Resize(new MagickGeometry((uint)resizedW, (uint)resizedH) { IgnoreAspectRatio = true });
        if (image.ColorSpace != ColorSpace.sRGB)
            image.ColorSpace = ColorSpace.sRGB;

        using var pixels = image.GetPixelsUnsafe();
        var bgr = pixels.ToByteArray(PixelMapping.BGR)!;

        var plane = InputSize * InputSize;
        var tensor = new float[3 * plane];
        Array.Fill(tensor, 114f);
        for (var y = 0; y < resizedH; y++)
        {
            for (var x = 0; x < resizedW; x++)
            {
                var src = (y * resizedW + x) * 3;
                var dst = y * InputSize + x;
                tensor[dst] = bgr[src];
                tensor[plane + dst] = bgr[src + 1];
                tensor[2 * plane + dst] = bgr[src + 2];
            }
        }

        using var inputValue = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, InputSize, InputSize]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, [_inputName], [inputValue], [_outputName]);
        var data = outputs[0].GetTensorDataAsSpan<float>();

        // Rows are [cx, cy, w, h, obj, cls0..cls79] per anchor, strides 8/16/32 in row-major grid order.
        // obj and cls are already sigmoided; the box needs the grid decode. No NMS: per class we only need
        // the best score and the largest box, plus the union of the confident person and cat boxes.
        float personScore = 0, personArea = 0, catScore = 0, catArea = 0;
        float focusX1 = float.MaxValue, focusY1 = float.MaxValue, focusX2 = float.MinValue, focusY2 = float.MinValue;
        var anchor = 0;
        foreach (var stride in Strides)
        {
            var gridSize = InputSize / stride;
            for (var gy = 0; gy < gridSize; gy++)
            {
                for (var gx = 0; gx < gridSize; gx++, anchor++)
                {
                    var row = data.Slice(anchor * _channels, _channels);
                    var person = row[4] * row[PersonColumn];
                    var cat = row[4] * row[CatColumn];
                    personScore = Math.Max(personScore, person);
                    catScore = Math.Max(catScore, cat);
                    if (person < AreaMinScore && cat < AreaMinScore)
                        continue;

                    // Decode, then undo the letterbox scale to get auto-oriented image pixels
                    var cx = (row[0] + gx) * stride / ratio;
                    var cy = (row[1] + gy) * stride / ratio;
                    var w = MathF.Exp(row[2]) * stride / ratio;
                    var h = MathF.Exp(row[3]) * stride / ratio;
                    var x1 = Math.Clamp(cx - w / 2, 0, imgW);
                    var y1 = Math.Clamp(cy - h / 2, 0, imgH);
                    var x2 = Math.Clamp(cx + w / 2, 0, imgW);
                    var y2 = Math.Clamp(cy + h / 2, 0, imgH);

                    var area = (x2 - x1) * (y2 - y1) / ((float)imgW * imgH);
                    if (person >= AreaMinScore)
                        personArea = Math.Max(personArea, area);
                    if (cat >= AreaMinScore)
                        catArea = Math.Max(catArea, area);

                    if (person >= FocusMinScore || cat >= FocusMinScore)
                    {
                        focusX1 = Math.Min(focusX1, x1);
                        focusY1 = Math.Min(focusY1, y1);
                        focusX2 = Math.Max(focusX2, x2);
                        focusY2 = Math.Max(focusY2, y2);
                    }
                }
            }
        }

        FocusBox? focus = focusX2 > focusX1 && focusY2 > focusY1
            ? new FocusBox(focusX1 / imgW, focusY1 / imgH, (focusX2 - focusX1) / imgW, (focusY2 - focusY1) / imgH)
            : null;

        return new PersonDetection(new SubjectScore(personScore, personArea), new SubjectScore(catScore, catArea), focus);
    }

    public void Dispose() => _session?.Dispose();
}
