using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TradingPilot.Symbols;

namespace TradingPilot.Trading;

/// <summary>
/// Runs Swin-Tiny ONNX model on rendered L2 order book heatmaps.
/// Replaces the weighted scoring in Stage 2 of MarketMicrostructureAnalyzer.
///
/// The model takes a 224×224 RGB image and outputs [P(down), P(flat), P(up)].
/// Score = P(up) - P(down), giving a value in [-1, +1] compatible with existing thresholds.
/// </summary>
public class SwinPredictor : IDisposable
{
    private const string OnnxPath = @"D:\Third-Parties\WebullHook\swin_trading.onnx";
    private const string MetaPath = @"D:\Third-Parties\WebullHook\swin_trading_meta.json";
    private const int ImageSize = 224;
    private const int WindowSnapshots = 300;

    // Normalization stats — loaded from _meta.json (actual L2 heatmap statistics).
    // Falls back to ImageNet if meta not found (first run before training).
    private float[] _mean = [0.485f, 0.456f, 0.406f];
    private float[] _std = [0.229f, 0.224f, 0.225f];

    private readonly ILogger<SwinPredictor> _logger;
    private InferenceSession? _session;
    private FileSystemWatcher? _watcher;
    private DateTime _modelLoadedAt;

    public bool IsAvailable => _session != null;

    public SwinPredictor(ILogger<SwinPredictor> logger)
    {
        _logger = logger;
        LoadModel();
        WatchModel();
    }

    /// <summary>
    /// Run prediction on recent L2 snapshots.
    /// Returns a score in [-1, +1] where positive = bullish, negative = bearish.
    /// Returns null if model is unavailable or insufficient data.
    /// </summary>
    public SwinPrediction? Predict(List<SymbolBookSnapshot> snapshots)
    {
        if (_session == null || snapshots.Count < WindowSnapshots)
            return null;

        try
        {
            var window = snapshots.TakeLast(WindowSnapshots).ToList();

            // Render heatmap
            var pixels = RenderHeatmap(window);

            // Normalize and convert to CHW tensor (1, 3, 224, 224)
            var input = PreprocessImage(pixels);

            // Run inference
            var inputMeta = _session.InputMetadata;
            var inputName = inputMeta.Keys.First();
            var inputTensor = new DenseTensor<float>(input, [1, 3, ImageSize, ImageSize]);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

            using var results = _session.Run(inputs);
            var logits = results.First().AsEnumerable<float>().ToArray();

            // Softmax
            var probs = Softmax(logits);

            return new SwinPrediction
            {
                DownProbability = probs[0],
                FlatProbability = probs[1],
                UpProbability = probs[2],
                Score = probs[2] - probs[0],  // [-1, +1]
                Confidence = Math.Max(probs[0], Math.Max(probs[1], probs[2])),
                PredictedClass = Array.IndexOf(probs, probs.Max()) switch
                {
                    0 => "DOWN",
                    1 => "FLAT",
                    2 => "UP",
                    _ => "UNKNOWN"
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swin prediction failed");
            return null;
        }
    }

    /// <summary>
    /// Render L2 snapshots as a 224×224 RGB heatmap.
    /// Red = ask sizes, Blue = bid sizes, Green = mid-price line.
    /// </summary>
    private static byte[,,] RenderHeatmap(List<SymbolBookSnapshot> window)
    {
        var img = new byte[ImageSize, ImageSize, 3]; // [H, W, C] = [Y, X, RGB]

        // 1. Find price range
        decimal priceMin = decimal.MaxValue, priceMax = decimal.MinValue;
        foreach (var snap in window)
        {
            foreach (var p in snap.BidPrices.Where(p => p > 0))
                if (p < priceMin) priceMin = p;
            foreach (var p in snap.AskPrices.Where(p => p > 0))
                if (p > priceMax) priceMax = p;
        }

        if (priceMin >= priceMax) return img;

        // Add 5% padding
        var range = priceMax - priceMin;
        priceMin -= range * 0.05m;
        priceMax += range * 0.05m;
        range = priceMax - priceMin;

        // 2. Find max size for normalization (95th percentile)
        var allSizes = new List<decimal>();
        foreach (var snap in window)
        {
            allSizes.AddRange(snap.BidSizes.Where(s => s > 0));
            allSizes.AddRange(snap.AskSizes.Where(s => s > 0));
        }

        if (allSizes.Count == 0) return img;

        allSizes.Sort();
        var maxSize = allSizes[(int)(allSizes.Count * 0.95)];
        if (maxSize <= 0) maxSize = 1;

        // 3. Paint each snapshot as a column
        int nSnaps = window.Count;
        for (int t = 0; t < nSnaps; t++)
        {
            var snap = window[t];
            int x = (int)((long)t * (ImageSize - 1) / Math.Max(nSnaps - 1, 1));

            // Bid levels → Blue channel
            for (int i = 0; i < snap.BidPrices.Length; i++)
            {
                var price = snap.BidPrices[i];
                var size = snap.BidSizes[i];
                if (price <= 0 || size <= 0) continue;

                int y = PriceToY(price, priceMin, range);
                if (y < 0 || y >= ImageSize) continue;

                var intensity = (byte)(Math.Sqrt((double)Math.Min(size / maxSize, 1m)) * 255);
                if (intensity > img[y, x, 2])
                    img[y, x, 2] = intensity; // Blue
            }

            // Ask levels → Red channel
            for (int i = 0; i < snap.AskPrices.Length; i++)
            {
                var price = snap.AskPrices[i];
                var size = snap.AskSizes[i];
                if (price <= 0 || size <= 0) continue;

                int y = PriceToY(price, priceMin, range);
                if (y < 0 || y >= ImageSize) continue;

                var intensity = (byte)(Math.Sqrt((double)Math.Min(size / maxSize, 1m)) * 255);
                if (intensity > img[y, x, 0])
                    img[y, x, 0] = intensity; // Red
            }

            // Mid-price line → Green channel
            int midY = PriceToY(snap.MidPrice, priceMin, range);
            if (midY >= 0 && midY < ImageSize)
                img[midY, x, 1] = 255; // Green
        }

        return img;
    }

    private static int PriceToY(decimal price, decimal priceMin, decimal priceRange)
    {
        if (priceRange <= 0) return ImageSize / 2;
        var normalized = (double)((price - priceMin) / priceRange);
        return (int)((1.0 - normalized) * (ImageSize - 1)); // High price = top (y=0)
    }

    /// <summary>
    /// Convert HWC uint8 image to CHW float32 tensor with channel normalization.
    /// Uses stats from _meta.json (actual L2 heatmap data), not ImageNet defaults.
    /// </summary>
    private float[] PreprocessImage(byte[,,] pixels)
    {
        var tensor = new float[3 * ImageSize * ImageSize];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < ImageSize; y++)
            {
                for (int x = 0; x < ImageSize; x++)
                {
                    float val = pixels[y, x, c] / 255f;
                    val = (val - _mean[c]) / _std[c];
                    tensor[c * ImageSize * ImageSize + y * ImageSize + x] = val;
                }
            }
        }
        return tensor;
    }

    private static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - max)).ToArray();
        var sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    private void LoadModel()
    {
        try
        {
            if (!File.Exists(OnnxPath))
            {
                _logger.LogInformation("Swin ONNX model not found at {Path}, Stage 2 will use weighted scoring", OnnxPath);
                return;
            }

            var options = new SessionOptions();
            // Try CUDA GPU first, fall back to CPU
            try
            {
                options.AppendExecutionProvider_CUDA(0);
                _logger.LogInformation("Swin using CUDA GPU provider");
            }
            catch
            {
                _logger.LogInformation("Swin falling back to CPU provider (install CUDA 12 toolkit for GPU)");
            }
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session?.Dispose();
            _session = new InferenceSession(OnnxPath, options);
            _modelLoadedAt = DateTime.UtcNow;

            // Load normalization stats from _meta.json (computed from actual L2 heatmaps)
            LoadNormalizationStats();

            _logger.LogWarning("Swin ONNX model loaded from {Path}", OnnxPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Swin ONNX model");
            _session = null;
        }
    }

    private void LoadNormalizationStats()
    {
        try
        {
            if (!File.Exists(MetaPath)) return;

            var json = File.ReadAllText(MetaPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("normalization", out var norm))
            {
                if (norm.TryGetProperty("mean", out var meanArr) && meanArr.GetArrayLength() == 3)
                {
                    _mean = [meanArr[0].GetSingle(), meanArr[1].GetSingle(), meanArr[2].GetSingle()];
                }
                if (norm.TryGetProperty("std", out var stdArr) && stdArr.GetArrayLength() == 3)
                {
                    _std = [stdArr[0].GetSingle(), stdArr[1].GetSingle(), stdArr[2].GetSingle()];
                }
            }

            _logger.LogInformation("Swin normalization: mean=[{M0:F4},{M1:F4},{M2:F4}] std=[{S0:F4},{S1:F4},{S2:F4}]",
                _mean[0], _mean[1], _mean[2], _std[0], _std[1], _std[2]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Swin meta, using default normalization");
        }
    }

    private void WatchModel()
    {
        try
        {
            var dir = Path.GetDirectoryName(OnnxPath);
            var file = Path.GetFileName(OnnxPath);
            if (dir == null) return;

            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                // Debounce: wait for file to finish writing
                Thread.Sleep(2000);
                _logger.LogInformation("Swin ONNX model file changed, reloading...");
                LoadModel();
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set up Swin model file watcher");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _session?.Dispose();
    }
}

public class SwinPrediction
{
    public float DownProbability { get; init; }
    public float FlatProbability { get; init; }
    public float UpProbability { get; init; }

    /// <summary>Score = P(up) - P(down), in [-1, +1]. Compatible with existing thresholds.</summary>
    public float Score { get; init; }

    /// <summary>Max probability across classes.</summary>
    public float Confidence { get; init; }

    public string PredictedClass { get; init; } = "";
}
